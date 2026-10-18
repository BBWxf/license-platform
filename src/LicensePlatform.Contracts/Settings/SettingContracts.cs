namespace LicensePlatform.Contracts.Settings;

public sealed record ApplicationSettingDto(
    Guid Id,
    string Key,
    string ValueJson,
    bool IsSecret,
    DateTimeOffset UpdatedAtUtc,
    long Version);

public sealed record SetApplicationSettingRequest(
    string ValueJson,
    bool IsSecret,
    long? ExpectedVersion);

public interface IApplicationSettingService
{
    Task<ApplicationSettingDto?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ApplicationSettingDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<ApplicationSettingDto> SetAsync(
        string key,
        SetApplicationSettingRequest request,
        CancellationToken cancellationToken = default);
}
