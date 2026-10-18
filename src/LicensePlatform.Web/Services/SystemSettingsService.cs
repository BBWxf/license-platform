using System.Reflection;
using System.Text.Json;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Domain.Auditing;
using LicensePlatform.Domain.Settings;
using LicensePlatform.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Services;

public sealed class SystemSettingsService(
    LicensePlatformDbContext dbContext,
    IClock clock,
    IConfiguration configuration,
    IHttpContextAccessor httpContextAccessor) : ISystemSettingsService
{
    private const string SystemNameKey = "branding.system-name";
    private const string LogoKey = "branding.logo";
    private const int MaxLogoBytes = 512 * 1024;

    public async Task<SystemSettingsEditor> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.ApplicationSettings
            .AsNoTracking()
            .Where(item => item.Key == SystemNameKey || item.Key == LogoKey)
            .ToListAsync(cancellationToken);
        var systemName = DeserializeString(settings.SingleOrDefault(item => item.Key == SystemNameKey)?.ValueJson)
                         ?? configuration["Branding:SystemName"]
                         ?? "软件授权管理系统";
        var hasLogo = settings.Any(item => item.Key == LogoKey);
        var instanceId = configuration["InstanceId"] ?? "not-configured";
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "development";
        return new SystemSettingsEditor(systemName, hasLogo, instanceId, version);
    }

    public async Task<BrandLogo?> GetLogoAsync(CancellationToken cancellationToken)
    {
        var value = await dbContext.ApplicationSettings
            .AsNoTracking()
            .Where(item => item.Key == LogoKey)
            .Select(item => item.ValueJson)
            .SingleOrDefaultAsync(cancellationToken);
        if (value is null)
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredLogo>(value);
            if (stored is null || stored.ContentType is not ("image/png" or "image/jpeg"))
            {
                return null;
            }

            var content = Convert.FromBase64String(stored.DataBase64);
            return content.Length <= MaxLogoBytes && HasExpectedSignature(content, stored.ContentType)
                ? new BrandLogo(content, stored.ContentType)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public async Task UpdateAsync(
        string systemName,
        IFormFile? logo,
        Guid actorId,
        string sourceIp,
        CancellationToken cancellationToken)
    {
        systemName = systemName.Trim();
        if (systemName.Length is < 2 or > 80)
        {
            throw new ArgumentException("System name must contain between 2 and 80 characters.", nameof(systemName));
        }

        var now = clock.UtcNow;
        await SetSettingAsync(SystemNameKey, JsonSerializer.Serialize(systemName), now, cancellationToken);
        if (logo is not null)
        {
            var content = await ReadLogoAsync(logo, cancellationToken);
            var stored = new StoredLogo(logo.ContentType, Convert.ToBase64String(content));
            await SetSettingAsync(LogoKey, JsonSerializer.Serialize(stored), now, cancellationToken);
        }

        dbContext.AuditRecords.Add(new AuditRecord(
            Guid.NewGuid(),
            AuditActorType.Administrator,
            actorId.ToString("D"),
            "SETTINGS.UPDATE",
            "ApplicationSetting",
            "branding",
            JsonSerializer.Serialize(new { systemName, logoUpdated = logo is not null }),
            httpContextAccessor.HttpContext?.TraceIdentifier,
            sourceIp,
            now));
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task SetSettingAsync(
        string key,
        string valueJson,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var setting = await dbContext.ApplicationSettings
            .SingleOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (setting is null)
        {
            dbContext.ApplicationSettings.Add(new ApplicationSetting(Guid.NewGuid(), key, valueJson, false, now));
        }
        else
        {
            setting.SetValue(valueJson, false, now);
        }
    }

    private static async Task<byte[]> ReadLogoAsync(IFormFile logo, CancellationToken cancellationToken)
    {
        if (logo.Length is <= 0 or > MaxLogoBytes || logo.ContentType is not ("image/png" or "image/jpeg"))
        {
            throw new InvalidOperationException("Logo must be a PNG or JPEG file no larger than 512KB.");
        }

        await using var stream = logo.OpenReadStream();
        using var memory = new MemoryStream((int)logo.Length);
        await stream.CopyToAsync(memory, cancellationToken);
        var content = memory.ToArray();
        if (!HasExpectedSignature(content, logo.ContentType))
        {
            throw new InvalidOperationException("Logo file signature does not match its declared content type.");
        }

        return content;
    }

    private static bool HasExpectedSignature(byte[] content, string contentType)
    {
        return contentType switch
        {
            "image/png" => content.Length >= 8
                           && content.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "image/jpeg" => content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
            _ => false
        };
    }

    private static string? DeserializeString(string? valueJson)
    {
        if (valueJson is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>(valueJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record StoredLogo(string ContentType, string DataBase64);
}
