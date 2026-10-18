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
