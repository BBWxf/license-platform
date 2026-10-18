# License Platform

ASP.NET Core 10 许可证平台及其 Ubuntu/Debian 单机容器部署运维文件。

## 严重安全警告

> 当前获准实现的 `http-ip` 模式通过固定公网 IPv4 的 HTTP 80 提供服务。
> **公网明文 HTTP 不提供加密、完整性或服务端身份认证，不是安全部署。**
> 口令、token、许可证和设备数据可能被窃听或篡改。

`--accept-insecure-http` 只是操作者的风险确认。正式生产应尽快执行 `sudo licensectl enable-https <domain-or-public-ipv4>`；在此之前使用防火墙、安全组、VPN/专线和来源白名单降低暴露。

## 架构

- `caddy`：公网入口，反向代理到应用；
- `app`：`src/LicensePlatform.Web/LicensePlatform.Web.csproj` 发布产物，容器内监听 8080；
- `db`：PostgreSQL 18，只在 internal Docker 网络中可达，不发布 5432；
- `license-platform-backup.timer`：每日本地备份，保留 14 天。

部署脚本只操作 Compose 项目 `license-platform`，不会停止或修改其他现有容器。端口冲突时安装会退出。

## 当前实现

仓库包含 ASP.NET Core 10 Razor Pages 管理后台、四个已签名客户端 API、EF Core/Npgsql 初始迁移，以及兼容 .NET Framework 4.8、.NET 6 和 .NET 8 的客户端 SDK。Web 入口支持一次性 `--migrate`、`--admin-init`、`--admin-reset` 命令和不泄露内部信息的 `GET /health`。客户端首次激活后不保存卡密明文，后续启动使用 DPAPI 保护的安装密钥和已签名租约缓存。

## 快速安装

主机需为 Ubuntu/Debian、systemd、Docker Engine、Compose v2，建议至少 2 核 2 GiB 和 10 GiB 可用磁盘。固定公网 IPv4 必须实际配置在本机接口上。

从源码构建：

```bash
sudo ./deploy/install.sh \
  --mode http-ip \
  --public-ip "$PUBLIC_IPV4" \
  --accept-insecure-http
```

使用预构建且固定 digest 的应用镜像：

```bash
sudo ./deploy/install.sh \
  --mode http-ip \
  --public-ip "$PUBLIC_IPV4" \
  --accept-insecure-http \
  --app-image 'registry.example.com/license-platform/web@sha256:<64位digest>'
```

禁止隐式镜像标签和 `latest`。仓库中的基础镜像使用明确版本变量；正式发布仍应换成经验证的 digest。

安装器会检查 root、OS、内存、磁盘、端口、Docker/Compose，生成位于 root-only 父目录中的 `root:<SECRETS_GID> 0440` secrets，仅通过容器补充组只读挂载；长期 `app` 进程固定以 UID/GID 1654 非 root 运行。随后启动数据库、运行 `app --migrate`、启动服务、检查 `/health`，并创建及启用已获授权的每日 systemd timer。

## 运维入口

```bash
sudo licensectl status
sudo licensectl health
sudo licensectl logs app
sudo licensectl backup
sudo licensectl reset-admin-password
sudo licensectl enable-https license.example.com
sudo licensectl timer-status
```

破坏性或有停机影响的恢复、升级和回滚都要求精确确认词：

```bash
sudo licensectl restore --help
sudo licensectl upgrade --help
sudo licensectl rollback --help
```

脚本不提供静默删卷、全局 prune 或自动卸载。

## 文档

- [Ubuntu / Debian 部署](docs/deployment.md)
- [后台与日常运维](docs/admin-operations.md)
- [客户端接入](docs/client-integration.md)
- [协议与运行契约](docs/protocol.md)
- [备份与恢复](docs/backup-restore.md)
- [升级与回滚](docs/upgrade-rollback.md)
- [安全边界与威胁模型](docs/security-boundary.md)
- [安全问题报告](SECURITY.md)
- [开发验证与验收边界](docs/verification.md)

## 重要运行契约

应用镜像必须：

- 入口为 `dotnet LicensePlatform.Web.dll`；
- 支持幂等 `--migrate` 以及 `--admin-init`、`--admin-reset` 一次性命令；
- 监听 8080；
- 提供无敏感信息的 `GET /health`；
- 提供 `/api/v1/client` 下的激活挑战、续租挑战、挑战完成和在线验证路由；
- 从部署声明的文件路径读取查询/HMAC 密钥、主解密密钥和初始管理员口令。

详见[协议与运行契约](docs/protocol.md)。
