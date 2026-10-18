using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Licensing.Client
{
    public sealed class InstallationKeyRecord
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("installationId")]
        public string InstallationId { get; set; } = string.Empty;

        [JsonProperty("createdAtUtc")]
        public DateTimeOffset CreatedAtUtc { get; set; }

        [JsonProperty("d")]
        public byte[] D { get; set; } = Array.Empty<byte>();

        [JsonProperty("x")]
        public byte[] X { get; set; } = Array.Empty<byte>();

        [JsonProperty("y")]
        public byte[] Y { get; set; } = Array.Empty<byte>();

        [JsonProperty("keyId")]
        public string KeyId { get; set; } = string.Empty;
    }

    public sealed class LicenseStateRecord
    {
        [JsonProperty("version")]
        public int Version { get; set; } = 1;

        [JsonProperty("lease")]
        public string LeaseToken { get; set; } = string.Empty;

        [JsonProperty("licenseKeyHash")]
        public string LicenseKeyHash { get; set; } = string.Empty;

        [JsonProperty("lastOnlineUtc")]
        public DateTimeOffset LastOnlineUtc { get; set; }

        /// <summary>Highest authenticated server or signed-lease time observed.</summary>
        [JsonProperty("trustedUtc")]
        public DateTimeOffset TrustedUtc { get; set; }

        /// <summary>Highest local wall-clock value observed, used to detect rollback.</summary>
        [JsonProperty("lastObservedUtc")]
        public DateTimeOffset LastObservedUtc { get; set; }

        /// <summary>DPAPI-protected local snapshot used for the Windows two-of-three offline device check.</summary>
        [JsonProperty("machineFingerprint")]
        public MachineFingerprint? MachineFingerprint { get; set; }
    }

    public sealed class MachineFingerprint
    {
        [JsonConstructor]
        public MachineFingerprint(string version, string digest, IDictionary<string, string> components, int requiredMatches = 2)
        {
            Version = version ?? throw new ArgumentNullException(nameof(version));
            Digest = digest ?? throw new ArgumentNullException(nameof(digest));
            Components = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(components ?? throw new ArgumentNullException(nameof(components)), StringComparer.Ordinal));
            RequiredMatches = requiredMatches;
        }

        [JsonProperty("version")]
        public string Version { get; }

        [JsonProperty("digest")]
        public string Digest { get; }

        [JsonProperty("components")]
        public IReadOnlyDictionary<string, string> Components { get; }

        [JsonProperty("requiredMatches")]
        public int RequiredMatches { get; }

        public bool Matches(MachineFingerprint other)
        {
            if (other == null || !string.Equals(Version, other.Version, StringComparison.Ordinal))
                return false;

            var required = Math.Max(RequiredMatches, other.RequiredMatches);
            var matches = Components.Count(pair =>
                other.Components.TryGetValue(pair.Key, out var value) &&
                FixedTimeEquals(pair.Value, value));
            return matches >= required;
        }

        internal static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            var difference = 0;
            for (var i = 0; i < left.Length; i++)
                difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }

    public sealed class ServerJwk
    {
        [JsonProperty("kty")]
        public string Kty { get; set; } = "EC";

        [JsonProperty("crv")]
        public string Crv { get; set; } = "P-256";

        [JsonProperty("x")]
        public string X { get; set; } = string.Empty;

        [JsonProperty("y")]
        public string Y { get; set; } = string.Empty;

        [JsonProperty("kid")]
        public string Kid { get; set; } = string.Empty;

        [JsonProperty("use")]
        public string? Use { get; set; }

        [JsonProperty("alg")]
        public string? Alg { get; set; }

        public static ServerJwk FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("A JWK JSON value is required.", nameof(json));
            var key = JsonConvert.DeserializeObject<ServerJwk>(json);
            if (key == null)
                throw new FormatException("The JWK JSON is invalid.");
            return key;
        }

        public static IReadOnlyList<ServerJwk> ParseMany(string jwkOrJwksJson)
        {
            var token = JToken.Parse(jwkOrJwksJson);
            if (token.Type == JTokenType.Object && token["keys"] is JArray keys)
                return keys.Select(item => item.ToObject<ServerJwk>() ?? throw new FormatException("JWKS contains an invalid key.")).ToArray();
            var single = token.ToObject<ServerJwk>();
            if (single == null)
                throw new FormatException("The JWK JSON is invalid.");
            return new[] { single };
        }
    }

    public sealed class LicenseLease
    {
        internal LicenseLease(
            string token,
            string keyId,
            string licenseId,
            long bindingVersion,
            string deviceId,
            string? installationKeyThumbprint,
            DateTimeOffset issuedAtUtc,
            DateTimeOffset notBeforeUtc,
            DateTimeOffset expiresAtUtc,
            IReadOnlyDictionary<string, JToken> claims)
        {
            Token = token;
            KeyId = keyId;
            LicenseId = licenseId;
            BindingVersion = bindingVersion;
            DeviceId = deviceId;
            InstallationKeyThumbprint = installationKeyThumbprint;
            IssuedAtUtc = issuedAtUtc;
            NotBeforeUtc = notBeforeUtc;
            ExpiresAtUtc = expiresAtUtc;
            Claims = claims;
        }

        public string Token { get; }
        public string KeyId { get; }
        public string LicenseId { get; }
        public long BindingVersion { get; }
        public string DeviceId { get; }
        public string? InstallationKeyThumbprint { get; }
        public DateTimeOffset IssuedAtUtc { get; }
        public DateTimeOffset NotBeforeUtc { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public IReadOnlyDictionary<string, JToken> Claims { get; }

        public bool HasFeature(string name)
        {
            if (!Claims.TryGetValue("features", out var features) || features.Type != JTokenType.Object)
                return false;
            var value = features[name];
            if (value == null)
                return false;
            if (value.Type == JTokenType.Boolean)
                return value.Value<bool>();
            return value.Type != JTokenType.Null;
        }

        public T? GetClaim<T>(string name)
        {
            return Claims.TryGetValue(name, out var value) ? value.ToObject<T>() : default;
        }
    }

    public sealed class LicenseClientOptions
    {
        public Uri? ServiceBaseUri { get; set; }
        public string ProductCode { get; set; } = string.Empty;
        public string DeviceHint { get; set; } = "Licensed device";
        public string ActivationChallengePath { get; set; } = "/api/v1/client/challenges/activation";
        public string RefreshChallengePath { get; set; } = "/api/v1/client/challenges/refresh";
        public string CompleteChallengePath { get; set; } = "/api/v1/client/challenges/complete";
        public string ValidationPath { get; set; } = "/api/v1/client/validations";
        public string ExpectedIssuer { get; set; } = "license-platform";
        public string? ExpectedAudience { get; set; }
        public bool AllowInsecureHttp { get; set; }
        public TimeSpan OfflineLeaseLimit { get; set; } = TimeSpan.FromHours(6);
        public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromHours(2);
        public TimeSpan ClockSkew { get; set; } = TimeSpan.FromMinutes(2);
        public TimeSpan ClockRollbackTolerance { get; set; } = TimeSpan.FromMinutes(5);
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
        public int MaxResponseBytes { get; set; } = 256 * 1024;
        public IList<ServerJwk> ServerVerificationKeys { get; } = new List<ServerJwk>();
        public Action<LicenseLogEntry>? Logger { get; set; }

        internal void Validate()
        {
            if (ServiceBaseUri == null
                || !ServiceBaseUri.IsAbsoluteUri
                || (ServiceBaseUri.Scheme != Uri.UriSchemeHttps && ServiceBaseUri.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(ServiceBaseUri.UserInfo)
                || !string.IsNullOrEmpty(ServiceBaseUri.Query)
                || !string.IsNullOrEmpty(ServiceBaseUri.Fragment)
                || ServiceBaseUri.AbsolutePath != "/")
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "ServiceBaseUri must be an absolute HTTP(S) origin without credentials, a path, query, or fragment.");
            if (string.IsNullOrWhiteSpace(ProductCode))
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "ProductCode is required.");
            ProductCode = ProductCode.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(DeviceHint) || DeviceHint.Trim().Length > 128)
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "DeviceHint is required and cannot exceed 128 characters.");
            DeviceHint = DeviceHint.Trim();
            if (string.IsNullOrWhiteSpace(ExpectedIssuer))
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "ExpectedIssuer is required.");
            ExpectedIssuer = ExpectedIssuer.Trim();
            if (string.IsNullOrWhiteSpace(ExpectedAudience))
                ExpectedAudience = ProductCode;
            if (OfflineLeaseLimit <= TimeSpan.Zero || OfflineLeaseLimit > TimeSpan.FromHours(6))
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "OfflineLeaseLimit must be greater than zero and no more than six hours.");
            if (RefreshInterval <= TimeSpan.Zero || RefreshInterval > OfflineLeaseLimit)
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "RefreshInterval must be greater than zero and no longer than OfflineLeaseLimit.");
            if (ClockSkew < TimeSpan.Zero || ClockRollbackTolerance < TimeSpan.Zero)
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "Clock tolerances cannot be negative.");
            if (RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(2))
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "RequestTimeout must be between one second and two minutes.");
            if (MaxResponseBytes < 4096 || MaxResponseBytes > 4 * 1024 * 1024)
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "MaxResponseBytes must be between 4 KiB and 4 MiB.");
            if (ServerVerificationKeys.Count == 0)
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, "At least one ES256 server verification key is required.");
            ValidatePath(ActivationChallengePath, nameof(ActivationChallengePath));
            ValidatePath(RefreshChallengePath, nameof(RefreshChallengePath));
            ValidatePath(CompleteChallengePath, nameof(CompleteChallengePath));
            ValidatePath(ValidationPath, nameof(ValidationPath));
        }

        private static void ValidatePath(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("/", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal) || value.IndexOf('#') >= 0)
                throw new LicensingException(LicenseErrorCode.InvalidConfiguration, name + " must be a same-origin absolute path beginning with one '/'.");
        }
    }

    public sealed class LicenseLogEntry
    {
        public LicenseLogEntry(DateTimeOffset timestampUtc, string eventName, LicenseErrorCode code, string message)
        {
            TimestampUtc = timestampUtc;
            EventName = eventName;
            Code = code;
            Message = message;
        }

        public DateTimeOffset TimestampUtc { get; }
        public string EventName { get; }
        public LicenseErrorCode Code { get; }
        public string Message { get; }
    }

    internal static class DateTimeClaims
    {
        internal static DateTimeOffset FromUnixSeconds(JToken token, string claim)
        {
            if (token == null || (token.Type != JTokenType.Integer && token.Type != JTokenType.Float))
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "Lease claim '" + claim + "' must be a NumericDate.");
            try
            {
                var seconds = token.Value<long>();
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (Exception ex) when (!(ex is LicensingException))
            {
                throw new LicensingException(LicenseErrorCode.LeaseMalformed, "Lease claim '" + claim + "' is outside the supported range.", ex);
            }
        }

        internal static string ToInvariantUnixSeconds(DateTimeOffset value) =>
            value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    }
}
