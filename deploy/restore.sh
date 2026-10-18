#!/usr/bin/env bash
# Restore a backup after explicit destructive-operation confirmation.

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
COMMON_PATH="${SCRIPT_DIR}/../lib/common.sh"
[[ -r "${COMMON_PATH}" ]] || COMMON_PATH="${SCRIPT_DIR}/lib/common.sh"
# shellcheck source=deploy/lib/common.sh
source "${COMMON_PATH}"

BACKUP_PATH=""
CONFIRM_VALUE=""
RESTORE_CONFIG=0
CONFIG_CONFIRM_VALUE=""

usage() {
  cat <<'USAGE'
用法:
  sudo licensectl restore --backup <备份.tar.gz|latest> --confirm-restore RESTORE-DATABASE
  sudo licensectl restore --backup <备份.tar.gz|latest> --confirm-restore RESTORE-DATABASE \
    --restore-config --confirm-config-overwrite OVERWRITE-CONFIG

数据库恢复会先创建安全备份，然后仅停止本项目的 app/caddy，覆盖本项目数据库。
--restore-config 还会覆盖 /etc/license-platform 下受管配置与解密密钥。
USAGE
}

while (($# > 0)); do
  case "$1" in
    --backup)
      (($# >= 2)) || fatal "--backup 缺少值"
      BACKUP_PATH="$2"
      shift 2
      ;;
    --confirm-restore)
      (($# >= 2)) || fatal "--confirm-restore 缺少值"
      CONFIRM_VALUE="$2"
      shift 2
      ;;
    --restore-config)
      RESTORE_CONFIG=1
      shift
      ;;
    --confirm-config-overwrite)
      (($# >= 2)) || fatal "--confirm-config-overwrite 缺少值"
      CONFIG_CONFIRM_VALUE="$2"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *) usage >&2; fatal "未知参数: $1" ;;
  esac
done

require_root
require_command docker
require_command flock
require_command tar
require_command sha256sum
require_command sed
load_deployment_env

confirm_exact "${CONFIRM_VALUE}" "RESTORE-DATABASE" "恢复会覆盖当前数据库，未获得精确确认"
if (( RESTORE_CONFIG == 1 )); then
  confirm_exact "${CONFIG_CONFIRM_VALUE}" "OVERWRITE-CONFIG" "恢复配置和解密密钥需要额外精确确认"
fi

if [[ "${BACKUP_PATH}" == "latest" ]]; then
  BACKUP_PATH="$(latest_backup_archive)"
fi
[[ -n "${BACKUP_PATH}" && -f "${BACKUP_PATH}" ]] || fatal "备份文件不存在: ${BACKUP_PATH:-<empty>}"
BACKUP_PATH="$(readlink -f "${BACKUP_PATH}")"

exec 8>"${LP_DATA_DIR}/operation.lock"
flock --nonblock 8 || fatal "已有恢复或升级任务正在运行。"
reject_pending_transactions

if [[ -f "${BACKUP_PATH}.sha256" ]]; then
  expected_hash="$(cut -d ' ' -f 1 <"${BACKUP_PATH}.sha256")"
  actual_hash="$(sha256sum "${BACKUP_PATH}" | cut -d ' ' -f 1)"
  [[ "${expected_hash}" == "${actual_hash}" ]] || fatal "备份归档外部 SHA-256 校验失败。"
else
  warn "未找到外部 .sha256 文件；仍会验证归档内部文件校验，但无法证明归档未被整体替换。"
fi

WORK_DIR="$(mktemp -d "${LP_DATA_DIR}/restore.XXXXXX")"
cleanup() {
  rm -rf -- "${WORK_DIR}"
}
trap cleanup EXIT

mapfile -t outer_members < <(tar --list --gzip --file "${BACKUP_PATH}")
EXPECTED_OUTER_MEMBERS=(database.dump configuration.tar.gz manifest.txt SHA256SUMS)
(( ${#outer_members[@]} == ${#EXPECTED_OUTER_MEMBERS[@]} )) || fatal "备份归档成员数量无效。"
for expected in "${EXPECTED_OUTER_MEMBERS[@]}"; do
  occurrences=0
  for entry in "${outer_members[@]}"; do
    if [[ "${entry}" == "${expected}" ]]; then
      ((occurrences += 1))
    fi
  done
  (( occurrences == 1 )) || fatal "备份缺少或重复成员: ${expected}"
done
while IFS= read -r verbose_entry; do
  [[ "${verbose_entry:0:1}" == "-" ]] || fatal "备份包含非普通文件，拒绝恢复。"
done < <(tar --list --verbose --gzip --file "${BACKUP_PATH}")

tar --extract --gzip --file "${BACKUP_PATH}" --directory "${WORK_DIR}" \
  --no-same-owner --no-same-permissions
for required in database.dump configuration.tar.gz manifest.txt SHA256SUMS; do
  [[ -f "${WORK_DIR}/${required}" && ! -L "${WORK_DIR}/${required}" ]] || fatal "备份缺少文件: ${required}"
done
(
  cd "${WORK_DIR}"
  sha256sum --check SHA256SUMS
)

grep -qx 'format_version=1' "${WORK_DIR}/manifest.txt" || fatal "不支持的备份格式版本。"
grep -qx "database=${POSTGRES_DB}" "${WORK_DIR}/manifest.txt" || fatal "备份数据库名与当前部署不一致。"
[[ -s "${WORK_DIR}/database.dump" ]] || fatal "数据库备份为空。"
(( $(stat -c '%s' "${WORK_DIR}/configuration.tar.gz") <= 16777216 )) || fatal "配置归档超过 16 MiB 安全上限。"
(( $(stat -c '%s' "${WORK_DIR}/manifest.txt") <= 65536 )) || fatal "备份清单超过 64 KiB 安全上限。"
(( $(stat -c '%s' "${WORK_DIR}/SHA256SUMS") <= 65536 )) || fatal "校验清单超过 64 KiB 安全上限。"

validate_restored_env() {
  local file="$1" line key value
  declare -A seen_keys=()
  while IFS= read -r line || [[ -n "${line}" ]]; do
    [[ -z "${line}" || "${line}" == \#* ]] && continue
    [[ "${line}" =~ ^([A-Z][A-Z0-9_]*)=([A-Za-z0-9_./:@-]+)$ ]] || fatal "恢复的 deployment.env 含不安全或无效行。"
    key="${BASH_REMATCH[1]}"
    value="${BASH_REMATCH[2]}"
    [[ ! ${seen_keys["${key}"]+_} ]] || fatal "恢复的 deployment.env 含重复键: ${key}"
    seen_keys["${key}"]=1
    case "${key}" in
      INSTALL_DIR|CONFIG_DIR|DATA_DIR|BACKUP_DIR|SOURCE_DIR|PUBLIC_IPV4|BIND_IPV4|CADDY_SITE_ADDRESS|HTTPS_ENABLED|ALLOW_INSECURE_HTTP|HSTS_ENABLED|SECRETS_GID|APP_IMAGE|APP_SOURCE_MODE|POSTGRES_IMAGE|CADDY_IMAGE|DOTNET_SDK_IMAGE|DOTNET_ASPNET_IMAGE|POSTGRES_DB|POSTGRES_USER|BACKUP_RETENTION_DAYS|MIGRATION_TIMEOUT_SECONDS|APP_MEMORY_LIMIT|APP_CPU_LIMIT|DB_MEMORY_LIMIT|DB_CPU_LIMIT|CADDY_MEMORY_LIMIT|CADDY_CPU_LIMIT) ;;
      *) fatal "恢复的 deployment.env 含未知键: ${key}" ;;
    esac
    [[ -n "${value}" ]] || fatal "恢复的 deployment.env 含空值: ${key}"
  done <"${file}"
}

restored_env_value() {
  local key="$1" line
  line="$(grep -E "^${key}=" "${RESTORED_CONFIG}/deployment.env" || true)"
  printf '%s\n' "${line#*=}"
}

prepare_restored_bind_ipv4() {
  RESTORED_PUBLIC_IPV4="$(restored_env_value PUBLIC_IPV4)"
  is_usable_bind_ipv4 "${RESTORED_PUBLIC_IPV4}" || fatal "备份 PUBLIC_IPV4 无效。"
  RESTORED_BIND_IPV4="$(restored_env_value BIND_IPV4)"
  if [[ -z "${RESTORED_BIND_IPV4}" ]]; then
    RESTORED_BIND_IPV4="${RESTORED_PUBLIC_IPV4}"
    write_env_value "${RESTORED_CONFIG}/deployment.env" BIND_IPV4 "${RESTORED_BIND_IPV4}"
    log "旧版配置备份未包含 BIND_IPV4；预检阶段已保留原公网地址绑定。"
  fi
  require_local_bind_ipv4 "${RESTORED_BIND_IPV4}"
}

validate_base64_secret() {
  local path="$1" expected_length="$2" expected_bytes="$3" label="$4"
  local value decoded_bytes file_size line_count
  [[ -f "${path}" && ! -L "${path}" ]] || fatal "恢复配置缺少秘密文件: ${label}"
  file_size="$(stat -c '%s' "${path}")"
  [[ "${file_size}" == "$((expected_length + 1))" ]] || fatal "恢复的秘密文件必须包含预期 Base64 值和一个末尾换行: ${label}"
  line_count="$(wc -l <"${path}")"
  [[ "${line_count//[[:space:]]/}" == "1" ]] || fatal "恢复的秘密文件必须精确包含一行: ${label}"
  IFS= read -r value <"${path}" || fatal "无法读取恢复的秘密文件: ${label}"
  [[ "${#value}" == "${expected_length}" && "${value}" =~ ^[A-Za-z0-9+/]+={0,2}$ ]] || fatal "恢复的秘密文件格式无效: ${label}"
  decoded_bytes="$(printf '%s' "${value}" | base64 --decode 2>/dev/null | wc -c)"
  [[ "${decoded_bytes//[[:space:]]/}" == "${expected_bytes}" ]] || fatal "恢复的秘密文件不是预期长度的有效 Base64: ${label}"
  unset value decoded_bytes
}

if (( RESTORE_CONFIG == 1 )); then
  require_command base64
  CONFIG_STAGE="${WORK_DIR}/config-stage"
  install -d -m 0700 "${CONFIG_STAGE}"
  EXPECTED_CONFIG_MEMBERS=(
    deployment.env
    Caddyfile
    secrets/postgres_password
    secrets/card_lookup_key
    secrets/master_encryption_key
    secrets/admin_initial_password
  )
  mapfile -t archived_config_members < <(tar --list --gzip --file "${WORK_DIR}/configuration.tar.gz")
  (( ${#archived_config_members[@]} == ${#EXPECTED_CONFIG_MEMBERS[@]} )) || fatal "配置归档成员数量无效。"
  [[ "${archived_config_members[0]}" == "deployment.env" ]] || fatal "配置归档格式无效。"
  for expected in "${EXPECTED_CONFIG_MEMBERS[@]}"; do
    occurrences=0
    for entry in "${archived_config_members[@]}"; do
      if [[ "${entry}" == "${expected}" ]]; then
        ((occurrences += 1))
      fi
    done
    (( occurrences == 1 )) || fatal "配置归档缺少或重复成员: ${expected}"
  done
  while IFS= read -r verbose_entry; do
    [[ "${verbose_entry:0:1}" == "-" ]] || fatal "配置归档包含非普通文件，拒绝恢复。"
  done < <(tar --list --verbose --gzip --file "${WORK_DIR}/configuration.tar.gz")

  tar --extract --gzip --file "${WORK_DIR}/configuration.tar.gz" \
    --directory "${CONFIG_STAGE}" --no-same-owner --no-same-permissions

  RESTORED_CONFIG="${CONFIG_STAGE}"
  [[ -f "${RESTORED_CONFIG}/deployment.env" && ! -L "${RESTORED_CONFIG}/deployment.env" ]] || fatal "恢复配置缺少 deployment.env。"
  [[ -f "${RESTORED_CONFIG}/Caddyfile" && ! -L "${RESTORED_CONFIG}/Caddyfile" ]] || fatal "恢复配置缺少 Caddyfile。"
  (( $(stat -c '%s' "${RESTORED_CONFIG}/deployment.env") <= 262144 )) || fatal "恢复的 deployment.env 超过 256 KiB 安全上限。"
  (( $(stat -c '%s' "${RESTORED_CONFIG}/Caddyfile") <= 262144 )) || fatal "恢复的 Caddyfile 超过 256 KiB 安全上限。"
  validate_restored_env "${RESTORED_CONFIG}/deployment.env"
  if [[ -z "$(restored_env_value HSTS_ENABLED)" ]]; then
    write_env_value "${RESTORED_CONFIG}/deployment.env" HSTS_ENABLED false
    log "旧版配置备份未包含 HSTS_ENABLED；预检阶段已安全迁移为 false。"
  fi

  prepare_restored_bind_ipv4

  for required_key in INSTALL_DIR CONFIG_DIR DATA_DIR BACKUP_DIR SOURCE_DIR PUBLIC_IPV4 BIND_IPV4 CADDY_SITE_ADDRESS HTTPS_ENABLED ALLOW_INSECURE_HTTP HSTS_ENABLED SECRETS_GID APP_IMAGE APP_SOURCE_MODE POSTGRES_IMAGE CADDY_IMAGE DOTNET_SDK_IMAGE DOTNET_ASPNET_IMAGE POSTGRES_DB POSTGRES_USER; do
    [[ -n "$(restored_env_value "${required_key}")" ]] || fatal "恢复的 deployment.env 缺少必需键: ${required_key}"
  done

  [[ "$(restored_env_value INSTALL_DIR)" == "${LP_HOME}" ]] || fatal "备份 INSTALL_DIR 与当前部署不一致。"
  [[ "$(restored_env_value CONFIG_DIR)" == "${LP_CONFIG_DIR}" ]] || fatal "备份 CONFIG_DIR 与当前部署不一致。"
  [[ "$(restored_env_value DATA_DIR)" == "${LP_DATA_DIR}" ]] || fatal "备份 DATA_DIR 与当前部署不一致。"
  [[ "$(restored_env_value BACKUP_DIR)" == "${LP_BACKUP_DIR}" ]] || fatal "备份 BACKUP_DIR 与当前部署不一致。"
  [[ "$(restored_env_value POSTGRES_DB)" == "${POSTGRES_DB}" ]] || fatal "备份 PostgreSQL 数据库名与当前部署不一致。"
  [[ "$(restored_env_value POSTGRES_USER)" == "${POSTGRES_USER}" ]] || fatal "备份 PostgreSQL 用户与当前部署不一致。"

  RESTORED_HTTPS_ENABLED="$(restored_env_value HTTPS_ENABLED)"
  RESTORED_ALLOW_INSECURE_HTTP="$(restored_env_value ALLOW_INSECURE_HTTP)"
  RESTORED_HSTS_ENABLED="$(restored_env_value HSTS_ENABLED)"
  RESTORED_HSTS_ENABLED="${RESTORED_HSTS_ENABLED:-false}"
  [[ "${RESTORED_HTTPS_ENABLED}" == "true" || "${RESTORED_HTTPS_ENABLED}" == "false" ]] || fatal "备份 HTTPS_ENABLED 无效。"
  [[ "${RESTORED_ALLOW_INSECURE_HTTP}" == "true" || "${RESTORED_ALLOW_INSECURE_HTTP}" == "false" ]] || fatal "备份 ALLOW_INSECURE_HTTP 无效。"
  [[ "${RESTORED_HSTS_ENABLED}" == "true" || "${RESTORED_HSTS_ENABLED}" == "false" ]] || fatal "备份 HSTS_ENABLED 无效。"
  RESTORED_CADDY_SITE_ADDRESS="$(restored_env_value CADDY_SITE_ADDRESS)"
  if [[ "${RESTORED_HTTPS_ENABLED}" == "true" ]]; then
    [[ "${RESTORED_ALLOW_INSECURE_HTTP}" == "false" ]] || fatal "HTTPS 备份必须禁用 ALLOW_INSECURE_HTTP。"
    [[ "${RESTORED_CADDY_SITE_ADDRESS}" == https://* ]] || fatal "HTTPS 备份的 CADDY_SITE_ADDRESS 必须使用 https://。"
    RESTORED_HTTPS_HOST="${RESTORED_CADDY_SITE_ADDRESS#https://}"
    [[ -n "${RESTORED_HTTPS_HOST}" && "${RESTORED_HTTPS_HOST}" != */* && "${RESTORED_HTTPS_HOST}" != *:* ]] || fatal "HTTPS 备份的 Caddy 地址只能包含单个域名或 IPv4，不能包含端口、路径或 IPv6。"
  else
    [[ "${RESTORED_ALLOW_INSECURE_HTTP}" == "true" && "${RESTORED_HSTS_ENABLED}" == "false" ]] || fatal "HTTP 备份必须启用显式不安全模式并禁用 HSTS。"
    [[ "${RESTORED_CADDY_SITE_ADDRESS}" == "http://${RESTORED_PUBLIC_IPV4}" ]] || fatal "HTTP 备份的 Caddy 地址必须与 PUBLIC_IPV4 精确一致。"
  fi
  [[ "${RESTORED_HSTS_ENABLED}" != "true" || "${RESTORED_HTTPS_ENABLED}" == "true" ]] || fatal "HSTS 备份必须同时启用 HTTPS。"

  RESTORED_SECRETS_GID="$(restored_env_value SECRETS_GID)"
  [[ "${RESTORED_SECRETS_GID}" =~ ^[0-9]+$ ]] \
    && ((10#${RESTORED_SECRETS_GID} >= 1 && 10#${RESTORED_SECRETS_GID} <= 2147483647)) \
    || fatal "备份 SECRETS_GID 无效。"
  RESTORED_APP_SOURCE_MODE="$(restored_env_value APP_SOURCE_MODE)"
  [[ "${RESTORED_APP_SOURCE_MODE}" == "local" || "${RESTORED_APP_SOURCE_MODE}" == "image" ]] || fatal "备份 APP_SOURCE_MODE 无效。"
  [[ "$(restored_env_value SOURCE_DIR)" == "${LP_HOME}/source" ]] || fatal "备份 SOURCE_DIR 与当前部署不一致。"

  validate_pinned_image "$(restored_env_value APP_IMAGE)" "备份 APP_IMAGE"
  validate_pinned_image "$(restored_env_value POSTGRES_IMAGE)" "备份 POSTGRES_IMAGE"
  validate_pinned_image "$(restored_env_value CADDY_IMAGE)" "备份 CADDY_IMAGE"
  validate_pinned_image "$(restored_env_value DOTNET_SDK_IMAGE)" "备份 DOTNET_SDK_IMAGE"
  validate_pinned_image "$(restored_env_value DOTNET_ASPNET_IMAGE)" "备份 DOTNET_ASPNET_IMAGE"

  validate_base64_secret "${RESTORED_CONFIG}/secrets/postgres_password" 44 32 postgres_password
  validate_base64_secret "${RESTORED_CONFIG}/secrets/card_lookup_key" 44 32 card_lookup_key
  validate_base64_secret "${RESTORED_CONFIG}/secrets/master_encryption_key" 88 64 master_encryption_key
  validate_base64_secret "${RESTORED_CONFIG}/secrets/admin_initial_password" 32 24 admin_initial_password

  compose_files=(--file "${LP_COMPOSE_FILE}")
  if [[ "${RESTORED_HTTPS_ENABLED}" == "true" ]]; then
    [[ -f "${LP_HOME}/compose.https.yml" ]] || fatal "HTTPS 备份无法恢复：缺少 compose.https.yml。"
    compose_files+=(--file "${LP_HOME}/compose.https.yml")
  fi
  RESTORE_DOCKER_CONFIG="${DOCKER_CONFIG:-/root/.docker}"
  if [[ "${RESTORE_DOCKER_CONFIG}" == /root/.docker && ! -d "${RESTORE_DOCKER_CONFIG}" ]]; then
    RESTORE_DOCKER_CONFIG="${LP_DATA_DIR}/docker-config"
    install -d -o root -g root -m 0700 "${RESTORE_DOCKER_CONFIG}"
  fi
  env -i PATH="${PATH}" HOME="${HOME:-/root}" DOCKER_CONFIG="${RESTORE_DOCKER_CONFIG}" \
    docker compose --project-name "${LP_PROJECT_NAME}" \
    --env-file "${RESTORED_CONFIG}/deployment.env" \
    "${compose_files[@]}" config --quiet
  env -i PATH="${PATH}" HOME="${HOME:-/root}" DOCKER_CONFIG="${RESTORE_DOCKER_CONFIG}" \
    docker run --rm --network none --read-only \
    --tmpfs /config:rw,noexec,nosuid,nodev --tmpfs /data:rw,noexec,nosuid,nodev \
    --env "CADDY_SITE_ADDRESS=${RESTORED_CADDY_SITE_ADDRESS}" \
    --volume "${RESTORED_CONFIG}/Caddyfile:/etc/caddy/Caddyfile:ro" \
    --entrypoint caddy "$(restored_env_value CADDY_IMAGE)" \
    validate --config /etc/caddy/Caddyfile --adapter caddyfile
  log "配置归档、Caddyfile、环境约束、镜像固定规则和秘密格式预检通过。"
else
  require_local_bind_ipv4 "${BIND_IPV4}"
fi

log "恢复前创建当前状态安全备份。"
LICENSE_PLATFORM_OPERATION_LOCK_HELD=1 "${LP_HOME}/bin/backup.sh" >/dev/null
SAFETY_BACKUP="$(latest_backup_archive)"
log "安全备份: ${SAFETY_BACKUP}"

log "仅停止本 Compose 项目的 caddy/app；不会停止或修改其他容器。"
compose stop caddy app

RESTORE_SUCCEEDED=0
restore_failure_guard() {
  local rc=$?
  trap - EXIT
  if (( rc != 0 && RESTORE_SUCCEEDED == 0 )); then
    warn "恢复未完成；app/caddy 保持停止。恢复前安全备份: ${SAFETY_BACKUP}"
  fi
  exit "${rc}"
}
trap restore_failure_guard EXIT

log "恢复 PostgreSQL 数据库；失败时 app/caddy 将保持停止，避免连接部分恢复的数据。"
compose exec --no-TTY db pg_restore \
  --username="${POSTGRES_USER}" \
  --dbname=postgres \
  --clean \
  --if-exists \
  --create \
  --no-owner \
  --exit-on-error <"${WORK_DIR}/database.dump"

if (( RESTORE_CONFIG == 1 )); then
  CONFIG_SAFETY="${LP_DATA_DIR}/rollback/config-before-restore-$(date --utc +%Y%m%dT%H%M%SZ).tar.gz"
  tar --create --gzip --file "${CONFIG_SAFETY}" --directory "$(dirname "${LP_CONFIG_DIR}")" "$(basename "${LP_CONFIG_DIR}")"
  chmod 0600 "${CONFIG_SAFETY}"
  log "当前配置安全副本: ${CONFIG_SAFETY}"

  install -o root -g root -m 0600 "${RESTORED_CONFIG}/deployment.env" "${LP_CONFIG_DIR}/deployment.env"
  install -o root -g root -m 0644 "${RESTORED_CONFIG}/Caddyfile" "${LP_CONFIG_DIR}/Caddyfile"
  for secret in postgres_password card_lookup_key master_encryption_key admin_initial_password; do
    [[ -f "${RESTORED_CONFIG}/secrets/${secret}" && ! -L "${RESTORED_CONFIG}/secrets/${secret}" ]] || fatal "恢复配置缺少秘密文件: ${secret}"
    install -o root -g "${RESTORED_SECRETS_GID}" -m 0440 \
      "${RESTORED_CONFIG}/secrets/${secret}" "${LP_CONFIG_DIR}/secrets/${secret}"
  done

  load_deployment_env
  [[ "${POSTGRES_USER}" =~ ^[a-z_][a-z0-9_]*$ ]] || fatal "恢复的 PostgreSQL 用户名无效。"
  compose exec --no-TTY db sh -eu -c '
    IFS= read -r restored_password || [ -n "${restored_password:-}" ]
    case "${restored_password}" in
      ""|*[!A-Za-z0-9+/=]*)
        printf "invalid restored postgres password\n" >&2
        exit 1
        ;;
    esac
    if [ "${#restored_password}" -lt 40 ] || [ "${#restored_password}" -gt 128 ]; then
      printf "invalid restored postgres password length\n" >&2
      exit 1
    fi
    printf "ALTER ROLE \"%s\" PASSWORD \\047%s\\047;\n" \
      "${POSTGRES_USER}" "${restored_password}" |
      psql --username="${POSTGRES_USER}" --dbname=postgres --set=ON_ERROR_STOP=1
    unset restored_password
  ' <"${LP_CONFIG_DIR}/secrets/postgres_password"
  log "配置与解密密钥已恢复，并通过标准输入同步了数据库角色口令。"
fi

log "启动本项目 app/caddy。"
compose up --detach app caddy
wait_for_service app 180
wait_for_service caddy 120
"${LP_HOME}/bin/health-check.sh"

RESTORE_SUCCEEDED=1
trap - EXIT
log "恢复完成。恢复前安全备份保留在: ${SAFETY_BACKUP}"
