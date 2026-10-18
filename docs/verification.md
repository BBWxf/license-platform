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
