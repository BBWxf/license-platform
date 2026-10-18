# 备份与恢复

## 1. 备份范围和安全属性

每个 `license-platform-backup-<UTC时间>.tar.gz` 包含：

- PostgreSQL `pg_dump` 自定义格式导出，带数据库创建信息；
- `/etc/license-platform` 配置归档；
- PostgreSQL 口令；
- 查询/HMAC 密钥和应用主解密密钥；
- 初始后台口令文件；
- 镜像引用、容器镜像 ID 等清单；
- 归档内文件 SHA-256 校验和；
- 外部 `<备份>.sha256` 校验文件。

> **备份包含数据库凭据和解密密钥，能够解密/恢复敏感平台数据。备份本身没有额外加密。**
>
> 本地权限是 root-only，但 root 权限不能防止主机整体失陷。复制到离线或异地主机前，必须使用组织批准的加密、密钥托管和访问审计方案。

本地备份默认在 `/var/backups/license-platform`，保留 14 天。它与服务位于同一主机，不能应对整机损坏、云账号失陷、勒索软件或机房事件，**不等于灾难恢复**。

## 2. 每日 systemd timer

安装器已获得明确授权，会创建并启用：

- `license-platform-backup.service`；
- `license-platform-backup.timer`。

计划为每天 03:30，附加最多 30 分钟随机延迟，`Persistent=true`。主机在计划时间关机时，systemd 会在后续启动后补跑。

检查：

```bash
sudo systemctl status license-platform-backup.timer
sudo systemctl list-timers license-platform-backup.timer
sudo journalctl -u license-platform-backup.service
```

手工触发并检查：

```bash
sudo systemctl start license-platform-backup.service
sudo systemctl status license-platform-backup.service
sudo ls -l /var/backups/license-platform
```

不要因为 timer 显示 active 就假设备份成功；必须监控 service 退出状态、备份新鲜度、大小异常和异地复制结果。

## 3. 手工备份

```bash
sudo licensectl backup
```

脚本行为：

1. 取得备份锁，防止并发；
2. 确认本项目 PostgreSQL 容器正在运行；
3. 运行一致性 `pg_dump`，不停止应用；
4. 归档配置和解密密钥；
5. 写入内部及外部 SHA-256；
6. 原子移动完成的归档；
7. 明确记录并删除超过 14 天、且匹配本项目备份命名的文件。

脚本不会清理数据库卷、Docker 卷、镜像、其他目录或其他项目备份。临时 `.partial` 文件不会被当作成功备份。

## 4. 异地副本

最低要求：

- 采用批准的强加密后再离开主机；
- 加密密钥与备份介质分开管理；
- 目标使用不可变/对象锁或离线介质；
- 限制读取、列出、删除权限；
- 保留访问和删除审计；
- 定期验证外部 SHA-256、解密和完整恢复；
- 记录 RPO、RTO 和保存期限；
- 不把解密口令放在同一脚本、同一目录或命令行历史中。

本项目不擅自选择云存储或 KMS，也不把未加密备份自动上传到第三方。

## 5. 恢复前检查

恢复是破坏性操作，必须维护窗口和业务批准。确认：

- 备份来自可信来源；
- 外部 `.sha256` 一同取得，并通过独立受信任渠道核对；
- 目标应用版本、数据库版本和 schema 兼容；
- 磁盘空间足够；
- 已记录当前镜像、配置和故障现象；
- 客户端流量已被通知或隔离；
- 明白脚本只停止本项目 `app/caddy`，不影响其他 Compose 项目；
- 明白恢复失败后应用会保持停止，防止连接部分恢复的数据。

恢复脚本会在覆盖前再创建一个当前状态安全备份。

## 6. 只恢复数据库

```bash
sudo licensectl restore \
  --backup /var/backups/license-platform/license-platform-backup-<时间>.tar.gz \
  --confirm-restore RESTORE-DATABASE
```

也可选择当前目录中最新备份：

```bash
sudo licensectl restore \
  --backup latest \
  --confirm-restore RESTORE-DATABASE
```

流程：校验归档、创建安全备份、停止本项目 `caddy/app`、通过 `pg_restore --clean --create` 覆盖本项目数据库、重启并执行健康检查。

数据库恢复不会默认覆盖当前配置或解密密钥。适用于同一部署、当前密钥仍正确的恢复场景。

## 7. 恢复数据库、配置和解密密钥

灾难恢复或密钥丢失时，需要额外确认：

```bash
sudo licensectl restore \
  --backup /secure/path/license-platform-backup-<时间>.tar.gz \
  --confirm-restore RESTORE-DATABASE \
  --restore-config \
  --confirm-config-overwrite OVERWRITE-CONFIG
```

此模式还会：

- 在 `/var/lib/license-platform/rollback` 保存恢复前配置副本；
- 严格校验恢复的 `deployment.env` 键和值格式；
- 覆盖受管 `deployment.env`、Caddyfile 和四个 secret（数据库口令、查询/HMAC 密钥、主解密密钥、初始后台口令）；
- 把恢复的 PostgreSQL 口令同步到数据库角色；
- 使用恢复后的应用解密密钥启动服务。

目标服务器网络地址、域名和镜像仓库可能与备份时不同。全配置恢复后，应在启动前/失败后核对 `PUBLIC_IPV4`、`CADDY_SITE_ADDRESS`、`HTTPS_ENABLED`、镜像引用和本机接口。若备份来自另一环境，不要直接恢复到生产，避免跨环境密钥和数据混用。

## 8. 恢复失败

若 `pg_restore` 或配置校验失败，脚本不自动启动 `app/caddy`。保留：

- PostgreSQL 容器；
- 恢复前安全备份；
- 原始备份；
- 错误日志。

收集：

```bash
sudo licensectl status
sudo licensectl logs db
sudo journalctl -u license-platform-backup.service
```

不要反复执行恢复、删除数据库卷或使用全局 prune。先确定失败阶段和数据状态，再决定使用恢复前安全备份、修复目标环境，或在隔离服务器演练。

## 9. 恢复演练

至少每季度，且在重要升级前，执行隔离恢复演练：

1. 使用与生产隔离的主机/网络；
2. 验证备份解密和 SHA-256；
3. 恢复数据库、配置和密钥；
4. 验证后台登录、产品、许可证和签名验证；
5. 验证历史 ES256 许可证仍能被客户端公钥验证；
6. 记录实际 RPO/RTO、人工步骤和缺口；
7. 安全销毁演练环境和副本。

只有成功恢复并验证业务语义的备份，才是可信备份。
