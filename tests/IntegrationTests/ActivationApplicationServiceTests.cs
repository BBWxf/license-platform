using System.Security.Cryptography;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Contracts.Licenses;
using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Domain.Products;
using LicensePlatform.Infrastructure.ApplicationServices;
using LicensePlatform.Infrastructure.Cryptography;
using LicensePlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LicensePlatform.IntegrationTests;

// InMemory exercises application behavior, not PostgreSQL constraints or transaction isolation.
public sealed class ActivationApplicationServiceTests
{
    [Fact]
    public async Task Unbind_AllowsNewDeviceWithoutRestartingEntitlement_AndRejectsOldDevice()
    {
        using var fixture = new Fixture();
        var original = await fixture.ActivateAsync();
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(1);
        var staleChallenge = await fixture.Service.CreateRefreshChallengeAsync(new(
            original.LicenseKeyId, original.BindingVersion, fixture.Fingerprint, fixture.PublicKey));
        fixture.License.UnbindDevice(fixture.HashFingerprint(), fixture.Clock.UtcNow);
        await fixture.Db.SaveChangesAsync();

        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var newFingerprint = new DeviceFingerprintDto("new-machine", "new-smbios", "new-volume");
        var challenge = await fixture.Service.CreateActivationChallengeAsync(new(
            fixture.Product.Code, fixture.PlaintextKey, newFingerprint, "New installation", newKey.ExportSubjectPublicKeyInfoPem()));
        var rebound = await fixture.Service.CompleteChallengeAsync(Fixture.Completion(challenge, newFingerprint, newKey));

        Assert.Equal(original.ActivatedAtUtc, rebound.ActivatedAtUtc);
        Assert.Equal(original.EntitlementExpiresAtUtc, rebound.EntitlementExpiresAtUtc);
        Assert.Equal(original.BindingVersion + 1, rebound.BindingVersion);
        Assert.Single(fixture.License.DeviceBindings, binding => binding.IsActive);
        Assert.Equal(2, fixture.License.DeviceBindings.Count);
        var oldValidation = await fixture.Service.ValidateAsync(new(original.LicenseKeyId, original.BindingVersion, fixture.Fingerprint));
        Assert.False(oldValidation.IsValid);
        Assert.Equal("binding_version_mismatch", oldValidation.FailureCode);
        Assert.True((await fixture.Service.ValidateAsync(new(rebound.LicenseKeyId, rebound.BindingVersion, newFingerprint))).IsValid);
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.CreateRefreshChallengeAsync(new(
            original.LicenseKeyId, original.BindingVersion, fixture.Fingerprint, fixture.PublicKey)));
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.CompleteChallengeAsync(
            Fixture.Completion(staleChallenge, fixture.Fingerprint, fixture.Key)));
    }

    [Fact]
    public async Task ChallengeCreation_DoesNotStartValidity_CompletionStartsItOnce()
    {
        using var fixture = new Fixture();
        var challenge = await fixture.CreateChallengeAsync();
        Assert.Null(fixture.License.ActivatedAtUtc);
        Assert.Null(fixture.License.ExpiresAtUtc);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(2);

        var result = await fixture.Service.CompleteChallengeAsync(Fixture.Completion(challenge, fixture.Fingerprint, fixture.Key));

        Assert.Equal(fixture.Clock.UtcNow, result.ActivatedAtUtc);
        Assert.Equal(fixture.Clock.UtcNow.AddDays(1), result.EntitlementExpiresAtUtc);
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.CompleteChallengeAsync(
            Fixture.Completion(challenge, fixture.Fingerprint, fixture.Key)));
    }

    [Fact]
    public async Task LostResponse_NewSameDeviceChallengeReissuesWithoutExtendingEntitlement()
    {
        using var fixture = new Fixture();
        var first = await fixture.ActivateAsync();
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(1);

        var retry = await fixture.ActivateAsync();

        Assert.Equal(first.ActivatedAtUtc, retry.ActivatedAtUtc);
        Assert.Equal(first.EntitlementExpiresAtUtc, retry.EntitlementExpiresAtUtc);
        Assert.Equal(first.BindingVersion, retry.BindingVersion);
        Assert.Single(fixture.License.DeviceBindings);
        Assert.Equal(fixture.Clock.UtcNow.AddHours(6), retry.Lease.ExpiresAtUtc);
    }

    [Theory]
    [InlineData(5, 5)]
    [InlineData(120, 120)]
    [InlineData(360, 360)]
    [InlineData(43200, 360)]
    public async Task IssuedLease_UsesConfiguredLifetimeWithSixHourCeiling(int configuredMinutes, int expectedMinutes)
    {
        using var fixture = new Fixture(configuredMinutes);
        var result = await fixture.ActivateAsync();
        Assert.Equal(fixture.Clock.UtcNow.AddMinutes(expectedMinutes), result.Lease.ExpiresAtUtc);
    }

    [Fact]
    public async Task PermanentEntitlement_StillRequiresLeaseRenewalWithinSixHours()
    {
        using var fixture = new Fixture(permanent: true);
        var result = await fixture.ActivateAsync();

        Assert.Null(result.EntitlementExpiresAtUtc);
        Assert.Equal(fixture.Clock.UtcNow.AddHours(6), result.Lease.ExpiresAtUtc);
    }

    [Fact]
    public async Task Refresh_NeedsNoPlaintextCard_AndClipsLeaseToRemainingEntitlement()
    {
        using var fixture = new Fixture();
        var activation = await fixture.ActivateAsync();
        fixture.Clock.UtcNow = activation.EntitlementExpiresAtUtc!.Value.AddMinutes(-30);
        var challenge = await fixture.Service.CreateRefreshChallengeAsync(new(
            activation.LicenseKeyId, activation.BindingVersion, fixture.Fingerprint, fixture.PublicKey));

        var refreshed = await fixture.Service.CompleteChallengeAsync(Fixture.Completion(challenge, fixture.Fingerprint, fixture.Key));

        Assert.Equal(activation.EntitlementExpiresAtUtc, refreshed.Lease.ExpiresAtUtc);
        Assert.Equal(activation.ActivatedAtUtc, refreshed.ActivatedAtUtc);
        Assert.Equal(activation.BindingVersion, refreshed.BindingVersion);
    }

    [Fact]
    public async Task ActiveBinding_RejectsOtherFingerprintOrReplacementInstallationKey()
    {
        using var fixture = new Fixture();
        await fixture.ActivateAsync();
        using var differentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.CreateActivationChallengeAsync(new(
            fixture.Product.Code, fixture.PlaintextKey, fixture.Fingerprint, "Replacement key", differentKey.ExportSubjectPublicKeyInfoPem())));
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.CreateActivationChallengeAsync(new(
            fixture.Product.Code, fixture.PlaintextKey, new("other-machine", "other-smbios", "other-volume"), "Other device", fixture.PublicKey)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledOrExpiredLicense_CannotReactivateAfterUnbind(bool disabled)
    {
        using var fixture = new Fixture();
        var result = await fixture.ActivateAsync();
        fixture.License.UnbindDevice(fixture.HashFingerprint(), fixture.Clock.UtcNow);
        if (disabled)
        {
            fixture.License.Disable("Test suspension", fixture.Clock.UtcNow);
        }
        else
        {
            fixture.Clock.UtcNow = result.EntitlementExpiresAtUtc!.Value;
        }
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainException>(() => fixture.CreateChallengeAsync());
        var validation = await fixture.Service.ValidateAsync(new(result.LicenseKeyId, fixture.License.BindingVersion, fixture.Fingerprint));
        Assert.Equal(disabled ? "license_disabled" : "expired", validation.FailureCode);
    }

    [Theory]
    [InlineData("product")]
    [InlineData("license")]
    [InlineData("signing-key")]
    public async Task Completion_RechecksDisabledOrRevokedState_AndDoesNotPersistFailedActivation(string changedResource)
    {
        using var fixture = new Fixture();
        var challenge = await fixture.CreateChallengeAsync();
        if (changedResource == "product")
        {
            fixture.Product.Disable(fixture.Clock.UtcNow);
        }
        else if (changedResource == "license")
        {
            fixture.License.Disable("Test suspension", fixture.Clock.UtcNow);
        }
        else
        {
            (await fixture.Db.ProductSigningKeys.SingleAsync()).Revoke(fixture.Clock.UtcNow);
        }
        await fixture.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.CompleteChallengeAsync(
            Fixture.Completion(challenge, fixture.Fingerprint, fixture.Key)));

        fixture.Db.ChangeTracker.Clear();
        Assert.Null((await fixture.Db.LicenseKeys.SingleAsync()).ActivatedAtUtc);
        Assert.Null((await fixture.Db.ActivationChallenges.SingleAsync()).ConsumedAtUtc);
        Assert.Empty(await fixture.Db.AuditRecords.ToListAsync());
    }

    [Fact]
    public async Task Completion_RejectsExpiredChallengeBadProofAndChangedFingerprint()
    {
        using var fixture = new Fixture();
        var challenge = await fixture.CreateChallengeAsync();
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.CompleteChallengeAsync(
            Fixture.Completion(challenge, fixture.Fingerprint, wrongKey)));
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.CompleteChallengeAsync(
            Fixture.Completion(challenge, fixture.Fingerprint with { SystemVolumeId = "changed" }, fixture.Key)));
        fixture.Clock.UtcNow = challenge.ExpiresAtUtc;
        await Assert.ThrowsAsync<DomainException>(() => fixture.Service.CompleteChallengeAsync(
            Fixture.Completion(challenge, fixture.Fingerprint, fixture.Key)));
        Assert.Null(fixture.License.ActivatedAtUtc);
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture(int leaseMinutes = 43200, bool permanent = false)
        {
            Db = new LicensePlatformDbContext(new DbContextOptionsBuilder<LicensePlatformDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
            var options = Options.Create(new CryptographyOptions
            {
                LicenseKeyHmacKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                LookupHmacKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                DataProtectionKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            });
            var cards = new HmacLicenseKeyCryptography(options);
            Lookup = new HmacLookupHashService(options);
            var protectedData = new AesGcmProtectedDataService(options);
            Product = new Product(Guid.NewGuid(), "TEST_APP", "Application test", "TEST", leaseMinutes, Clock.UtcNow);
            var term = permanent ? LicenseTerm.Permanent() : LicenseTerm.Days(1);
            var batch = new LicenseBatch(Guid.NewGuid(), Product.Id, "Test batch", 1, term, Clock.UtcNow);
            var generated = cards.Generate(Product.LicensePrefix);
            PlaintextKey = generated.PlainText;
            License = new LicenseKey(Guid.NewGuid(), Product.Id, batch.Id, generated.LookupHash, generated.Hint, term, Clock.UtcNow);
            Db.AddRange(Product, batch, License, new Es256ProductSigningKeyFactory(protectedData).Create(Product.Id, Clock.UtcNow));
            Db.SaveChanges();
            Service = new ActivationApplicationService(Db, cards, Lookup, new CryptoRandomOpaqueTokenGenerator(),
                new Es256DeviceProofVerifier(), new Es256LeaseSigningService(protectedData), Clock);
        }

        public LicensePlatformDbContext Db { get; }
        public MutableClock Clock { get; } = new();
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public DeviceFingerprintDto Fingerprint { get; } = new("test-machine", "test-smbios", "test-volume");
        public HmacLookupHashService Lookup { get; }
        public Product Product { get; }
        public LicenseKey License { get; }
        public string PlaintextKey { get; }
        public string PublicKey => Key.ExportSubjectPublicKeyInfoPem();
        public ActivationApplicationService Service { get; }

        public DeviceFingerprintHashes HashFingerprint() => Lookup.ComputeDeviceFingerprintHashes(
            Fingerprint.MachineGuid, Fingerprint.SmbiosUuid, Fingerprint.SystemVolumeId);

        public Task<ActivationChallengeDto> CreateChallengeAsync() => Service.CreateActivationChallengeAsync(new(
            Product.Code, PlaintextKey, Fingerprint, "Test installation", PublicKey));

        public async Task<ActivationResultDto> ActivateAsync() => await Service.CompleteChallengeAsync(
            Completion(await CreateChallengeAsync(), Fingerprint, Key));

        public static CompleteActivationChallengeRequest Completion(ActivationChallengeDto challenge, DeviceFingerprintDto fingerprint, ECDsa key)
        {
            var nonce = Convert.FromBase64String(challenge.Nonce.Replace('-', '+').Replace('_', '/') + "=");
            var proof = key.SignData(nonce, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return new(challenge.ChallengeToken, fingerprint, Convert.ToBase64String(proof).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        }

        public void Dispose()
        {
            Key.Dispose();
            Db.Dispose();
        }
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 18, 0, 0, 0, TimeSpan.Zero);
    }
}
