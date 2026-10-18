using LicensePlatform.Domain.Common;

namespace LicensePlatform.Domain.Administration;

public sealed class Administrator : Entity
{
    private Administrator()
    {
    }

    public Administrator(
        Guid id,
        string userName,
        string passwordHash,
        DateTimeOffset createdAtUtc)
        : base(id)
    {
        UserName = NormalizeUserName(userName);
        NormalizedUserName = UserName.ToUpperInvariant();
        PasswordHash = Guard.Required(passwordHash, nameof(passwordHash), 2_048);
        CreatedAtUtc = Guard.Utc(createdAtUtc, nameof(createdAtUtc));
        PasswordChangedAtUtc = CreatedAtUtc;
    }

    public string UserName { get; private set; } = string.Empty;

    public string NormalizedUserName { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public int FailedAccessCount { get; private set; }

    public DateTimeOffset? LockoutEndUtc { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public DateTimeOffset PasswordChangedAtUtc { get; private set; }

    public DateTimeOffset? LastLoginAtUtc { get; private set; }

    public bool IsLockedOut(DateTimeOffset nowUtc)
    {
        nowUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        return LockoutEndUtc.HasValue && nowUtc < LockoutEndUtc.Value;
    }

    public void RecordFailedAccess(DateTimeOffset nowUtc)
    {
        nowUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        FailedAccessCount = checked(FailedAccessCount + 1);
        if (FailedAccessCount >= 5)
        {
            var lockoutMinutes = Math.Min(30, 5 * (FailedAccessCount - 4));
            LockoutEndUtc = nowUtc.AddMinutes(lockoutMinutes);
        }

        Touch();
    }

    public void RecordSuccessfulLogin(DateTimeOffset nowUtc)
    {
        LastLoginAtUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        FailedAccessCount = 0;
        LockoutEndUtc = null;
        Touch();
    }

    public void ChangePassword(string passwordHash, DateTimeOffset nowUtc)
    {
        PasswordHash = Guard.Required(passwordHash, nameof(passwordHash), 2_048);
        PasswordChangedAtUtc = Guard.Utc(nowUtc, nameof(nowUtc));
        FailedAccessCount = 0;
        LockoutEndUtc = null;
        Touch();
    }

    private static string NormalizeUserName(string userName)
    {
        var normalized = Guard.Required(userName, nameof(userName), 100);
        if (normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new DomainException("Administrator user name may contain only ASCII letters, digits, '.', '_' and '-'.");
        }

        return normalized;
    }
}
