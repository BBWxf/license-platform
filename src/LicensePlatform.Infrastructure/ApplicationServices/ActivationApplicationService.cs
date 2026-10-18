using System.Security.Cryptography;
using System.Text.Json;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Contracts.Leases;
using LicensePlatform.Contracts.Licenses;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Domain.Products;
using LicensePlatform.Infrastructure.Cryptography;
using LicensePlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Infrastructure.ApplicationServices;

public sealed class ActivationApplicationService : IActivationApplicationService
{
    private const int ChallengeLifetimeMinutes = 5;
    private const int MaximumOfflineLeaseMinutes = 6 * 60;
    private const string LeaseIssuer = "license-platform";

    private readonly LicensePlatformDbContext _dbContext;
    private readonly ILicenseKeyCryptography _licenseKeyCryptography;
    private readonly ILookupHashService _lookupHashes;
    private readonly IOpaqueTokenGenerator _tokenGenerator;
    private readonly IDeviceProofVerifier _deviceProofVerifier;
    private readonly ILeaseSigningService _leaseSigner;
    private readonly IClock _clock;

    public ActivationApplicationService(
        LicensePlatformDbContext dbContext,
        ILicenseKeyCryptography licenseKeyCryptography,
        ILookupHashService lookupHashes,
        IOpaqueTokenGenerator tokenGenerator,
        IDeviceProofVerifier deviceProofVerifier,
        ILeaseSigningService leaseSigner,
        IClock clock)
    {
        _dbContext = dbContext;
        _licenseKeyCryptography = licenseKeyCryptography;
        _lookupHashes = lookupHashes;
        _tokenGenerator = tokenGenerator;
        _deviceProofVerifier = deviceProofVerifier;
        _leaseSigner = leaseSigner;
        _clock = clock;
    }

    public async Task<ActivationChallengeDto> CreateActivationChallengeAsync(
        CreateActivationChallengeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nowUtc = _clock.UtcNow;
        var productCode = Required(request.ProductCode, nameof(request.ProductCode)).ToUpperInvariant();
        var normalizedLicenseKey = _licenseKeyCryptography.Normalize(request.LicenseKey);
        var prefix = _licenseKeyCryptography.GetPrefix(normalizedLicenseKey);
        var lookupHash = _licenseKeyCryptography.ComputeLookupHash(normalizedLicenseKey);

        var product = await _dbContext.Products
            .SingleOrDefaultAsync(candidate => candidate.Code == productCode, cancellationToken);
        if (product is null
            || !product.IsEnabled
            || !string.Equals(product.LicensePrefix, prefix, StringComparison.Ordinal))
        {
            throw new DomainException("The product or license key is invalid.");
        }

        var license = await _dbContext.LicenseKeys
            .Include(candidate => candidate.DeviceBindings)
            .SingleOrDefaultAsync(
                candidate => candidate.ProductId == product.Id
                    && candidate.KeyHash.SequenceEqual(lookupHash),
                cancellationToken);
        if (license is null || license.Status != LicenseKeyStatus.Enabled)
        {
            throw new DomainException("The product or license key is invalid.");
        }

        var fingerprint = HashFingerprint(request.DeviceFingerprint);
        if (license.ActivatedAtUtc.HasValue)
        {
            license.EnsureCanIssueLease(nowUtc);
            var activeBinding = license.DeviceBindings.SingleOrDefault(binding => binding.IsActive);
            if (activeBinding is not null
                && (!activeBinding.Matches(fingerprint)
                    || !activeBinding.MatchesPublicKey(request.DevicePublicKey)))
            {
                throw new DomainException("The license is already activated on another device.");
            }
        }

        return await CreateChallengeAsync(
            product,
            license,
            fingerprint,
            request.DeviceHint,
            request.DevicePublicKey,
            ActivationChallengePurpose.Activate,
            nowUtc,
            cancellationToken);
    }

    public async Task<ActivationChallengeDto> CreateRefreshChallengeAsync(
        CreateLeaseRefreshChallengeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LicenseKeyId == Guid.Empty || request.BindingVersion < 1)
        {
            throw new DomainException("License id and binding version are required.");
        }

        var nowUtc = _clock.UtcNow;
        var license = await _dbContext.LicenseKeys
            .Include(candidate => candidate.Product)
            .Include(candidate => candidate.DeviceBindings)
            .SingleOrDefaultAsync(candidate => candidate.Id == request.LicenseKeyId, cancellationToken)
            ?? throw new DomainException("The license cannot be refreshed.");
        var product = license.Product ?? throw new DomainException("The license product is unavailable.");

        if (!product.IsEnabled || request.BindingVersion != license.BindingVersion)
        {
            throw new DomainException("The license cannot be refreshed.");
        }

        license.EnsureCanIssueLease(nowUtc);
        var fingerprint = HashFingerprint(request.DeviceFingerprint);
        var activeBinding = license.DeviceBindings.SingleOrDefault(binding => binding.IsActive);
        if (activeBinding is null
            || !activeBinding.Matches(fingerprint)
            || !activeBinding.MatchesPublicKey(request.DevicePublicKey))
        {
            throw new DomainException("The device does not match the active license binding.");
        }

        return await CreateChallengeAsync(
            product,
            license,
            fingerprint,
            activeBinding.DeviceHint,
            activeBinding.DevicePublicKey,
            ActivationChallengePurpose.RefreshLease,
            nowUtc,
            cancellationToken);
    }

    public async Task<ActivationResultDto> CompleteChallengeAsync(
        CompleteActivationChallengeRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var nowUtc = _clock.UtcNow;
        var challengeHash = _lookupHashes.ComputeChallengeHash(request.ChallengeToken);
        var challenge = await _dbContext.ActivationChallenges
            .Include(candidate => candidate.Product)
            .Include(candidate => candidate.LicenseKey)
                .ThenInclude(license => license!.DeviceBindings)
            .SingleOrDefaultAsync(
                candidate => candidate.ChallengeHash.SequenceEqual(challengeHash),
                cancellationToken)
            ?? throw new DomainException("The activation challenge is invalid.");
        var product = challenge.Product ?? throw new DomainException("The challenge product is unavailable.");
        var license = challenge.LicenseKey ?? throw new DomainException("The challenge license is unavailable.");
        var fingerprint = HashFingerprint(request.DeviceFingerprint);

        if (!product.IsEnabled || !challenge.MatchesFingerprint(fingerprint, requiredMatches: 3))
        {
            throw new DomainException("The activation challenge is invalid.");
        }

        if (!_deviceProofVerifier.VerifyEs256(
                challenge.DevicePublicKey,
                challenge.Nonce,
                request.ProofSignature))
        {
            throw new DomainException("The device proof signature is invalid.");
        }

        var wasAlreadyActivated = license.ActivatedAtUtc.HasValue;
        challenge.Consume(license.BindingVersion, nowUtc);
        DeviceBinding binding;
        if (challenge.Purpose == ActivationChallengePurpose.Activate)
        {
            license.Activate(nowUtc);
            binding = license.BindDevice(
                fingerprint,
                challenge.DeviceHint,
                challenge.DevicePublicKey,
                nowUtc);
        }
        else if (challenge.Purpose == ActivationChallengePurpose.RefreshLease)
        {
            license.EnsureCanIssueLease(nowUtc);
            binding = license.DeviceBindings.SingleOrDefault(candidate => candidate.IsActive)
                ?? throw new DomainException("The license has no active device binding.");
            if (!binding.Matches(fingerprint)
                || !binding.MatchesPublicKey(challenge.DevicePublicKey))
            {
                throw new DomainException("The device does not match the active license binding.");
            }
        }
        else
        {
            throw new DomainException("The challenge purpose is not supported by this flow.");
        }

        var signingKey = await _dbContext.ProductSigningKeys.SingleOrDefaultAsync(
            key => key.ProductId == product.Id && key.Status == SigningKeyStatus.Active,
            cancellationToken)
            ?? throw new DomainException("The product has no active signing key.");
        var signedLease = CreateSignedLease(product, license, binding, signingKey, nowUtc);

        var action = challenge.Purpose == ActivationChallengePurpose.Activate
            ? wasAlreadyActivated ? "lease.reissued" : "license.activated"
            : "lease.refreshed";
        _dbContext.AuditRecords.Add(new AuditRecord(
            Guid.NewGuid(),
            AuditActorType.LicenseClient,
            license.Id.ToString("D"),
            action,
            nameof(LicenseKey),
            license.Id.ToString("D"),
            JsonSerializer.Serialize(new
            {
                productId = product.Id,
                bindingVersion = license.BindingVersion,
                deviceId = _lookupHashes.ComputeDeviceId(binding.GetFingerprint())
            }),
            traceId: null,
            ipAddress: null,
            nowUtc));

        await _dbContext.SaveChangesAsync(cancellationToken);
        return new ActivationResultDto(
            license.Id,
            license.ActivatedAtUtc!.Value,
            license.ExpiresAtUtc,
            license.BindingVersion,
            signedLease);
    }

    public async Task<LicenseValidationDto> ValidateAsync(
        ValidateLicenseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.LicenseKeyId == Guid.Empty || request.BindingVersion < 1)
        {
            return Invalid("invalid_request", currentBindingVersion: 0);
        }

        var license = await _dbContext.LicenseKeys
            .AsNoTracking()
            .Include(candidate => candidate.Product)
            .Include(candidate => candidate.DeviceBindings)
            .SingleOrDefaultAsync(candidate => candidate.Id == request.LicenseKeyId, cancellationToken);
        if (license is null)
        {
            return Invalid("not_found", currentBindingVersion: 0);
        }

        if (license.Product is null || !license.Product.IsEnabled)
        {
            return Invalid("product_disabled", license.BindingVersion, license.ExpiresAtUtc);
        }

        if (license.Status != LicenseKeyStatus.Enabled)
        {
            return Invalid("license_disabled", license.BindingVersion, license.ExpiresAtUtc);
        }

        if (!license.ActivatedAtUtc.HasValue)
        {
            return Invalid("not_activated", license.BindingVersion, license.ExpiresAtUtc);
        }

        if (license.IsExpiredAt(_clock.UtcNow))
        {
            return Invalid("expired", license.BindingVersion, license.ExpiresAtUtc);
        }

        if (request.BindingVersion != license.BindingVersion)
        {
            return Invalid("binding_version_mismatch", license.BindingVersion, license.ExpiresAtUtc);
        }

        var activeBinding = license.DeviceBindings.SingleOrDefault(binding => binding.IsActive);
        if (activeBinding is null)
        {
            return Invalid("not_bound", license.BindingVersion, license.ExpiresAtUtc);
        }

        var fingerprint = HashFingerprint(request.DeviceFingerprint);
        if (!activeBinding.Matches(fingerprint))
        {
            return Invalid("device_mismatch", license.BindingVersion, license.ExpiresAtUtc);
        }

        return new LicenseValidationDto(true, null, license.ExpiresAtUtc, license.BindingVersion);
    }

    private async Task<ActivationChallengeDto> CreateChallengeAsync(
        Product product,
        LicenseKey license,
        DeviceFingerprintHashes fingerprint,
        string deviceHint,
        string devicePublicKey,
        ActivationChallengePurpose purpose,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var token = _tokenGenerator.Generate();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var expiresAtUtc = nowUtc.AddMinutes(ChallengeLifetimeMinutes);
        var challenge = new ActivationChallenge(
            Guid.NewGuid(),
            product.Id,
            license.Id,
            _lookupHashes.ComputeChallengeHash(token),
            fingerprint,
            nonce,
            deviceHint,
            devicePublicKey,
            purpose,
            license.BindingVersion,
            nowUtc,
            expiresAtUtc);
        _dbContext.ActivationChallenges.Add(challenge);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return new ActivationChallengeDto(
            token,
            Base64Url.Encode(nonce),
            purpose == ActivationChallengePurpose.Activate
                ? ActivationChallengePurposeDto.Activate
                : ActivationChallengePurposeDto.RefreshLease,
            expiresAtUtc);
    }

    private SignedLeaseDto CreateSignedLease(
        Product product,
        LicenseKey license,
        DeviceBinding binding,
        ProductSigningKey signingKey,
        DateTimeOffset nowUtc)
    {
        var leaseLifetimeMinutes = Math.Min(product.LeaseLifetimeMinutes, MaximumOfflineLeaseMinutes);
        var leaseExpiresAtUtc = nowUtc.AddMinutes(leaseLifetimeMinutes);
        if (license.ExpiresAtUtc.HasValue && leaseExpiresAtUtc > license.ExpiresAtUtc.Value)
        {
            leaseExpiresAtUtc = license.ExpiresAtUtc.Value;
        }

        var payload = new LeasePayloadDto(
            LeaseIssuer,
            product.Code,
            product.Id,
            product.Code,
            license.Id,
            _lookupHashes.ComputeDeviceId(binding.GetFingerprint()),
            PublicKeyThumbprint.FromPem(binding.DevicePublicKey),
            license.BindingVersion,
            nowUtc,
            nowUtc,
            leaseExpiresAtUtc,
            license.ExpiresAtUtc,
            Guid.NewGuid().ToString("N"));
        return _leaseSigner.Sign(payload, signingKey);
    }

    private DeviceFingerprintHashes HashFingerprint(DeviceFingerprintDto fingerprint)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        return _lookupHashes.ComputeDeviceFingerprintHashes(
            fingerprint.MachineGuid,
            fingerprint.SmbiosUuid,
            fingerprint.SystemVolumeId);
    }

    private static LicenseValidationDto Invalid(
        string failureCode,
        long currentBindingVersion,
        DateTimeOffset? entitlementExpiresAtUtc = null)
    {
        return new LicenseValidationDto(
            false,
            failureCode,
            entitlementExpiresAtUtc,
            currentBindingVersion);
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"{name} is required.");
        }

        return value.Trim();
    }
}
