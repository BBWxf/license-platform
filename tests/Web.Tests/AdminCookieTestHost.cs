using System.Net;
using System.Security.Claims;
using LicensePlatform.Application.Security;
using LicensePlatform.Domain.Administration;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LicensePlatform.Web.Tests;

// InMemory supplies only this middleware harness's credential state. It does not model PostgreSQL transactions or concurrency.
internal sealed class AdminCookieTestHost : IAsyncDisposable
{
    public const string CookieName = "LicensePlatform.Admin";
    public const string InitialHash = "synthetic-password-hash-before-reset";
    private readonly IHost _host;
    private ClaimsPrincipal _principal = new(new ClaimsIdentity());

    private AdminCookieTestHost()
    {
        var dbOptions = new DbContextOptionsBuilder<LicensePlatformDbContext>()
            .UseInMemoryDatabase("admin-cookie-" + Guid.NewGuid().ToString("N"))
            .Options;
        _host = new HostBuilder()
            .ConfigureLogging(logging => logging.ClearProviders().AddProvider(Logs))
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddDataProtection().UseEphemeralDataProtectionProvider();
                    services.AddScoped(_ =>
                    {
                        DatabaseResolutions++;
                        if (DatabaseFailure is not null)
                        {
                            throw DatabaseFailure;
                        }

                        return new LicensePlatformDbContext(dbOptions);
                    });
                    services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
                        .AddCookie(options =>
                        {
                            AdminCookieAuthentication.Configure(options, new ConfigurationBuilder().Build(), allowInsecureHttp: false);
                            options.TimeProvider = Clock;
                        });
                    services.AddAuthorization();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.Map("/issue", async context =>
                        {
                            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, _principal);
                            context.Response.StatusCode = StatusCodes.Status204NoContent;
                        });
                        endpoints.Map("/Admin/Probe", async context =>
                        {
                            ProtectedCalls++;
                            await context.Response.WriteAsync("authorized");
                        }).RequireAuthorization();
                    });
                }))
            .Build();
    }

    public Guid AdministratorId { get; } = Guid.NewGuid();
    public TestClock Clock { get; } = new();
    public CapturingLoggerProvider Logs { get; } = new();
    public HttpClient Client { get; private set; } = null!;
    public Exception? DatabaseFailure { get; set; }
    public int DatabaseResolutions { get; private set; }
    public int ProtectedCalls { get; private set; }

    public static async Task<AdminCookieTestHost> StartAsync()
    {
        var host = new AdminCookieTestHost();
        try
        {
            await host._host.StartAsync();
            host.Client = host._host.GetTestClient();
            host.Client.BaseAddress = new Uri("https://localhost");
            await host.WithDatabaseAsync(async db =>
            {
                db.Administrators.Add(new Administrator(host.AdministratorId, "operator", InitialHash, host.Clock.UtcNow));
                await db.SaveChangesAsync();
            });
            host.DatabaseResolutions = 0;
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public async Task<string> IssueCookieAsync(IEnumerable<Claim>? claims = null)
    {
        claims ??=
        [
            new Claim(ClaimTypes.NameIdentifier, AdministratorId.ToString("D")),
            new Claim(ClaimTypes.Name, "operator"),
            new Claim(AdminCredentialStamp.ClaimType, AdminCredentialStamp.Create(InitialHash))
        ];
        _principal = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
        using var response = await Client.GetAsync("/issue");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        return ReadCookie(response);
    }

    public async Task<HttpResponseMessage> ProbeAsync(string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Admin/Probe");
        request.Headers.Add("Cookie", cookie);
        return await Client.SendAsync(request);
    }

    public async Task WithDatabaseAsync(Func<LicensePlatformDbContext, Task> action)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<LicensePlatformDbContext>());
    }

    public static string ReadCookie(HttpResponseMessage response)
    {
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            value => value.StartsWith(CookieName + "=", StringComparison.Ordinal));
        return setCookie.Split(';', 2)[0];
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }
}
