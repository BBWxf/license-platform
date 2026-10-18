using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Licensing.Client
{
    internal sealed class LicenseApiClient
    {
        private const string InstallationHeader = "X-Licensing-Installation";
        private const string TimestampHeader = "X-Licensing-Timestamp";
        private const string NonceHeader = "X-Licensing-Nonce";
        private const string ContentHashHeader = "X-Licensing-Content-SHA256";
        private const string KeyIdHeader = "X-Licensing-Key-Id";
        private const string AlgorithmHeader = "X-Licensing-Algorithm";
        private const string SignatureHeader = "X-Licensing-Signature";

        private readonly HttpClient _httpClient;
        private readonly LicenseClientOptions _options;
        private readonly ILicenseClock _clock;
        private readonly JsonSerializerSettings _jsonSettings;
        private int _insecureWarningLogged;

        internal LicenseApiClient(HttpClient httpClient, LicenseClientOptions options, ILicenseClock clock)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _options = options;
            _clock = clock;
            _jsonSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore,
                DateParseHandling = DateParseHandling.DateTimeOffset,
                TypeNameHandling = TypeNameHandling.None,
                MetadataPropertyHandling = MetadataPropertyHandling.Ignore
            };
        }

        internal async Task<ApiChallengeResult> CreateActivationChallengeAsync(
            string licenseKey,
            InstallationKeyRecord installation,
            MachineFingerprint fingerprint,
            CancellationToken cancellationToken)
        {
            var request = new CreateActivationChallengeRequest
            {
                ProductCode = _options.ProductCode,
                LicenseKey = licenseKey,
                DeviceFingerprint = ToRequestFingerprint(fingerprint),
                DeviceHint = _options.DeviceHint,
                DevicePublicKey = CryptoUtilities.ToSubjectPublicKeyInfoPem(installation)
            };
            var response = await SendAsync(_options.ActivationChallengePath, request, installation, cancellationToken).ConfigureAwait(false);
            return ParseChallenge(response, expectedPurpose: 1);
        }

        internal async Task<ApiChallengeResult> CreateRefreshChallengeAsync(
            Guid licenseKeyId,
            long bindingVersion,
            InstallationKeyRecord installation,
            MachineFingerprint fingerprint,
            CancellationToken cancellationToken)
        {
            var request = new CreateLeaseRefreshChallengeRequest
            {
                LicenseKeyId = licenseKeyId,
                BindingVersion = bindingVersion,
                DeviceFingerprint = ToRequestFingerprint(fingerprint),
                DevicePublicKey = CryptoUtilities.ToSubjectPublicKeyInfoPem(installation)
            };
            var response = await SendAsync(_options.RefreshChallengePath, request, installation, cancellationToken).ConfigureAwait(false);
            return ParseChallenge(response, expectedPurpose: 2);
        }

        internal async Task<ApiLeaseResult> CompleteChallengeAsync(
            ApiChallengeResult challengeResult,
            InstallationKeyRecord installation,
            MachineFingerprint fingerprint,
            CancellationToken cancellationToken)
        {
            if (challengeResult == null) throw new ArgumentNullException(nameof(challengeResult));
            byte[] nonce;
            try
            {
                nonce = Base64Url.Decode(challengeResult.Challenge.Nonce);
            }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.InvalidResponse, "The challenge nonce is not valid Base64Url.", ex);
            }
            if (nonce.Length != 32)
                throw new LicensingException(LicenseErrorCode.InvalidResponse, "The challenge nonce must contain exactly 32 bytes.");

            byte[]? signature = null;
            try
            {
                // The server contract verifies ES256 directly over the decoded 32-byte nonce.
                signature = CryptoUtilities.SignJws(installation, nonce);
                var request = new CompleteActivationChallengeRequest
                {
                    ChallengeToken = challengeResult.Challenge.ChallengeToken,
                    DeviceFingerprint = ToRequestFingerprint(fingerprint),
                    ProofSignature = Base64Url.Encode(signature)
                };
                var response = await SendAsync(_options.CompleteChallengePath, request, installation, cancellationToken).ConfigureAwait(false);
                return ParseActivationResult(response);
            }
            finally
            {
                Array.Clear(nonce, 0, nonce.Length);
                if (signature != null) Array.Clear(signature, 0, signature.Length);
            }
        }

        internal async Task<ApiValidationResult> ValidateAsync(
            Guid licenseKeyId,
            long bindingVersion,
            InstallationKeyRecord installation,
            MachineFingerprint fingerprint,
            CancellationToken cancellationToken)
        {
            var request = new ValidateLicenseRequest
            {
                LicenseKeyId = licenseKeyId,
                BindingVersion = bindingVersion,
                DeviceFingerprint = ToRequestFingerprint(fingerprint)
            };
            var response = await SendAsync(_options.ValidationPath, request, installation, cancellationToken).ConfigureAwait(false);
            try
            {
                var validation = JsonConvert.DeserializeObject<LicenseValidationResponse>(response.Body, _jsonSettings)
                    ?? throw new FormatException("The validation response is empty.");
                if (validation.CurrentBindingVersion < 0)
                    throw new FormatException("The current binding version is invalid.");
                return new ApiValidationResult(validation, response.ServerTimeUtc);
            }
            catch (LicensingException) { throw; }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.InvalidResponse, "The license validation response is malformed.", ex);
            }
        }

        private ApiChallengeResult ParseChallenge(ApiHttpResponse response, int expectedPurpose)
        {
            try
            {
                var challenge = JsonConvert.DeserializeObject<ActivationChallengeResponse>(response.Body, _jsonSettings)
                    ?? throw new FormatException("The challenge response is empty.");
                if (string.IsNullOrWhiteSpace(challenge.ChallengeToken) || string.IsNullOrWhiteSpace(challenge.Nonce) ||
                    challenge.ExpiresAtUtc == default(DateTimeOffset) || challenge.Purpose != expectedPurpose)
                    throw new FormatException("Required challenge fields are missing or the purpose is incorrect.");
                var nonce = Base64Url.Decode(challenge.Nonce);
                try
                {
                    if (nonce.Length != 32)
                        throw new FormatException("The challenge nonce must contain 32 bytes.");
                }
                finally
                {
                    Array.Clear(nonce, 0, nonce.Length);
                }
                return new ApiChallengeResult(challenge, response.ServerTimeUtc);
            }
            catch (LicensingException) { throw; }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.InvalidResponse, "The activation challenge response is malformed.", ex);
            }
        }

        private ApiLeaseResult ParseActivationResult(ApiHttpResponse response)
        {
            try
            {
                var result = JsonConvert.DeserializeObject<ActivationResultResponse>(response.Body, _jsonSettings)
                    ?? throw new FormatException("The activation result is empty.");
                if (result.LicenseKeyId == Guid.Empty || result.BindingVersion < 1 || result.Lease == null ||
                    string.IsNullOrWhiteSpace(result.Lease.CompactToken) ||
                    !string.Equals(result.Lease.Algorithm, "ES256", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(result.Lease.KeyId))
                    throw new FormatException("Required activation result fields are missing.");
                return new ApiLeaseResult(result.Lease.CompactToken, result.LicenseKeyId, result.BindingVersion, response.ServerTimeUtc);
            }
            catch (LicensingException) { throw; }
            catch (Exception ex)
            {
                throw new LicensingException(LicenseErrorCode.InvalidResponse, "The activation completion response is malformed.", ex);
            }
        }

        private static DeviceFingerprintRequest ToRequestFingerprint(MachineFingerprint fingerprint)
        {
            if (fingerprint == null)
                throw new LicensingException(LicenseErrorCode.FingerprintUnavailable, "A device fingerprint is required.");
            if (!fingerprint.Components.TryGetValue("machineGuid", out var machineGuid) || string.IsNullOrWhiteSpace(machineGuid) ||
                !fingerprint.Components.TryGetValue("smbiosUuid", out var smbiosUuid) || string.IsNullOrWhiteSpace(smbiosUuid) ||
                !fingerprint.Components.TryGetValue("systemVolume", out var systemVolumeId) || string.IsNullOrWhiteSpace(systemVolumeId))
                throw new LicensingException(LicenseErrorCode.FingerprintUnavailable, "The server contract requires MachineGuid, SMBIOS UUID, and system-volume fingerprint components.");
            return new DeviceFingerprintRequest
            {
                MachineGuid = machineGuid,
                SmbiosUuid = smbiosUuid,
                SystemVolumeId = systemVolumeId
            };
        }

        private async Task<ApiHttpResponse> SendAsync<T>(
            string path,
            T payload,
            InstallationKeyRecord installation,
            CancellationToken cancellationToken)
        {
            var body = JsonConvert.SerializeObject(payload, Formatting.None, _jsonSettings);
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var contentHash = Base64Url.Encode(CryptoUtilities.Sha256(bodyBytes));
            var timestamp = DateTimeClaims.ToInvariantUnixSeconds(_clock.UtcNow);
            var nonceBytes = new byte[16];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(nonceBytes);
            var requestNonce = Base64Url.Encode(nonceBytes);
            Array.Clear(nonceBytes, 0, nonceBytes.Length);

            var uri = ResolveUri(path);
            var canonical = string.Join("\n", new[]
            {
                LicensingProtocol.Version,
                "POST",
                uri.PathAndQuery,
                timestamp,
                requestNonce,
                contentHash
            });
            var requestSignature = Base64Url.Encode(CryptoUtilities.SignJws(installation, Encoding.UTF8.GetBytes(canonical)));

            using (var request = new HttpRequestMessage(HttpMethod.Post, uri))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation(InstallationHeader, installation.InstallationId);
                request.Headers.TryAddWithoutValidation(TimestampHeader, timestamp);
                request.Headers.TryAddWithoutValidation(NonceHeader, requestNonce);
                request.Headers.TryAddWithoutValidation(ContentHashHeader, contentHash);
                request.Headers.TryAddWithoutValidation(KeyIdHeader, installation.KeyId);
                request.Headers.TryAddWithoutValidation(AlgorithmHeader, "ES256");
                request.Headers.TryAddWithoutValidation(SignatureHeader, requestSignature);
                request.Content = new ByteArrayContent(bodyBytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

                try
                {
                    using (var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                    {
                        EnsureResponseOrigin(uri, response.RequestMessage?.RequestUri);
                        var responseBody = await ReadResponseBodyAsync(response.Content, cancellationToken).ConfigureAwait(false);
                        if (!response.IsSuccessStatusCode)
                            throw CreateHttpError(response.StatusCode, responseBody);
                        return new ApiHttpResponse(responseBody, response.Headers.Date);
                    }
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new LicensingException(LicenseErrorCode.NetworkUnavailable, "The licensing request timed out.", ex, isTransient: true);
                }
                catch (OperationCanceledException ex)
                {
                    throw new LicensingException(LicenseErrorCode.OperationCancelled, "The licensing operation was cancelled.", ex);
                }
                catch (HttpRequestException ex)
                {
                    throw new LicensingException(LicenseErrorCode.NetworkUnavailable, "The licensing service could not be reached.", ex, isTransient: true);
                }
                finally
                {
                    Array.Clear(bodyBytes, 0, bodyBytes.Length);
                }
            }
        }

        private static void EnsureResponseOrigin(Uri requested, Uri? final)
        {
            if (final != null && (!string.Equals(requested.Scheme, final.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(requested.Host, final.Host, StringComparison.OrdinalIgnoreCase) || requested.Port != final.Port))
                throw new LicensingException(LicenseErrorCode.InvalidResponse, "The licensing response came from a different origin. Custom handlers must not follow redirects.");
        }

        private async Task<string> ReadResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
        {
            if (content.Headers.ContentLength.HasValue && content.Headers.ContentLength.Value > _options.MaxResponseBytes)
                throw new LicensingException(LicenseErrorCode.InvalidResponse, "The licensing response exceeds the configured size limit.");

            using (var input = await content.ReadAsStreamAsync().ConfigureAwait(false))
            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                while (true)
                {
                    var read = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;
                    if (output.Length + read > _options.MaxResponseBytes)
                        throw new LicensingException(LicenseErrorCode.InvalidResponse, "The licensing response exceeds the configured size limit.");
                    output.Write(buffer, 0, read);
                }
                try
                {
                    return new UTF8Encoding(false, true).GetString(output.ToArray());
                }
                catch (DecoderFallbackException ex)
                {
                    throw new LicensingException(LicenseErrorCode.InvalidResponse, "The licensing response is not valid UTF-8.", ex);
                }
            }
        }

        private Uri ResolveUri(string path)
        {
            var baseUri = _options.ServiceBaseUri
                ?? throw new LicensingException(
                    LicenseErrorCode.InvalidConfiguration,
                    "ServiceBaseUri must be an absolute HTTP(S) origin.");

            var isHttps = string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
            var isHttp = string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
            if (!isHttps && !(isHttp && baseUri.IsLoopback) && !(isHttp && _options.AllowInsecureHttp))
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "The licensing service must use HTTPS. Set AllowInsecureHttp=true explicitly to permit non-loopback HTTP.");
            if (isHttp && !baseUri.IsLoopback && _options.AllowInsecureHttp && Interlocked.Exchange(ref _insecureWarningLogged, 1) == 0)
            {
                try
                {
                    _options.Logger?.Invoke(new LicenseLogEntry(
                        _clock.UtcNow,
                        "insecure-http-enabled",
                        LicenseErrorCode.InvalidConfiguration,
                        "HIGH RISK: licensing credentials, challenges, and leases are being sent over public HTTP without transport encryption."));
                }
                catch { }
            }

            var resolved = new Uri(baseUri, path);
            if (!string.Equals(resolved.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(resolved.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) || resolved.Port != baseUri.Port)
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "Licensing endpoint paths must remain on the configured service origin.");
            return resolved;
        }

        private static LicensingException CreateHttpError(HttpStatusCode statusCode, string body)
        {
            string? serverCode = null;
            string? serverMessage = null;
            try
            {
                var json = JObject.Parse(body);
                var error = json["error"] as JObject;
                serverCode = error?.Value<string>("code") ?? json.Value<string>("code");
                serverMessage = error?.Value<string>("message") ?? json.Value<string>("message") ?? json.Value<string>("title");
            }
            catch { }

            var numeric = (int)statusCode;
            if (numeric >= 500 && numeric <= 599)
                return new LicensingException(
                    LicenseErrorCode.ServerError,
                    serverMessage ?? "The licensing service returned a server error.",
                    isTransient: true,
                    httpStatusCode: statusCode,
                    serverCode: serverCode);

            if (numeric >= 400 && numeric <= 499)
                return new LicensingException(
                    LicenseErrorCode.BusinessRejected,
                    serverMessage ?? ("The licensing request was rejected with HTTP " + numeric.ToString(CultureInfo.InvariantCulture) + "."),
                    httpStatusCode: statusCode,
                    serverCode: serverCode);

            return new LicensingException(
                LicenseErrorCode.InvalidResponse,
                "The licensing service returned unexpected HTTP " + numeric.ToString(CultureInfo.InvariantCulture) + ".",
                httpStatusCode: statusCode,
                serverCode: serverCode);
        }

        private sealed class ApiHttpResponse
        {
            internal ApiHttpResponse(string body, DateTimeOffset? serverTimeUtc)
            {
                Body = body;
                ServerTimeUtc = serverTimeUtc;
            }

            internal string Body { get; }
            internal DateTimeOffset? ServerTimeUtc { get; }
        }
    }
}
