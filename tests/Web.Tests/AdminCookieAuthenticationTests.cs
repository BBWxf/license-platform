using System.Net;
using System.Security.Claims;
using LicensePlatform.Application.Security;
using LicensePlatform.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LicensePlatform.Web.Tests;

public sealed class AdminCookieAuthenticationTests
{
    [Theory]
    [InlineData(false, CookieSecurePolicy.Always)]
    [InlineData(true, CookieSecurePolicy.None)]
    public void Production_cookie_options_preserve_security_and_expiration_settings(bool insecureHttp, CookieSecurePolicy securePolicy)
    {
        var options = new CookieAuthenticationOptions();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:CookieName"] = "Custom.Admin"
        }).Build();

        AdminCookieAuthentication.Configure(options, configuration, insecureHttp);

        Assert.Equal("Custom.Admin", options.Cookie.Name);
        Assert.True(options.Cookie.HttpOnly);
        Assert.True(options.Cookie.IsEssential);
        Assert.Equal(SameSiteMode.Strict, options.Cookie.SameSite);
        Assert.Equal(securePolicy, options.Cookie.SecurePolicy);
        Assert.Equal(TimeSpan.FromMinutes(30), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
        Assert.Equal("/Account/Login", options.LoginPath.Value);
        Assert.Equal("/Account/Login", options.AccessDeniedPath.Value);
        Assert.NotNull(options.Events.OnValidatePrincipal);
    }

    [Fact]
    public async Task Current_credential_stamp_is_accepted_without_early_renewal()
    {
        await using var host = await AdminCookieTestHost.StartAsync();
        var cookie = await host.IssueCookieAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(10));

        using var response = await host.ProbeAsync(cookie);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("authorized", await response.Content.ReadAsStringAsync());
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal(1, host.ProtectedCalls);
        Assert.Equal(1, host.DatabaseResolutions);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("deleted")]
    [InlineData("unavailable")]
    public async Task Credential_revocation_rejects_and_deletes_the_cookie_even_when_sliding_renewal_is_due(string reason)
    {
        await using var host = await AdminCookieTestHost.StartAsync();
        var cookie = await host.IssueCookieAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(16));
        const string canary = "synthetic-database-credential-canary";
        switch (reason)
        {
            case "reset":
                await host.WithDatabaseAsync(async db =>
                {
                    var administrator = await db.Administrators.SingleAsync();
                    administrator.ChangePassword("synthetic-password-hash-after-reset", host.Clock.UtcNow);
                    await db.SaveChangesAsync();
                });
                break;
            case "deleted":
                await host.WithDatabaseAsync(async db =>
                {
                    db.Administrators.Remove(await db.Administrators.SingleAsync());
                    await db.SaveChangesAsync();
                });
                break;
            case "unavailable":
                host.DatabaseFailure = new InvalidOperationException(canary, new IOException(canary));
                break;
        }

        using var response = await host.ProbeAsync(cookie);

        AssertRejected(response);
        Assert.Equal(0, host.ProtectedCalls);
        Assert.Equal(AdminCookieTestHost.CookieName + "=", AdminCookieTestHost.ReadCookie(response));
        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("expires=", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(canary, await response.Content.ReadAsStringAsync());
        foreach (var log in host.Logs.Entries)
        {
            Assert.DoesNotContain(canary, log.Message);
            Assert.DoesNotContain(canary, log.Exception?.ToString() ?? string.Empty);
            Assert.DoesNotContain(cookie, log.Message);
            Assert.DoesNotContain(AdminCookieTestHost.InitialHash, log.Message);
        }

        if (reason == "unavailable")
        {
            var log = Assert.Single(host.Logs.Entries, entry => entry.Category == "AdminCookieValidation");
            Assert.Equal(LogLevel.Error, log.Level);
            Assert.Null(log.Exception);
        }
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("malformed-id")]
    [InlineData("noncanonical-id")]
    [InlineData("missing-stamp")]
    [InlineData("empty-stamp")]
    [InlineData("mismatched-stamp")]
    public async Task Missing_malformed_or_mismatched_identity_claims_are_rejected(string invalidClaim)
    {
        await using var host = await AdminCookieTestHost.StartAsync();
        var id = invalidClaim switch
        {
            "malformed-id" => "not-a-guid",
            "noncanonical-id" => host.AdministratorId.ToString("N"),
            _ => host.AdministratorId.ToString("D")
        };
        var claims = new List<Claim>();
        if (invalidClaim != "missing-id")
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, id));
        }

        if (invalidClaim != "missing-stamp")
        {
            claims.Add(new Claim(AdminCredentialStamp.ClaimType, invalidClaim switch
            {
                "empty-stamp" => " ",
                "mismatched-stamp" => new string('0', 64),
                _ => AdminCredentialStamp.Create(AdminCookieTestHost.InitialHash)
            }));
        }

        var cookie = await host.IssueCookieAsync(claims);
        using var response = await host.ProbeAsync(cookie);

        AssertRejected(response);
        Assert.Equal(AdminCookieTestHost.CookieName + "=", AdminCookieTestHost.ReadCookie(response));
        Assert.Equal(0, host.ProtectedCalls);
        Assert.Equal(invalidClaim == "mismatched-stamp" ? 1 : 0, host.DatabaseResolutions);
    }

    [Fact]
    public async Task Sliding_renewal_extends_expiration_but_the_renewed_cookie_is_still_revocable()
    {
        await using var host = await AdminCookieTestHost.StartAsync();
        var originalCookie = await host.IssueCookieAsync();
        host.Clock.Advance(TimeSpan.FromMinutes(16));
        using var renewal = await host.ProbeAsync(originalCookie);
        Assert.Equal(HttpStatusCode.OK, renewal.StatusCode);
        var renewedCookie = AdminCookieTestHost.ReadCookie(renewal);
        Assert.NotEqual(originalCookie, renewedCookie);

        host.Clock.Advance(TimeSpan.FromMinutes(15));
        using var expiredOriginal = await host.ProbeAsync(originalCookie);
        AssertRejected(expiredOriginal);
        using var current = await host.ProbeAsync(renewedCookie);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(2, host.ProtectedCalls);

        await host.WithDatabaseAsync(async db =>
        {
            (await db.Administrators.SingleAsync()).ChangePassword("synthetic-new-hash", host.Clock.UtcNow);
            await db.SaveChangesAsync();
        });
        using var revokedRenewal = await host.ProbeAsync(renewedCookie);
        AssertRejected(revokedRenewal);
        Assert.Equal(AdminCookieTestHost.CookieName + "=", AdminCookieTestHost.ReadCookie(revokedRenewal));
        Assert.Equal(2, host.ProtectedCalls);
    }

    [Fact]
    public async Task Request_cancellation_is_propagated_without_treating_it_as_a_credential_store_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var logs = new CapturingLoggerProvider();
        using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(logs))
            .AddScoped<LicensePlatform.Infrastructure.Persistence.LicensePlatformDbContext>(_ =>
                throw new OperationCanceledException(cancellation.Token))
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            RequestAborted = cancellation.Token
        };
        var options = new CookieAuthenticationOptions();
        AdminCookieAuthentication.Configure(options, new ConfigurationBuilder().Build(), allowInsecureHttp: false);
        var scheme = new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString("D")),
            new Claim(AdminCredentialStamp.ClaimType, AdminCredentialStamp.Create(AdminCookieTestHost.InitialHash))
        ], scheme.Name));
        var validation = new CookieValidatePrincipalContext(httpContext, scheme, options,
            new AuthenticationTicket(principal, scheme.Name));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => options.Events.ValidatePrincipal(validation));

        Assert.Same(principal, validation.Principal);
        Assert.Empty(logs.Entries);
        Assert.False(httpContext.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public async Task Unreadable_cookie_is_rejected_without_reading_credential_state()
    {
        await using var host = await AdminCookieTestHost.StartAsync();

        using var response = await host.ProbeAsync(AdminCookieTestHost.CookieName + "=not-a-protected-ticket");

        AssertRejected(response);
        Assert.Equal(0, host.DatabaseResolutions);
        Assert.Equal(0, host.ProtectedCalls);
    }

    private static void AssertRejected(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", response.Headers.Location!.AbsolutePath);
    }
}
