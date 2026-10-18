using System;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Licensing.Client;
using Licensing.Client.Windows;

namespace Net48.WinForms.Sample
{
    public sealed class MainForm : Form
    {
        private readonly TextBox _licenseKey = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly Button _verify = new Button { Text = "Activate / Verify", AutoSize = true };
        private readonly Label _status = new Label { Dock = DockStyle.Fill, AutoSize = true, Text = "Enter a license key." };
        private readonly CancellationTokenSource _shutdown = new CancellationTokenSource();
        private readonly HttpClientHandler? _httpHandler;
        private readonly LicenseClient? _client;
        private readonly string? _configurationError;

        public MainForm()
        {
            Text = ".NET Framework 4.8 Licensing Sample";
            ClientSize = new Size(620, 180);
            StartPosition = FormStartPosition.CenterScreen;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 2,
                RowCount = 3,
                AutoSize = true
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = "License key", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            layout.Controls.Add(_licenseKey, 1, 0);
            layout.Controls.Add(_verify, 1, 1);
            layout.Controls.Add(_status, 0, 2);
            layout.SetColumnSpan(_status, 2);
            Controls.Add(layout);

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
                    Logger = entry => System.Diagnostics.Trace.WriteLine(string.Format("[{0:O}] {1} ({2}): {3}", entry.TimestampUtc, entry.EventName, entry.Code, entry.Message))
                };
                foreach (var key in ServerJwk.ParseMany(jwkJson))
                    options.ServerVerificationKeys.Add(key);

                _httpHandler = new HttpClientHandler();
                _client = WindowsLicenseClientFactory.Create(
                    _httpHandler,
                    options,
                    new WindowsStorageOptions("Net48.WinForms.Sample"));
            }
            catch (Exception ex)
            {
                _configurationError = ex.Message;
            }

            _verify.Click += async (_, __) => await VerifyAtStartupAsync().ConfigureAwait(true);
            Shown += async (_, __) =>
            {
                var configuredKey = Environment.GetEnvironmentVariable("LICENSE_KEY");
                if (!string.IsNullOrWhiteSpace(configuredKey))
                {
                    _licenseKey.Text = configuredKey;
                    await VerifyAtStartupAsync().ConfigureAwait(true);
                }
                else if (_configurationError != null)
                {
                    _status.Text = _configurationError;
                }
                else
                {
                    await VerifyCachedAtStartupAsync().ConfigureAwait(true);
                }
            };
        }

        private async Task VerifyCachedAtStartupAsync()
        {
            _verify.Enabled = false;
            _status.Text = "Checking the protected activation cache...";
            try
            {
                var result = await _client!.StartAsync(_shutdown.Token).ConfigureAwait(true);
                _status.Text = result.Success
                    ? string.Format("Licensed through {0:O} ({1}).", result.Lease!.ExpiresAtUtc, result.UsedOfflineFallback ? "signed offline fallback" : "online")
                    : result.Error!.Code == LicenseErrorCode.ActivationRequired
                        ? "Enter the license key once to activate this installation."
                        : "License denied: " + result.Error;
            }
            finally
            {
                _verify.Enabled = true;
            }
        }

        private async Task VerifyAtStartupAsync()
        {
            if (_client == null)
            {
                _status.Text = _configurationError ?? "The client is not configured.";
                return;
            }
            if (string.IsNullOrWhiteSpace(_licenseKey.Text))
            {
                _status.Text = "A license key is required.";
                return;
            }

            _verify.Enabled = false;
            _status.Text = "Contacting licensing service...";
            try
            {
                // StartAsync always prioritizes online verification on process startup.
                var result = await _client.StartAsync(_licenseKey.Text, _shutdown.Token).ConfigureAwait(true);
                if (result.Success)
                    _licenseKey.Clear();
                _status.Text = result.Success
                    ? string.Format("Licensed through {0:O} ({1}).", result.Lease!.ExpiresAtUtc, result.UsedOfflineFallback ? "signed offline fallback" : "online")
                    : "License denied: " + result.Error;
            }
            finally
            {
                _verify.Enabled = true;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _shutdown.Cancel();
                _client?.Dispose();
                _httpHandler?.Dispose();
                _shutdown.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
