using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Licensing;

namespace LicensePlatform.Domain.Tests;

public sealed class LicenseKeyLifecycleTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FirstActivation_StartsTheEntitlementClockOnce()
    {
        var license = CreateLicense(LicenseTerm.Months(1));

        license.Activate(Start);
        license.Activate(Start.AddDays(10));

        Assert.Equal(Start, license.ActivatedAtUtc);
        Assert.Equal(Start.AddDays(30), license.ExpiresAtUtc);
    }

    [Fact]
    public void ExtensionBeforeActivation_IsAddedToInitialExpiration()
    {
        var license = CreateLicense(LicenseTerm.Years(1));

        license.Extend(LicenseTerm.Months(1), Start);
        license.Activate(Start.AddDays(2));

        Assert.Equal(30, license.AdditionalDays);
        Assert.Equal(Start.AddDays(2 + 365 + 30), license.ExpiresAtUtc);
    }

    [Fact]
    public void ExtensionAfterActivation_ExtendsExistingExpiration()
    {
        var license = CreateLicense(LicenseTerm.Days(10));
        license.Activate(Start);

        license.Extend(LicenseTerm.Years(1), Start.AddDays(5));

        Assert.Equal(Start.AddDays(375), license.ExpiresAtUtc);
    }

    [Fact]
    public void ExtensionAfterExpiration_StartsFromNowRatherThanOldExpiration()
    {
        var license = CreateLicense(LicenseTerm.Days(1));
        license.Activate(Start);
        var extendedAt = Start.AddDays(10);

        license.Extend(LicenseTerm.Days(5), extendedAt);

        Assert.Equal(extendedAt.AddDays(5), license.ExpiresAtUtc);
    }

    [Fact]
    public void PermanentLicense_CannotBeExtended()
    {
        var license = CreateLicense(LicenseTerm.Permanent());
        license.Activate(Start);

        Assert.Null(license.ExpiresAtUtc);
        Assert.Throws<DomainException>(() => license.Extend(LicenseTerm.Days(1), Start));
    }

    [Fact]
    public void DisabledLicense_CannotActivateOrIssueLease_AndCanBeEnabledAgain()
    {
        var license = CreateLicense(LicenseTerm.Days(30));
        license.Disable("chargeback", Start);

        Assert.Throws<DomainException>(() => license.Activate(Start));

        license.Enable(Start.AddMinutes(1));
        license.Activate(Start.AddMinutes(1));
        license.EnsureCanIssueLease(Start.AddMinutes(2));

        Assert.Equal(LicenseKeyStatus.Enabled, license.Status);
        Assert.Null(license.DisabledReason);
    }

    [Fact]
    public void ExpirationBoundary_IsExclusive()
    {
        var license = CreateLicense(LicenseTerm.Days(1));
        license.Activate(Start);

        license.EnsureCanIssueLease(Start.AddDays(1).AddTicks(-1));
        Assert.Throws<DomainException>(() => license.EnsureCanIssueLease(Start.AddDays(1)));
    }

    private static LicenseKey CreateLicense(LicenseTerm term)
    {
        return new LicenseKey(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Enumerable.Repeat((byte)0x42, 32).ToArray(),
            "ABCD…WXYZ",
            term,
            Start);
    }
}
