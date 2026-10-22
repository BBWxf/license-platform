# 开发验证与验收边界

本文记录可复现的非 Docker 验证方式。测试通过不等于生产部署已经过真实主机验收；没有执行的项目不计为通过。

## 1. 本地开发环境

- 服务端/测试需要 .NET SDK 10；`global.json` 允许同主版本内更新的 feature band。
- Windows SDK 的 DPAPI 测试必须在 Windows 上执行。
- 客户端示例保留 .NET Framework 4.8、.NET 6、.NET 8 兼容目标；**.NET 6 已结束支持，不建议新项目采用**。
- .NET 8 目标编译成功不等于在 .NET 8 运行时执行成功。只有 .NET 10 的本机使用 `DOTNET_ROLL_FORWARD=Major` 运行客户端测试时，必须注明实际运行时。

常规命令（使用支持期内的操作系统与完整 SDK）：

```bash
dotnet restore LicensePlatform.sln
dotnet build LicensePlatform.sln --configuration Release --no-restore -m:1
dotnet test tests/Domain.Tests/Domain.Tests.csproj --configuration Release --no-build
dotnet test tests/IntegrationTests/IntegrationTests.csproj --configuration Release --no-build
dotnet test tests/Client.Tests/Client.Tests.csproj --configuration Release --no-build
dotnet test tests/Web.Tests/Web.Tests.csproj --configuration Release --no-build
dotnet format LicensePlatform.sln --verify-no-changes --no-restore
dotnet publish src/LicensePlatform.Web/LicensePlatform.Web.csproj --configuration Release --no-restore -o artifacts/web
```

当前验收主机为 Windows 10 1909（build 18363），不在 .NET 10 支持列表中。在该主机上使用 `COMPlus_DefaultStackSize=400000`（十六进制 4 MiB managed worker stack）、`-m:1`，构建时增加 `--disable-build-servers -p:UseSharedCompilation=false -nr:false`。最终发布仍在 MSBuild Copy 任务出现过 Stack overflow/超时；诊断确认任务内部并行复制后，额外设置 **`MSBUILDCOPYTASKPARALLELISM=1`** 成功完成发布。构建与发布分开时，已成功构建后可以使用 `publish --no-build --no-restore`。这些设置仅为本机 workaround，不能当作 Linux 生产部署要求。**不得为绕过本机发布问题关闭 Static Web Assets**，因为正式应用使用 `MapStaticAssets()`。

若出现 NuGet/MSBuild DLL 或 shared runtime 缺失，应保留故障 SDK，重新从已验证来源解压到新的空目录；不要把缺少工具链文件误判为业务代码失败，也不要重复长时间挂起构建。

## 2. 不依赖数据库的测试边界

`IntegrationTests` 内的应用流程测试使用 EF InMemory，并使用真实 ES256 签名、设备证明和 AES-GCM 私钥保护。覆盖首次成功激活才起算、同设备丢响应后重新挑战、管理员解绑后换绑、不重置到期时间、旧设备及旧挑战失效、禁用/到期拒绝、六小时租约上限（包括永久卡）、剩余有效期裁剪等。

**InMemory 不证明 PostgreSQL 事务、唯一约束、并发控制或迁移实际执行成功。** 模型/迁移测试只验证迁移发现、模型无漂移、PostgreSQL 建表及幂等迁移 SQL 可以生成。

Web 的 TestServer 测试用于真实认证中间件、Cookie 撤销/续期、请求签名与请求体处理，不冒充 PostgreSQL 运行测试。

## 3. 原生或外部 PostgreSQL 18 集成测试

不需要 Docker。默认没有数据库配置时，两个 PostgreSQL 测试会明确显示 **Skipped**。

1. 准备专用的 PostgreSQL 18 测试数据库，名称必须以 `license_platform_test` 开头，禁止使用生产数据库。
2. 测试账号需要在该数据库创建/删除 schema 的权限，不需要超级用户。
3. 把 Npgsql 连接配置写入访问受限的 secret 文件；可使用受限 `.pgpass` 文件或把口令保存在该 secret 文件中。使用外部数据库时应验证 TLS 证书（如 `SSL Mode=VerifyFull`），不得为了方便关闭外部链路 TLS。
4. 环境变量只传**文件路径**，不能传密码或完整带密码的连接串。避免 shell tracing，不输出 secret 文件内容。

```bash
LICENSE_PLATFORM_TEST_DATABASE_FILE=/secure/license-platform/test-database.connection \
  dotnet test tests/IntegrationTests/IntegrationTests.csproj \
  --configuration Release --filter FullyQualifiedName~PostgreSqlActivationTests
```

每个测试创建随机 `test_<GUID>` schema，在其中执行正式迁移和测试，最后只删除该测试自己的 schema。测试会修改专用数据库；显式设置上述配置即为启用这组测试。异常统一输出无敏感信息的失败提示，不展开 provider 异常。

覆盖：

- PostgreSQL 18 实际迁移、迁移历史与无待执行迁移；
- 两个不同设备完成竞争挑战时只允许一个激活提交；
- 同一个挑战并发完成时只允许一次消费；
- 成功激活后同设备重新发起挑战不重置有效期。

并发测试在两次 `SaveChangesAsync` 前设置 rendezvous，确保竞争的是同一已读取版本，而不是把顺序执行误报为并发覆盖。进程被强杀时可能留下 `test_<GUID>` schema，只能在确认属于该测试数据库后由管理员清理。

## 4. 正式发布产物 smoke check

需要 Node.js，无需安装额外 npm 包：

```bash
node tools/published-smoke.cjs /path/to/dotnet /path/to/artifacts/web
```

脚本只监听并访问 `127.0.0.1`，使用临时 Data Protection 目录，不提供数据库口令或访问外部服务，并在结束时停止自己启动的进程、清理自己的临时目录。验证：

- 发布目录存在 Static Web Assets manifest，且不混入 BuildHost / Microsoft.Build / Roslyn 设计时组件；
- 登录页可渲染，HTTP 风险告警持续显示，安全头存在，HTTP 不发送 HSTS；
- CSS、JS、scoped CSS 和 Bootstrap 静态资源可访问；
- 匿名后台访问被重定向到登录页；缺少 antiforgery token 的登录 POST 被拒绝；
- 数据库不可用时 `/health` 必须返回 503 和精确的 `{"status":"unhealthy"}`，且不可缓存。

此项**不验证真实数据库下的健康 200、后台业务端到端操作、证书或公网可达性**。

## 5. Docker 验收范围

按项目范围决定，不执行 Docker 相关测试，也不以其作为本轮完成条件。源码保留 Compose、Caddy、备份/恢复/升级/回滚支持。shell 语法检查、隔离文件系统与命令 stub 故障注入不启动容器，不等于 Docker 实机验收。

明确排除：`docker compose config`、容器构建/启动、真实容器 UID/GID、Caddy 容器、Docker 备份/恢复/升级/回滚测试。

## 6. 本次执行记录

执行日期：2026-10-18。以下记录应用功能基线的实际验证结果；后续 NAT/EIP 和镜像标签修复未改动 .NET 应用代码，不重复运行应用测试，增量结果另列于第 7、8 节。

已执行：

- 整套 solution Release restore/build 成功，0 错误；唯一警告为 .NET 6 示例的 NETSDK1138（结束支持）。Core/Relational/Design EF 包已对齐到 10.0.12。
- 整套 .NET 回归共 **318 项通过、2 项明确跳过、0 项失败**。
- `dotnet format LicensePlatform.sln --verify-no-changes --no-restore` 通过。

- Domain.Tests：19 项通过。
- IntegrationTests：37 项通过、2 项明确跳过（原生/外部 PostgreSQL 未配置）。包括模型无漂移、幂等迁移 SQL 生成、签名器六小时边界，以及完成挑战时重新检查禁用/撤销且失败不落盘。解绑换绑用例在修复前出现预期失败，修复后通过。
- Web.Tests：197 项通过，无失败/跳过。覆盖真实 Cookie 中间件撤销/续期、签名/重放、CLI、密码 PHC 格式、日志脱敏及超大未知长度请求体的 413 回归；额外验证 PostgreSQL 唯一约束冲突映射为 409（而非可能触发离线回退的 500），其他数据库故障仍保持通用 500。唯一冲突与超大请求体用例均先复现失败再修复，异常测试不需要数据库。
- 客户端 Release 测试：65 项通过，无失败/跳过。目标为 `net8.0-windows`，实际运行于 .NET 10.0.8（Major roll-forward）。
- 三个客户端示例分别 Release 编译成功；只有 .NET 6 示例的 NETSDK1138 结束支持警告。
- 最终正式发布成功，产物位于项目 `artifacts/web/`，保留 Static Web Assets/压缩资源且不含 BuildHost/Roslyn/MSBuild 设计时组件。最终产物 loopback smoke check 通过，包括数据库不可用时的通用 503，而非声称数据库健康。最初两次发布出现 Copy 任务 Stack overflow/超时（exit 124），限制 Copy 任务并行度后成功；未修改生产功能来掩盖失败。
- 部署隔离故障注入：`node --test tests/Deployment/transactions.test.mjs`，22 项通过，无失败/跳过。包含安装重跑/手动回滚中断恢复、marker 隔离、latest 筛选与不同精度时间戳排序。全部 deploy shell 文件及 fixture stub 的 `bash -n` 通过；测试 runner 的 Node 语法检查通过。未执行真实 Docker/systemd 操作。
- 原生/外部 PostgreSQL：本机未发现 PostgreSQL 工具或已配置测试实例，尚未执行；不计通过。
- Docker：依约排除，未执行。

上述通过项已完成；真实 PostgreSQL 18 两项仍待在访问受限的专用实例执行。首次安装无旧健康部署可恢复；镜像回滚不自动回退数据库迁移。实际 Linux 所有权/systemd/网络/断电持久性未由 stub 测试证明，详见部署文档。公网明文 HTTP 仍不是安全生产部署。

## 7. NAT/EIP 部署增量验证

执行日期：2026-10-18。公网身份 `PUBLIC_IPV4` 与本机监听 `BIND_IPV4` 分离，保留原安装命令；不要求将云 EIP 添加到本机接口。

- `transactions.test.mjs`：**29 项通过、0 失败/跳过**。保留原 22 项；新增首次环境写入私网绑定、旧配置不继承进程绑定、保存绑定重跑、显式覆盖、覆盖失败和强杀恢复，以及应用回滚/恢复保留 NAT 绑定。该 runner 注入绑定候选、跳过网络预检，不冒充网络集成验收。新增回滚重试用例最初因固定测试时钟重用事务目录失败，允许 fixture 时钟前进后通过，未修改生产事务规则掩盖失败。
- `networking.test.mjs`：**91 项通过、0 失败/跳过**。使用 fail-closed 的 `ip`、`ss`、`curl` 和只读 Docker 查询 mock，覆盖直绑/NAT、绑定优先级、地址/路由歧义拒绝、端口与协议冲突、旧配置回载隔离，以及数字 IP/域名 URL 保留公网身份并通过 `--connect-to` 连接私网绑定。
- 从实际脚本提取恢复白名单/绑定预检及 HSTS 探测函数执行隔离测试，不执行脚本入口。覆盖新旧归档绑定、继承变量隔离、失效绑定拒绝、HSTS/TLS 错误和临时文件清理；静态固定配置恢复及仅数据库恢复的绑定预检位于安全备份、停服务和数据库覆盖之前。Compose 绑定与全部健康探测调用点仅做静态接线检查。
- 所有 deploy shell、两份 fixture stub 的 `bash -n`，两份测试 runner 的 `node --check` 及 `git diff --check` 通过。非法 CR/LF 测试参数通过 NUL 分帧文件传递，避免 Windows argv 归一化影响断言。

整套部署隔离测试联合执行：**120 项通过、0 失败、0 跳过**（约 104 秒）。复现命令：

```bash
node --test tests/Deployment/*.test.mjs
```

本轮未运行真实 Docker（包括 Compose config）、systemd、数据库恢复/归档完整编排、实际网络/TLS 或用户服务器操作；未验证云 EIP 映射、安全组或公网可达性。未改动及重跑 .NET 应用代码和测试，沿用第 6 节应用基线；真实 PostgreSQL 两项仍未执行。所有网络数据和秘密均为临时 fixture 的合成值，不读取生产秘密。

## 8. 基础镜像标签修复

执行日期：2026-10-18。用户服务器已通过 NAT 预检，但在首次镜像构建阶段返回 `aspnet:10.0.8-bookworm-slim: not found`。修复 Dockerfile、安装器和文档的默认值为 `sdk:10.0.302-noble` / `aspnet:10.0.10-noble`。

- 通过微软 MCR 的只读 HTTP manifest 接口核对：旧 SDK/ASP.NET Bookworm 标签均为 404；上述最终 Noble 标签均存在，并含 Linux amd64/arm/arm64 清单。只读取清单 JSON，不拉取镜像层、不调用 Docker。
- 官方 [.NET 10 发布元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json) 确认 SDK 10.0.302 对应 ASP.NET Runtime 10.0.10；`global.json` 允许同一主/次版本内的稳定 SDK feature band 更新。
- 新增离线镜像选择一致性、SDK roll-forward 回归，并在临时文件 fixture 中验证：首次失败配置改名备份后，可生成修正的默认镜像选择，原四个 secret 均不变。该用例不代表实际主机恢复或镜像构建通过。
- 失败首次安装留下的 `deployment.env` 不能直接当作健康重跑基线；本次仅指导在没有本项目容器、数据卷及 pending 事务、且未改动初始配置的情况下保留备份后重试，不删除密钥或数据卷，也不放宽已有部署的健康基线要求。

最终源码的 `node --test tests/Deployment/*.test.mjs`：**123 项通过、0 失败、0 跳过**（镜像选择 2 项、网络 91 项、事务 30 项，约 100 秒）。安装器 shell 语法、变动测试 runner 的 Node 语法及 diff 空白检查通过。

未执行容器拉取/构建/启动、Compose config、systemd 或 .NET 应用重跑；运行时镜像的补丁升级尚未经真实容器验收。此前 Windows .NET 10.0.8 应用基线保持独立，不冒充本次 Linux 运行时 10.0.10 的执行结果。

## 9. 后台审计 JSON 查询修复

2026-10-18 用户服务器报告：基础设施和 `/health` 正常，但后台首页请求因 PostgreSQL `42883: function substring(jsonb, integer, integer) does not exist` 失败。`DataJson` 为 CLR string、数据库类型为 jsonb；将其 `.Length` / `.Substring` 放在 EF 查询投影中会产生 JSON 属性访问和不适用的字符串函数。审计列表也存在相同写法。

修复限定为首页及审计列表读取：数据库继续执行排序、过滤及最近 10 条/分页限制，只返回列表必需的原始字段；查询物化后在 .NET 内存中分别截断为 160/220 字符摘要。没有数据库模型、迁移、密钥或管理员凭据变更。健康端点检查不覆盖后台页面查询，因此健康通过不能代替业务页面验收。

新增回归先验证旧生产代码：17 项中 15 项行为测试通过，但两项 Npgsql SQL 断言均因包含 `substring(a.data_json, ...)` 失败，复现了 InMemory 单独测试无法检出的 provider 翻译问题。SQL 用例通过 `IQueryExpressionInterceptor` 捕获实际服务使用的分页表达式，仅替换 provider 根并绑定已提取的标量参数，再由真实 Npgsql provider 生成 SQL；不是在测试中另写一条正确查询。模拟连接在生成前后均保持 Closed，无需连接数据库。

修复后 Release 重新构建并执行完整 `Web.Tests`：**214 项通过、0 失败、0 跳过**，包括新增 17 项。行为覆盖最近 10 条、过滤后分页与总数、空集/无匹配/越界页、参数钳制、字段回退及 160/220 字符边界；SQL 回归确认原始 JSON 列读取保留数据库排序/过滤/分页，且不再含 `substring` 或 JSON `Length` 提取。

同时重跑未改动项目的现有 Release 产物：Domain 19 项通过、Integration 37 项通过/2 项跳过、Client 65 项通过；本轮合计 **335 项通过、2 项明确跳过、0 项失败**。使用完整临时 SDK 10.0.108 和第 1 节旧 Windows workaround；Client 仍实际运行于 .NET 10.0.8（Major roll-forward）。两个真实 PostgreSQL 18 测试未配置而跳过；本次新增 SQL 用例只生成查询、不连接数据库。变更 C# 文件的 `dotnet format --verify-no-changes --no-restore --include ...` 通过。未执行真实 Docker、用户服务器升级或真实数据库下的后台页面验收。

## 10. WinForms / WPF 示例配置与按钮流程修复

2026-10-18 用户反馈 WinForms 点击“激活 / 验证”没有明显反应。按钮事件本已绑定；本轮修复的是配置无法在窗口内修正、后台 PEM 导出与示例 JWK 输入不衔接、空卡密无法再次验证，以及反馈和关闭生命周期问题。没有证据表明用户当时一定遇到了 DPAPI 锁等待；新增工作线程边界是针对 SDK 同步前置等待的防护，不冒充已复现的用户根因。

改动仅涉及桌面示例、链接的共享示例代码、客户端测试与文档，不改变核心 SDK API、服务端、数据库或部署。保留两个示例原 Windows 存储用途名；不读取 GUI 的卡密环境变量、不忽略 HTTPS 证书、不自动开启 HTTP、不改变签名/离线规则。后台现有客户端配置可直接在本地适配，不要求重新部署后台。

实际执行：

- 完整 Release `Client.Tests`：**271 项通过、0 失败、0 跳过**（原 65 项加新增 206 项）。使用临时 SDK 10.0.108 与第 1 节旧 Windows workaround；测试目标仍为 `net8.0-windows`，通过 Major roll-forward **实际运行于 .NET 10.0.8**，不是 .NET 8 运行时验收。
- 配置回归覆盖现有后台 JSON 结构、规范 91 字节 P-256 SPKI 到 JWK 的坐标等价性、原 `kid`、密钥状态、重复字段/标识、大小/深度/编码/曲线点、文件/环境优先级、地址覆盖、HTTP 必须显式同意与安全错误消息；不需要生产配置或私钥文件。
- 使用实际 `SampleLicenseOperation`、实际 SDK、生成的 ES256 签名、内存存储和 fake HTTP handler，验证“未激活提示 → 后台格式配置首次激活 → null/空/空白卡密再次在线验证”，确认后续请求不重传测试卡密、缓存不保存明文。不是对用户服务器的真实激活。
- 共享操作回归验证同步前置工作不阻塞调用线程、单次在途操作、取消、异常脱敏、完整异步尾部结束后才清理、重复/重入关闭与仅一次资源释放；所有阻塞测试有时限和清理。
- WinForms `net48` 与 WPF `net8.0-windows` 均完成最终 Release 构建，均为 **0 警告、0 错误**。
- 实际启动两个桌面程序，通过本机 UI Automation 检查 **6 个场景全部通过**：每个示例分别验证缺配置、坏 JSON、有效公开配置但 HTTP 默认禁止；检查中文反馈、按钮状态、产品/地址显示、HTTP 未勾选及正常退出，并查看窗口截图布局。每个场景使用新临时目录中的程序副本，移除继承的公开配置/卡密变量，只使用合成公钥配置。此项覆盖程序目录配置加载，不冒充手工文件选择器验收，也未触发网络激活或读取既有示例的受保护激活状态。
- GUI 检查确实先复现 WPF 空闲关闭时 `Closing` 内重入 `Close()` 的 `InvalidOperationException`；改为 Dispatcher 排队关闭后，三种 WPF 场景均正常退出。WinForms 同步清理后的再次关闭也改为排队，避免关闭事件重入。
- 变更 C# 的 `dotnet format --verify-no-changes --no-restore --include ...`、`git diff --check` 与新增文档相对链接检查通过。初次编译被新增测试的 xUnit2014 拦住，改用明确的同步拒绝断言辅助方法后完整测试通过；没有通过禁用分析器掩盖问题。最初的临时 PowerShell GUI 脚本被本机执行策略阻止，未改变该策略，后改用临时编译的 .NET UI Automation 验证程序。

未执行：生产卡密或用户服务器的端到端激活、真实 HTTPS 证书/公网连通性、GUI 中在途网络取消的端到端操作、独立 .NET 8 运行时验证、PostgreSQL/Docker/部署或原 8080 服务操作。GUI 中的取消/安全清理由共享操作回归证明，不把它算作真实服务器测试。WinForms 已实际启动，但不据此宣称完整 .NET Framework 4.8 授权端到端流程通过；WPF 窗口本次实际使用 .NET 10.0.8 Desktop roll-forward。未改动的服务端测试套件本轮未重跑，其既有结果见前节。

## 11. 中文管理后台视觉改版增量验证

执行日期：2026-10-18。本轮只调整 Razor 视图与本地 CSS，新增测试夹具和回归，不修改 PageModel、业务服务、数据库模型、认证配置或 API。桌面采用深色侧栏和浅色工作区，移动端复用 Bootstrap 折叠导航；概览继续显示原有五项真实统计，不添加虚构图表或健康状态。

已执行：

- 临时 SDK 10.0.108 / 实际运行时 .NET 10.0.8，沿用第 1 节旧 Windows 的构建设置，Release 构建成功，**0 警告、0 错误**。
- 完整 `Web.Tests`：**254 项通过、0 失败、0 跳过**，其中新增 **40 项**真实 Razor 渲染回归。覆盖匿名访问限制、十个后台内容 URL、父栏目活动态、五项传入统计、空数据/长文本编码、POST handler/字段/防伪、GET 筛选、登录错误与密码不回显、上传表单、HTTP 警告、安全响应头、本地静态资源以及唯一 DOM ID/标签关联。
- `AdminPageTestHost` / `AdminPageFixtureData` 只在测试程序集内：真实 Cookie/credential stamp、临时 Data Protection、内存管理员和只读合成页面服务，不运行生产入口、初始化、迁移或 PostgreSQL。可选 Kestrel 预览仅绑定 `127.0.0.1` 随机端口；测试登录入口不会进入生产应用，Cookie/防伪材料不输出或保存为日志/页面快照。
- 使用已安装 Chrome、临时独立会话及 Node/CDP，检查 **320、390、768、1024、1440px**，覆盖登录、概览、产品、批次、授权查询、审计、创建表单、设置以及有数据时的授权详情；分别执行有数据、空数据和长文本三种场景。实际检查整页无横向溢出、局部表格滚动、唯一 ID、输入标签、本地资源；查看了桌面/手机截图，包括长品牌/摘要和 Logo 成功/失败回退。
- 浏览器交互检查通过：移动导航展开/收起、登录必填反馈、永久卡时长显隐、解绑确认展开、键盘 Tab/可见焦点、授权 GET 筛选及保留条件的下一页、带防伪令牌的真实 POST 退出。使用减少动画偏好，并检查 720px 逻辑视口的 200% 桌面缩放等效布局；后者不冒充浏览器原生缩放菜单测试。
- 对桌面各页实际可见文字的计算样式执行 WCAG 对比度计算（普通文字 ≥4.5:1，大字 ≥3:1），修正面包屑分隔符对比度后通过。浏览器还发现批次类型选择框自定义 ID 与标签不匹配，补齐 `for="validity-kind"` 并加入回归；绑定原因字段使用唯一 ID，但保留原模型绑定名称。
- 最终独立目录发布及 `tools/published-smoke.cjs` 通过：登录/HTTP 警告、安全头、CSS/JS/scoped CSS/Bootstrap、匿名重定向、缺失防伪令牌拒绝，以及数据库不可用时精确通用 503。以白名单环境启动 smoke，未继承生产连接/秘密设置；不把预期的数据库不可用记为健康数据库通过。
- 新增 C# 的限定 `dotnet format --verify-no-changes --no-restore --include ...` 与 `git diff --check` 通过。测试夹具最初遇到静态资源 manifest 加载和未出错时 ModelOnly summary 断言问题，已修正；两次临时构建碰到正在编辑中的测试辅助方法，完成编辑后重新构建及完整回归通过，没有禁用分析器或安全机制。临时浏览器驱动最初被其 DNS 隔离规则挡住 loopback，修正仅本次会话的回环排除规则后完成检查。

未执行：真实管理员密码登录成功、数据库业务写入/真实卡密生成导出/设备解绑延期、PostgreSQL、独立跨浏览器验收、原生浏览器缩放菜单、Docker、线上部署或原 8080 服务操作。浏览器登录使用测试程序集内的合成会话，不能据此声称生产认证和数据库业务端到端通过；原有业务及 Cookie 安全回归由完整 Web 测试覆盖。本轮未重新执行不相关的客户端/领域/外部数据库套件，不合并历史结果为本次通过数。

## 12. 卡密批次长期保留与重复导出

执行日期：2026-10-22。后台批次列表保留“导出卡密”按钮，下载读取原始加密 CSV，不再消费或清空存档，也不再限制生成后 15 分钟。新归档的到期时间明确为 null；迁移 `20261022000000_PersistLicenseBatchExports` 将仍有非空密文的旧有限期记录转为长期归档，保留原密文、消费历史与卡密业务状态。旧版已清空/缺失的数据不能从 HMAC 还原，非空密文仍须通过实际解密认证；回退迁移在存在 null 到期记录时拒绝执行，不删除存档或编造期限。

已执行：

- 使用临时 SDK 10.0.108、实际 .NET 10.0.8，沿用第 1 节旧 Windows workaround。Domain、Integration、Web 受影响项目的最终 Release 构建均为 **0 警告、0 错误**。最初旧临时 SDK 缺少 NuGet helper runtimeconfig；从已有 SDK ZIP 完整解压到新的独立目录，核对 5722 个文件存在及长度后重新构建成功。没有安装系统 SDK或删除旧目录；文件核对不等于重新验证下载签名/官方哈希。
- 完整 `Domain.Tests`：**30 项通过、0 失败、0 跳过**。保留旧有限期的一次性领域契约，新增长期归档禁止消费/擦除、UTC/期限边界与历史消费状态保护。
- 完整 `Web.Tests`：**285 项通过、0 失败、0 跳过**，包含新增 **31 项**服务及页面用例。真实服务使用隔离 EF InMemory、TestClock、真实随机生成的内存 AES/HMAC；验证立即重复、超过 15 分钟和 30 天后仍返回原 CSV、归档/卡密/批次/产品/绑定持久状态及版本不变、每次独立审计，以及旧消费/过期但保留密文的兼容路径。缺失/空/损坏/错误密钥/错误 purpose/解密后空内容安全失败；审计失败及解密后、审计保存中/完成后的取消不返回文件，并清零实际解密缓冲区。四个独立上下文的并发回归仅针对 InMemory，不冒充 PostgreSQL 并发验证。
- 页面回归使用真实已编译 Razor、Cookie/credential stamp/防伪链路和窄范围下载 spy：连续三次 HTTP 文件响应及按钮持续存在，attachment、UTF-8 CSV、安全响应头、禁止缓存、默认不支持范围/ETag；匿名、无效及已撤销 Cookie 不调用服务；不可用时仅保存固定安全提示并跳回列表，CSV 不进入 HTML/TempData；原管理 POST 仍要求防伪。默认只读展示 fake 不改变为真实卡密服务。
- 完整 `IntegrationTests`：**42 项通过、0 失败、2 项跳过**；两项 PostgreSQL opt-in 测试因没有受控数据库配置而明确跳过。验证 nullable expiry、原唯一索引/FK/版本语义、migration 可发现、snapshot 无模型漂移、向前/幂等 SQL 的非空密文转换及 Down 的先行拒绝保护。另通过截获实际 `ListAsync` 表达式再交给 Npgsql 翻译，确认数据库端 `EXISTS` + bytea 长度检查，payload 仅出现在长度函数中、不作为列表列返回，且不再按消费/到期过滤。PostgreSQL `length(bytea)` 与 `octet_length(bytea)` 均为字节长度；SQL 生成前后连接保持 Closed。
- 上述本轮套件合计 **357 项通过、2 项明确跳过、0 项失败**。首次 Web 执行的两处测试错误（把无 query 的导航链接当导出链接、将 provider 的 `length(bytea)` 误限定为 `octet_length`）以及一处 xUnit 集合大小警告修正后，完整重新构建与回归通过；未禁用分析器或降低安全校验。
- 最终独立目录发布和白名单环境下的 `tools/published-smoke.cjs` 通过：实际启动正式产物，检查登录、明文 HTTP 警告、安全头、本地静态资源、匿名后台重定向、防伪拒绝和数据库不可用时通用 503。数据库为拒绝连接的 loopback stub，不是健康数据库或完整业务端到端验收。
- Chrome 隔离 loopback 预览检查有数据/空数据/长文本三种状态和 **320、390、768、1024、1440px**；实际查看批次桌面、手机、长文本及创建页截图。检查重复导出提示、按钮文字、手机局部表格滚动后可见的按钮，并主动点击默认合成服务的导出按钮验证安全不可用跳转；不把该 null-export 浏览器操作说成真实数据库 CSV 下载。原导航、时长显隐、键盘、筛选/分页、POST 退出及布局/本地资源/标签/对比度检查同时通过；浏览器和临时会话均已退出。
- 变更 C# 的限定 `dotnet format --verify-no-changes --no-restore --include ...`、临时 Node 驱动语法、文档相对链接和 `git diff --check` 检查通过。测试导出内容和认证材料仅在内存使用，未写入日志、截图、URL、命令行或环境变量。

未执行：真实 PostgreSQL 上的数据回填、迁移锁/性能、回退执行及数据库并发；生产卡密/真实管理员密码登录、匹配生产主密钥和历史备份恢复、其他浏览器、独立 Linux/容器运行时或线上部署。本轮不修改 SDK，也未重跑客户端套件。Docker 实机测试继续按既定范围排除，不作为待补完成条件；未操作原 8080 服务。本地改动尚未提交、推送或部署。长期保留增加可恢复性，也扩大敏感归档泄露窗口，仍必须保护主密钥和备份，不能以重复导出代替备份。

## 13. 审计日志中文可读性优化

执行日期：2026-10-22。审计列表与首页“最近操作”共用中文展示规则，覆盖生产代码实际发出的 20 种事件；默认突出操作含义、说明及中文对象类型，长业务编号和操作代码收进原生“查看详情”。操作类型改为分组中文下拉框，仍提交原事件码，并兼容既有自定义/部分 `action` 链接。认证、防伪、审计写入、数据库结构和业务规则均未修改。

已执行：

- 临时完整 SDK 10.0.108 / 实际 .NET 10.0.8，沿用第 1 节旧 Windows workaround。Web 和测试项目 Release 构建成功，**0 警告、0 错误**。
- 完整 `Web.Tests`：**313 项通过、0 失败、0 跳过**，相较上一节新增 **28 项**。覆盖 20 种真实操作映射、公开名称快照、数量/固定天数期限、绑定版本、缺失/错误类型/重复属性/损坏/非对象/超长/过深 JSON，以及白名单字段的长度和 HTML 编码。未展示原始 JSON、自由文本原因/备注、设备标识或未知属性；测试敏感占位值只用于布尔检查，不放入失败输出。
- 特别验证：登录失败和锁定中的账号不是已认证操作者；`result:"recorded"` 不解释为成功；导出仅为服务端准备文件，不保证浏览器保存；重新签发租约不误说为首次激活；null 到期时间不等于永久。管理员初始化可能由 hosted service 自动引导或本机命令触发，同一个历史 `local-cli` 来源标记只显示为“本机管理”，不编造手动执行命令。新增异常期限代码回归先复现失败，再改为精确匹配枚举文本，防止去空白/控制字符后把损坏值“修复”为永久卡；修复后完整套件重新通过。
- 真实查询回归确认完整 JSON 中位于第 220 个字符之后的合法字段仍能生成说明，原 160/220 字符 `Summary` 契约不变，Razor 不再使用 `Summary` 或 `Result`。捕获实际生产 `IQueryable` 并交给 Npgsql 生成 SQL，保持数据库端筛选/排序/分页、首页最多十条及只读行为；格式化、JSON 解析和字符串截断都在物化之后，SQL 生成前后连接保持 Closed，不冒充真实 PostgreSQL 执行。
- 实际编译 Razor 的新增测试覆盖中文选项对应原代码、自定义筛选编码、GET 提交重置第一页、分页保留筛选、空页恢复/清除入口、UTC、原生详情与命名滚动区域，以及匿名访问不得调用可选服务。默认只读页面夹具未改为业务写入；测试 host 仅增加可选审计/概览服务覆盖点。
- 最终正式产物发布到新的独立临时目录，白名单环境下执行 `tools/published-smoke.cjs` 通过：真实启动发布入口，检查登录页、明文 HTTP 警告、安全头、CSS/JS/scoped CSS/Bootstrap、匿名后台重定向、缺失防伪令牌拒绝，以及数据库不可用时精确通用 503。数据库为拒绝连接的 loopback stub，不代表健康数据库或真实业务端到端通过。
- Chrome/CDP 隔离 loopback 检查有数据、空数据、长文本三种状态；审计与概览两页均覆盖 **320、390、768、1024、1440px**。审计和概览注入的是调用真实生产查询服务的独立 InMemory 数据，不运行生产入口、初始化、迁移或 PostgreSQL。实际执行中文选项提交、清除筛选、旧部分代码筛选、保留条件的下一页、空页恢复、未知筛选编码、详情点击/键盘展开收起、手机局部滚动、移动导航和带防伪的 POST 退出；检查整页无横向溢出、焦点/标签/ID、本地资源、桌面文字对比度及 720px 逻辑视口的 200% 缩放等效布局。实际查看了审计桌面/手机、长文本、空态、展开详情和概览截图；合成敏感占位值不进入 HTML 或截图。没有 JS 运行时/console 错误或非预期资源失败；空态夹具故意无 Logo 的 404 单独验证为可见文字回退，不伪称所有请求均为 200。
- 首轮窄屏实测发现，详情摘要内的屏幕阅读器文字以页面为绝对定位参考，导致表格外的根文档横向溢出。给审计专用 `summary` 建立相对定位后，完整三状态浏览器矩阵重新通过，没有通过全局隐藏 overflow 来掩盖问题。临时 CDP 驱动还修正了同 URL 导航等待、Enter 字符事件，以及把预期无 Logo 的 404 当成资源故障的判断；这些驱动修正不改变生产认证/资源行为。
- 变更 C# 的限定 `dotnet format --verify-no-changes --no-restore --include ...`、临时 Node 驱动语法、两个改动文档中的本地链接（1 个）以及 `git diff --check` 均通过。浏览器、loopback host 和 smoke 进程均已退出，只清理各自创建的临时会话，不操作用户服务。若后续要长期复用本机 SDK/Chrome 的隔离启动方式，建议用 `/run-skill-generator` 固化这套已有预览流程。

未执行：真实 PostgreSQL 查询/事务/迁移、真实管理员密码登录及生产业务端到端、其他浏览器、独立 Linux/容器运行时或线上部署；未重新执行本轮未改的客户端、领域及 Integration 套件，不将历史通过数合并为本次成绩。Docker 实机测试仍按既定范围排除，不作为待补完成条件；未操作原 8080 服务。本轮仅本地修改，未提交、推送或部署。
