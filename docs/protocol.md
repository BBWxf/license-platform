# 协议与运行契约

## 1. 文档状态

本文记录当前仓库中 `LicensePlatform.Contracts`、`LicensePlatform.Web` 和 `Licensing.Client` 已实现的 v1 契约。客户端协议前缀为 `/api/v1/client`，所有客户端业务路由均为带 ES256 安装密钥签名的 `POST application/json`。发布时仍应把 Contracts、迁移版本、产品公钥和本文作为同一兼容单元冻结。

## 2. 容器运行契约

应用镜像必须满足：

| 项目 | 契约 |
|---|---|
| 进程入口 | Compose 的最小 `/bin/sh` 包装入口读取数据库口令后 `exec dotnet LicensePlatform.Web.dll` |
| 正常服务参数 | 无额外参数 |
| 一次性参数 | `--migrate`、`--admin-init --username ... --password-file ...`、`--admin-reset --username ... --password-file ...`；完成后退出，不启动长期 Web 进程 |
| 运行身份 | 长期进程固定为 UID/GID 1654 非 root；仅安装/升级时用一次性、最小 capability 容器校正数据卷所有权 |
| 监听 | 容器内 `http://+:8080` |
| 健康端点 | `GET /health`，健康返回 `{"status":"healthy"}`，无需身份认证且不泄露内部信息 |
| 数据库配置 | 容器入口从 `/run/secrets/postgres_password` 创建 `/tmp/.pgpass`，再设置不含口令的 `ConnectionStrings__Database` |
| 查询/HMAC 材料 | `Security__CardLookupKeyFile=/run/secrets/card_lookup_key` |
| 私钥解密材料 | `Security__MasterEncryptionKeyFile=/run/secrets/master_encryption_key` |
| 初始管理员材料 | `Security__BootstrapAdminPasswordFile=/run/secrets/admin_initial_password` |
| Data Protection 路径 | `Security__DataProtectionPath=/app/data/data-protection` |
| 反向代理头 | 正确处理受信任 Caddy 传入的转发头，不信任任意公网伪造头 |

迁移必须可重复运行，使用数据库锁或等效机制防止并发迁移，并在任何失败时返回非零。迁移中不得静默删除业务数据；破坏性迁移需要单独发布评审、备份和回退方案。

## 3. 网络契约

当前链路：

```text
客户端 --公网 HTTP:80--> Caddy --backend 内部网络 HTTP:8080--> app
                                                   |
                                                   +--> PostgreSQL:5432
```

只有 Caddy 发布主机端口。`app` 和 `db` 都不能直接暴露到公网。Caddy 到 app 的明文流量局限在单主机 Docker 内部网络，但公网客户端到 Caddy 的当前 HTTP 仍然不安全。

未来 HTTPS 模式中，TLS 在 Caddy 终止。应用生成绝对 URL、重定向、安全 cookie 或来源判断时，必须只信任已配置代理，并正确使用转发后的 scheme/host。不能盲目信任来自任意远端的 `X-Forwarded-*`。

## 4. 时间和标识规则

当前领域模型要求：

- 实体 ID 是非空 UUID；
- 实体有递增 `Version`，可用于并发控制语义，但 API 是否暴露 ETag 以实际实现为准；
- 所有领域时间使用 UTC offset `+00:00`；
- 产品代码转为大写，只允许 `[A-Z0-9_-]`，最长 64；
- 产品名称最长 200；
- 租约为 5 到 43,200 分钟；
- 期限单位：`Days=1`、`Months=2`、`Years=3`、`Permanent=4`；
- 非永久期限值必须大于 0，永久期限值必须为 0；
- 月按 30 天、年按 365 天。

JSON 时间若由 API 暴露，应使用带 `Z` 的 RFC 3339/ISO 8601 UTC，例如 `2026-08-30T12:34:56Z`。协议不能依赖服务器或客户端本地时区。

## 5. 安装密钥请求签名

每个 Windows 安装生成一个不可导出的使用语义上的 P-256 安装私钥，并以 DPAPI 保护保存。所有四个客户端请求都带以下单值头：

| 头 | 值 |
|---|---|
| `X-Licensing-Installation` | 32 个小写十六进制字符格式的 installation UUID（无连字符） |
| `X-Licensing-Timestamp` | Unix 秒十进制文本 |
| `X-Licensing-Nonce` | 16 随机字节的无填充 Base64Url |
| `X-Licensing-Content-SHA256` | 实际 UTF-8 请求正文 SHA-256 的无填充 Base64Url |
| `X-Licensing-Key-Id` | 安装 P-256 公钥的 RFC 7638 风格 JWK thumbprint |
| `X-Licensing-Algorithm` | 固定 `ES256` |
| `X-Licensing-Signature` | 64 字节 IEEE P1363 `r || s` 签名的无填充 Base64Url |

签名输入是以下六行 UTF-8 文本，最后没有额外换行：

```text
licensing-v1
POST
<原始 PathAndQuery>
<X-Licensing-Timestamp>
<X-Licensing-Nonce>
<X-Licensing-Content-SHA256>
```

服务端校验正文哈希、时间窗口、真实 NIST P-256 曲线、公钥 thumbprint、签名和 `keyId:nonce` 重放缓存。激活挑战从正文取得安装公钥；续租挑战还会在业务层把它与现有绑定比较；完成挑战从挑战记录取得公钥；在线验证从当前有效绑定取得公钥。请求签名用于客户端持钥证明和防重放，**不能为明文 HTTP 提供保密性或服务端身份认证**。

## 6. 客户端 API v1

固定路由如下；请求和响应 JSON 使用 camelCase，正文上限 64 KiB：

| 路由 | 请求核心字段 | 成功响应 |
|---|---|---|
| `POST /api/v1/client/challenges/activation` | `productCode`、仅首次使用的 `licenseKey`、`deviceFingerprint`、`deviceHint`、`devicePublicKey` | `challengeToken`、32 字节 Base64Url `nonce`、`purpose=1`、`expiresAtUtc` |
| `POST /api/v1/client/challenges/refresh` | `licenseKeyId`、`bindingVersion`、`deviceFingerprint`、`devicePublicKey` | 同上，但 `purpose=2` |
| `POST /api/v1/client/challenges/complete` | `challengeToken`、`deviceFingerprint`、对解码后 32 字节挑战 nonce 的 ES256/P1363 `proofSignature` | `licenseKeyId`、`activatedAtUtc`、`entitlementExpiresAtUtc`、`bindingVersion`、`lease` |
| `POST /api/v1/client/validations` | `licenseKeyId`、`bindingVersion`、`deviceFingerprint` | `isValid`、`failureCode`、`entitlementExpiresAtUtc`、`currentBindingVersion` |

`deviceFingerprint` 包含 `machineGuid`、`smbiosUuid`、`systemVolumeId`。服务端只保存它们的 keyed hash；匹配策略为三项中至少两项。卡密只出现在首次激活挑战正文；服务端以 HMAC 查找，不保存卡密明文，SDK 也不在激活后缓存卡密明文。

成功完成挑战返回的 `lease` 包含 `compactToken`、`keyId`、固定 `algorithm=ES256` 和 `expiresAtUtc`。错误使用通用 Problem Details，扩展字段至少有稳定 `code` 和 `requestId`；认证失败返回 401，限流返回 429 并带 `Retry-After: 60`，内部异常、SQL、连接串和密钥不会进入响应。

### 6.1 挑战和幂等语义

- 挑战 token 只存 keyed hash，短时有效且只能消费一次；
- 首次成功完成激活时开始计算卡密总期限并建立唯一有效设备绑定；
- 同一卡密已绑定到相同指纹和相同安装公钥时，可重新取得激活用途挑战，用于恢复“服务端已完成但客户端未收到响应”的情况；
- 不同设备或不同安装公钥不能借此重绑；管理员必须先在后台解绑；
- 续租与在线验证不再提交卡密。

### 6.2 ES256 compact JWS 租约

租约是三段 compact JWS。头字段为 `alg=ES256`、`typ=JWT`、产品密钥 `kid`。当前签名载荷包含：

- `iss=license-platform`、`aud=<PRODUCT_CODE>`；
- `sub=<license UUID>`、`jti=<lease id>`；
- NumericDate `iat`、`nbf`、`exp`；
- `product_id`、`product_code`；
- keyed-hash 派生的 `device_id`；
- `installation_key_thumbprint` 和等值的 `cnf.jkt`；
- 正整数 `binding_version`；
- 可空 `entitlement_exp` 和布尔 `permanent`。

客户端只信任配置中按 `kid` 固定的产品 P-256 公钥，拒绝 `none`、算法混淆、未知 `kid`、`crit`、未编码载荷、重复 JSON 属性、错误 issuer/audience、安装密钥或产品不匹配以及超出六小时上限的租约。ES256 只提供真实性和完整性，不加密载荷。

## 7. 健康检查语义

`GET /health` 是基础存活/就绪契约：

- 健康时返回 `2xx`；
- 不要求认证，因而响应只能是通用状态，不返回版本细节、数据库信息、主机名或异常；
- 在数据库不可用、关键解密材料不可读、迁移未完成等无法安全提供服务时，应返回非 `2xx`；
- Caddy 和部署脚本依赖此端点；
- 该端点不替代端到端许可业务探测。

若要增加 `/live` 和 `/ready`，必须保持 `/health` 的部署兼容，或同步修改并测试所有部署脚本。

## 8. 版本兼容

每次发布至少记录：

- 应用镜像 digest；
- 数据库 schema/migration 版本；
- OpenAPI/Contracts 版本；
- 支持的最低客户端版本；
- 许可证载荷/签名格式版本；
- 公钥集合和状态；
- 向前/向后兼容结论；
- 回滚应用是否兼容升级后的数据库。

升级脚本会先备份并运行 `--migrate`，但自动失败处理只尝试回退应用镜像，不会自动降级数据库。协议和 schema 的兼容设计必须考虑这一边界。
