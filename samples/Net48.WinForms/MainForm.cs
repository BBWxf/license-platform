using System;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Forms;
using Licensing.Client;
using Licensing.Client.Windows;
using Licensing.Samples;

namespace Net48.WinForms.Sample
{
    public sealed class MainForm : Form
    {
        private readonly Button _import = new Button { Text = "导入客户端配置…", AutoSize = true };
        private readonly Button _apply = new Button { Text = "应用配置", AutoSize = true };
        private readonly TextBox _source = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
        private readonly TextBox _product = new TextBox { Dock = DockStyle.Fill, ReadOnly = true };
        private readonly TextBox _serverUrl = new TextBox { Dock = DockStyle.Fill };
        private readonly CheckBox _allowHttp = new CheckBox { Text = "仅受控联调：显式允许明文 HTTP（不安全）", AutoSize = true };
        private readonly TextBox _licenseKey = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly Button _verify = new Button { Text = "激活 / 验证", AutoSize = true };
        private readonly Button _cancel = new Button { Text = "取消请求", AutoSize = true };
        private readonly TextBox _status = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = SystemColors.Window
        };
        private readonly SampleLicenseOperation _operation = new SampleLicenseOperation();
        private SampleClientConfiguration? _configuration;
        private HttpClientHandler? _httpHandler;
        private LicenseClient? _client;
        private bool _configurationDirty = true;
        private bool _updatingFields;
        private bool _busy;
        private bool _closing;
        private bool _allowClose;

        public MainForm()
        {
            Text = ".NET Framework 4.8 卡密授权示例";
            ClientSize = new Size(820, 500);
            MinimumSize = new Size(720, 480);
            StartPosition = FormStartPosition.CenterScreen;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(16),
                ColumnCount = 2,
                RowCount = 9
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < 8; row++)
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var configurationButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            configurationButtons.Controls.AddRange(new Control[] { _import, _apply });
            layout.Controls.Add(configurationButtons, 0, 0);
            layout.SetColumnSpan(configurationButtons, 2);
            AddField(layout, "配置来源", _source, 1);
            AddField(layout, "产品公开编号", _product, 2);
            AddField(layout, "服务根地址", _serverUrl, 3);
            layout.Controls.Add(_allowHttp, 0, 4);
            layout.SetColumnSpan(_allowHttp, 2);
            var warning = new Label
            {
                Text = "正式使用必须 HTTPS。HTTP 会暴露卡密和授权数据，只能在已认证 VPN / 受控隧道内联调；勾选不会提供加密保护。",
                AutoSize = true,
                Dock = DockStyle.Fill,
                ForeColor = Color.DarkRed,
                Margin = new Padding(3, 8, 3, 10)
            };
            layout.Controls.Add(warning, 0, 5);
            layout.SetColumnSpan(warning, 2);
            AddField(layout, "首次激活卡密", _licenseKey, 6);
            var operationButtons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            operationButtons.Controls.AddRange(new Control[] { _verify, _cancel });
            layout.Controls.Add(operationButtons, 0, 7);
            layout.SetColumnSpan(operationButtons, 2);
            layout.Controls.Add(_status, 0, 8);
            layout.SetColumnSpan(_status, 2);
            Controls.Add(layout);

            _import.Click += (_, __) => ImportConfiguration();
            _apply.Click += async (_, __) => await ApplyConfigurationAsync();
            _verify.Click += async (_, __) => await VerifyAsync();
            _cancel.Click += (_, __) =>
            {
                _operation.Cancel();
                _cancel.Enabled = false;
                SetStatus("正在取消，请等待当前操作结束；不会在请求尚未结束时释放资源。");
            };
            _serverUrl.TextChanged += (_, __) => MarkConfigurationDirty();
            _allowHttp.CheckedChanged += (_, __) => MarkConfigurationDirty();
            Shown += async (_, __) => await LoadInitialConfigurationAsync();
            FormClosing += MainFormClosing;
            SetStatus("尚未配置。请先在后台“产品”中下载“客户端配置”，在此导入、核对服务地址并应用；不要导入卡密 CSV 或私钥。");
            UpdateControls();
        }

        private static void AddField(TableLayoutPanel layout, string caption, Control control, int row)
        {
            layout.Controls.Add(new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            layout.Controls.Add(control, 1, row);
        }

        private async Task LoadInitialConfigurationAsync()
        {
            try
            {
                var configuration = SampleClientConfiguration.LoadInitial(
                    AppDomain.CurrentDomain.BaseDirectory, Environment.GetEnvironmentVariable);
                if (configuration == null)
                    return;
                var defaultPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client-config.json");
                SetConfiguration(configuration, File.Exists(defaultPath) ? defaultPath : configuration.Source);
                await ApplyConfigurationAsync();
            }
            catch (SampleConfigurationException ex)
            {
                SetStatus(ex.Message + "\r\n请导入正确的产品配置；不会自动回退到另一份配置。");
            }
            catch (Exception)
            {
                SetStatus("读取初始配置失败，请使用“导入客户端配置”选择可信的产品配置文件。");
            }
        }

        private void ImportConfiguration()
        {
            if (_busy || _closing)
                return;
            using (var dialog = new OpenFileDialog
            {
                Title = "选择后台下载的产品客户端配置（不是卡密 CSV）",
                Filter = "客户端配置 (*.json)|*.json",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                try
                {
                    var configuration = SampleClientConfiguration.LoadFile(dialog.FileName);
                    SetConfiguration(configuration, dialog.FileName);
                }
                catch (SampleConfigurationException ex)
                {
                    RejectConfiguration(ex.Message);
                }
                catch (Exception)
                {
                    RejectConfiguration("无法导入配置，请检查文件和权限。不要选择卡密 CSV 或私钥文件。");
                }
            }
        }

        private void SetConfiguration(SampleClientConfiguration configuration, string source)
        {
            _configuration = configuration;
            _updatingFields = true;
            try
            {
                _source.Text = source;
                _product.Text = configuration.ProductCode;
                _serverUrl.Text = configuration.ServerBaseUrl;
                _allowHttp.Checked = false;
            }
            finally
            {
                _updatingFields = false;
            }
            MarkConfigurationDirty();
        }

        private void RejectConfiguration(string message)
        {
            _configuration = null;
            _configurationDirty = true;
            _licenseKey.Clear();
            _source.Clear();
            _product.Clear();
            SetStatus(message + "\r\n配置未应用，请重新选择可信的配置文件。");
            UpdateControls();
        }

        private void MarkConfigurationDirty()
        {
            if (_updatingFields || _closing)
                return;
            _configurationDirty = true;
            _licenseKey.Clear();
            SetStatus("配置尚未应用。请核对服务根地址（不要带 /Admin 或 /api），然后点击“应用配置”。HTTP 默认禁止。");
            UpdateControls();
        }

        private async Task ApplyConfigurationAsync()
        {
            if (_busy || _closing || _configuration == null)
                return;
            HttpClientHandler? nextHandler = null;
            LicenseClient? nextClient = null;
            try
            {
                var options = _configuration.CreateOptions(_serverUrl.Text, _allowHttp.Checked);
                nextHandler = new HttpClientHandler();
                nextClient = WindowsLicenseClientFactory.Create(
                    nextHandler, options, new WindowsStorageOptions("Net48.WinForms.Sample"));
                ReleaseClient();
                _httpHandler = nextHandler;
                _client = nextClient;
                nextHandler = null;
                nextClient = null;
                _configurationDirty = false;
                SetStatus("配置已应用，正在检查此 Windows 用户的受保护激活状态…");
                UpdateControls();
                await VerifyAsync();
            }
            catch (SampleConfigurationException ex)
            {
                _configurationDirty = true;
                SetStatus(ex.Message + "\r\n请修正地址或配置后再次应用。");
            }
            catch (Exception)
            {
                _configurationDirty = true;
                SetStatus("无法创建授权客户端，请检查配置、系统权限和受保护存储。未放行授权；不会显示敏感异常内容。");
            }
            finally
            {
                nextClient?.Dispose();
                nextHandler?.Dispose();
                if (!_closing)
                    UpdateControls();
            }
        }

        private async Task VerifyAsync()
        {
            if (_busy || _closing)
                return;
            if (_client == null || _configurationDirty)
            {
                SetStatus("请先导入并应用客户端配置，再进行激活或验证。");
                return;
            }

            var client = _client;
            var key = _licenseKey.Text.Trim();
            _licenseKey.Clear();
            _busy = true;
            UpdateControls();
            SetStatus("正在验证，请稍候…网络请求超时为 15 秒，本机存储等待也可通过“取消请求”中止。");
            _status.Refresh();
            var focusLicenseKey = false;
            try
            {
                var result = await _operation.RunAsync(client, key);
                if (!_closing)
                {
                    SetStatus(SampleLicenseFeedback.Describe(result));
                    focusLicenseKey = result.Error?.Code == LicenseErrorCode.ActivationRequired;
                }
            }
            catch (Exception)
            {
                if (!_closing)
                    SetStatus("本次操作未完成，请检查配置后重试。未放行授权；不会显示卡密或原始异常。");
            }
            finally
            {
                key = string.Empty;
                _busy = false;
                if (!_closing)
                {
                    UpdateControls();
                    if (focusLicenseKey)
                        _licenseKey.Focus();
                }
            }
        }

        private void SetStatus(string text)
        {
            if (!_closing && !IsDisposed)
                _status.Text = text;
        }

        private void UpdateControls()
        {
            if (_closing || IsDisposed)
                return;
            _import.Enabled = !_busy;
            _apply.Enabled = !_busy && _configuration != null;
            _serverUrl.Enabled = !_busy && _configuration != null;
            _allowHttp.Enabled = !_busy && _configuration != null;
            _verify.Enabled = !_busy && !_configurationDirty && _client != null;
            _licenseKey.Enabled = _verify.Enabled;
            _cancel.Enabled = _busy;
            _verify.Text = _busy ? "正在验证…" : "激活 / 验证";
        }

        private async void MainFormClosing(object? sender, FormClosingEventArgs e)
        {
            if (_allowClose)
                return;
            e.Cancel = true;
            if (_closing)
                return;
            SetStatus("正在取消请求并安全关闭…");
            _closing = true;
            _import.Enabled = _apply.Enabled = _verify.Enabled = _cancel.Enabled = false;
            await CloseResourcesAsync();
            _allowClose = true;
            if (!IsDisposed && IsHandleCreated)
            {
                // Idle cleanup can finish synchronously; do not reenter the current closing event.
                BeginInvoke(new Action(() =>
                {
                    if (!IsDisposed)
                        Close();
                }));
            }
        }

        private async Task CloseResourcesAsync()
        {
            try
            {
                await _operation.CloseAsync(ReleaseClient).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Teardown must not expose configuration or authentication state.
            }
        }

        private void ReleaseClient()
        {
            var client = _client;
            var handler = _httpHandler;
            _client = null;
            _httpHandler = null;
            try
            {
                client?.Dispose();
            }
            finally
            {
                handler?.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closing)
            {
                _closing = true;
                _ = CloseResourcesAsync();
            }
            base.Dispose(disposing);
        }
    }
}
