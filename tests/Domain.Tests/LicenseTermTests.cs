using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Licensing;

namespace LicensePlatform.Domain.Tests;

public sealed class LicenseTermTests
{
    [Fact]
    public void Months_AreExactlyThirtyDays()
    {
        Assert.Equal(30, LicenseTerm.Months(1).ToDays());
        Assert.Equal(90, LicenseTerm.Months(3).ToDays());
    }

    [Fact]
    public void Years_AreExactlyThreeHundredSixtyFiveDays()
    {
        Assert.Equal(365, LicenseTerm.Years(1).ToDays());
        Assert.Equal(730, LicenseTerm.Years(2).ToDays());
    }

    [Fact]
    public void PermanentTerm_HasNoExpiration()
    {
        var activatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(LicenseTerm.Permanent().CalculateExpiration(activatedAt));
        Assert.Throws<DomainException>(() => LicenseTerm.Permanent().ToDays());
    }
}
