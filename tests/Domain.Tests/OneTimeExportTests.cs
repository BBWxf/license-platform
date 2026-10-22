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

    [Fact]
    public void NullExpiration_CreatesADurableExportWithoutChangingItsPayload()
    {
        var export = CreateExport(null);

        Assert.Null(export.ExpiresAtUtc);
        Assert.Equal(Start, export.CreatedAtUtc);
        Assert.Null(export.ConsumedAtUtc);
        Assert.False(export.IsConsumed);
        Assert.Equal(3, export.ProtectedPayload.Length);
        Assert.Equal(32, export.DownloadTokenHash.Length);
        Assert.Equal(1, export.Version);
    }

    [Fact]
    public void DurableExport_CannotBeConsumedOrErased()
    {
        var export = CreateExport(null);
        var payload = export.ProtectedPayload.ToArray();
        var tokenHash = export.DownloadTokenHash.ToArray();
        var version = export.Version;

        var consumeError = Assert.Throws<DomainException>(() => export.Consume(Start.AddMinutes(1)));
        Assert.Equal("A durable export cannot be consumed.", consumeError.Message);
        Assert.Throws<DomainException>(() => export.Consume(Start.AddYears(100)));
        var eraseError = Assert.Throws<DomainException>(export.ErasePayload);
        Assert.Equal("A durable export payload cannot be erased.", eraseError.Message);

        Assert.Null(export.ConsumedAtUtc);
        Assert.False(export.IsConsumed);
        Assert.Null(export.ExpiresAtUtc);
        Assert.True(payload.SequenceEqual(export.ProtectedPayload));
        Assert.True(tokenHash.SequenceEqual(export.DownloadTokenHash));
        Assert.Equal(version, export.Version);
    }

    [Fact]
    public void DurableExport_RejectsConsumptionBeforeValidatingItsTimestamp()
    {
        var export = CreateExport(null);

        var error = Assert.Throws<DomainException>(
            () => export.Consume(Start.AddMinutes(1).ToOffset(TimeSpan.FromHours(1))));

        Assert.Equal("A durable export cannot be consumed.", error.Message);
        Assert.False(export.IsConsumed);
        Assert.Equal(1, export.Version);
    }

    [Fact]
    public void DurableExport_WithHistoricalConsumption_CannotBeConsumedOrErased()
    {
        var export = CreateExport(Start.AddMinutes(10));
        export.Consume(Start.AddMinutes(1));
        // Simulate EF materializing an export promoted by the additive data migration.
        typeof(LicenseBatchExport).GetProperty(nameof(LicenseBatchExport.ExpiresAtUtc))!
            .SetValue(export, null);
        var payload = export.ProtectedPayload.ToArray();
        var tokenHash = export.DownloadTokenHash.ToArray();
        var version = export.Version;
        var consumedAt = export.ConsumedAtUtc;

        var consumeError = Assert.Throws<DomainException>(() => export.Consume(Start.AddMinutes(2)));
        var eraseError = Assert.Throws<DomainException>(export.ErasePayload);

        Assert.Equal("A durable export cannot be consumed.", consumeError.Message);
        Assert.Equal("A durable export payload cannot be erased.", eraseError.Message);
        Assert.Equal(consumedAt, export.ConsumedAtUtc);
        Assert.True(export.IsConsumed);
        Assert.Null(export.ExpiresAtUtc);
        Assert.True(payload.SequenceEqual(export.ProtectedPayload));
        Assert.True(tokenHash.SequenceEqual(export.DownloadTokenHash));
        Assert.Equal(version, export.Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExportCreation_RequiresUtcEvenWhenExpirationIsNull(bool durable)
    {
        Assert.Throws<DomainException>(() => CreateExport(
            durable ? null : Start.AddMinutes(10),
            Start.ToOffset(TimeSpan.FromHours(1))));
    }

    [Fact]
    public void FiniteExpiration_RequiresUtc()
    {
        Assert.Throws<DomainException>(
            () => CreateExport(Start.AddMinutes(10).ToOffset(TimeSpan.FromHours(1))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public void FiniteExpiration_MustBeAfterCreation(int minutesAfterCreation)
    {
        Assert.Throws<DomainException>(() => CreateExport(Start.AddMinutes(minutesAfterCreation)));
    }

    [Fact]
    public void FiniteExport_StillRequiresUtcConsumption()
    {
        var export = CreateExport(Start.AddMinutes(10));

        Assert.Throws<DomainException>(
            () => export.Consume(Start.AddMinutes(1).ToOffset(TimeSpan.FromHours(1))));

        Assert.False(export.IsConsumed);
        Assert.Equal(Start.AddMinutes(10), export.ExpiresAtUtc);
        Assert.Equal(1, export.Version);
    }

    [Fact]
    public void FiniteExport_ErasingAnAlreadyEmptyConsumedPayloadIsStillIdempotent()
    {
        var export = CreateExport(Start.AddMinutes(10));
        export.Consume(Start.AddMinutes(1));
        export.ErasePayload();
        var version = export.Version;

        export.ErasePayload();

        Assert.Empty(export.ProtectedPayload);
        Assert.Equal(version, export.Version);
    }

    private static LicenseBatchExport CreateExport(DateTimeOffset? expiresAt, DateTimeOffset? createdAt = null)
    {
        return new LicenseBatchExport(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Enumerable.Repeat((byte)0x11, 32).ToArray(),
            [0x01, 0x02, 0x03],
            createdAt ?? Start,
            expiresAt);
    }
}
