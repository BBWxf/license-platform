using LicensePlatform.Infrastructure.Cryptography;
using Microsoft.Extensions.Options;

namespace LicensePlatform.IntegrationTests;

public sealed class LicenseKeyCryptographyTests
{
    private readonly HmacLicenseKeyCryptography _service = new(Options.Create(CreateOptions()));

    [Fact]
    public void GeneratedKey_ContainsProductPrefixAndExactlyOneHundredSixtyEntropyBits()
    {
        var generated = _service.Generate("pro");
        var body = generated.Normalized[(generated.Prefix.Length + 1)..];

        Assert.Equal("PRO", generated.Prefix);
        Assert.Equal(32, body.Length);
        Assert.Equal(generated.Normalized, _service.Normalize(generated.PlainText));
        Assert.Equal(32, generated.LookupHash.Length);
        Assert.DoesNotContain(body, character => "ILOU".Contains(character));
    }

    [Fact]
    public void LookupHash_IsStableAcrossFormattingAndCrockfordAliases()
    {
        const string zeros = "PRO-00000000000000000000000000000000";
        const string aliases = "pro-OOOO-OOOO-OOOO-OOOO-OOOO-OOOO-OOOO-OOOO";

        Assert.Equal(zeros, _service.Normalize(aliases));
        Assert.Equal("PRO", _service.GetPrefix(aliases));
        Assert.Equal(_service.ComputeLookupHash(zeros), _service.ComputeLookupHash(aliases));
    }

    [Fact]
    public void Prefix_IsPartOfTheLookupHashDomain()
    {
        const string body = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        Assert.NotEqual(
            _service.ComputeLookupHash($"AAA-{body}"),
            _service.ComputeLookupHash($"BBB-{body}"));
    }

    [Fact]
    public void DeviceFingerprintHashing_UsesThreeIndependentComponentsAndStableDeviceId()
    {
        var lookup = new HmacLookupHashService(Options.Create(CreateOptions()));
        var initial = lookup.ComputeDeviceFingerprintHashes("machine", "smbios", "volume");
        var repeated = lookup.ComputeDeviceFingerprintHashes("MACHINE", "SMBIOS", "VOLUME");
        var changedVolume = lookup.ComputeDeviceFingerprintHashes("machine", "smbios", "other-volume");

        Assert.Equal(32, initial.MachineGuidHash.Length);
        Assert.Equal(32, initial.SmbiosHash.Length);
        Assert.Equal(32, initial.SystemVolumeHash.Length);
        Assert.True(initial.MatchesAtLeast(changedVolume));
        Assert.Equal(lookup.ComputeDeviceId(initial), lookup.ComputeDeviceId(repeated));
        Assert.NotEqual(lookup.ComputeDeviceId(initial), lookup.ComputeDeviceId(changedVolume));
    }

    [Fact]
    public void GeneratedKeys_AreUniqueInRepresentativeSample()
    {
        var keys = Enumerable.Range(0, 1_000)
            .Select(_ => _service.Generate("PRO").Normalized)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(1_000, keys.Count);
    }

    private static CryptographyOptions CreateOptions() => new()
    {
        LicenseKeyHmacKeyBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)0xA1, 32).ToArray()),
        LookupHmacKeyBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)0xB2, 32).ToArray()),
        DataProtectionKeyBase64 = Convert.ToBase64String(Enumerable.Repeat((byte)0xC3, 32).ToArray())
    };
}
