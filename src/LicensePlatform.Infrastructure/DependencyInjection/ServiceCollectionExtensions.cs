using LicensePlatform.Application.Abstractions;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Infrastructure.ApplicationServices;
using LicensePlatform.Infrastructure.Cryptography;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LicensePlatform.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLicensePlatformInfrastructure(
        this IServiceCollection services,
        string connectionString,
        Action<CryptographyOptions> configureCryptography)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(configureCryptography);

        services.AddOptions<CryptographyOptions>().Configure(configureCryptography);
        services.AddDbContext<LicensePlatformDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql => npgsql.SetPostgresVersion(18, 0)));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ILicenseKeyCryptography, HmacLicenseKeyCryptography>();
        services.AddSingleton<ILookupHashService, HmacLookupHashService>();
        services.AddSingleton<IOpaqueTokenGenerator, CryptoRandomOpaqueTokenGenerator>();
        services.AddSingleton<IProtectedDataService, AesGcmProtectedDataService>();
        services.AddSingleton<IProductSigningKeyFactory, Es256ProductSigningKeyFactory>();
        services.AddSingleton<ILeaseSigningService, Es256LeaseSigningService>();
        services.AddSingleton<IDeviceProofVerifier, Es256DeviceProofVerifier>();
        services.AddScoped<IActivationApplicationService, ActivationApplicationService>();

        return services;
    }
}
