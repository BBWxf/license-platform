# Ubuntu / Debian 部署

## 1. 安全结论先行

> **当前 `http-ip` 模式是公网明文 HTTP，不是安全部署。**
>
> 它不提供传输加密、消息完整性或服务端身份认证。登录口令、许可证、API 令牌、设备信息和返回数据都可能被窃听或篡改。`--accept-insecure-http` 只表示操作者已知并接受该风险，不会让 HTTP 变安全。

在迁移到域名 HTTPS 前，至少应使用主机防火墙、安全组、VPN/专线和来源地址白名单缩小暴露面。不要在 URL 查询参数中传递口令、许可证或令牌。

## 2. 部署结构

Compose 项目名固定为 `license-platform`，服务名固定为：

- `caddy`：唯一公网入口，宿主机绑定 `BIND_IPV4` 的 TCP 80，使用 `PUBLIC_IPV4` 作为公网访问身份，反向代理到 `app:8080`；
- `app`：ASP.NET Core 10 Web 应用，仅在 Compose 网络中监听 8080，不发布主机端口；
- `db`：PostgreSQL 18，仅连接 `backend` 内部网络，不发布主机端口。

安装后的主要路径：

| 路径 | 用途 | 权限 |
|---|---|---|
| `/opt/license-platform` | Compose、脚本、受管构建源码、文档 | root 管理 |
| `/etc/license-platform/deployment.env` | 非明文业务秘密的部署配置 | `root:root 0600` |
| `/etc/license-platform/secrets/` | 数据库口令、查询/HMAC 密钥、主解密密钥、初始后台口令 | 父目录 `root:root 0700`；文件 `root:<SECRETS_GID> 0440`，仅容器补充组只读 |
| `/var/lib/license-platform` | 锁和回滚状态 | root 管理 |
| `/var/backups/license-platform` | 14 天本地备份 | root-only |
| `/usr/local/sbin/licensectl` | 运维入口 | 指向受管脚本 |

数据库和应用运行数据使用 Docker 卷。数据库没有宿主机端口映射，`backend` 网络声明为 `internal: true`。

## 3. 前置条件

- Ubuntu 或 Debian，使用 systemd；
- root 权限；
- 已安装且正在运行的 Docker Engine；
- Docker Compose v2，即 `docker compose`；
- 目标规格至少 2 核、2 GiB 内存；安装器在低于 1.5 GiB 时拒绝，低于约 2 GiB 时警告；
- 至少 5 GiB 可用磁盘，建议初始留出 10 GiB 以上；
- 一个固定公网 IPv4；可以直接配置在本机，也可以由云平台 NAT/EIP 映射到本机私网 IPv4；
- 本机监听 IPv4 的 TCP 80 未被其他进程或容器占用；HTTPS 重跑还检查 TCP/UDP 443；
- 上游防火墙/安全组已按最小来源范围配置。

安装器不会停止、删除、重命名或修改其他 Compose 项目、容器和主机进程。端口冲突时直接退出，由操作者处理。

## 4. 镜像和发布约束

仓库默认值都是明确版本，不使用 `latest`：

- PostgreSQL：`postgres:18.4-bookworm`；
- Caddy：`caddy:2.10.2-alpine`；
- .NET SDK：`mcr.microsoft.com/dotnet/sdk:10.0.302-noble`；
- ASP.NET Runtime：`mcr.microsoft.com/dotnet/aspnet:10.0.10-noble`；
- 本地应用镜像：`license-platform:0.1.0-local`。

这两个 .NET Noble 标签已通过微软 MCR manifest HTTP 接口确认存在并包含 Linux amd64/arm64；它们对应 .NET 10.0.10 发布的 SDK/运行时组合。`global.json` 的 `latestFeature` 允许 SDK 10.0.302。旧版默认的 `10.0.108-bookworm-slim` / `10.0.8-bookworm-slim` 标签不存在，不能继续使用；修改源码默认值不会自动覆盖已保存的镜像选择。此核对不等于容器构建或启动验收。

**版本标签仍然可能被仓库端重指向。正式发布时必须把应用和基础镜像替换为经发布流程验证的 `@sha256:<64位digest>`，并记录 SBOM、签名或来源证明。** 当前项目无法推断最终应用镜像 digest，所以安装器允许明确版本变量，但拒绝隐式标签和 `latest`。

应在独立预发布环境验证新版镜像、迁移、备份恢复和回滚后，再修改固定版本。固定旧版本也不代表没有漏洞；需要定期审阅厂商安全公告并发布新的、同样固定的版本/digest。

## 5. 安装

> 当前 Web 入口已实现迁移、健康检查、显式 HTTP 模式、文件 secret 装载和管理员一次性命令。安装器仍会在本地构建前检查这些契约标记，避免把不兼容镜像投入运行。

### 5.1 从当前源码构建

目标源码必须包含：

```text
src/LicensePlatform.Web/LicensePlatform.Web.csproj
```

并且 Web 镜像必须遵守以下运行契约：

- 正常启动入口为 `dotnet LicensePlatform.Web.dll`；Compose 会用一个最小 `/bin/sh` 包装入口，从 Docker secret 读取数据库口令后再 `exec` 该进程；
- 接受 `--migrate`、`--admin-init` 和 `--admin-reset` 一次性命令，完成后以非零/零退出码表示失败/成功；
- 长期进程固定以 UID/GID 1654 非 root 运行；
- 监听 `http://+:8080`；
- 提供无需身份认证、且不返回敏感内容的 `GET /health`；
- 读取 `ConnectionStrings__Database`；部署入口从 `postgres_password` secret 生成 `/tmp/.pgpass`，连接串和 Compose 环境文件均不含口令；
- 读取 `Security__CardLookupKeyFile`、`Security__MasterEncryptionKeyFile`、`Security__BootstrapAdminPasswordFile` 指向的 Docker secret 文件，以及 `Security__DataProtectionPath`。

执行：

```bash
cd /path/to/license-platform
sudo ./deploy/install.sh \
  --mode http-ip \
  --public-ip "$PUBLIC_IPV4" \
  --accept-insecure-http
```

**云服务器 NAT/EIP 不需要额外参数或把公网地址添加到网卡。** `PUBLIC_IPV4` 保留公网 Host、域名 DNS 比较和 HTTPS SNI/证书身份；`BIND_IPV4` 是实际属于本机接口的具体单播 IPv4，用于 Docker 端口发布及本机探测连接。

绑定选择顺序：显式 `--bind-ip` → 已保存绑定（旧配置使用其自身 `PUBLIC_IPV4`）→ 本机直接配置的公网 IP → 唯一明确默认出口的本机源 IPv4。路由识别仅查询本地内核，不访问云 metadata 或公网查 IP 服务。没有唯一默认路由、多路径或出口不一致时停止，可追加 `--bind-ip <本机IPv4>` 明确指定；不回退到 `0.0.0.0`，不修改网卡或防火墙。已有绑定失效时也会停止，不自动迁移接口。

两类地址保存在 `/etc/license-platform/deployment.env`。重跑默认保留原绑定；只有明确传入 `--bind-ip` 才更换。云平台仍需将公网端口正确映射到此本机地址，并配置安全组；**本机探测成功不代表外部访问已经通过**。

### 5.2 使用预构建应用镜像

推荐生产发布使用私有仓库中已审查并固定 digest 的镜像：

```bash
sudo ./deploy/install.sh \
  --mode http-ip \
  --public-ip "$PUBLIC_IPV4" \
  --accept-insecure-http \
  --app-image 'registry.example.com/license-platform/web@sha256:<64位digest>'
```

私有仓库认证应事先通过 root 的 Docker 凭据或组织规定的凭据助手完成。不要把仓库密码写进命令行、`deployment.env` 或文档。

### 5.3 安装器实际执行的检查和动作

1. 检查 root、Ubuntu/Debian、systemd、内存、磁盘；
2. 检查公网 IPv4 格式及保留地址，选择并验证本机监听 IPv4；
3. 检查 Docker daemon、Compose v2 和本机绑定的 TCP 80；HTTPS 重跑同时检查 TCP/UDP 443；
4. 只在文件不存在时生成秘密，幂等重跑不会轮换已有秘密；
5. `deployment.env` 保持 `root:root 0600`；secret 文件位于 root-only 父目录中并设为 `root:<SECRETS_GID> 0440`，只让非 root 容器补充组读取；
6. 拉取固定版本依赖镜像，构建或拉取应用镜像；
7. 启动 `db`，等待健康；
8. 运行一次性 `app --migrate`；
9. 启动 `app`、`caddy` 并检查容器及 `/health`；
10. 安装并立即启用已获授权的 `license-platform-backup.timer`。

安装失败不会清理数据库卷、备份或其他容器。阅读终端错误和本项目日志后处理；不要用 `docker system prune --volumes` 之类命令“重试”。

### 5.4 已有部署上的安装器重跑事务

已有 `deployment.env` 时，安装器只接受**默认受管路径且当前健康**的部署作为重跑基线。修改受管文件前会创建备份，按当前容器实际 image ID 固定 app/db/Caddy 的本地安全镜像，并把下列内容保存到 `/var/lib/license-platform/rollback/install-<UTC时间>/`：

- Compose 文件、HTTPS 模板、受管入口、bin/lib/docs、环境文件及运维 Caddyfile；
- 两个受管 systemd 单元文件、备份 timer 原先的 enabled/disabled 和 active/inactive 状态，以及受管入口链接原先是否存在；
- 本地源码 staging、旧源码路径与切换阶段。

`install.pending` 在改写前原子落盘并刷新；安装期间停止旧备份 timer。普通失败会恢复上述文件、来源模式和源码，将三个实际安全镜像写回环境，等待 db/app/Caddy 并执行严格健康检查，最后恢复 timer 状态。恢复失败返回独立失败码并保留标记。若进程被强制终止，重新执行**同一发布目录中的安装命令**会先恢复未完成事务，成功后以非零退出并要求重新发起安装，不会紧接着再安装一次。不要删除 pending 标记来绕过恢复，也不要在恢复前删除发布目录、源码回滚副本、事务快照或安全镜像。

成功重跑保留现有资源限额、数据库名称、保留期及未知配置键，只更新安装器明确管理的路径、入口和镜像选择；已有秘密不轮换。文档用全新 staging 替换，不合并旧目录。旧源码、安全镜像及事务快照不会自动清理。

边界：这不是数据库事务，已执行的迁移、数据卷内容和新创建的秘密不会自动撤销；恢复旧数据库镜像也不代表 schema/数据格式可降级。首次安装没有健康旧部署可回退，失败后可能留下本项目服务、秘密和受管文件；首次安装的半成品若没有事务标记，需要人工评估后恢复，不能以反复重跑代替修复。非标准路径、masked/static/runtime-enabled 等 timer 状态会被拒绝，而不是猜测恢复。主机包、Docker daemon、其他项目和防火墙不在事务范围内。

若旧版在**首次镜像构建**因上述不存在的标签失败，尚未启动服务，也没有本项目容器、数据卷或 pending 事务，不具备健康重跑基线。此特定情况可在更新源码后，将生成的 `deployment.env` 改名保留为独立备份，再按首次安装运行；保留原 `secrets/`，不删除或轮换密钥。若已有容器/数据卷、曾成功运行、配置有自定义或失败发生在迁移之后，不可据此重置配置，应按已有部署恢复流程处理。

### 5.5 不依赖 Docker 的部署回归测试

```bash
node --test tests/Deployment/*.test.mjs
```

需要 Node.js 和 Bash（Windows 可通过 `LP_TEST_BASH` 指定 Git Bash 的 `bash.exe`）。事务测试只在临时目录运行脚本副本，固定全部受管路径，并用 shell 函数替换镜像、Compose、systemd、锁和权限操作；网络预检在该 runner 中替换为受控绑定候选，单独由网络 runner 测试，不把事务通过冒充网络覆盖。故障注入包括源码两次重命名之间、配置替换及提交前的进程强制终止，以及 NAT 绑定改写失败和恢复。

网络 runner 使用受控 `ip`、`ss`、`curl` 和只读 Docker 查询 mock，覆盖地址/路由选择、端口冲突、严格 Host/TLS 参数、旧配置回载与恢复预检；Compose 仅静态检查。**不会调用 Docker 可执行文件、安装真实服务、访问公网或修改系统目录。** Windows 还显式模拟受管符号链接；这些结果不证明 Linux root 权限、真实 systemd/镜像/网络/TLS 或断电持久性已经验收。

## 6. 安装后核验

```bash
sudo licensectl status
sudo licensectl health
sudo systemctl status license-platform-backup.timer
sudo systemctl list-timers license-platform-backup.timer
```

初始后台口令只保存在：

```bash
sudo cat /etc/license-platform/secrets/admin_initial_password
```

首次成功登录后必须轮换；也可在主机 TTY 执行 `sudo licensectl reset-admin-password`，密码不会进入命令行或环境变量。已有管理员时，引导 hosted service 不会重新创建或重置账号。不要通过聊天、工单或普通日志传递口令。

检查数据库没有对主机发布端口：

```bash
docker compose \
  --project-name license-platform \
  --env-file /etc/license-platform/deployment.env \
  --file /opt/license-platform/compose.yml ps
```

## 7. 防火墙原则

- SSH 只允许可信管理来源；
- TCP 80 只允许当前确有需要的客户端来源；
- 不开放 PostgreSQL 5432；
- 不开放应用 8080；
- 启用 HTTPS 后开放 TCP 443；若使用 HTTP/3，再按需开放 UDP 443；
- 云安全组和主机防火墙要同时检查，不能假设其中一个会替代另一个。

具体命令取决于现有防火墙体系。安装脚本不擅自修改防火墙，避免破坏现有主机策略或远程管理连接。

## 8. 从 HTTP 迁移到 HTTPS

准备条件：

1. 域名模式下，把 A 记录解析到 `PUBLIC_IPV4`；IP 模式下只能使用当前部署的同一公网 IPv4；
2. TCP 80/443 可从公网和 ACME CA 到达；如需要 HTTP/3，再允许 UDP 443；
3. 443 未被其他进程或容器占用；
4. 当前 Caddy 镜像和 ACME 服务支持目标名称。公网 IPv4 证书通常是短期证书，必须监控自动续期。

在维护窗口执行：

```bash
sudo licensectl enable-https license.example.com
# 或在当前 Caddy/ACME 确实支持公网 IP 证书时：
sudo licensectl enable-https "$PUBLIC_IPV4"
```

该命令会：

- 校验域名/IP，域名 A 记录必须包含当前 `PUBLIC_IPV4`；
- 检查 TCP/UDP 443 冲突；
- 在隔离的一次性 Caddy 容器中验证 HTTPS 模板；
- 获取操作锁并自动创建数据库、配置、密钥备份；
- 保存原 `deployment.env` 和 Caddyfile 到 `/var/lib/license-platform/rollback/https-*`；
- 原子写入 `CADDY_SITE_ADDRESS=https://...`、`HTTPS_ENABLED=true`、`ALLOW_INSECURE_HTTP=false`、`HSTS_ENABLED=false`；
- 加载 `compose.https.yml`，强制重建 app/Caddy，使 Secure Cookie 和 HTTPS 重定向生效；首次切换明确不发送 HSTS；
- 最多等待 300 秒，以正常系统信任链、禁用代理和重定向的方式调用 `https://<目标>/health`；通过 `curl --connect-to` 固定连接本机 `BIND_IPV4`，保留目标公网 Host、TLS SNI 及证书名称校验（数字 IP URL 同样适用）。

这些是本机入口检查，不验证 EIP 转发、安全组或公网入站路径；还必须从外部客户端核验。

任何步骤失败都会恢复原环境文件和 Caddyfile，并发起 HTTP app/Caddy 重建；命令不会使用 `curl -k` 或忽略证书错误来伪造成功。若进程被中断，下一次执行 `enable-https` 会读取持久事务标记并先恢复经严格检查的 HTTP 基线。成功后更新所有客户端 `ServiceBaseUri`，删除配置、文档和监控中遗留的 `http://` 地址。

### 8.1 延迟启用 HSTS

HTTPS 切换成功后，至少先完成证书自动续期监控、HTTPS 可用性告警和回滚演练。确认后再执行：

```bash
sudo licensectl enable-hsts --confirm-enable-hsts ENABLE-HSTS
```

该命令会再次验证保留公网 TLS 身份的 HTTPS 本机入口基线、创建备份和事务状态，只重建应用，并要求经本机 `BIND_IPV4` 连接的 `/health` 响应精确返回 `Strict-Transport-Security: max-age=2592000`（30 天）且不包含 `includeSubDomains`。失败时会恢复服务器配置，但**无法清除浏览器已经缓存的 HSTS**；因此 HSTS 启用后必须持续保证该域名或 IP 的 HTTPS 可用。默认不包含子域名，避免把尚未验证的其他主机一并锁定。

## 9. 小内存共享主机

- 用 `free -h` 的 `available` 评估可用内存，不只看 `free`：Linux 文件缓存通常可以回收。`docker stats --no-stream` 的内存是当前快照，容器 `mem_limit` 是上限，不是预分配；单次 CPU 数据也不能证明持续瓶颈。
- 不要仅因机器是 2 核 2 GiB 就直接压低应用/数据库上限。需先观察登录、批量发卡、导出、备份和升级时的峰值，并为其他服务与操作系统留出余量。密码哈希仍保持原安全强度；授权/撤销结果不引入缓存。
- 本地 Dockerfile 使用单节点 MSBuild、串行 NuGet restore，并关闭持久构建服务器/共享编译，以约束构建并行度。它不修改运行时 CPU 配额，也不是构建进程的硬内存上限；构建可能更慢，仍可能与其他服务争抢资源。共享小内存主机优先使用外部构建并固定 digest 的应用镜像，通过 `licensectl upgrade --app-image ...` 升级。
- 首页卡密状态改为一次数据库条件聚合，整个概览由 6 次查询减少为 4 次；不缓存统计，不改变有效/过期边界。设置页读取元数据时不返回 Logo 的大 JSON，实际 Logo 下载路径保持不变。这些查询优化主要减少相关后台请求的开销，不能保证明显降低空闲时的应用内存。
- 没有 swap 会降低应对短时峰值的余地，但是否添加应由主机管理员结合磁盘空间、文件系统和其他服务决定；swap 不能替代物理内存，也不是性能提升措施。不要为了降低数字清理系统缓存或修改其他服务限制。

## 10. 卸载边界

本项目没有提供自动卸载/删卷脚本，以避免静默删除数据。若确需卸载，先制作并离线验证备份，记录卷名和镜像，再由操作者逐项确认。不得使用会影响其他项目的全局 prune 命令。
