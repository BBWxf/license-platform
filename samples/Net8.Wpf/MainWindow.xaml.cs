using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using Licensing.Client;
using Licensing.Client.Windows;
using Licensing.Samples;
using Microsoft.Win32;

namespace Net8.Wpf.Sample;

public partial class MainWindow : Window
{
    private readonly SampleLicenseOperation _operation = new();
    private SampleClientConfiguration? _configuration;
    private HttpClientHandler? _httpHandler;
    private LicenseClient? _client;
    private bool _configurationDirty = true;
    private bool _updatingFields = true;
    private bool _busy;
    private bool _closing;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _updatingFields = false;
        SetStatus("尚未配置。请先在后台“产品”中下载“客户端配置”，在此导入、核对服务地址并应用；不要导入卡密 CSV 或私钥。");
        UpdateControls();
        Loaded += async (_, _) => await LoadInitialConfigurationAsync();
        Closing += MainWindowClosing;
    }

    private async Task LoadInitialConfigurationAsync()
    {
        try
        {
            var configuration = SampleClientConfiguration.LoadInitial(
                AppContext.BaseDirectory, Environment.GetEnvironmentVariable);
            if (configuration == null)
                return;
            var defaultPath = Path.Combine(AppContext.BaseDirectory, "client-config.json");
            SetConfiguration(configuration, File.Exists(defaultPath) ? defaultPath : configuration.Source);
            await ApplyConfigurationAsync();
        }
        catch (SampleConfigurationException ex)
        {
            SetStatus(ex.Message + "\n请导入正确的产品配置；不会自动回退到另一份配置。");
        }
        catch (Exception)
        {
            SetStatus("读取初始配置失败，请使用“导入客户端配置”选择可信的产品配置文件。");
        }
    }

    private void ImportConfiguration_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _closing)
            return;
        var dialog = new OpenFileDialog
        {
            Title = "选择后台下载的产品客户端配置（不是卡密 CSV）",
            Filter = "客户端配置 (*.json)|*.json",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
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

    private void SetConfiguration(SampleClientConfiguration configuration, string source)
    {
        _configuration = configuration;
        _updatingFields = true;
        try
        {
            ConfigurationSource.Text = source;
            ProductCode.Text = configuration.ProductCode;
            ServerUrl.Text = configuration.ServerBaseUrl;
            AllowHttp.IsChecked = false;
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
        LicenseKey.Clear();
        ConfigurationSource.Clear();
        ProductCode.Clear();
        SetStatus(message + "\n配置未应用，请重新选择可信的配置文件。");
        UpdateControls();
    }

    private void ConfigurationChanged(object sender, RoutedEventArgs e) => MarkConfigurationDirty();

    private void MarkConfigurationDirty()
    {
        if (_updatingFields || _closing)
            return;
        _configurationDirty = true;
        LicenseKey.Clear();
        SetStatus("配置尚未应用。请核对服务根地址（不要带 /Admin 或 /api），然后点击“应用配置”。HTTP 默认禁止。");
        UpdateControls();
    }

    private async void ApplyConfiguration_Click(object sender, RoutedEventArgs e) => await ApplyConfigurationAsync();

    private async Task ApplyConfigurationAsync()
    {
        if (_busy || _closing || _configuration == null)
            return;
        HttpClientHandler? nextHandler = null;
        LicenseClient? nextClient = null;
        try
        {
            var options = _configuration.CreateOptions(ServerUrl.Text, AllowHttp.IsChecked == true);
            nextHandler = new HttpClientHandler();
            nextClient = WindowsLicenseClientFactory.Create(
                nextHandler, options, new WindowsStorageOptions("Net8.Wpf.Sample"));
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
            SetStatus(ex.Message + "\n请修正地址或配置后再次应用。");
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

    private async void VerifyButton_Click(object sender, RoutedEventArgs e) => await VerifyAsync();

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
        var key = LicenseKey.Password.Trim();
        LicenseKey.Clear();
        _busy = true;
        UpdateControls();
        SetStatus("正在验证，请稍候…网络请求超时为 15 秒，本机存储等待也可通过“取消请求”中止。");
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
                    LicenseKey.Focus();
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _operation.Cancel();
        CancelButton.IsEnabled = false;
        SetStatus("正在取消，请等待当前操作结束；不会在请求尚未结束时释放资源。");
    }

    private void SetStatus(string text)
    {
        if (!_closing)
            Status.Text = text;
    }

    private void UpdateControls()
    {
        if (_closing)
            return;
        ImportButton.IsEnabled = !_busy;
        ApplyButton.IsEnabled = !_busy && _configuration != null;
        ServerUrl.IsEnabled = !_busy && _configuration != null;
        AllowHttp.IsEnabled = !_busy && _configuration != null;
        VerifyButton.IsEnabled = !_busy && !_configurationDirty && _client != null;
        LicenseKey.IsEnabled = VerifyButton.IsEnabled;
        CancelButton.IsEnabled = _busy;
        VerifyButton.Content = _busy ? "正在验证…" : "激活 / 验证";
    }

    private async void MainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;
        e.Cancel = true;
        if (_closing)
            return;
        SetStatus("正在取消请求并安全关闭…");
        _closing = true;
        ImportButton.IsEnabled = ApplyButton.IsEnabled = VerifyButton.IsEnabled = CancelButton.IsEnabled = false;
        try
        {
            await _operation.CloseAsync(ReleaseClient);
        }
        catch (Exception)
        {
            // Teardown must not expose configuration or authentication state.
        }
        _allowClose = true;
        // Idle cleanup can finish synchronously; let the original Closing event unwind first.
        _ = Dispatcher.BeginInvoke(new Action(Close));
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
}
