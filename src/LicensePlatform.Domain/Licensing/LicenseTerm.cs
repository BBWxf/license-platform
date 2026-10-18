using LicensePlatform.Domain.Common;

namespace LicensePlatform.Domain.Licensing;

public enum LicenseTermUnit
{
    Days = 1,
    Months = 2,
    Years = 3,
    Permanent = 4
}

public readonly record struct LicenseTerm
{
    public LicenseTermUnit Unit { get; }

    public int Value { get; }

    public bool IsPermanent => Unit == LicenseTermUnit.Permanent;

    public LicenseTerm(LicenseTermUnit unit, int value)
    {
        if (!Enum.IsDefined(unit))
        {
            throw new DomainException("License term unit is invalid.");
        }

        if (unit == LicenseTermUnit.Permanent)
        {
            if (value != 0)
            {
                throw new DomainException("A permanent license term must have a value of zero.");
            }
        }
        else if (value <= 0)
        {
            throw new DomainException("A non-permanent license term must have a positive value.");
        }

        Unit = unit;
        Value = value;
    }

    public static LicenseTerm Days(int value) => new(LicenseTermUnit.Days, value);

    public static LicenseTerm Months(int value) => new(LicenseTermUnit.Months, value);

    public static LicenseTerm Years(int value) => new(LicenseTermUnit.Years, value);

    public static LicenseTerm Permanent() => new(LicenseTermUnit.Permanent, 0);

    public int ToDays()
    {
        return Unit switch
        {
            LicenseTermUnit.Days => Value,
            LicenseTermUnit.Months => checked(Value * 30),
            LicenseTermUnit.Years => checked(Value * 365),
            LicenseTermUnit.Permanent => throw new DomainException("A permanent term has no finite day count."),
            _ => throw new DomainException("License term unit is invalid.")
        };
    }

    public DateTimeOffset? CalculateExpiration(DateTimeOffset activatedAtUtc)
    {
        Guard.Utc(activatedAtUtc, nameof(activatedAtUtc));
        return IsPermanent ? null : activatedAtUtc.AddDays(ToDays());
    }
}
