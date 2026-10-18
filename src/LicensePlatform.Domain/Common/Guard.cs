namespace LicensePlatform.Domain.Common;

internal static class Guard
{
    public static string Required(string? value, string name, int maxLength)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new DomainException($"{name} is required.");
        }

        if (trimmed.Length > maxLength)
        {
            throw new DomainException($"{name} cannot exceed {maxLength} characters.");
        }

        return trimmed;
    }

    public static byte[] Bytes(byte[]? value, string name, int? exactLength = null)
    {
        if (value is null || value.Length == 0)
        {
            throw new DomainException($"{name} is required.");
        }

        if (exactLength is not null && value.Length != exactLength.Value)
        {
            throw new DomainException($"{name} must contain exactly {exactLength.Value} bytes.");
        }

        return value.ToArray();
    }

    public static DateTimeOffset Utc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new DomainException($"{name} must use the UTC offset.");
        }

        return value;
    }
}
