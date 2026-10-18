using LicensePlatform.Contracts.Common;
using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Licensing;

namespace LicensePlatform.Application.Licensing;

public static class LicenseTermConverter
{
    public static LicenseTerm ToDomain(this LicenseTermDto term)
    {
        ArgumentNullException.ThrowIfNull(term);

        var unit = term.Unit switch
        {
            LicenseTermUnitDto.Days => LicenseTermUnit.Days,
            LicenseTermUnitDto.Months => LicenseTermUnit.Months,
            LicenseTermUnitDto.Years => LicenseTermUnit.Years,
            LicenseTermUnitDto.Permanent => LicenseTermUnit.Permanent,
            _ => throw new DomainException("License term unit is invalid.")
        };

        return new LicenseTerm(unit, term.Value);
    }

    public static LicenseTermDto ToContract(this LicenseTerm term)
    {
        var unit = term.Unit switch
        {
            LicenseTermUnit.Days => LicenseTermUnitDto.Days,
            LicenseTermUnit.Months => LicenseTermUnitDto.Months,
            LicenseTermUnit.Years => LicenseTermUnitDto.Years,
            LicenseTermUnit.Permanent => LicenseTermUnitDto.Permanent,
            _ => throw new DomainException("License term unit is invalid.")
        };

        return new LicenseTermDto(unit, term.Value);
    }
}
