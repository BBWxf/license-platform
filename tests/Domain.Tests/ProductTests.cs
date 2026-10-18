using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Products;

namespace LicensePlatform.Domain.Tests;

public sealed class ProductTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LicensePrefix_IsNormalizedAndConfigurable()
    {
        var product = new Product(Guid.NewGuid(), "editor", "Editor", "edt", 60, Start);

        product.ChangeLicensePrefix("pro2", Start.AddMinutes(1));

        Assert.Equal("PRO2", product.LicensePrefix);
        Assert.Throws<DomainException>(() =>
            product.ChangeLicensePrefix("invalid-prefix", Start.AddMinutes(2)));
    }
}
