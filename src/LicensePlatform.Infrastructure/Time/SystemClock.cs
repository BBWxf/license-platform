using LicensePlatform.Application.Abstractions;

namespace LicensePlatform.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
