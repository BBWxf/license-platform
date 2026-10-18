using System;

namespace Licensing.Client
{
    internal static class LicensingProtocol
    {
        internal const string Version = "licensing-v1";
        internal const string SdkVersion = "1.0.0";
    }

    internal sealed class DeviceFingerprintRequest
    {
        public string MachineGuid { get; set; } = string.Empty;
        public string SmbiosUuid { get; set; } = string.Empty;
        public string SystemVolumeId { get; set; } = string.Empty;
    }

    internal sealed class CreateActivationChallengeRequest
    {
        public string ProductCode { get; set; } = string.Empty;
        public string LicenseKey { get; set; } = string.Empty;
        public DeviceFingerprintRequest DeviceFingerprint { get; set; } = new DeviceFingerprintRequest();
        public string DeviceHint { get; set; } = string.Empty;
        public string DevicePublicKey { get; set; } = string.Empty;
    }

    internal sealed class CreateLeaseRefreshChallengeRequest
    {
        public Guid LicenseKeyId { get; set; }
        public long BindingVersion { get; set; }
        public DeviceFingerprintRequest DeviceFingerprint { get; set; } = new DeviceFingerprintRequest();
        public string DevicePublicKey { get; set; } = string.Empty;
    }

    internal sealed class ActivationChallengeResponse
    {
        public string ChallengeToken { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public int Purpose { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    internal sealed class CompleteActivationChallengeRequest
    {
        public string ChallengeToken { get; set; } = string.Empty;
        public DeviceFingerprintRequest DeviceFingerprint { get; set; } = new DeviceFingerprintRequest();
        public string ProofSignature { get; set; } = string.Empty;
    }

    internal sealed class ActivationResultResponse
    {
        public Guid LicenseKeyId { get; set; }
        public DateTimeOffset ActivatedAtUtc { get; set; }
        public DateTimeOffset? EntitlementExpiresAtUtc { get; set; }
        public long BindingVersion { get; set; }
        public SignedLeaseResponse Lease { get; set; } = new SignedLeaseResponse();
    }

    internal sealed class SignedLeaseResponse
    {
        public string CompactToken { get; set; } = string.Empty;
        public string KeyId { get; set; } = string.Empty;
        public string Algorithm { get; set; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    internal sealed class ValidateLicenseRequest
    {
        public Guid LicenseKeyId { get; set; }
        public long BindingVersion { get; set; }
        public DeviceFingerprintRequest DeviceFingerprint { get; set; } = new DeviceFingerprintRequest();
    }

    internal sealed class LicenseValidationResponse
    {
        public bool IsValid { get; set; }
        public string? FailureCode { get; set; }
        public DateTimeOffset? EntitlementExpiresAtUtc { get; set; }
        public long CurrentBindingVersion { get; set; }
    }

    internal sealed class ApiChallengeResult
    {
        internal ApiChallengeResult(ActivationChallengeResponse challenge, DateTimeOffset? serverTimeUtc)
        {
            Challenge = challenge;
            ServerTimeUtc = serverTimeUtc;
        }

        internal ActivationChallengeResponse Challenge { get; }
        internal DateTimeOffset? ServerTimeUtc { get; }
    }

    internal sealed class ApiLeaseResult
    {
        internal ApiLeaseResult(string token, Guid licenseKeyId, long bindingVersion, DateTimeOffset? serverTimeUtc)
        {
            Token = token;
            LicenseKeyId = licenseKeyId;
            BindingVersion = bindingVersion;
            ServerTimeUtc = serverTimeUtc;
        }

        internal string Token { get; }
        internal Guid LicenseKeyId { get; }
        internal long BindingVersion { get; }
        internal DateTimeOffset? ServerTimeUtc { get; }
    }

    internal sealed class ApiValidationResult
    {
        internal ApiValidationResult(LicenseValidationResponse validation, DateTimeOffset? serverTimeUtc)
        {
            Validation = validation;
            ServerTimeUtc = serverTimeUtc;
        }

        internal LicenseValidationResponse Validation { get; }
        internal DateTimeOffset? ServerTimeUtc { get; }
    }
}
