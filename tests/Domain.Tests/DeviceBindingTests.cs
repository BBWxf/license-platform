using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Licensing;

namespace LicensePlatform.Domain.Tests;

public sealed class DeviceBindingTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Bind_IsIdempotentForSameDevice_AndRejectsSecondDevice()
    {
        var license = CreateActivatedLicense();
        var firstDevice = Fingerprint(0x10, 0x11, 0x12);
        var secondDevice = Fingerprint(0x20, 0x21, 0x22);

        var first = license.BindDevice(firstDevice, "device-a", "public-key-a", Start);
        var repeated = license.BindDevice(firstDevice, "device-a", "public-key-a", Start.AddMinutes(1));

        Assert.Same(first, repeated);
        Assert.Single(license.DeviceBindings);
        Assert.Throws<DomainException>(() =>
            license.BindDevice(firstDevice, "device-a", "rotated-public-key", Start.AddMinutes(1)));
        Assert.Throws<DomainException>(() =>
            license.BindDevice(secondDevice, "device-b", "public-key-b", Start.AddMinutes(1)));
    }

    [Fact]
    public void Binding_MatchesWhenAtLeastTwoOriginalComponentsRemainStable()
    {
        var license = CreateActivatedLicense();
        var initial = Fingerprint(0x10, 0x11, 0x12);
        var oneComponentChanged = Fingerprint(0x10, 0x11, 0x99);
        var twoComponentsChanged = Fingerprint(0x10, 0x98, 0x99);
        var binding = license.BindDevice(initial, "device-a", "public-key-a", Start);

        Assert.True(binding.Matches(oneComponentChanged));
        Assert.False(binding.Matches(twoComponentsChanged));
    }

    [Fact]
    public void SuccessfulUnbind_IncrementsVersionExactlyOnce_AndRebindUsesNewVersion()
    {
        var license = CreateActivatedLicense();
        var firstDevice = Fingerprint(0x10, 0x11, 0x12);
        var slightlyChangedFirstDevice = Fingerprint(0x10, 0x11, 0x99);
        var secondDevice = Fingerprint(0x20, 0x21, 0x22);
        license.BindDevice(firstDevice, "device-a", "public-key-a", Start);

        license.UnbindDevice(slightlyChangedFirstDevice, Start.AddMinutes(1));
        var rebound = license.BindDevice(secondDevice, "device-b", "public-key-b", Start.AddMinutes(2));

        Assert.Equal(2, license.BindingVersion);
        Assert.Equal(2, rebound.BindingVersion);
        Assert.Equal(2, license.DeviceBindings.Count);
        Assert.Single(license.DeviceBindings, binding => binding.IsActive);
    }

    [Fact]
    public void FailedOrRepeatedUnbind_DoesNotIncrementBindingVersion()
    {
        var license = CreateActivatedLicense();
        var device = Fingerprint(0x10, 0x11, 0x12);
        license.BindDevice(device, "device-a", "public-key-a", Start);

        Assert.Throws<DomainException>(() =>
            license.UnbindDevice(Fingerprint(0x10, 0x90, 0x91), Start.AddMinutes(1)));
        Assert.Equal(1, license.BindingVersion);

        license.UnbindDevice(device, Start.AddMinutes(1));
        Assert.Throws<DomainException>(() => license.UnbindDevice(device, Start.AddMinutes(2)));
        Assert.Equal(2, license.BindingVersion);
    }

    [Fact]
    public void Unbind_InvalidatesChallengeCreatedForPreviousBindingVersion()
    {
        var license = CreateActivatedLicense();
        var device = Fingerprint(0x10, 0x11, 0x12);
        license.BindDevice(device, "device-a", "public-key-a", Start);
        var challenge = new ActivationChallenge(
            Guid.NewGuid(),
            license.ProductId,
            license.Id,
            Hash(0x30),
            device,
            Hash(0x31),
            "device-a",
            "device-public-key",
            ActivationChallengePurpose.RefreshLease,
            license.BindingVersion,
            Start,
            Start.AddMinutes(5));

        license.UnbindDevice(device, Start.AddMinutes(1));

        Assert.Throws<DomainException>(() =>
            challenge.Consume(license.BindingVersion, Start.AddMinutes(2)));
    }

    private static LicenseKey CreateActivatedLicense()
    {
        var license = new LicenseKey(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Hash(0x40),
            "ABCD…WXYZ",
            LicenseTerm.Months(1),
            Start);
        license.Activate(Start);
        return license;
    }

    private static DeviceFingerprintHashes Fingerprint(byte machineGuid, byte smbios, byte volume) =>
        new(Hash(machineGuid), Hash(smbios), Hash(volume));

    private static byte[] Hash(byte value) => Enumerable.Repeat(value, 32).ToArray();
}
