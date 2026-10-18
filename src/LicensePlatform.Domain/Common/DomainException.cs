namespace LicensePlatform.Domain.Common;

public sealed class DomainException : InvalidOperationException
{
    public DomainException(string message)
        : base(message)
    {
    }
}
