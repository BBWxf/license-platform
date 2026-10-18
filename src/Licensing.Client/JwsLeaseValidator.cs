using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Licensing.Client
{
    internal sealed class JwsLeaseValidator
    {
        private static readonly JsonLoadSettings StrictJson = new JsonLoadSettings
        {
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
            CommentHandling = CommentHandling.Ignore,
            LineInfoHandling = LineInfoHandling.Ignore
        };

        private readonly LicenseClientOptions _options;

        internal JwsLeaseValidator(LicenseClientOptions options)
        {
            _options = options;
            foreach (var key in options.ServerVerificationKeys)
            {
                try
                {
                    using (CryptoUtilities.CreateEcdsa(key)) { }
                }
                catch (LicensingException) { throw; }
                catch (Exception ex)
                {
                    throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "A server ES256 JWK cannot be imported.", ex);
                }
            }
            var duplicate = options.ServerVerificationKeys.GroupBy(item => item.Kid, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
            if (duplicate != null)
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "Server JWK kid values must be unique: " + duplicate.Key);
        }

        internal LicenseLease Validate(
            string compactJws,
            InstallationKeyRecord installationKey,
            DateTimeOffset validationTimeUtc,
            bool allowExpiredForOnlineRefresh = false)
        {
            try
            {
                return ValidateCore(compactJws, installationKey, validationTimeUtc, allowExpiredForOnlineRefresh);
            }
            catch (Exception ex) when (ex is JsonException || ex is FormatException || ex is InvalidCastException ||
                ex is ArgumentException || ex is OverflowException)
            {
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The lease JWS contains malformed headers or claims.", ex);
            }
        }

        private LicenseLease ValidateCore(
            string compactJws,
            InstallationKeyRecord installationKey,
            DateTimeOffset validationTimeUtc,
            bool allowExpiredForOnlineRefresh)
        {
            if (string.IsNullOrWhiteSpace(compactJws))
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The lease token is empty.");

            var segments = compactJws.Split('.');
            if (segments.Length != 3 || segments.Any(string.IsNullOrEmpty))
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The lease must be a three-segment compact JWS.");

            JObject header;
            byte[] signature;
            try
            {
                header = JObject.Parse(new UTF8Encoding(false, true).GetString(Base64Url.Decode(segments[0])), StrictJson);
                signature = Base64Url.Decode(segments[2]);
            }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The lease JWS header or signature is malformed.", ex);
            }

            var algorithm = header.Value<string>("alg");
            if (!string.Equals(algorithm, "ES256", StringComparison.Ordinal))
                throw new LicensingException(LicenseErrorCode.UnsupportedAlgorithm, "Only ES256 lease signatures are accepted.");
            if (header["crit"] != null || (header["b64"] != null &&
                (header["b64"]!.Type != JTokenType.Boolean || !header["b64"]!.Value<bool>())))
                throw new LicensingException(LicenseErrorCode.UnsupportedAlgorithm, "Critical or unencoded-payload JWS headers are not supported.");

            var kid = header.Value<string>("kid");
            if (string.IsNullOrWhiteSpace(kid))
                throw new LicensingException(LicenseErrorCode.UnknownSigningKey, "The lease JWS does not identify a signing key.");
            var serverKey = _options.ServerVerificationKeys.SingleOrDefault(item => string.Equals(item.Kid, kid, StringComparison.Ordinal));
            if (serverKey == null)
                throw new LicensingException(LicenseErrorCode.UnknownSigningKey, "The lease was signed by an unknown server key: " + kid);

            var signingInput = Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]);
            try
            {
                using (var verifier = CryptoUtilities.CreateEcdsa(serverKey))
                {
                    if (!CryptoUtilities.VerifyJws(verifier, signingInput, signature))
                        throw new LicensingException(LicenseErrorCode.InvalidServerSignature, "The lease signature is invalid.");
                }
            }
            catch (LicensingException) { throw; }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.InvalidServerSignature, "The lease signature could not be verified.", ex);
            }
            finally
            {
                Array.Clear(signature, 0, signature.Length);
            }

            JObject payload;
            try
            {
                payload = JObject.Parse(new UTF8Encoding(false, true).GetString(Base64Url.Decode(segments[1])), StrictJson);
            }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The signed lease payload is not a JSON object.", ex);
            }

            ValidateIssuerAndAudience(payload);

            var issuedAt = RequiredDate(payload, "iat");
            var expiresAt = RequiredDate(payload, "exp");
            var notBefore = payload["nbf"] == null ? issuedAt : DateTimeClaims.FromUnixSeconds(payload["nbf"]!, "nbf");
            if (expiresAt <= issuedAt || expiresAt - issuedAt > _options.OfflineLeaseLimit)
                throw new LicensingException(LicenseErrorCode.LeaseLifetimeExceeded, "The signed lease lifetime exceeds the configured maximum of six hours.");
            if (issuedAt > validationTimeUtc + _options.ClockSkew || notBefore > validationTimeUtc + _options.ClockSkew)
                throw new LicensingException(LicenseErrorCode.LeaseNotYetValid, "The signed lease is not yet valid.");
            if (!allowExpiredForOnlineRefresh && expiresAt < validationTimeUtc - _options.ClockSkew)
                throw new LicensingException(LicenseErrorCode.LeaseExpired, "The signed lease has expired.");

            var licenseId = payload.Value<string>("sub");
            if (!Guid.TryParse(licenseId, out var parsedLicenseId) || parsedLicenseId == Guid.Empty)
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The lease sub claim must be a non-empty license GUID.");

            var bindingToken = payload["binding_version"];
            if (bindingToken == null || bindingToken.Type != JTokenType.Integer || bindingToken.Value<long>() < 1)
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The lease binding_version claim must be a positive integer.");
            var bindingVersion = bindingToken.Value<long>();

            var deviceId = payload.Value<string>("device_id");
            if (string.IsNullOrWhiteSpace(deviceId))
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The lease device_id claim is required.");

            var directThumbprint = payload.Value<string>("installation_key_thumbprint");
            var confirmationThumbprint = payload.SelectToken("cnf.jkt")?.Value<string>();
            if (!string.IsNullOrWhiteSpace(directThumbprint) && !string.IsNullOrWhiteSpace(confirmationThumbprint) &&
                !MachineFingerprint.FixedTimeEquals(directThumbprint!, confirmationThumbprint!))
                throw new LicensingException(LicenseErrorCode.LeaseBindingMismatch, "The lease installation key confirmation claims disagree.");
            var installationThumbprint = directThumbprint ?? confirmationThumbprint;
            if (!string.IsNullOrWhiteSpace(installationThumbprint) &&
                !MachineFingerprint.FixedTimeEquals(installationKey.KeyId, installationThumbprint!))
                throw new LicensingException(LicenseErrorCode.LeaseBindingMismatch, "The lease is bound to a different installation key.");

            var productCode = payload.Value<string>("product_code");
            if (!string.IsNullOrWhiteSpace(productCode) && !string.Equals(productCode, _options.ProductCode, StringComparison.Ordinal))
                throw new LicensingException(LicenseErrorCode.LeaseBindingMismatch, "The lease product_code does not match the configured product.");

            var claims = new ReadOnlyDictionary<string, JToken>(
                payload.Properties().ToDictionary(property => property.Name, property => property.Value.DeepClone(), StringComparer.Ordinal));
            return new LicenseLease(
                compactJws,
                kid!,
                parsedLicenseId.ToString("D"),
                bindingVersion,
                deviceId!,
                installationThumbprint,
                issuedAt,
                notBefore,
                expiresAt,
                claims);
        }

        private void ValidateIssuerAndAudience(JObject payload)
        {
            if (!string.Equals(payload.Value<string>("iss"), _options.ExpectedIssuer, StringComparison.Ordinal))
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The lease issuer is not trusted.");

            var expectedAudience = _options.ExpectedAudience!;
            var audience = payload["aud"];
            var matches = audience != null && (
                (audience.Type == JTokenType.String && string.Equals(audience.Value<string>(), expectedAudience, StringComparison.Ordinal)) ||
                (audience.Type == JTokenType.Array && audience.Values<string>().Any(item => string.Equals(item, expectedAudience, StringComparison.Ordinal))));
            if (!matches)
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The lease audience is not trusted.");
        }

        private static DateTimeOffset RequiredDate(JObject payload, string name)
        {
            var value = payload[name];
            if (value == null)
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "The lease is missing required claim '" + name + "'.");
            return DateTimeClaims.FromUnixSeconds(value, name);
        }
    }
}
