using System.Net;
using System.Security.Claims;
using System.Text.Json;
using LicensePlatform.Application.Security;
using LicensePlatform.Domain.Administration;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.Middleware;
using LicensePlatform.Web.Pages.Admin;
using LicensePlatform.Web.Security;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LicensePlatform.Web.Tests;

// Renders the actual compiled Web Razor pages without executing Program, bootstrap, migrations,
// production configuration, or infrastructure registration. InMemory contains ONLY a synthetic
// administrator for real credential-stamp validation; business-page data comes from read-only fakes.
public sealed class AdminPageTestHost : IAsyncDisposable
{
    private const string CookieName = "LicensePlatform.Admin";
    private const string SyntheticHash = "admin-page-fixture-not-a-password-hash";
    private const string SignInPath = "/__admin-page-fixture/sign-in";
    private static readonly Guid AdministratorId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private readonly IHost _host;
    private readonly bool _loopback;

    private AdminPageTestHost(AdminPageDataState state, bool loopback, ILicenseBatchAdminService? batchService,
        IAuditAdminService? auditService, IAdminDashboardQuery? dashboardQuery)
    {
        _loopback = loopback;
        Data = new AdminPageFixtureData(state);
        var webAssembly = typeof(DashboardModel).Assembly;
        var staticAssetsManifest = Path.ChangeExtension(webAssembly.Location, ".staticwebassets.runtime.json");
        var contentRoot = FindContentRoot(staticAssetsManifest);
        var staticAssetsConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [WebHostDefaults.StaticWebAssetsKey] = staticAssetsManifest
        }).Build();
        var databaseOptions = new DbContextOptionsBuilder<LicensePlatformDbContext>()
            .UseInMemoryDatabase("admin-page-credentials-" + Guid.NewGuid().ToString("N"))
            .Options;
        _host = new HostBuilder()
            .ConfigureAppConfiguration((_, configuration) =>
            {
                // Do not import appsettings, environment variables, user secrets, or connection strings.
                configuration.Sources.Clear();
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Branding:SystemName"] = Data.SystemName
                });
            })
            .ConfigureLogging(logging => logging.ClearProviders())
            .ConfigureWebHost(web =>
            {
                web.UseContentRoot(contentRoot)
                    .UseWebRoot(Path.Combine(contentRoot, "wwwroot"))
                    .UseSetting(WebHostDefaults.ApplicationKey, webAssembly.GetName().Name)
                    .UseEnvironment(Environments.Development);
                if (loopback)
                {
                    // Optional screenshot preview only; never a public listener or production port.
                    web.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
                }
                else
                {
                    web.UseTestServer();
                }

                web.ConfigureServices(services =>
                {
                    services.AddDataProtection().UseEphemeralDataProtectionProvider();
                    services.AddScoped(_ => new LicensePlatformDbContext(databaseOptions));
                    services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                        .AddCookie(options => AdminCookieAuthentication.Configure(
                            options, new ConfigurationBuilder().Build(), allowInsecureHttp: loopback));
                    services.AddAuthorization();
                    services.AddAntiforgery(options =>
                    {
                        options.Cookie.HttpOnly = true;
                        options.Cookie.SameSite = SameSiteMode.Strict;
                        options.Cookie.SecurePolicy = loopback ? CookieSecurePolicy.None : CookieSecurePolicy.SameAsRequest;
                    });
                    services.Configure<CookieTempDataProviderOptions>(options =>
                    {
                        options.Cookie.HttpOnly = true;
                        options.Cookie.SameSite = SameSiteMode.Strict;
                        options.Cookie.SecurePolicy = loopback ? CookieSecurePolicy.None : CookieSecurePolicy.SameAsRequest;
                    });
                    services.AddRazorPages(options =>
                    {
                        options.Conventions.AuthorizeFolder("/Admin");
                        options.Conventions.AllowAnonymousToPage("/Account/Login");
                    }).AddApplicationPart(webAssembly);
                    services.AddSingleton<IAdminDashboardQuery>(dashboardQuery ?? Data);
                    services.AddSingleton<IProductAdminService>(Data);
                    services.AddSingleton<ILicenseBatchAdminService>(batchService ?? Data);
                    services.AddSingleton<ILicenseAdminService>(Data);
                    services.AddSingleton<IAuditAdminService>(auditService ?? Data);
                    services.AddSingleton<ISystemSettingsService>(Data);
                    services.AddSingleton<IAdminAuthenticationService>(Data);
                });
                web.Configure(app =>
                {
                    // Resolve the Web assembly's real generated scoped assets as well as wwwroot.
                    // The isolated empty configuration intentionally has no default web-host chain.
                    StaticWebAssetsLoader.UseStaticWebAssets(
                        app.ApplicationServices.GetRequiredService<IWebHostEnvironment>(), staticAssetsConfiguration);
                    app.UseMiddleware<SecurityHeadersMiddleware>();
                    app.UseStaticFiles();
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        // Test assembly only. A browser can visit this loopback URL to establish its
                        // own encrypted cookie without writing or printing any ticket material.
                        endpoints.MapGet(SignInPath, async context =>
                        {
                            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, AdministratorId.ToString("D")),
                                new Claim(ClaimTypes.Name, "fixture-operator"),
                                new Claim(AdminCredentialStamp.ClaimType, AdminCredentialStamp.Create(SyntheticHash))
                            ], CookieAuthenticationDefaults.AuthenticationScheme));
                            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                            context.Response.Headers.CacheControl = "no-store";
                            context.Response.Redirect("/Admin/Dashboard");
                        });
                        endpoints.MapRazorPages();
                    });
                });
            })
            .Build();
    }

    internal AdminPageFixtureData Data { get; }
    public HttpClient Client { get; private set; } = null!;
    public Uri BaseUri { get; private set; } = null!;
    public Uri BrowserSignInUri => new(BaseUri, SignInPath);

    public static async Task<AdminPageTestHost> StartAsync(
        AdminPageDataState state = AdminPageDataState.Populated, bool loopback = false,
        ILicenseBatchAdminService? batchService = null, IAuditAdminService? auditService = null,
        IAdminDashboardQuery? dashboardQuery = null)
    {
        var fixture = new AdminPageTestHost(state, loopback, batchService, auditService, dashboardQuery);
        try
        {
            await fixture._host.StartAsync();
            await using (var scope = fixture._host.Services.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<LicensePlatformDbContext>();
                database.Administrators.Add(new Administrator(
                    AdministratorId, "fixture-operator", SyntheticHash, AdminPageFixtureData.Timestamp));
                await database.SaveChangesAsync();
            }

            if (fixture._loopback)
            {
                var server = fixture._host.Services.GetRequiredService<IServer>();
                var addresses = server.Features.Get<IServerAddressesFeature>()
                    ?? throw new InvalidOperationException("Preview listener address is unavailable.");
                fixture.BaseUri = new Uri(addresses.Addresses.Single());
                fixture.Client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
            }
            else
            {
                fixture.BaseUri = new Uri("https://localhost");
                fixture.Client = fixture._host.GetTestClient();
            }

            fixture.Client.BaseAddress = fixture.BaseUri;
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync();
            throw;
        }
    }

    // Returned ticket stays in memory. Callers must not log it, include it in assertion messages,
    // save it to disk, or pass it in process arguments. BrowserSignInUri avoids handling it at all.
    public async Task<string> IssueCookieAsync()
    {
        using var response = await Client.GetAsync(SignInPath);
        if (response.StatusCode != HttpStatusCode.Redirect
            || !response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            throw new InvalidOperationException("Fixture sign-in failed.");
        }

        var cookies = values.Where(value => value.StartsWith(CookieName + "=", StringComparison.Ordinal)).ToArray();
        if (cookies.Length != 1)
        {
            throw new InvalidOperationException("Fixture sign-in did not issue one administrator cookie.");
        }

        return cookies[0].Split(';', 2)[0];
    }

    public async Task AuthenticateAsync()
    {
        var cookie = await IssueCookieAsync();
        Client.DefaultRequestHeaders.Remove("Cookie");
        Client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
    }

    // Change only the isolated credential store, exercising the production cookie-stamp validator.
    // Existing encrypted tickets remain in memory and are rejected on their next request.
    public async Task RevokeAdministratorCookieAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<LicensePlatformDbContext>();
        var administrator = await database.Administrators.SingleAsync(item => item.Id == AdministratorId);
        administrator.ChangePassword("admin-page-fixture-revoked-not-a-password-hash", AdminPageFixtureData.Timestamp.AddMinutes(1));
        await database.SaveChangesAsync();
    }

    private static string FindContentRoot(string staticAssetsManifest)
    {
        // A temporary preview console may live outside the checkout. The generated Web manifest
        // already records its local wwwroot, so resolve that first instead of relying on the cwd.
        if (File.Exists(staticAssetsManifest))
        {
            using var manifest = JsonDocument.Parse(File.ReadAllText(staticAssetsManifest));
            foreach (var root in manifest.RootElement.GetProperty("ContentRoots").EnumerateArray())
            {
                var parent = Directory.GetParent(root.GetString()!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (parent is not null && File.Exists(Path.Combine(parent.FullName, "LicensePlatform.Web.csproj")))
                {
                    return parent.FullName;
                }
            }
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src", "LicensePlatform.Web");
            if (File.Exists(Path.Combine(candidate, "LicensePlatform.Web.csproj")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("The local Web project content root could not be located.");
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}
