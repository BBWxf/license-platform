#!/usr/bin/env bash
# Create a local, self-contained PostgreSQL/configuration/key backup.

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
COMMON_PATH="${SCRIPT_DIR}/../lib/common.sh"
[[ -r "${COMMON_PATH}" ]] || COMMON_PATH="${SCRIPT_DIR}/lib/common.sh"
# shellcheck source=deploy/lib/common.sh
source "${COMMON_PATH}"

if (($# > 0)); then
  case "$1" in
    -h|--help)
      printf '用法: sudo licensectl backup\n'
      exit 0
      ;;
    *) fatal "未知参数: $1" ;;
  esac
fi

require_root
require_command docker
require_command flock
require_command tar
require_command sha256sum
require_command find
load_deployment_env

install -d -o root -g root -m 0700 "${LP_BACKUP_DIR}"
if [[ "${LICENSE_PLATFORM_OPERATION_LOCK_HELD:-0}" != "1" ]]; then
  exec 8>"${LP_DATA_DIR}/operation.lock"
  flock --nonblock 8 || fatal "恢复或升级任务正在运行，暂不创建备份。"
fi
exec 9>"${LP_DATA_DIR}/backup.lock"
if ! flock --nonblock 9; then
  fatal "已有备份任务正在运行。"
fi

[[ "$(service_state db)" == "running" ]] || fatal "PostgreSQL 容器未运行，无法备份。"

RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-14}"
[[ "${RETENTION_DAYS}" =~ ^[0-9]+$ ]] && ((RETENTION_DAYS >= 1)) || fatal "BACKUP_RETENTION_DAYS 必须是正整数。"

TIMESTAMP="$(date --utc +%Y%m%dT%H%M%S.%NZ)"
WORK_DIR="$(mktemp -d "${LP_BACKUP_DIR}/.backup-${TIMESTAMP}.XXXXXX")"
ARCHIVE="${LP_BACKUP_DIR}/license-platform-backup-${TIMESTAMP}.tar.gz"
TEMP_ARCHIVE="${ARCHIVE}.partial"

cleanup() {
  rm -rf -- "${WORK_DIR}"
  rm -f -- "${TEMP_ARCHIVE}"
}
trap cleanup EXIT

log "导出 PostgreSQL 数据库。"
compose exec --no-TTY db pg_dump \
  --username="${POSTGRES_USER}" \
  --dbname="${POSTGRES_DB}" \
  --format=custom \
  --create \
  --no-owner >"${WORK_DIR}/database.dump"
[[ -s "${WORK_DIR}/database.dump" ]] || fatal "pg_dump 输出为空。"
chmod 0600 "${WORK_DIR}/database.dump"

log "归档受管部署配置和解密密钥。"
CONFIG_MEMBERS=(
  deployment.env
  Caddyfile
  secrets/postgres_password
  secrets/card_lookup_key
  secrets/master_encryption_key
  secrets/admin_initial_password
)
for member in "${CONFIG_MEMBERS[@]}"; do
  [[ -f "${LP_CONFIG_DIR}/${member}" && ! -L "${LP_CONFIG_DIR}/${member}" ]] || \
    fatal "受管配置文件缺失或是符号链接: ${LP_CONFIG_DIR}/${member}"
done
verify_root_0600_file "${LP_CONFIG_DIR}/deployment.env"
for secret in postgres_password card_lookup_key master_encryption_key admin_initial_password; do
  verify_runtime_secret_file "${LP_CONFIG_DIR}/secrets/${secret}"
done
tar --create --gzip \
  --file "${WORK_DIR}/configuration.tar.gz" \
  --directory "${LP_CONFIG_DIR}" \
  "${CONFIG_MEMBERS[@]}"
chmod 0600 "${WORK_DIR}/configuration.tar.gz"

DB_IMAGE_ID="$(docker inspect --format '{{.Image}}' "$(service_container_id db)")"
APP_IMAGE_ID="$(docker inspect --format '{{.Image}}' "$(service_container_id app)" 2>/dev/null || true)"
CADDY_IMAGE_ID="$(docker inspect --format '{{.Image}}' "$(service_container_id caddy)" 2>/dev/null || true)"
cat >"${WORK_DIR}/manifest.txt" <<MANIFEST
format_version=1
created_utc=${TIMESTAMP}
project=${LP_PROJECT_NAME}
database=${POSTGRES_DB}
postgres_user=${POSTGRES_USER}
postgres_image=${POSTGRES_IMAGE}
postgres_image_id=${DB_IMAGE_ID}
app_image=${APP_IMAGE}
app_image_id=${APP_IMAGE_ID}
caddy_image=${CADDY_IMAGE}
caddy_image_id=${CADDY_IMAGE_ID}
contains_configuration=true
contains_database_credentials=true
contains_decryption_keys=true
MANIFEST
chmod 0600 "${WORK_DIR}/manifest.txt"

(
  cd "${WORK_DIR}"
  sha256sum database.dump configuration.tar.gz manifest.txt >SHA256SUMS
  chmod 0600 SHA256SUMS
)
tar --create --gzip --file "${TEMP_ARCHIVE}" --directory "${WORK_DIR}" \
  database.dump configuration.tar.gz manifest.txt SHA256SUMS
chown root:root "${TEMP_ARCHIVE}"
chmod 0600 "${TEMP_ARCHIVE}"
mv "${TEMP_ARCHIVE}" "${ARCHIVE}"
(
  cd "${LP_BACKUP_DIR}"
  sha256sum "$(basename "${ARCHIVE}")" >"$(basename "${ARCHIVE}").sha256"
)
chown root:root "${ARCHIVE}.sha256"
chmod 0600 "${ARCHIVE}.sha256"

log "备份完成: ${ARCHIVE}"
warn "该备份包含数据库口令和解密密钥，未额外加密；必须保持 root-only 并保护离线副本。"

log "执行 ${RETENTION_DAYS} 天保留策略。"
while IFS= read -r -d '' expired; do
  log "删除超过保留期的备份文件: ${expired}"
  rm -f -- "${expired}"
done < <(
  find -P "${LP_BACKUP_DIR}" -maxdepth 1 -type f -user root \
    \( -name 'license-platform-backup-*.tar.gz' -o -name 'license-platform-backup-*.tar.gz.sha256' \) \
    -mtime "+$((RETENTION_DAYS - 1))" -print0
)

printf '%s\n' "${ARCHIVE}"
