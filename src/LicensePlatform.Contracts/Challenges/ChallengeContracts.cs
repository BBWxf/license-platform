using LicensePlatform.Contracts.Leases;
using LicensePlatform.Contracts.Licenses;

namespace LicensePlatform.Contracts.Challenges;

public enum ActivationChallengePurposeDto
{
    Activate = 1,
    RefreshLease = 2
}

public sealed record CreateActivationChallengeRequest(
    string ProductCode,
    string LicenseKey,
    DeviceFingerprintDto DeviceFingerprint,
    string DeviceHint,
    string DevicePublicKey);

public sealed record CreateLeaseRefreshChallengeRequest(
    Guid LicenseKeyId,
    long BindingVersion,
    DeviceFingerprintDto DeviceFingerprint,
    string DevicePublicKey);

public sealed record ActivationChallengeDto(
    string ChallengeToken,
    string Nonce,
    ActivationChallengePurposeDto Purpose,
    DateTimeOffset ExpiresAtUtc);

public sealed record CompleteActivationChallengeRequest(
    string ChallengeToken,
    DeviceFingerprintDto DeviceFingerprint,
    string ProofSignature);

public sealed record ActivationResultDto(
    Guid LicenseKeyId,
    DateTimeOffset ActivatedAtUtc,
    DateTimeOffset? EntitlementExpiresAtUtc,
    long BindingVersion,
    SignedLeaseDto Lease);

public sealed record ValidateLicenseRequest(
    Guid LicenseKeyId,
    long BindingVersion,
    DeviceFingerprintDto DeviceFingerprint);

public sealed record LicenseValidationDto(
    bool IsValid,
    string? FailureCode,
    DateTimeOffset? EntitlementExpiresAtUtc,
    long CurrentBindingVersion);

public interface IActivationApplicationService
{
    Task<ActivationChallengeDto> CreateActivationChallengeAsync(
        CreateActivationChallengeRequest request,
        CancellationToken cancellationToken = default);

    Task<ActivationChallengeDto> CreateRefreshChallengeAsync(
        CreateLeaseRefreshChallengeRequest request,
        CancellationToken cancellationToken = default);

    Task<ActivationResultDto> CompleteChallengeAsync(
        CompleteActivationChallengeRequest request,
        CancellationToken cancellationToken = default);

    Task<LicenseValidationDto> ValidateAsync(
        ValidateLicenseRequest request,
        CancellationToken cancellationToken = default);
}
