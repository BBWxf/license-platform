using Licensing.Client;
using Licensing.Client.Windows;

var baseUrl = Environment.GetEnvironmentVariable("LICENSING_BASE_URL");
var serverJwk = Environment.GetEnvironmentVariable("LICENSING_SERVER_JWK");
var productCode = Environment.GetEnvironmentVariable("LICENSING_PRODUCT_CODE");
var licenseKey = args.FirstOrDefault() ?? Environment.GetEnvironmentVariable("LICENSE_KEY");

if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serviceUri) || string.IsNullOrWhiteSpace(serverJwk) ||
    string.IsNullOrWhiteSpace(productCode))
{
    Console.Error.WriteLine("Set LICENSING_BASE_URL, LICENSING_SERVER_JWK, and LICENSING_PRODUCT_CODE.");
    return 2;
}

var options = new LicenseClientOptions
{
    ServiceBaseUri = serviceUri,
    ProductCode = productCode,
    DeviceHint = Environment.MachineName,
    ExpectedIssuer = Environment.GetEnvironmentVariable("LICENSING_ISSUER") ?? "license-platform",
    ExpectedAudience = Environment.GetEnvironmentVariable("LICENSING_AUDIENCE"),
    AllowInsecureHttp = string.Equals(Environment.GetEnvironmentVariable("LICENSE_ALLOW_INSECURE_HTTP"), "true", StringComparison.OrdinalIgnoreCase),
    Logger = entry => Console.WriteLine($"[{entry.TimestampUtc:O}] {entry.EventName} ({entry.Code}): {entry.Message}")
};
foreach (var key in ServerJwk.ParseMany(serverJwk))
    options.ServerVerificationKeys.Add(key);

using var httpHandler = new HttpClientHandler();
using var client = WindowsLicenseClientFactory.Create(httpHandler, options, new WindowsStorageOptions("Net6.Console.Sample"));

// Supply the card only for first activation. Later startups use the DPAPI-protected cache
// and never require the application to persist or resubmit the card.
var result = string.IsNullOrWhiteSpace(licenseKey)
    ? await client.StartAsync(CancellationToken.None)
    : await client.StartAsync(licenseKey, CancellationToken.None);
if (!result.Success)
{
    Console.Error.WriteLine($"License denied: {result.Error}");
    return 1;
}

Console.WriteLine($"License {result.Lease!.LicenseId} valid until {result.Lease.ExpiresAtUtc:O}.");
Console.WriteLine(result.UsedOfflineFallback ? "Mode: signed offline fallback" : "Mode: online verified");
return 0;
