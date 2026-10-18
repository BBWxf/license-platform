using System.Security.Claims;
using LicensePlatform.Application.Security;
using LicensePlatform.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace LicensePlatform.Web.Security;

public static class AdminCookieAuthentication
{
    public static void Configure(
        CookieAuthenticationOptions options,
        IConfiguration configuration,
        bool allowInsecureHttp)
    {
        options.Cookie.Name = configuration["Security:CookieName"] ?? "LicensePlatform.Admin";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = allowInsecureHttp
            ? CookieSecurePolicy.None
            : CookieSecurePolicy.Always;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
        options.SlidingExpiration = true;
        options.Events.OnValidatePrincipal = ValidatePrincipalAsync;
    }

    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var adminIdValue = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var suppliedStamp = context.Principal?.FindFirst(AdminCredentialStamp.ClaimType)?.Value;
        if (!Guid.TryParseExact(adminIdValue, "D", out var adminId)
            || string.IsNullOrWhiteSpace(suppliedStamp))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }

        string? passwordHash;
        try
        {
            var dbContext = context.HttpContext.RequestServices
                .GetRequiredService<LicensePlatformDbContext>();
            passwordHash = await dbContext.Administrators
                .AsNoTracking()
                .Where(item => item.Id == adminId)
                .Select(item => item.PasswordHash)
                .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var logger = context.HttpContext.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("AdminCookieValidation");
            logger.LogError("Administrator credential state could not be loaded; the cookie was rejected.");
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
            return;
        }

        if (passwordHash is null || !AdminCredentialStamp.Matches(passwordHash, suppliedStamp))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(context.Scheme.Name);
        }
    }
}
