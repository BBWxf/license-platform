#!/usr/bin/env bash
# Upgrade only this Compose project's application image, with backup and image rollback state.

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
COMMON_PATH="${SCRIPT_DIR}/../lib/common.sh"
[[ -r "${COMMON_PATH}" ]] || COMMON_PATH="${SCRIPT_DIR}/lib/common.sh"
# shellcheck source=deploy/lib/common.sh
source "${COMMON_PATH}"

NEW_APP_IMAGE=""
NEW_SOURCE_MODE=""
SOURCE_PATH=""
CONFIRM_VALUE=""

usage() {
  cat <<'USAGE'
用法:
  sudo licensectl upgrade --app-image <仓库/应用:版本|仓库/应用@sha256:digest> \
    --confirm-upgrade UPGRADE

  sudo licensectl upgrade --build-local-image <应用:明确版本> --source <源码根目录> \
    --confirm-upgrade UPGRADE

升级前自动备份数据库/配置/密钥；仅停止并重建本项目 app/caddy。
失败时自动尝试回到旧应用镜像，但不会自动回滚数据库迁移。
USAGE
}

while (($# > 0)); do
  case "$1" in
    --app-image)
      (($# >= 2)) || fatal "--app-image 缺少值"
      [[ -z "${NEW_SOURCE_MODE}" ]] || fatal "只能选择一种升级来源"
      NEW_APP_IMAGE="$2"
      NEW_SOURCE_MODE="image"
      shift 2
      ;;
    --build-local-image)
      (($# >= 2)) || fatal "--build-local-image 缺少值"
      [[ -z "${NEW_SOURCE_MODE}" ]] || fatal "只能选择一种升级来源"
      NEW_APP_IMAGE="$2"
      NEW_SOURCE_MODE="local"
      shift 2
      ;;
    --source)
      (($# >= 2)) || fatal "--source 缺少值"
      SOURCE_PATH="$2"
      shift 2
      ;;
    --confirm-upgrade)
      (($# >= 2)) || fatal "--confirm-upgrade 缺少值"
      CONFIRM_VALUE="$2"
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
require_command grep
load_deployment_env
confirm_exact "${CONFIRM_VALUE}" "UPGRADE" "升级会造成短暂停机并执行数据库迁移，未获得精确确认"
[[ -n "${NEW_APP_IMAGE}" && -n "${NEW_SOURCE_MODE}" ]] || fatal "必须指定 --app-image 或 --build-local-image。"
validate_pinned_image "${NEW_APP_IMAGE}" "新应用镜像"

if [[ "${NEW_SOURCE_MODE}" == "local" ]]; then
  [[ -n "${SOURCE_PATH}" ]] || fatal "本地构建必须指定 --source。"
  SOURCE_PATH="$(readlink -f "${SOURCE_PATH}")"
  [[ -d "${SOURCE_PATH}/src" && ! -L "${SOURCE_PATH}/src" ]] || fatal "源码 src 必须是普通目录而不是符号链接。"
  [[ -f "${SOURCE_PATH}/src/LicensePlatform.Web/LicensePlatform.Web.csproj" ]] || fatal "源码缺少 Web 项目。"
  require_command grep
  for contract_marker in '--migrate' '--admin-reset' '/health' 'AllowInsecureHttp' 'MasterEncryptionKeyFile' 'CardLookupKeyFile'; do
    grep -r --include='*.cs' -Fq -- "${contract_marker}" "${SOURCE_PATH}/src/LicensePlatform.Web" || \
      fatal "本地升级源码缺少运行契约标记: ${contract_marker}"
  done
else
  [[ -z "${SOURCE_PATH}" ]] || fatal "--app-image 模式不接受 --source。"
fi

exec 8>"${LP_DATA_DIR}/operation.lock"
flock --nonblock 8 || fatal "已有恢复、升级或其他独占运维任务正在运行。"
reject_pending_transactions upgrade.pending

[[ "${SOURCE_DIR}" == "${LP_HOME}/source" ]] || fatal "SOURCE_DIR 必须是受管路径 ${LP_HOME}/source。"
PREVIOUS_SOURCE_MODE="${APP_SOURCE_MODE:-image}"
if [[ "${PREVIOUS_SOURCE_MODE}" == "remote" ]]; then
  warn "检测到旧版 APP_SOURCE_MODE=remote；本次按 image 语义迁移。"
  PREVIOUS_SOURCE_MODE="image"
fi
[[ "${PREVIOUS_SOURCE_MODE}" == "local" || "${PREVIOUS_SOURCE_MODE}" == "image" ]] || fatal "当前 APP_SOURCE_MODE 无效。"

state_value() {
  local state_file="$1" key="$2" line
  line="$(grep -E "^${key}=[A-Za-z0-9_./:@-]*$" "${state_file}" || true)"
  [[ -n "${line}" && "${line}" != *$'\n'* ]] || fatal "升级状态缺少或重复安全字段: ${key}"
  printf '%s\n' "${line#*=}"
}

restore_source_from_state() {
  local state_dir="$1" state_file="${state_dir}/state.env"
  local target_mode source_dir source_stage source_backup previous_present
  target_mode="$(state_value "${state_file}" TARGET_SOURCE_MODE)"
  [[ "${target_mode}" == "local" ]] || return 0
  source_dir="$(state_value "${state_file}" SOURCE_DIR)"
  source_stage="$(state_value "${state_file}" SOURCE_STAGE)"
  source_backup="$(state_value "${state_file}" PREVIOUS_SOURCE_BACKUP)"
  previous_present="$(state_value "${state_file}" PREVIOUS_SOURCE_PRESENT)"
  [[ "${source_dir}" == "${LP_HOME}/source" ]] || fatal "升级状态中的 SOURCE_DIR 不安全。"
  [[ "${source_stage}" == "${LP_HOME}/source.stage."* && "${source_backup}" == "${LP_HOME}/source.before-"* ]] || fatal "升级状态中的源码事务路径不安全。"
  [[ "${previous_present}" == "true" || "${previous_present}" == "false" ]] || fatal "升级状态中的 PREVIOUS_SOURCE_PRESENT 无效。"

  if [[ -e "${source_stage}" || -L "${source_stage}" ]]; then
    [[ -d "${source_stage}" && ! -L "${source_stage}" ]] || fatal "源码 staging 路径不是安全目录。"
    rm -rf -- "${source_stage}"
  fi
  if [[ "${previous_present}" == "true" ]]; then
    if [[ -e "${source_backup}" || -L "${source_backup}" ]]; then
      [[ -d "${source_backup}" && ! -L "${source_backup}" ]] || fatal "旧源码回滚路径不是安全目录。"
      if [[ -e "${source_dir}" || -L "${source_dir}" ]]; then
        [[ -d "${source_dir}" && ! -L "${source_dir}" ]] || fatal "受管源码路径不是安全目录。"
        rm -rf -- "${source_dir}"
      fi
      mv -- "${source_backup}" "${source_dir}"
    else
      [[ -d "${source_dir}" && ! -L "${source_dir}" ]] || fatal "旧源码目录和回滚副本均不存在。"
    fi
  elif [[ -e "${source_dir}" || -L "${source_dir}" ]]; then
    [[ -d "${source_dir}" && ! -L "${source_dir}" ]] || fatal "受管源码路径不是安全目录。"
    rm -rf -- "${source_dir}"
  fi
}

perform_upgrade_rollback() (
  set -Eeuo pipefail
  local state_dir="$1" state_file="${state_dir}/state.env"
  local rollback_image previous_mode
  verify_root_0600_file "${state_dir}/deployment.env"
  verify_root_0600_file "${state_file}"
  [[ "$(state_value "${state_file}" STATE_VERSION)" == "2" ]] || fatal "不支持的升级事务状态版本。"
  rollback_image="$(state_value "${state_file}" ROLLBACK_IMAGE)"
  previous_mode="$(state_value "${state_file}" PREVIOUS_SOURCE_MODE)"
  validate_pinned_image "${rollback_image}" "回滚应用镜像"
  [[ "${previous_mode}" == "local" || "${previous_mode}" == "image" ]] || fatal "升级状态中的旧来源模式无效。"

  restore_source_from_state "${state_dir}"
  install -o root -g root -m 0600 "${state_dir}/deployment.env" "${LP_ENV_FILE}"
  write_env_values "${LP_ENV_FILE}" \
    APP_IMAGE "${rollback_image}" \
    APP_SOURCE_MODE "${previous_mode}"
  unset APP_IMAGE APP_SOURCE_MODE
  load_deployment_env
  [[ "${APP_IMAGE}" == "${rollback_image}" ]] || fatal "回滚镜像未持久化。"
  compose config --quiet
  compose up --detach --force-recreate app caddy
  wait_for_service app 180
  wait_for_service caddy 120
  "${LP_HOME}/bin/health-check.sh"
)

PENDING_FILE="${LP_DATA_DIR}/upgrade.pending"
if [[ -e "${PENDING_FILE}" || -L "${PENDING_FILE}" ]]; then
  verify_root_0600_file "${PENDING_FILE}"
  INTERRUPTED_STATE_DIR="$(<"${PENDING_FILE}")"
  [[ -n "${INTERRUPTED_STATE_DIR}" && "${INTERRUPTED_STATE_DIR}" == "${LP_DATA_DIR}/rollback/"* && "${INTERRUPTED_STATE_DIR}" != *$'\n'* ]] || fatal "升级事务标记无效，拒绝自动恢复。"
  warn "检测到中断的升级事务，先尝试恢复已固定的旧应用镜像。"
  if perform_upgrade_rollback "${INTERRUPTED_STATE_DIR}"; then
    write_env_value "${INTERRUPTED_STATE_DIR}/state.env" STATUS ROLLED_BACK_AFTER_INTERRUPTION
    rm -f -- "${PENDING_FILE}"
    fatal "中断的升级已恢复并通过严格健康检查；请重新运行本次升级命令。"
  fi
  write_env_value "${INTERRUPTED_STATE_DIR}/state.env" STATUS ROLLBACK_FAILED
  fatal "中断升级的自动回滚失败；事务标记已保留，app/caddy 状态不可假定为健康。"
fi

log "确认当前部署健康，建立可验证的回滚基线。"
"${LP_HOME}/bin/health-check.sh"
CURRENT_APP_CONTAINER="$(service_container_id app)"
[[ -n "${CURRENT_APP_CONTAINER}" ]] || fatal "当前 app 容器不存在，无法建立可回滚状态。"
CURRENT_IMAGE_ID="$(docker inspect --format '{{.Image}}' "${CURRENT_APP_CONTAINER}")"
[[ -n "${CURRENT_IMAGE_ID}" ]] || fatal "无法读取当前应用镜像 ID。"

TIMESTAMP="$(date --utc +%Y%m%dT%H%M%S.%NZ)"
STATE_DIR="${LP_DATA_DIR}/rollback/${TIMESTAMP}"
install -d -o root -g root -m 0700 "${STATE_DIR}"
install -o root -g root -m 0600 "${LP_ENV_FILE}" "${STATE_DIR}/deployment.env"
ROLLBACK_IMAGE="license-platform-rollback:${TIMESTAMP,,}"
docker image tag "${CURRENT_IMAGE_ID}" "${ROLLBACK_IMAGE}"

SOURCE_STAGE=""
PREVIOUS_SOURCE_BACKUP=""
PREVIOUS_SOURCE_PRESENT="false"
pre_switch_cleanup() {
  local rc=$?
  trap - EXIT
  if (( rc != 0 )) && [[ -n "${SOURCE_STAGE:-}" && ( -e "${SOURCE_STAGE}" || -L "${SOURCE_STAGE}" ) ]]; then
    [[ -d "${SOURCE_STAGE}" && ! -L "${SOURCE_STAGE}" ]] && rm -rf -- "${SOURCE_STAGE}"
  fi
  exit "${rc}"
}
trap pre_switch_cleanup EXIT

if [[ "${NEW_SOURCE_MODE}" == "local" ]]; then
  [[ -f "${SOURCE_PATH}/Dockerfile" && ! -L "${SOURCE_PATH}/Dockerfile" ]] || fatal "本地升级源码缺少普通文件 Dockerfile。"
  if [[ -e "${SOURCE_DIR}" || -L "${SOURCE_DIR}" ]]; then
    [[ -d "${SOURCE_DIR}" && ! -L "${SOURCE_DIR}" ]] || fatal "受管 SOURCE_DIR 必须是普通目录而不是符号链接。"
    PREVIOUS_SOURCE_PRESENT="true"
  fi
  SOURCE_STAGE="$(mktemp -d "${LP_HOME}/source.stage.XXXXXX")"
  PREVIOUS_SOURCE_BACKUP="${LP_HOME}/source.before-${TIMESTAMP,,}"
  [[ ! -e "${PREVIOUS_SOURCE_BACKUP}" && ! -L "${PREVIOUS_SOURCE_BACKUP}" ]] || fatal "源码回滚路径已存在。"
  cp -a -- "${SOURCE_PATH}/src" "${SOURCE_STAGE}/src"
  for root_file in Dockerfile global.json Directory.Build.props Directory.Build.targets Directory.Packages.props .dockerignore; do
    if [[ -f "${SOURCE_PATH}/${root_file}" && ! -L "${SOURCE_PATH}/${root_file}" ]]; then
      install -o root -g root -m 0644 "${SOURCE_PATH}/${root_file}" "${SOURCE_STAGE}/${root_file}"
    fi
  done
  chown -R root:root "${SOURCE_STAGE}"
  chmod -R u=rwX,go=rX "${SOURCE_STAGE}"
  log "从全新 staging 构建应用镜像 ${NEW_APP_IMAGE}，不会保留旧版本已删除文件。"
  SOURCE_DIR="${SOURCE_STAGE}" APP_IMAGE="${NEW_APP_IMAGE}" compose build --pull app
else
  log "拉取新应用镜像 ${NEW_APP_IMAGE}。"
  APP_IMAGE="${NEW_APP_IMAGE}" compose pull app
fi

log "升级前创建备份。"
LICENSE_PLATFORM_OPERATION_LOCK_HELD=1 "${LP_HOME}/bin/backup.sh" >/dev/null
UPGRADE_BACKUP="$(latest_backup_archive)"
cat >"${STATE_DIR}/state.env" <<STATE
STATE_VERSION=2
STATUS=PREPARED
CREATED_UTC=${TIMESTAMP}
PREVIOUS_APP_IMAGE=${APP_IMAGE}
PREVIOUS_APP_IMAGE_ID=${CURRENT_IMAGE_ID}
PREVIOUS_SOURCE_MODE=${PREVIOUS_SOURCE_MODE}
ROLLBACK_IMAGE=${ROLLBACK_IMAGE}
PRE_UPGRADE_BACKUP=${UPGRADE_BACKUP}
TARGET_APP_IMAGE=${NEW_APP_IMAGE}
TARGET_SOURCE_MODE=${NEW_SOURCE_MODE}
SOURCE_DIR=${LP_HOME}/source
SOURCE_STAGE=${SOURCE_STAGE}
PREVIOUS_SOURCE_BACKUP=${PREVIOUS_SOURCE_BACKUP}
PREVIOUS_SOURCE_PRESENT=${PREVIOUS_SOURCE_PRESENT}
STATE
chown root:root "${STATE_DIR}/state.env"
chmod 0600 "${STATE_DIR}/state.env"
log "回滚状态: ${STATE_DIR}"
log "升级前备份: ${UPGRADE_BACKUP}"

SWITCH_STARTED=0
upgrade_exit_handler() {
  local rc=$?
  trap - EXIT
  if (( rc != 0 && SWITCH_STARTED == 1 )); then
    warn "升级失败；正在恢复已固定的旧应用镜像并执行严格健康检查。数据库迁移不会自动降级。"
    if perform_upgrade_rollback "${STATE_DIR}"; then
      write_env_value "${STATE_DIR}/state.env" STATUS ROLLED_BACK
      rm -f -- "${PENDING_FILE}"
      warn "旧应用镜像已持久化并通过严格健康检查；仍需确认数据库迁移向后兼容。"
      exit "${rc}"
    fi
    write_env_value "${STATE_DIR}/state.env" STATUS ROLLBACK_FAILED
    warn "自动回滚失败；事务标记已保留，不能假定服务或数据库兼容。"
    exit 2
  fi
  if [[ -n "${SOURCE_STAGE:-}" && ( -e "${SOURCE_STAGE}" || -L "${SOURCE_STAGE}" ) ]]; then
    [[ -d "${SOURCE_STAGE}" && ! -L "${SOURCE_STAGE}" ]] && rm -rf -- "${SOURCE_STAGE}"
  fi
  exit "${rc}"
}
trap upgrade_exit_handler EXIT

PENDING_TEMP="$(mktemp "${PENDING_FILE}.XXXXXX")"
printf '%s\n' "${STATE_DIR}" >"${PENDING_TEMP}"
chown root:root "${PENDING_TEMP}"
chmod 0600 "${PENDING_TEMP}"
mv -f -- "${PENDING_TEMP}" "${PENDING_FILE}"
SWITCH_STARTED=1

log "仅停止本项目 caddy/app；不会停止或修改其他容器。"
compose stop caddy app
if [[ "${NEW_SOURCE_MODE}" == "local" ]]; then
  if [[ "${PREVIOUS_SOURCE_PRESENT}" == "true" ]]; then
    mv -- "${LP_HOME}/source" "${PREVIOUS_SOURCE_BACKUP}"
  fi
  mv -- "${SOURCE_STAGE}" "${LP_HOME}/source"
  SOURCE_STAGE=""
fi
write_env_values "${LP_ENV_FILE}" \
  APP_IMAGE "${NEW_APP_IMAGE}" \
  APP_SOURCE_MODE "${NEW_SOURCE_MODE}"
unset APP_IMAGE APP_SOURCE_MODE SOURCE_DIR
load_deployment_env
[[ "${APP_IMAGE}" == "${NEW_APP_IMAGE}" && "${APP_SOURCE_MODE}" == "${NEW_SOURCE_MODE}" ]] || fatal "升级目标未原子持久化。"

prepare_app_data_permissions
run_migration
compose up --detach --force-recreate app caddy
wait_for_service app 180
wait_for_service caddy 120
"${LP_HOME}/bin/health-check.sh"

write_env_value "${STATE_DIR}/state.env" STATUS SUCCEEDED
rm -f -- "${PENDING_FILE}"
SWITCH_STARTED=0
trap - EXIT
log "升级完成。可回滚状态保留在 ${STATE_DIR}。"
warn "数据库迁移未提供自动降级；回滚应用镜像前必须确认数据库模式向后兼容。"
