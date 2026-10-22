# 客户端接入

## 1. 接入前提

> **不要把当前公网 `http://<IPv4>` 当作可信许可服务。**
>
> 明文 HTTP 上的账号、激活码、许可证、设备标识、租约和 token 都可能被旁路读取或篡改。若客户端必须在 HTTPS 上线前联调，只能把流量放在已认证的 VPN、专线或等效受控隧道内，并把服务器来源限制到最小范围；这仍不能让公网 HTTP 本身变安全。

正式客户端应以域名 HTTPS 为基地址，并执行正常的系统证书链和主机名验证。不得提供“忽略证书错误”“信任任意证书”或自动降级 HTTP 的生产选项。

## 2. SDK 与当前协议

- 核心库：`Licensing.Client`，目标 `netstandard2.0`；
- Windows 集成：`Licensing.Client.Windows`，目标 `net48`、`net6.0-windows`、`net8.0-windows`；
- 示例：`samples/Net48.WinForms`、`samples/Net6.Console`、`samples/Net8.Wpf`；
- 固定客户端路由：激活挑战、续租挑战、挑战完成、在线验证，详见[协议与运行契约](protocol.md)。

发布应用时要把 SDK 版本、产品代码、预期 issuer/audience 和产品 ES256 公钥集合一并冻结。客户端不得通过未经认证的 HTTP 动态替换公钥。

### 先运行 WinForms / WPF 示例

两个桌面示例可以直接导入**现有后台下载的客户端 JSON 配置**，无需手动把 PEM 公钥改成 JWK，也无需重新部署后台。详细启动路径见 [示例说明](../samples/README.md)。

1. 在后台创建产品，下载该产品的“客户端配置”；`productCode` 是产品公开编号，不是内部 UUID。
2. 在该产品下生成一张测试卡密，在“卡密批次”点击“导出卡密”下载 CSV。存档可重复导出，但文件含完整卡密，仍须妥善保管；不要把卡密 CSV 当成客户端配置导入。
3. 打开示例，点击“导入客户端配置…”，选择可信渠道取得的 JSON。
4. 核对产品公开编号与服务根地址。若文件中仍是 `http://SERVER_IP`，在窗口内改成真实根地址（不要加 `/Admin`、`/api` 或查询参数），再点击“应用配置”。
5. 未激活时会提示输入卡密。在密码框输入测试卡密，点击“激活 / 验证”；点击后会立即显示进度，结束后显示中文结果和错误码。
6. 激活后输入框会清空。后续**留空点击“激活 / 验证”**即可在线优先验证已有激活状态，不需要重复输入卡密。

HTTP 默认禁止，配置文件不能自行开启。仅在已认证 VPN / 受控隧道内联调时，才可另外勾选窗口内的明文 HTTP 风险确认；它不提供加密，不适用于公网正式使用。两个示例保持系统 HTTPS 证书链/主机名检查，不提供跳过证书验证。

配置优先级为：窗口显式导入 > EXE 所在目录的 `client-config.json` > 完整旧公开环境变量。旧变量为 `LICENSING_BASE_URL`、`LICENSING_PRODUCT_CODE`、`LICENSING_SERVER_JWK`（单 JWK 或 JWKS），可选 `LICENSING_ISSUER` / `LICENSING_AUDIENCE`。不会跨来源补字段，默认文件损坏时也不会静默回退；窗口会显示错误，允许重新导入。GUI 不读取 `LICENSE_KEY` 或环境中的 HTTP 风险许可。卡密仅通过密码框输入，不放入配置、命令行或环境变量。

示例共享适配层位于 `samples/Shared/`：保留原 `kid`，将后台的规范 P-256 PUBLIC KEY PEM 转为 SDK 的 JWK，保留 ACTIVE/RETIRED 公钥、排除 REVOKED；导出文件里的 `offlineLeaseMinutes` 不会自动改变 SDK 的六小时上限或刷新默认值。它只是本地导入入口，不是在线公钥发现或自动信任更新；公钥仍须来自可信渠道。核心 SDK 的 `ServerJwk.ParseMany()` 仍只接受 JWK/JWKS，不能直接传入后台整份 JSON 或 PEM。

## 3. 客户端配置最小集

`LicenseClientOptions` 的关键配置：

- `ServiceBaseUri`：生产必须是 `https://`；默认拒绝 HTTP；
- `ProductCode`：必填，SDK 标准化为大写；
- `DeviceHint`：供管理员识别设备，最长 128 字符，不要放原始硬件标识或个人敏感信息；
- `ExpectedIssuer`：默认 `license-platform`；`ExpectedAudience` 默认等于产品代码；
- `ServerVerificationKeys`：至少一个按 `kid` 索引的受信任 P-256 JWK；
- `OfflineLeaseLimit`：大于 0 且不超过 6 小时，默认 6 小时；
- `RefreshInterval`：默认 2 小时，不能超过离线上限；
- `ClockSkew`、`ClockRollbackTolerance`、`MaxResponseBytes`；
- `RequestTimeout`：默认 15 秒，只允许 1 秒到 2 分钟；超时属于可使用有效签名离线租约的网络失败；
- `AllowInsecureHttp`：只有明确接受明文联调风险时才设为 `true`。它不会关闭请求签名，但也不会让 HTTP 具有 TLS 安全性。

SDK 接受调用方持有的 `HttpMessageHandler`，只拥有内部的 `HttpClient` wrapper：释放 `LicenseClient` 不会释放 handler 或其 `DelegatingHandler` 链，也不会影响共享该链的其他客户端；调用方须在所有客户端结束后自行释放整条链。若链中使用内置 `HttpClientHandler`（包括多层 `DelegatingHandler` 包装），构造客户端时会强制把 `AllowAutoRedirect` 设为 `false`；不要在构造后修改它。缺少 primary handler 的链或已使用、无法修改重定向策略的内置 handler 会在构造时被拒绝。自定义 handler 同样不得跟随重定向。这样可在发送卡密或签名请求前阻断 307/308 跨 origin 转发；SDK 还会拒绝自定义 handler 返回的跨 origin 最终响应。

## 4. 创建客户端与启动

```csharp
using Licensing.Client;
using Licensing.Client.Windows;

var options = new LicenseClientOptions
{
    ServiceBaseUri = new Uri("https://license.example.com"),
    ProductCode = "MY-PRODUCT",
    DeviceHint = Environment.MachineName,
    ExpectedIssuer = "license-platform",
    RequestTimeout = TimeSpan.FromSeconds(15)
};
foreach (var key in ServerJwk.ParseMany(productJwkJson))
    options.ServerVerificationKeys.Add(key);

using var httpHandler = new HttpClientHandler();
using var client = WindowsLicenseClientFactory.Create(
    httpHandler,
    options,
    new WindowsStorageOptions("MyCompany.MyProduct"));

// 每个进程启动都先尝试在线验证；已激活安装不需要卡密。
var result = await client.StartAsync(cancellationToken);
if (!result.Success && result.Error?.Code == LicenseErrorCode.ActivationRequired)
{
    // 只在首次激活界面收集一次，调用完成后立即清空 UI/变量。
    result = await client.StartAsync(oneTimeLicenseKey, cancellationToken);
}
```

`WindowsLicenseClientFactory` 使用当前 Windows 用户范围的 DPAPI，原子保存 P-256 安装密钥及租约/可信时间状态。卡密明文不会写入 SDK 状态；缓存只保留不可逆 SHA-256 选择器，后续在线验证和续租只提交许可证 UUID、绑定版本、指纹和安装公钥证明。

### 启动与运行时区别

- 每次进程启动调用无卡的 `StartAsync()`；它总是在线优先；
- 首次没有缓存时返回 `ActivationRequired`，此时才调用一次 `StartAsync(licenseKey)`；
- 运行期间调用 `GetLeaseAsync()`；两小时刷新间隔到达前可直接使用仍有效的签名缓存；
- 到刷新点后先在线验证，必要时执行“续租挑战 + 完成挑战”，不再提交卡密；
- `ClearAsync()` 清除租约缓存，但安装密钥仍由独立 store 管理。

只在网络不可达、请求超时或服务器 `5xx` 时允许退回仍有效的已签名租约。明确业务拒绝（卡密禁用、产品停用、绑定版本变化、过期等）会清除缓存，不能离线绕过。租约自身从签发到到期不得超过六小时；SDK 还记录可信服务端时间和本地观察时间，检测超容差的时钟回拨。

## 5. ES256 离线验证

当前领域模型声明产品签名算法为 `ES256`。客户端验证至少应做到：

- 明确只接受协议规定的算法，拒绝 `none` 和算法混淆；
- 根据受信任的 `kid` 选择公钥，不接受载荷自行提供的任意公钥或远程 key URL；
- 验证签名字节和编码形式；SDK 要求无填充、无空白且规范的 Base64Url，以及严格 UTF-8 的 JSON，拒绝重复属性和不受支持的 JWS 扩展；
- 验证产品代码、许可证标识、客户端/设备绑定（若协议定义）、签发时间、生效时间、到期时间和协议版本；
- 对未知 `kid` 安全失败，并按受控公钥更新流程处理；
- 区分 `Retired` 与 `Revoked`：退休公钥通常仍用于验证历史签名，撤销如何处置必须由明确协议规定；
- 将许可证缓存保存在操作系统提供的受保护存储中，并防止普通用户随意替换或回滚；
- 不能仅因 JSON 能解析或 `exp` 尚未来到就认为许可证有效。

ES256 只保护被签名载荷的真实性和完整性，不加密载荷。许可证中不应包含不必要的个人信息或秘密。

## 6. 公钥轮换

推荐的兼容顺序：

1. 服务端生成新密钥，但不立即撤销旧密钥；
2. 通过受认证渠道发布新公钥和 `kid`；
3. 客户端版本或独立公钥包同时信任新旧公钥；
4. 观测覆盖率后，服务端开始用新私钥签名；
5. 旧公钥保持足够长时间以验证尚在有效期内的历史许可证；
6. 只有在事件响应或明确业务条件下撤销，并定义离线客户端恢复路径。

公钥更新自身必须有信任根：随受签名应用更新发布，或由已有信任密钥签署。通过未认证 HTTP 下载新公钥会让攻击者替换整个信任链。

当前 `ServerJwk`/JWKS 是受信任公钥集合，没有客户端 `Retired`/`Revoked` 状态字段，也不自动在线发现撤销状态。退休密钥若仍在集合内，历史签名仍可验证；撤销密钥必须由受控更新从集合移除，并用新配置重新创建客户端。被移除的 `kid` 与未知 `kid` 均返回 `UnknownSigningKey`，其缓存不能离线放行。尚未收到受控更新的离线客户端无法得知新撤销事件，不能宣称撤销会即时传播到所有离线设备。

## 7. 期限和租约语义

领域约束：

- 期限单位为天、月、年或永久；
- 月按 30 天，年按 365 天；
- 永久期限没有有限到期时间；
- 产品配置允许的租约参数范围是 5 分钟到 30 天，但服务端签发给当前 SDK 的单个离线租约会再取其与 6 小时上限的较小值。

客户端要区分“许可证总期限”和“在线租约/续租窗口”。永久许可证仍然只有最长六小时的单次签名租约，不表示永久离线，也不能忽略产品停用、密钥撤销或其他协议状态。

## 8. 认证信息和本地存储

- 管理员凭据不得用于客户端；
- 每个集成方/客户端应使用最小权限凭据，能分离时不要共享；
- bearer token 只能放在授权头或协议指定的安全位置，不能放查询参数；
- Windows 安装密钥和租约状态由当前用户 DPAPI 保护，并用原子替换和进程间互斥写入；应用不得在别处保存卡密明文；
- 卸载、设备转让和用户退出时按业务流程清除租约状态；换机前由管理员先解绑；
- 崩溃转储、遥测和支持包中必须脱敏。

DPAPI 文件头、密文损坏或应用用途不匹配会安全失败；SDK 返回 `StorageFailure`，不会把损坏状态当作空缓存或有效离线许可，也不会静默生成新安装身份覆盖原文件。租约缓存可由显式 `ClearAsync()` 清除后重新激活；安装私钥损坏须走受控恢复/解绑流程。对同一租约的并发保存不会降低可信时间或本地观察时间，过期进程也不能覆盖更新的在线租约。

这些措施不等于硬件防回滚：DPAPI 不阻止当前用户解密自己的数据，也不能识别攻击者同时恢复整套历史受保护文件和系统时钟。离线回拨检测依赖尚未被整体回滚的持久化时间锚；对有此威胁的环境，应缩短离线窗口、加强主机权限和在线验证，不能把六小时租约描述为不可绕过的本地信任根。

## 9. 上线检查表

- [ ] 生产基地址是域名 HTTPS，不存在 HTTP 降级；
- [ ] 证书链和主机名验证开启；
- [ ] OpenAPI/协议版本已冻结并做兼容测试；
- [ ] ES256 正反例、未知 `kid`、过期、未来生效、载荷篡改均有测试；
- [ ] 超时、退避、限流和断网行为已做规模测试；
- [ ] 客户端时钟错误有清晰提示；
- [ ] 日志和遥测不含敏感载荷；
- [ ] 公钥轮换与紧急撤销流程已演练；
- [ ] 离线/宽限策略有产品和安全负责人批准；
- [ ] 服务端备份恢复后，客户端仍可验证既有许可证。
