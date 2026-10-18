using System.Net.Http;
using System.Windows;
using Licensing.Client;
using Licensing.Client.Windows;

namespace Net8.Wpf.Sample;

public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly HttpClientHandler? _httpHandler;
    private readonly LicenseClient? _client;
    private readonly string? _configurationError;

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var baseUrl = Environment.GetEnvironmentVariable("LICENSING_BASE_URL");
            var jwkJson = Environment.GetEnvironmentVariable("LICENSING_SERVER_JWK");
            var productCode = Environment.GetEnvironmentVariable("LICENSING_PRODUCT_CODE");
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var serviceUri) || string.IsNullOrWhiteSpace(jwkJson) || string.IsNullOrWhiteSpace(productCode))
                throw new InvalidOperationException("Set LICENSING_BASE_URL, LICENSING_SERVER_JWK, and LICENSING_PRODUCT_CODE.");

            var options = new LicenseClientOptions
            {
                ServiceBaseUri = serviceUri,
                ProductCode = productCode,
                DeviceHint = Environment.MachineName,
                ExpectedIssuer = Environment.GetEnvironmentVariable("LICENSING_ISSUER") ?? "license-platform",
                ExpectedAudience = Environment.GetEnvironmentVariable("LICENSING_AUDIENCE"),
                AllowInsecureHttp = string.Equals(Environment.GetEnvironmentVariable("LICENSE_ALLOW_INSECURE_HTTP"), "true", StringComparison.OrdinalIgnoreCase),
                Logger = entry => System.Diagnostics.Trace.WriteLine($"[{entry.TimestampUtc:O}] {entry.EventName} ({entry.Code}): {entry.Message}")
            };
            foreach (var key in ServerJwk.ParseMany(jwkJson))
                options.ServerVerificationKeys.Add(key);

            _httpHandler = new HttpClientHandler();
            _client = WindowsLicenseClientFactory.Create(
                _httpHandler,
                options,
                new WindowsStorageOptions("Net8.Wpf.Sample"));
        }
        catch (Exception ex)
        {
            _configurationError = ex.Message;
        }

        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _shutdown.Cancel();
            _client?.Dispose();
            _httpHandler?.Dispose();
            _shutdown.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var configuredKey = Environment.GetEnvironmentVariable("LICENSE_KEY");
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            LicenseKey.Password = configuredKey;
            await VerifyAsync();
        }
        else if (_configurationError != null)
        {
            Status.Text = _configurationError;
        }
        else
        {
            await VerifyCachedAsync();
        }
    }

    private async Task VerifyCachedAsync()
    {
        VerifyButton.IsEnabled = false;
        Status.Text = "Checking the protected activation cache...";
        try
        {
            var result = await _client!.StartAsync(_shutdown.Token);
            Status.Text = result.Success
                ? $"License {result.Lease!.LicenseId} valid through {result.Lease.ExpiresAtUtc:O} " +
                  $"({(result.UsedOfflineFallback ? "signed offline fallback" : "online")})."
                : result.Error!.Code == LicenseErrorCode.ActivationRequired
                    ? "Enter the license key once to activate this installation."
                    : "License denied: " + result.Error;
        }
        finally
        {
            VerifyButton.IsEnabled = true;
        }
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e) => await VerifyAsync();

    private async Task VerifyAsync()
    {
        if (_client == null)
        {
            Status.Text = _configurationError ?? "The client is not configured.";
            return;
        }
        if (string.IsNullOrWhiteSpace(LicenseKey.Password))
        {
            Status.Text = "A license key is required.";
            return;
        }

        VerifyButton.IsEnabled = false;
        Status.Text = "Contacting licensing service...";
        try
        {
            // StartAsync always attempts online verification first at process startup.
            var result = await _client.StartAsync(LicenseKey.Password, _shutdown.Token);
            if (result.Success)
                LicenseKey.Clear();
            Status.Text = result.Success
                ? $"License {result.Lease!.LicenseId} valid through {result.Lease.ExpiresAtUtc:O} " +
                  $"({(result.UsedOfflineFallback ? "signed offline fallback" : "online")})."
                : "License denied: " + result.Error;
        }
        finally
        {
            VerifyButton.IsEnabled = true;
        }
    }
}
