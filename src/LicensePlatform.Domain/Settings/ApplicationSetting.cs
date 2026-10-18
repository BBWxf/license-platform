using LicensePlatform.Domain.Common;

namespace LicensePlatform.Domain.Settings;

public sealed class ApplicationSetting : Entity
{
    private ApplicationSetting()
    {
    }

    public ApplicationSetting(
        Guid id,
        string key,
        string valueJson,
        bool isSecret,
        DateTimeOffset updatedAtUtc)
        : base(id)
    {
        Key = NormalizeKey(key);
        ValueJson = Guard.Required(valueJson, nameof(valueJson), 1_000_000);
        IsSecret = isSecret;
        UpdatedAtUtc = Guard.Utc(updatedAtUtc, nameof(updatedAtUtc));
    }

    public string Key { get; private set; } = string.Empty;

    public string ValueJson { get; private set; } = "null";

    public bool IsSecret { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void SetValue(string valueJson, bool isSecret, DateTimeOffset nowUtc)
    {
        ValueJson = Guard.Required(valueJson, nameof(valueJson), 1_000_000);
        IsSecret = isSecret;
        UpdatedAtUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        Touch();
    }

    private static string NormalizeKey(string key)
    {
        var normalized = Guard.Required(key, nameof(key), 200).ToLowerInvariant();
        if (normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_' and not ':'))
        {
            throw new DomainException("Setting key contains unsupported characters.");
        }

        return normalized;
    }
}
