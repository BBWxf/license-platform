namespace LicensePlatform.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
