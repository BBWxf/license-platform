# 升级与回滚

## 1. 重要边界

- 升级只操作 `license-platform` Compose 项目的 `app/caddy`；数据库容器保持运行；其他项目/容器不受操作。
- 升级会造成短暂停机，并在新应用镜像上运行 `--migrate`。
- 升级前自动备份数据库、配置和解密密钥。
- 脚本保存旧应用镜像的本地回滚标签和部署环境。
- **应用镜像可以回退，数据库迁移不会自动降级。** 旧应用是否兼容新 schema 必须由发布测试证明。
- 如需恢复升级前数据库，必须另外运行 `licensectl restore` 并提供破坏性操作确认；这会丢弃升级后产生的数据。

## 2. 发布前检查

- 在独立环境完成镜像扫描、签名/来源证明和 SBOM 审阅；
- 固定应用镜像 `sha256` digest，禁止 `latest`；
- 审阅所有迁移是否可重复、是否锁表、是否删除或不可逆转换数据；
- 验证 2 核 2 GiB 环境的迁移时间和峰值内存；
- 验证新应用读取旧 schema、旧应用读取新 schema 的兼容矩阵；
- 运行备份恢复演练；
- 确认磁盘可容纳新旧镜像、数据库增长和备份；
- 通知维护窗口并准备业务验收；
- 记录当前 `licensectl status` 和 `licensectl health`。

## 3. 使用预构建镜像升级

生产推荐：

```bash
sudo licensectl upgrade \
  --app-image 'registry.example.com/license-platform/web@sha256:<64位digest>' \
  --confirm-upgrade UPGRADE
```

流程：

1. 获取全局运维锁；
2. 为当前应用镜像创建 `license-platform-rollback:<UTC时间>` 本地标签；
3. 创建升级前备份；
4. 保存 `/var/lib/license-platform/rollback/<UTC时间>/state.env` 和旧 `deployment.env`；
5. 在停机前拉取新镜像；
6. 仅停止本项目 `caddy/app`；
7. 更新受管应用镜像变量；
8. 运行新镜像 `--migrate`；
9. 启动并执行健康检查。

私有仓库凭据不要放命令行。提前使用组织批准的 Docker credential helper 登录。

## 4. 本地源码构建升级

仅适合受控环境；生产更推荐 CI 构建、签名并发布固定 digest：

```bash
sudo licensectl upgrade \
  --build-local-image 'license-platform:0.2.0-local' \
  --source /secure/path/to/release-source \
  --confirm-upgrade UPGRADE
```

脚本把根 Dockerfile、构建属性和 `src/` 复制到全新的 `/opt/license-platform/source.stage.*`，从 staging 使用 `--pull` 构建；构建成功并进入切换事务后才原子提升到 `/opt/license-platform/source`。旧源码移动到 `source.before-*`，因此不会残留新版本已经删除的文件，并可在失败或中断恢复。源码目录必须包含 Web csproj 和部署运行契约标记。

本地构建增加供应链、资源耗尽和不可重复风险；不要在生产主机从未经审核的工作树构建。

## 5. 升级失败

退出失败时，脚本会尝试：

- 恢复旧 `deployment.env` 和旧来源模式；
- 若本地源码已经提升，恢复旧源码目录；
- 把按旧 image ID 固定的本地回滚标签持久写回 `deployment.env`；
- 强制重建 `app/caddy`，等待就绪并执行严格公网健康检查；
- 保留数据库、备份和回滚状态。

切换前会创建 `/var/lib/license-platform/upgrade.pending`。若进程被 SIGKILL 或主机断电，下一次执行 `upgrade` 会先尝试恢复旧镜像、配置和源码；恢复成功后命令会停止并要求操作者重新发起升级。回滚失败时标记保留，脚本返回独立失败状态，不能假定服务健康。

它不自动撤销已执行的数据库迁移。立即检查：

```bash
sudo licensectl status
sudo licensectl logs app
sudo licensectl logs db
sudo licensectl health
```

如果旧应用不兼容新 schema，不要反复重启。评估修复前向发布，或经业务批准恢复升级前数据库备份。

## 6. 手工应用镜像回滚

列出状态：

```bash
sudo ls -la /var/lib/license-platform/rollback
```

回到最新保存的应用镜像：

```bash
sudo licensectl rollback \
  --state latest \
  --confirm-rollback ROLLBACK-APPLICATION
```

选择特定状态目录名：

```bash
sudo licensectl rollback \
  --state '<UTC状态目录名>' \
  --confirm-rollback ROLLBACK-APPLICATION
```

`latest` 只在 UTC 时间戳命名的应用升级目录中选择可回退状态：版本 2 必须为 `SUCCEEDED`；兼容版本 1 无 STATUS 或 `SUCCEEDED`。`https-*`、`hsts-*`、`install-*`、`manual-*`、未完成/失败升级以及已经消费的回退状态都不会被误选。显式选择也要求同样的应用升级状态规则。

回滚前脚本验证当前部署健康、再次备份当前状态，并按当前容器实际 image ID 创建 `license-platform-manual-rollback:<时间>` 安全标签。版本 2 状态会同时恢复升级前来源模式；若升级使用本地源码，还会把旧源码目录恢复到受管路径，并保留较新源码安全副本。镜像和来源模式一次原子写入，随后强制重建 `app/caddy`、等待就绪并执行严格健康检查。

停机与源码切换前，脚本保存回退前环境和源码事务路径到 `rollback/manual-<UTC时间>/`，原子写入并刷新 `/var/lib/license-platform/manual-rollback.pending`。普通失败尝试恢复回退前**实际镜像**、配置和源码；无法恢复时返回独立失败状态并保留标记，不会伪报成功。若进程被强制终止，再次执行相同确认的 `licensectl rollback` 会先处理该标记，恢复回退前部署并做严格健康检查；成功后删除标记、恢复升级状态的可回退资格，以非零退出要求重新发起回退。恢复过程自身也允许在源码两次重命名之间再次中断后重试；对目录缺失、符号链接或路径冲突会拒绝覆盖。

升级、手工回退、安装重跑、HTTPS/HSTS 切换的 pending 标记互斥：只有对应命令能豁免自己的标记进入恢复；其他修改部署的命令（包括数据库恢复和重置管理员密码）会拒绝继续。不要手工删除标记、源码安全副本、事务快照或安全镜像来绕过恢复。它不会运行旧迁移，也不会恢复数据库。

## 7. 是否恢复数据库

只有在以下条件同时满足时才考虑：

- 新迁移无法前向修复；
- 旧应用无法兼容新 schema；
- 已明确升级后数据会丢失；
- 业务、数据负责人和安全负责人批准；
- 升级前备份已在隔离环境验证。

升级状态的 `PRE_UPGRADE_BACKUP` 指向备份路径。核对后，按[备份与恢复](backup-restore.md)使用双重明确确认。不要把应用回滚和数据库恢复合并成无确认的一键动作。

## 8. 清理旧回滚状态

脚本不会自动删除回滚镜像或状态，因为不知道组织保留和审计要求。磁盘需要清理时：

1. 确认新版本稳定；
2. 确认相关数据库和配置备份已验证并有异地副本；
3. 记录要删除的具体状态目录和镜像标签；
4. 逐个删除本项目对象；
5. 不使用 `docker system prune --volumes`。

清理属于人工受控动作，不应写成会静默删除数据的定时任务。
