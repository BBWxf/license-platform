# 后台与日常运维

## 1. 角色分离

建议至少区分：

- **主机管理员**：root、Docker、systemd、备份介质和镜像发布；
- **平台管理员**：后台用户、产品、许可证、签名密钥和审计；
- **客户端集成人员**：只获得公开协议、产品标识和必要公钥，不接触服务器私钥、数据库口令或 root 备份。

不要共用个人后台账号。若应用支持 MFA、短会话、IP 限制和细粒度 RBAC，应全部启用。任何应用层功能以实际发布版本和 OpenAPI/后台界面为准，本文不虚构尚未实现的路由或权限。

## 2. `licensectl` 命令

所有命令默认以 root 运行，并限定在 Compose 项目 `license-platform`：

```bash
sudo licensectl status
sudo licensectl health
sudo licensectl logs app
sudo licensectl logs caddy --follow
sudo licensectl backup
sudo licensectl reset-admin-password
sudo licensectl enable-https license.example.com
# 仅在证书续期、告警和回滚演练完成后：
sudo licensectl enable-hsts --confirm-enable-hsts ENABLE-HSTS
sudo licensectl timer-status
```

查看所有子命令：

```bash
sudo licensectl --help
sudo licensectl restore --help
sudo licensectl upgrade --help
sudo licensectl rollback --help
sudo licensectl enable-https --help
sudo licensectl enable-hsts --help
```

重启只影响本项目 `app/caddy`，需要精确确认词：

```bash
sudo licensectl restart --confirm-restart RESTART
```

不要直接依赖自动生成的容器名。排障应通过 `licensectl` 或显式 Compose 项目名和配置文件操作。

## 3. 首次登录

初始后台口令位于：

```bash
sudo cat /etc/license-platform/secrets/admin_initial_password
```

要求：

1. 在受控管理终端读取，不复制到工单、聊天或 shell 脚本；
2. 首次登录立即设置独立强口令；当前版本为单一超级管理员、密码认证，不提供 MFA 或新增管理员入口；
3. Cookie 会话为非持久、30 分钟、`HttpOnly + SameSite=Strict`；HTTPS 模式还强制 `Secure`；
4. 已有管理员时，引导 hosted service 只检查后退出，不会重复创建或重置；
5. 审计中核对 `ADMIN.INITIALIZED`、登录成功/失败和密码重置事件。

### 忘记管理员密码

推荐在服务器 TTY 执行：

```bash
sudo licensectl reset-admin-password
# 非默认用户名：
sudo licensectl reset-admin-password --username admin
```

命令隐藏读取两次，要求 14 到 512 个字符，并通过短生命周期 `root:<SECRETS_GID> 0440` 临时文件传给一次性非 root app 容器；密码不会进入参数、环境变量或日志。自动化场景可传 `--password-file <root可读普通文件>`，命令会复制到受控临时文件并在退出时删除；调用方仍负责安全删除原文件。密码哈希变化后，旧管理 Cookie 的凭据摘要不再匹配，下一次请求会被拒绝并删除；数据库不可用时该检查安全失败，不继续接受旧会话。

## 4. 产品和租约参数

当前领域模型给出的约束包括：

- 产品代码会标准化为大写；
- 产品代码只允许 ASCII 字母、数字、`-` 和 `_`，最长 64；
- 产品名称最长 200；
- 租约有效期为 5 分钟到 30 天（43,200 分钟）；
- 产品可以启用或停用。

运维建议：

- 产品代码一旦被客户端和许可证引用，不要随意复用或改变语义；
- 短租约降低撤销传播时间，但提高客户端对网络和平台可用性的依赖；
- 长租约提高离线容忍，但延长禁用/撤销生效窗口；
- 变更租约前先评估客户端续租退避、时钟误差和平台故障时的行为；
- 停用产品是高影响动作，应有双人复核和审计记录。

## 5. 许可证期限

领域模型支持：

- 天；
- 月，按 30 天换算；
- 年，按 365 天换算；
- 永久，值必须为 0，且没有有限到期时间。

所有时间应使用 UTC。业务方应清楚“月/年”不是自然月/闰年日历计算，避免合同文本与系统计算口径不一致。

## 6. 签名密钥操作

当前模型的产品签名算法为 `ES256`，密钥状态包括：

- `Active`：用于新签名；
- `Retired`：停止新签名，但客户端通常仍需保留其公钥以验证历史许可证；
- `Revoked`：已撤销，应按协议和业务策略拒绝或升级处置。

关键原则：

1. 私钥不得导出给客户端；客户端只分发公钥/公钥集合；
2. 新密钥上线前，先把公钥传播到所有需要离线验证的客户端；
3. 观察传播覆盖后再切换新签名；
4. 先退休、经过最长许可证/租约兼容窗口后再考虑撤销；
5. 撤销是高影响动作，不能等同于删除；
6. 备份中的应用解密密钥决定加密私钥能否恢复，必须同时保护和演练；
7. 日志中不得输出私钥、解密密钥、完整许可证或 bearer token。

具体密钥轮换按钮/API 以运行版本为准。没有实现证明时，不应声称系统已自动轮换或自动撤销。

## 7. 日志与审计

```bash
sudo licensectl logs app
sudo licensectl logs caddy
sudo journalctl -u license-platform-backup.service
```

Caddy 访问日志输出到容器标准输出，并由 Docker `json-file` 轮转（单文件 10 MiB、最多 5 个）。应用必须实施自己的安全日志脱敏。

建议审计事件至少包括：

- 登录成功/失败、MFA 和会话撤销；
- 管理员和权限变更；
- 产品启停和租约参数变更；
- 许可证签发、吊销或状态变更；
- 签名密钥创建、退休、撤销；
- 备份、恢复、升级和回滚；
- 配置及镜像版本变更。

访问日志可能含客户端 IP、路径和用户代理，属于敏感运维数据。不得把 token 放在查询字符串，否则可能进入代理、浏览器和上游日志。

## 8. 常见排障

### 容器不健康

```bash
sudo licensectl status
sudo licensectl logs db
sudo licensectl logs app
sudo licensectl logs caddy
sudo licensectl health
```

先定位是数据库、迁移、应用健康端点还是反向代理问题。不要删除卷尝试修复。

### 端口冲突

安装器发现 TCP 80 被其他容器/进程占用会退出。应识别所有者并由其负责人处理；安装器不会停止它。不要通过改动现有容器来“让路”，除非该容器的所有者明确批准并有变更计划。

### 磁盘增长

检查 Docker 卷、镜像、容器日志和 `/var/backups/license-platform`。备份脚本只删除符合本项目命名且超过 14 天的本地备份与校验文件，并记录删除。它不会清理 Docker 镜像、卷或其他目录。

### 数据库问题

数据库没有主机端口。临时进入数据库应通过本项目 Compose：

```bash
sudo docker compose \
  --project-name license-platform \
  --env-file /etc/license-platform/deployment.env \
  --file /opt/license-platform/compose.yml \
  exec db psql --username license_platform --dbname license_platform
```

不要为了图方便发布 5432，也不要把数据库口令放入 shell 历史。
