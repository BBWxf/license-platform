using LicensePlatform.Domain.Common;
using LicensePlatform.Domain.Licensing;

namespace LicensePlatform.Domain.Tests;

public sealed class OneTimeExportTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Export_CanBeConsumedOnlyOnce()
    {
        var export = CreateExport(Start.AddMinutes(10));

        export.Consume(Start.AddMinutes(1));

        Assert.True(export.IsConsumed);
        Assert.Throws<DomainException>(() => export.Consume(Start.AddMinutes(2)));
    }

    [Fact]
    public void ExpiredExport_CannotBeConsumed()
    {
        var export = CreateExport(Start.AddMinutes(10));

        Assert.Throws<DomainException>(() => export.Consume(Start.AddMinutes(10)));
        Assert.False(export.IsConsumed);
    }

    [Fact]
    public void Payload_CanBeErasedOnlyAfterConsumption()
    {
        var export = CreateExport(Start.AddMinutes(10));
        Assert.Throws<DomainException>(export.ErasePayload);

        export.Consume(Start.AddMinutes(1));
        export.ErasePayload();

        Assert.Empty(export.ProtectedPayload);
    }

    private static LicenseBatchExport CreateExport(DateTimeOffset expiresAt)
    {
        return new LicenseBatchExport(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Enumerable.Repeat((byte)0x11, 32).ToArray(),
            [0x01, 0x02, 0x03],
            Start,
            expiresAt);
    }
}
