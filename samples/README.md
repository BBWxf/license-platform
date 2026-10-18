# Windows 客户端示例

## WinForms / WPF 快速使用

- `Net48.WinForms`：.NET Framework 4.8，Windows Forms。
- `Net8.Wpf`：.NET 8 Windows Desktop，WPF。
- `Net6.Console`：旧命令行示例，.NET 6 已结束支持，不建议新项目采用；本次桌面配置导入不适用于此示例。

安装仓库要求的 .NET SDK 10，以及目标应用所需的运行时/开发组件。可在支持该 SDK 的 Visual Studio 中打开 `LicensePlatform.sln`，把相应桌面示例设为启动项目；也可在仓库根目录构建：

```powershell
dotnet build samples/Net48.WinForms/Net48.WinForms.csproj -c Release
dotnet build samples/Net8.Wpf/Net8.Wpf.csproj -c Release
```

构建后运行：

- `samples/Net48.WinForms/bin/Release/net48/Net48.WinForms.exe`
- `samples/Net8.Wpf/bin/Release/net8.0-windows/Net8.Wpf.exe`（需要 .NET 8 Desktop Runtime）

### 操作顺序

1. 后台“产品”中下载该产品的**客户端配置 JSON**；在同一产品下生成一张测试卡密。
2. 打开示例 → **导入客户端配置…** → 选择 JSON（不是卡密 CSV）。
3. 核对产品公开编号，修正服务根地址中的占位符，点击**应用配置**。根地址例如 `https://license.example.com`，不要带 `/Admin` 或 `/api`。
4. 未激活时，在密码框输入测试卡密 → **激活 / 验证**。
5. 激活后卡密输入框自动清空；以后直接留空点击同一按钮，验证当前用户的已有激活状态。

也可将可信客户端配置命名为 `client-config.json` 放到 EXE 同目录，程序启动时自动读取并尝试应用。导入/修改配置后必须重新“应用配置”；未应用前验证按钮禁用。缺配置、坏文件、HTTP 被禁止、正在请求、取消及验证结果均显示在窗口下方。

**安全提示：**正式使用必须 HTTPS。HTTP 默认禁止，只有已认证 VPN / 受控隧道内联调才另外勾选明文风险确认；勾选不提供加密。不关闭证书验证，不通过不可信网络自动替换公钥。完整卡密仅在密码框输入，不放在命令行、环境变量、配置文件或日志中。成功显示的时间是本次租约截止时间，**不是卡密总到期时间**。

### 取消与关闭

请求期间禁止重复操作，可以点“取消请求”。取消是合作式的，程序会等待当前操作真正结束后才释放资源，不能用强行释放客户端来制造“已取消”。网络请求默认超时 15 秒；本机 DPAPI 存储可能先等待另一实例的锁，等待不占 UI 线程，也支持取消。关闭窗口会先取消并等待在途任务退出。不要删除安装身份来处理普通网络错误。

两个示例保留原来的存储用途名 `Net48.WinForms.Sample` / `Net8.Wpf.Sample`，避免升级示例时改变已有安装身份。自行集成时应使用自己的稳定用途名，而非与示例或其他产品共用。

## 共享代码与接入边界

`Shared/SampleClientConfiguration.cs` 负责现有后台 JSON / PEM 的公开配置适配；`SampleLicenseOperation.cs` 负责单次在途任务、工作线程边界、取消与关闭；`SampleLicenseFeedback.cs` 负责不包含敏感异常原文的中文反馈。源码链接到两个 GUI 项目，不改变核心 SDK API。

这些文件是教学示例，不是生产授权 UI 或应用业务门禁。实际应用必须以 `LicenseResult.Success` 决定是否开放受保护功能，不能仅因为点过按钮或显示过成功就永久放行；运行中仍要按策略调用 `GetLeaseAsync()`。不要复制 `async void` 事件处理到非 UI 业务接口，也不要在 UI 线程用 `.Result` / `.Wait()` 阻塞。一个 `await` 不保证 SDK 同步前置工作会让出 UI，参照共享操作辅助类处理。

完整协议、SDK 配置、公钥更新、离线边界与 Windows DPAPI 说明见 [客户端接入](../docs/client-integration.md)；实际验证记录和未执行项见 [开发验证](../docs/verification.md)。
