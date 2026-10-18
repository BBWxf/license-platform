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

执行日期：2026-10-18。以下为最终源码对应的实际验证结果。

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
