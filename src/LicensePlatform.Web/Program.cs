using System.Net;
using System.Threading.RateLimiting;
using LicensePlatform.Application.Abstractions;
using LicensePlatform.Contracts.Challenges;
using LicensePlatform.Infrastructure.Cryptography;
using LicensePlatform.Infrastructure.DependencyInjection;
using LicensePlatform.Infrastructure.Persistence;
using LicensePlatform.Web.CommandLine;
using LicensePlatform.Web.Health;
using LicensePlatform.Web.Middleware;
using LicensePlatform.Web.Security;
using LicensePlatform.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

var commandParseResult = WebCommandLine.Parse(args);
if (!commandParseResult.Succeeded)
{
    Console.Error.WriteLine(commandParseResult.Error);
    Console.Error.WriteLine(WebCommandLine.Usage);
    return 2;
}

var command = commandParseResult.Command!;
var builder = WebApplication.CreateBuilder(command.RemainingArguments);
var configuration = builder.Configuration;
var allowInsecureHttp = configuration.GetValue<bool>("Security:AllowInsecureHttp");
var hstsEnabled = configuration.GetValue<bool>("Security:HstsEnabled");
if (allowInsecureHttp && hstsEnabled)
{
    throw new InvalidOperationException("HSTS cannot be enabled while insecure HTTP mode is active.");
}

var connectionString = configuration.GetConnectionString("Database");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Database is required.");
}

var cardLookupKey = new Lazy<string>(() => ConfiguredSecretReader.ReadBase64Secret(
    configuration,
    builder.Environment.ContentRootPath,
    "Security:CardLookupKeyFile",
    "Cryptography:LicenseKeyHmacKeyBase64",
    minimumDecodedBytes: 32));
var masterEncryptionKey = new Lazy<string>(() => ConfiguredSecretReader.ReadBase64Secret(
    configuration,
    builder.Environment.ContentRootPath,
    "Security:MasterEncryptionKeyFile",
    "Cryptography:DataProtectionKeyBase64",
    minimumDecodedBytes: 32));

builder.Services.AddLicensePlatformInfrastructure(connectionString, options =>
{
    options.LicenseKeyHmacKeyBase64 = cardLookupKey.Value;
    var configuredLookupKey = configuration["Cryptography:LookupHmacKeyBase64"];
    options.LookupHmacKeyBase64 = string.IsNullOrWhiteSpace(configuredLookupKey)
        ? cardLookupKey.Value
        : configuredLookupKey;
    options.DataProtectionKeyBase64 = masterEncryptionKey.Value;
});

var dataProtectionPath = ConfiguredSecretReader.ResolvePath(
    builder.Environment.ContentRootPath,
    configuration["Security:DataProtectionPath"]
        ?? throw new InvalidOperationException("Security:DataProtectionPath is required."));
builder.Services
    .AddDataProtection()
    .SetApplicationName("LicensePlatform.Web")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));

builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IAdminPasswordHasher, AdminPasswordHasher>();
builder.Services.AddScoped<IAdminAuthenticationService, AdminAuthenticationService>();
builder.Services.AddScoped<IAdministratorBootstrapService, AdministratorBootstrapService>();
builder.Services.AddScoped<IAdminDashboardQuery, AdminDashboardQuery>();
builder.Services.AddScoped<IProductAdminService, ProductAdminService>();
builder.Services.AddScoped<ILicenseBatchAdminService, LicenseBatchAdminService>();
builder.Services.AddScoped<ILicenseAdminService, LicenseAdminService>();
builder.Services.AddScoped<IAuditAdminService, AuditAdminService>();
builder.Services.AddScoped<ISystemSettingsService, SystemSettingsService>();
builder.Services.AddHostedService<AdministratorBootstrapHostedService>();

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["requestId"] = context.HttpContext.TraceIdentifier;
    };
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => AdminCookieAuthentication.Configure(options, configuration, allowInsecureHttp));
builder.Services.AddAuthorization();
builder.Services.Configure<CookieTempDataProviderOptions>(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = allowInsecureHttp
        ? CookieSecurePolicy.None
        : CookieSecurePolicy.Always;
});
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = allowInsecureHttp
        ? CookieSecurePolicy.None
        : CookieSecurePolicy.Always;
});
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
});

builder.Services.PostConfigure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.RequireHeaderSymmetry = true;

    var forwardedHeadersEnabled = configuration.GetValue<bool>("ASPNETCORE_FORWARDEDHEADERS_ENABLED");
    if (forwardedHeadersEnabled)
    {
        // The application is not published directly by the supplied deployment. Restrict
        // forwarded headers to loopback and private proxy networks instead of trusting all peers.
        options.KnownProxies.Add(IPAddress.Loopback);
        options.KnownProxies.Add(IPAddress.IPv6Loopback);
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("10.0.0.0/8"));
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("172.16.0.0/12"));
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("192.168.0.0/16"));
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse("fc00::/7"));
    }

    foreach (var value in configuration.GetSection("Security:KnownProxies").Get<string[]>() ?? [])
    {
        if (!IPAddress.TryParse(value, out var address))
        {
            throw new InvalidOperationException("Security:KnownProxies contains an invalid IP address.");
        }

        options.KnownProxies.Add(address);
    }

    foreach (var value in configuration.GetSection("Security:KnownProxyNetworks").Get<string[]>() ?? [])
    {
        try
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(value));
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "Security:KnownProxyNetworks contains an invalid CIDR network.",
                exception);
        }
    }
});

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(
        PositiveSetting(configuration, "Security:HstsMaxAgeDays", 30));
    options.IncludeSubDomains = configuration.GetValue<bool>("Security:HstsIncludeSubDomains");
});
builder.Services.AddHttpsRedirection(options =>
{
    options.HttpsPort = configuration.GetValue<int?>("Security:HttpsPort") ?? 443;
    options.RedirectStatusCode = StatusCodes.Status308PermanentRedirect;
});

var loginLimit = PositiveSetting(configuration, "RateLimits:LoginAttemptsPerMinute", 5);
var challengeLimit = PositiveSetting(configuration, "RateLimits:ChallengesPerMinutePerIp", 60);
var clientLimit = PositiveSetting(configuration, "RateLimits:ClientRequestsPerMinutePerIp", 120);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        await ApiProblemResults.WriteAsync(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            "rate_limit_exceeded",
            "请求过于频繁，请稍后重试。",
            cancellationToken);
    };
    options.AddPolicy("login", context => HttpMethods.IsPost(context.Request.Method)
        ? FixedWindow(context, loginLimit)
        : RateLimitPartition.GetNoLimiter("login-read"));
    options.AddPolicy("client-challenges", context => FixedWindow(context, challengeLimit));
    options.AddPolicy("client-validations", context => FixedWindow(context, clientLimit));
});

builder.Services.AddSingleton<ClientRequestReplayCache>();
builder.Services.AddScoped<IClientRequestPublicKeyResolver, ClientRequestPublicKeyResolver>();
builder.Services.AddScoped<ClientRequestSignatureVerifier>();
builder.Services.AddScoped<ClientRequestSignatureFilter>();
builder.Services
    .AddHealthChecks()
    .AddCheck<LicensePlatformHealthCheck>(
        "license-platform",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

var app = builder.Build();

if (command.Kind != WebCommandKind.Run)
{
    return await ExecuteCommandAsync(app.Services, command);
}

app.UseForwardedHeaders();
app.UseMiddleware<RequestCorrelationMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
if (!allowInsecureHttp && hstsEnabled)
{
    app.UseHsts();
}

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
}
else
{
    app.UseExceptionHandler("/Error");
}

if (!allowInsecureHttp)
{
    app.UseHttpsRedirection();
}

app.UseRouting();
app.UseMiddleware<ClientApiExceptionHandlingMiddleware>();
app.UseMiddleware<ClientApiRequestBufferingMiddleware>();
app.UseStatusCodePages(async statusCodeContext =>
{
    var httpContext = statusCodeContext.HttpContext;
    if (httpContext.Request.Path.StartsWithSegments(ClientApiPaths.Prefix)
        && !httpContext.Response.HasStarted
        && httpContext.Response.ContentLength is null)
    {
        var statusCode = httpContext.Response.StatusCode;
        await ApiProblemResults.WriteAsync(
            httpContext,
            statusCode,
            "http_error",
            statusCode == StatusCodes.Status404NotFound
                ? "请求的资源不存在。"
                : "请求无法处理。",
            httpContext.RequestAborted);
    }
});
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    AllowCachingResponses = false,
    ResponseWriter = static async (context, report) =>
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(
            report.Status == HealthStatus.Healthy
                ? "{\"status\":\"healthy\"}"
                : "{\"status\":\"unhealthy\"}",
            context.RequestAborted);
    }
}).AllowAnonymous().DisableRateLimiting();

var clientApi = app.MapGroup(ClientApiPaths.Prefix)
    .AllowAnonymous()
    .DisableAntiforgery()
    .AddEndpointFilter<ClientRequestSignatureFilter>();

clientApi.MapPost("/challenges/activation", static async (
        CreateActivationChallengeRequest request,
        IActivationApplicationService service,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.CreateActivationChallengeAsync(request, cancellationToken)))
    .RequireRateLimiting("client-challenges");

clientApi.MapPost("/challenges/refresh", static async (
        CreateLeaseRefreshChallengeRequest request,
        IActivationApplicationService service,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.CreateRefreshChallengeAsync(request, cancellationToken)))
    .RequireRateLimiting("client-challenges");

clientApi.MapPost("/challenges/complete", static async (
        CompleteActivationChallengeRequest request,
        IActivationApplicationService service,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.CompleteChallengeAsync(request, cancellationToken)))
    .RequireRateLimiting("client-challenges");

clientApi.MapPost("/validations", static async (
        ValidateLicenseRequest request,
        IActivationApplicationService service,
        CancellationToken cancellationToken) =>
        TypedResults.Ok(await service.ValidateAsync(request, cancellationToken)))
    .RequireRateLimiting("client-validations");

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();

await app.RunAsync();
return 0;

static RateLimitPartition<string> FixedWindow(HttpContext context, int permitLimit)
{
    var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
    {
        AutoReplenishment = true,
        PermitLimit = permitLimit,
        QueueLimit = 0,
        Window = TimeSpan.FromMinutes(1)
    });
}

static int PositiveSetting(IConfiguration configuration, string key, int defaultValue)
{
    var value = configuration.GetValue<int?>(key) ?? defaultValue;
    if (value < 1)
    {
        throw new InvalidOperationException($"{key} must be greater than zero.");
    }

    return value;
}

static async Task<int> ExecuteCommandAsync(IServiceProvider services, WebCommand command)
{
    try
    {
        await using var scope = services.CreateAsyncScope();
        switch (command.Kind)
        {
            case WebCommandKind.Migrate:
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<LicensePlatformDbContext>();
                    await dbContext.Database.MigrateAsync();
                    Console.WriteLine("Database migration completed.");
                    return 0;
                }
            case WebCommandKind.AdminInit:
            case WebCommandKind.AdminReset:
                {
                    var password = command.PasswordFile is null
                        ? ConsoleSecretReader.ReadPassword("Administrator password: ")
                        : await ConfiguredSecretReader.ReadPasswordAsync(command.PasswordFile);
                    var bootstrap = scope.ServiceProvider.GetRequiredService<IAdministratorBootstrapService>();
                    var changed = command.Kind == WebCommandKind.AdminInit
                        ? await bootstrap.InitializeAsync(command.UserName!, password, CancellationToken.None)
                        : await bootstrap.ResetPasswordAsync(command.UserName!, password, CancellationToken.None);
                    if (!changed)
                    {
                        Console.Error.WriteLine(command.Kind == WebCommandKind.AdminInit
                            ? "Administrator initialization was not performed because an administrator already exists."
                            : "Administrator password reset was not performed because the account was not found.");
                        return 1;
                    }

                    Console.WriteLine(command.Kind == WebCommandKind.AdminInit
                        ? "Administrator initialized."
                        : "Administrator password reset.");
                    return 0;
                }
            default:
                return 2;
        }
    }
    catch
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("LicensePlatform.CommandLine");
        // Do not log exception payloads: password-file decoding and database failures may contain secrets.
        logger.LogError("The requested command failed.");
        Console.Error.WriteLine("The requested command failed. Review the application log for details.");
        return 1;
    }
}

public partial class Program
{
}
