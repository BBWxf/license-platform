using LicensePlatform.Application.Abstractions;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Contracts.Licenses;
using LicensePlatform.Domain.Licensing;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.Security;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class ClientRequestPublicKeyResolverTests
{
    [Theory]
    [InlineData("activation")]
    [InlineData("refresh")]
    public async Task Challenge_creation_uses_the_supplied_device_public_key_without_querying_persistence(string kind)
    {
        using var request = new SignedClientRequest(kind);
        // No provider is configured: an accidental query would fail instead of being hidden by a fake database.
        await using var database = new LicensePlatformDbContext(new DbContextOptionsBuilder<LicensePlatformDbContext>().Options);
        var resolver = new ClientRequestPublicKeyResolver(database, new NoLookupExpected());

        Assert.Equal(request.Resolver.PublicKey, await resolver.ResolveAsync(request.Request, CancellationToken.None));
    }

    [Fact]
    public async Task Missing_lookup_identifiers_and_unsupported_request_types_fail_closed_without_a_query()
    {
        await using var database = new LicensePlatformDbContext(new DbContextOptionsBuilder<LicensePlatformDbContext>().Options);
        var resolver = new ClientRequestPublicKeyResolver(database, new NoLookupExpected());
        var fingerprint = new DeviceFingerprintDto("test-machine", "test-board", "test-volume");

        Assert.Null(await resolver.ResolveAsync(new CompleteActivationChallengeRequest(" ", fingerprint, "proof"), CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(new ValidateLicenseRequest(Guid.Empty, 1, fingerprint), CancellationToken.None));
        Assert.Null(await resolver.ResolveAsync(new object(), CancellationToken.None));
    }

    private sealed class NoLookupExpected : ILookupHashService
    {
        public DeviceFingerprintHashes ComputeDeviceFingerprintHashes(string machineGuid, string smbiosUuid, string systemVolumeId) =>
            throw new InvalidOperationException("No lookup is expected.");
        public string ComputeDeviceId(DeviceFingerprintHashes fingerprint) => throw new InvalidOperationException("No lookup is expected.");
        public byte[] ComputeChallengeHash(string challengeToken) => throw new InvalidOperationException("No lookup is expected.");
        public byte[] ComputeExportTokenHash(string exportToken) => throw new InvalidOperationException("No lookup is expected.");
    }
}
