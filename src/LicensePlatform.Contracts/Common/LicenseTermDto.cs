namespace LicensePlatform.Contracts.Common;

public enum LicenseTermUnitDto
{
    Days = 1,
    Months = 2,
    Years = 3,
    Permanent = 4
}

public sealed record LicenseTermDto(LicenseTermUnitDto Unit, int Value);
