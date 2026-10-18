#!/usr/bin/env bash
# Roll back the application image only. Database restoration remains a separate explicit action.

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
COMMON_PATH="${SCRIPT_DIR}/../lib/common.sh"
[[ -r "${COMMON_PATH}" ]] || COMMON_PATH="${SCRIPT_DIR}/lib/common.sh"
# shellcheck source=deploy/lib/common.sh
source "${COMMON_PATH}"

STATE_SELECTOR="latest"
CONFIRM_VALUE=""

usage() {
  cat <<'USAGE'
用法:
  sudo licensectl rollback [--state latest|<状态目录名>] \
    --confirm-rollback ROLLBACK-APPLICATION

默认只回退应用镜像，不回退数据库。若旧应用与新迁移不兼容，请先评估，
再用 licensectl restore 的双重显式确认恢复升级前备份。
若存在中断的手动回退，先恢复回退前部署并退出；不会立即再执行一次回退。
USAGE
}

while (($# > 0)); do
  case "$1" in
    --state)
      (($# >= 2)) || fatal "--state 缺少值"
      STATE_SELECTOR="$2"
      shift 2
      ;;
    --confirm-rollback)
      (($# >= 2)) || fatal "--confirm-rollback 缺少值"
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
require_command sync
load_deployment_env
confirm_exact "${CONFIRM_VALUE}" "ROLLBACK-APPLICATION" "回退会短暂停止本项目 app/caddy，未获得精确确认"

exec 8>"${LP_DATA_DIR}/operation.lock"
flock --nonblock 8 || fatal "已有恢复、升级或其他独占运维任务正在运行。"
reject_pending_transactions manual-rollback.pending

ROLLBACK_ROOT="${LP_DATA_DIR}/rollback"
[[ -d "${ROLLBACK_ROOT}" && ! -L "${ROLLBACK_ROOT}" ]] || fatal "回滚根目录缺失或不安全。"
PENDING_FILE="${LP_DATA_DIR}/manual-rollback.pending"

state_value() {
  local state_file="$1" key="$2" line
  line="$(grep -E "^${key}=[A-Za-z0-9_./:@-]*$" "${state_file}" || true)"
  [[ -n "${line}" && "${line}" != *$'\n'* ]] || fatal "回滚状态缺少或重复安全字段: ${key}"
  printf '%s\n' "${line#*=}"
}

is_upgrade_state_name() {
  [[ "$1" =~ ^[0-9]{8}T[0-9]{6}(\.[0-9]{1,9})?Z$ ]]
}

upgrade_state_sort_key() {
  local timestamp="${1%Z}" seconds fraction=""
  seconds="${timestamp%%.*}"
  [[ "${timestamp}" != *.* ]] || fraction="${timestamp#*.}"
  fraction="${fraction}000000000"
  printf '%s.%sZ\n' "${seconds}" "${fraction:0:9}"
}

# This is also used for latest selection: unrelated HTTPS/HSTS/install/manual
# journals, unfinished upgrades and already-consumed states are not candidates.
is_eligible_upgrade_state() (
  local directory="$1" version status
  [[ -d "${directory}" && ! -L "${directory}" ]] || exit 1
  is_upgrade_state_name "${directory##*/}" || exit 1
  verify_root_0600_file "${directory}/state.env"
  version="$(state_value "${directory}/state.env" STATE_VERSION)"
  case "${version}" in
    1)
      status="$(grep -E '^STATUS=' "${directory}/state.env" || true)"
      [[ -z "${status}" || "${status}" == 'STATUS=SUCCEEDED' ]] || exit 1
      ;;
    2) [[ "$(state_value "${directory}/state.env" STATUS)" == "SUCCEEDED" ]] || exit 1 ;;
    *) exit 1 ;;
  esac
  [[ "$(state_value "${directory}/state.env" ROLLBACK_IMAGE)" =~ ^license-platform-rollback:[0-9a-z._-]+$ ]]
)

# All recovery paths are fixed direct children of managed roots. Never source a
# journal or accept a prefix-only path check (which would admit ../ traversal).
recover_manual_rollback() (
  set -Eeuo pipefail
  local transaction_dir="$1" journal="${1}/state.env"
  local name="${1##*/}" upgrade_name source_required previous_present previous_backup current_safety image version
  [[ "${transaction_dir}" == "${ROLLBACK_ROOT}/${name}" && "${name}" =~ ^manual-[0-9]{8}T[0-9]{6}\.[0-9]{9}Z$ \
    && -d "${transaction_dir}" && ! -L "${transaction_dir}" ]] || fatal "手动回退事务目录不安全。"
  verify_root_0600_file "${journal}"
  verify_root_0600_file "${transaction_dir}/deployment.env"
  [[ "$(state_value "${journal}" STATE_VERSION)" == "1" ]] || fatal "不支持的手动回退事务版本。"
  upgrade_name="$(state_value "${journal}" UPGRADE_STATE)"
  is_upgrade_state_name "${upgrade_name}" || fatal "手动回退事务中的升级状态名无效。"
  [[ -d "${ROLLBACK_ROOT}/${upgrade_name}" && ! -L "${ROLLBACK_ROOT}/${upgrade_name}" ]] || fatal "升级状态目录不安全。"
  verify_root_0600_file "${ROLLBACK_ROOT}/${upgrade_name}/state.env"
  version="$(state_value "${journal}" UPGRADE_STATE_VERSION)"
  [[ "${version}" == "1" || "${version}" == "2" ]] || fatal "手动回退事务中的升级状态版本无效。"
  image="$(state_value "${journal}" CURRENT_IMAGE)"
  [[ "${image}" =~ ^license-platform-manual-rollback:[0-9a-z._-]+$ ]] || fatal "手动回退安全镜像名无效。"
  docker image inspect "${image}" >/dev/null 2>&1 || fatal "手动回退安全镜像已不存在。"
  source_required="$(state_value "${journal}" SOURCE_SWITCH_REQUIRED)"
  [[ "${source_required}" == "true" || "${source_required}" == "false" ]] || fatal "手动回退源码标志无效。"
  compose stop caddy app
  if [[ "${source_required}" == "true" ]]; then
    previous_present="$(state_value "${journal}" PREVIOUS_SOURCE_PRESENT)"
    previous_backup="$(state_value "${journal}" PREVIOUS_SOURCE_BACKUP)"
    current_safety="$(state_value "${journal}" CURRENT_SOURCE_SAFETY)"
    [[ "${previous_present}" == "true" || "${previous_present}" == "false" ]] || fatal "旧源码存在标志无效。"
    [[ "${previous_backup}" == "${LP_HOME}/source.before-${upgrade_name,,}" ]] || fatal "旧源码回滚路径不安全。"
    [[ "${current_safety}" == "${LP_HOME}/source.before-manual-rollback-${name#manual-}" ]] || fatal "当前源码安全路径不安全。"
    if [[ -e "${current_safety}" || -L "${current_safety}" ]]; then
      [[ -d "${current_safety}" && ! -L "${current_safety}" ]] || fatal "当前源码安全副本不安全。"
      if [[ "${previous_present}" == "true" ]]; then
        if [[ -e "${previous_backup}" || -L "${previous_backup}" ]]; then
          [[ -d "${previous_backup}" && ! -L "${previous_backup}" \
            && ! -e "${LP_HOME}/source" && ! -L "${LP_HOME}/source" ]] || fatal "源码事务状态冲突，拒绝覆盖。"
        else
          [[ -d "${LP_HOME}/source" && ! -L "${LP_HOME}/source" ]] || fatal "待归位的旧源码缺失或不安全。"
          mv -- "${LP_HOME}/source" "${previous_backup}"
        fi
      else
        [[ ! -e "${previous_backup}" && ! -L "${previous_backup}" \
          && ! -e "${LP_HOME}/source" && ! -L "${LP_HOME}/source" ]] || fatal "源码事务状态冲突，拒绝覆盖。"
      fi
      mv -- "${current_safety}" "${LP_HOME}/source"
    else
      # Either the first rename never happened, or a previous recovery already
      # completed both renames. Both cases must retain the original old backup.
      [[ -d "${LP_HOME}/source" && ! -L "${LP_HOME}/source" ]] || fatal "回退前源码缺失或不安全。"
      if [[ "${previous_present}" == "true" ]]; then
        [[ -d "${previous_backup}" && ! -L "${previous_backup}" ]] || fatal "旧源码回滚副本缺失或不安全。"
      else
        [[ ! -e "${previous_backup}" && ! -L "${previous_backup}" ]] || fatal "旧源码路径状态冲突。"
      fi
    fi
  fi

  # Build the restored configuration next to its destination, then rename it.
  # The safety tag pins the actual pre-rollback image even if a tag was moved.
  local env_temporary
  env_temporary="$(mktemp "${LP_ENV_FILE}.XXXXXX")"
  install -o root -g root -m 0600 "${transaction_dir}/deployment.env" "${env_temporary}"
  write_env_value "${env_temporary}" APP_IMAGE "${image}"
  [[ ! -L "${LP_ENV_FILE}" ]] || fatal "拒绝恢复到符号链接配置。"
  mv -f -- "${env_temporary}" "${LP_ENV_FILE}"
  unset APP_IMAGE APP_SOURCE_MODE SOURCE_DIR
  load_deployment_env
  compose config --quiet
  compose up --detach --force-recreate app caddy
  wait_for_service app 180
  wait_for_service caddy 120
  "${LP_HOME}/bin/health-check.sh"
  # Version 1 may not originally have had STATUS; SUCCEEDED is eligible too.
  write_env_value "${ROLLBACK_ROOT}/${upgrade_name}/state.env" STATUS SUCCEEDED
  write_env_value "${journal}" STATUS RECOVERED
  sync -f "${LP_HOME}"
  sync -f "${LP_ENV_FILE}"
  sync -f "${journal}"
)

if [[ -e "${PENDING_FILE}" || -L "${PENDING_FILE}" ]]; then
  verify_root_0600_file "${PENDING_FILE}"
  INTERRUPTED_TRANSACTION="$(<"${PENDING_FILE}")"
  warn "检测到中断的手动回退；先恢复回退前镜像、配置和源码。"
  # Do not call recovery as an if/! condition: Bash would suppress errexit
  # throughout its body, allowing a failed restore to be reported as healthy.
  set +e
  recover_manual_rollback "${INTERRUPTED_TRANSACTION}"
  RECOVERY_RC=$?
  set -e
  if (( RECOVERY_RC == 0 )); then
    rm -f -- "${PENDING_FILE}"
    sync -f "${LP_DATA_DIR}"
    fatal "中断的手动回退已恢复并通过严格健康检查；请重新运行回退命令。"
  fi
  fatal "中断的手动回退恢复失败；事务标记保留，不能假定 app/caddy 健康。"
fi

if [[ "${STATE_SELECTOR}" == "latest" ]]; then
  STATE_DIR=""
  STATE_SORT_KEY=""
  shopt -s nullglob
  for candidate in "${ROLLBACK_ROOT}"/*; do
    is_eligible_upgrade_state "${candidate}" 2>/dev/null || continue
    CANDIDATE_SORT_KEY="$(upgrade_state_sort_key "${candidate##*/}")"
    if [[ -z "${STATE_DIR}" || "${CANDIDATE_SORT_KEY}" > "${STATE_SORT_KEY}" ]]; then
      STATE_DIR="${candidate}"
      STATE_SORT_KEY="${CANDIDATE_SORT_KEY}"
    fi
  done
  shopt -u nullglob
else
  is_upgrade_state_name "${STATE_SELECTOR}" || fatal "状态目录名必须是升级 UTC 时间戳。"
  STATE_DIR="${ROLLBACK_ROOT}/${STATE_SELECTOR}"
fi
[[ -n "${STATE_DIR}" ]] && is_eligible_upgrade_state "${STATE_DIR}" || fatal "找不到可回退的成功升级状态: ${STATE_SELECTOR}"

STATE_VERSION="$(state_value "${STATE_DIR}/state.env" STATE_VERSION)"
ROLLBACK_IMAGE="$(state_value "${STATE_DIR}/state.env" ROLLBACK_IMAGE)"
PRE_UPGRADE_BACKUP="$(state_value "${STATE_DIR}/state.env" PRE_UPGRADE_BACKUP)"
validate_pinned_image "${ROLLBACK_IMAGE}" "回滚镜像"
docker image inspect "${ROLLBACK_IMAGE}" >/dev/null 2>&1 || fatal "本机已不存在回滚镜像: ${ROLLBACK_IMAGE}"

PREVIOUS_SOURCE_MODE="image"
TARGET_SOURCE_MODE="image"
SOURCE_ROLLBACK_REQUIRED="false"
PREVIOUS_SOURCE_PRESENT="false"
PREVIOUS_SOURCE_BACKUP=""
if [[ "${STATE_VERSION}" == "2" ]]; then
  PREVIOUS_SOURCE_MODE="$(state_value "${STATE_DIR}/state.env" PREVIOUS_SOURCE_MODE)"
  TARGET_SOURCE_MODE="$(state_value "${STATE_DIR}/state.env" TARGET_SOURCE_MODE)"
  [[ "${PREVIOUS_SOURCE_MODE}" == "local" || "${PREVIOUS_SOURCE_MODE}" == "image" ]] || fatal "回滚状态中的旧来源模式无效。"
  [[ "${TARGET_SOURCE_MODE}" == "local" || "${TARGET_SOURCE_MODE}" == "image" ]] || fatal "回滚状态中的目标来源模式无效。"
  if [[ "${TARGET_SOURCE_MODE}" == "local" ]]; then
    [[ "$(state_value "${STATE_DIR}/state.env" SOURCE_DIR)" == "${LP_HOME}/source" ]] || fatal "回滚状态中的 SOURCE_DIR 不安全。"
    PREVIOUS_SOURCE_PRESENT="$(state_value "${STATE_DIR}/state.env" PREVIOUS_SOURCE_PRESENT)"
    PREVIOUS_SOURCE_BACKUP="$(state_value "${STATE_DIR}/state.env" PREVIOUS_SOURCE_BACKUP)"
    [[ "${PREVIOUS_SOURCE_PRESENT}" == "true" || "${PREVIOUS_SOURCE_PRESENT}" == "false" ]] || fatal "回滚状态中的 PREVIOUS_SOURCE_PRESENT 无效。"
    UPGRADE_STATE_NAME="${STATE_DIR##*/}"
    [[ "${PREVIOUS_SOURCE_BACKUP}" == "${LP_HOME}/source.before-${UPGRADE_STATE_NAME,,}" ]] || fatal "回滚状态中的旧源码路径不安全。"
    [[ -d "${LP_HOME}/source" && ! -L "${LP_HOME}/source" ]] || fatal "当前受管源码目录缺失或不安全。"
    if [[ "${PREVIOUS_SOURCE_PRESENT}" == "true" ]]; then
      [[ -d "${PREVIOUS_SOURCE_BACKUP}" && ! -L "${PREVIOUS_SOURCE_BACKUP}" ]] || fatal "旧源码回滚副本缺失或不安全。"
    else
      [[ ! -e "${PREVIOUS_SOURCE_BACKUP}" && ! -L "${PREVIOUS_SOURCE_BACKUP}" ]] || fatal "状态声明无旧源码，但回滚路径已存在。"
      [[ "${PREVIOUS_SOURCE_MODE}" == "image" ]] || fatal "旧来源为 local 时必须存在旧源码回滚副本。"
    fi
    SOURCE_ROLLBACK_REQUIRED="true"
  fi
fi

log "确认当前部署健康并在回退前创建备份。"
"${LP_HOME}/bin/health-check.sh"
CURRENT_APP_CONTAINER="$(service_container_id app)"
[[ -n "${CURRENT_APP_CONTAINER}" ]] || fatal "当前 app 容器不存在，无法建立回退前安全镜像。"
CURRENT_IMAGE_ID="$(docker inspect --format '{{.Image}}' "${CURRENT_APP_CONTAINER}")"
[[ "${CURRENT_IMAGE_ID}" =~ ^sha256:[0-9a-f]{64}$ ]] || fatal "无法读取当前应用镜像 ID。"
LICENSE_PLATFORM_OPERATION_LOCK_HELD=1 "${LP_HOME}/bin/backup.sh" >/dev/null
BEFORE_ROLLBACK_BACKUP="$(latest_backup_archive)"
MANUAL_TIMESTAMP="$(date --utc +%Y%m%dT%H%M%S.%NZ)"
TRANSACTION_DIR="${ROLLBACK_ROOT}/manual-${MANUAL_TIMESTAMP}"
[[ ! -e "${TRANSACTION_DIR}" && ! -L "${TRANSACTION_DIR}" ]] || fatal "手动回退事务目录已存在。"
install -d -o root -g root -m 0700 "${TRANSACTION_DIR}"
install -o root -g root -m 0600 "${LP_ENV_FILE}" "${TRANSACTION_DIR}/deployment.env"
CURRENT_IMAGE="license-platform-manual-rollback:${MANUAL_TIMESTAMP,,}"
docker image tag "${CURRENT_IMAGE_ID}" "${CURRENT_IMAGE}"
CURRENT_SOURCE_SAFETY=""
if [[ "${SOURCE_ROLLBACK_REQUIRED}" == "true" ]]; then
  CURRENT_SOURCE_SAFETY="${LP_HOME}/source.before-manual-rollback-${MANUAL_TIMESTAMP}"
  [[ ! -e "${CURRENT_SOURCE_SAFETY}" && ! -L "${CURRENT_SOURCE_SAFETY}" ]] || fatal "当前源码安全副本路径已存在。"
fi
cat >"${TRANSACTION_DIR}/state.env" <<STATE
STATE_VERSION=1
STATUS=PREPARED
UPGRADE_STATE=${STATE_DIR##*/}
UPGRADE_STATE_VERSION=${STATE_VERSION}
CURRENT_IMAGE=${CURRENT_IMAGE}
SOURCE_SWITCH_REQUIRED=${SOURCE_ROLLBACK_REQUIRED}
PREVIOUS_SOURCE_PRESENT=${PREVIOUS_SOURCE_PRESENT}
PREVIOUS_SOURCE_BACKUP=${PREVIOUS_SOURCE_BACKUP}
CURRENT_SOURCE_SAFETY=${CURRENT_SOURCE_SAFETY}
BEFORE_ROLLBACK_BACKUP=${BEFORE_ROLLBACK_BACKUP}
STATE
chown root:root "${TRANSACTION_DIR}/state.env"
chmod 0600 "${TRANSACTION_DIR}/state.env"

SWITCH_STARTED=0
rollback_exit_handler() {
  local rc=$? recovery_rc
  trap - EXIT INT TERM
  if (( rc != 0 && SWITCH_STARTED == 1 )); then
    warn "应用回退失败；正在恢复回退前镜像、配置和源码并执行严格健康检查。"
    set +e
    recover_manual_rollback "${TRANSACTION_DIR}"
    recovery_rc=$?
    set -e
    if (( recovery_rc == 0 )); then
      rm -f -- "${PENDING_FILE}"
      sync -f "${LP_DATA_DIR}"
      warn "回退前部署已恢复并通过严格健康检查。"
      exit "${rc}"
    fi
    warn "手动回退恢复失败；事务标记保留在 ${PENDING_FILE}，不能假定 app/caddy 健康。"
    exit 2
  fi
  exit "${rc}"
}
trap rollback_exit_handler EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

PENDING_TEMP="$(mktemp "${PENDING_FILE}.XXXXXX")"
printf '%s\n' "${TRANSACTION_DIR}" >"${PENDING_TEMP}"
chown root:root "${PENDING_TEMP}"
chmod 0600 "${PENDING_TEMP}"
mv -f -- "${PENDING_TEMP}" "${PENDING_FILE}"
sync -f "${PENDING_FILE}"
SWITCH_STARTED=1

log "仅停止本项目 caddy/app；不会停止或修改其他容器。"
compose stop caddy app
if [[ "${SOURCE_ROLLBACK_REQUIRED}" == "true" ]]; then
  mv -- "${LP_HOME}/source" "${CURRENT_SOURCE_SAFETY}"
  if [[ "${PREVIOUS_SOURCE_PRESENT}" == "true" ]]; then
    mv -- "${PREVIOUS_SOURCE_BACKUP}" "${LP_HOME}/source"
  fi
fi
write_env_values "${LP_ENV_FILE}" \
  APP_IMAGE "${ROLLBACK_IMAGE}" \
  APP_SOURCE_MODE "${PREVIOUS_SOURCE_MODE}"
unset APP_IMAGE APP_SOURCE_MODE SOURCE_DIR
load_deployment_env
[[ "${APP_IMAGE}" == "${ROLLBACK_IMAGE}" && "${APP_SOURCE_MODE}" == "${PREVIOUS_SOURCE_MODE}" ]] || fatal "手动回退目标未原子持久化。"
compose config --quiet
compose up --detach --force-recreate app caddy
wait_for_service app 180
wait_for_service caddy 120
"${LP_HOME}/bin/health-check.sh"

write_env_value "${STATE_DIR}/state.env" STATUS MANUALLY_ROLLED_BACK
write_env_value "${TRANSACTION_DIR}/state.env" STATUS SUCCEEDED
sync -f "${LP_HOME}"
sync -f "${LP_ENV_FILE}"
sync -f "${TRANSACTION_DIR}/state.env"
rm -f -- "${PENDING_FILE}"
SWITCH_STARTED=0
sync -f "${LP_DATA_DIR}"
trap - EXIT INT TERM
log "应用镜像回退完成。回退前备份: ${BEFORE_ROLLBACK_BACKUP}"
if [[ -n "${CURRENT_SOURCE_SAFETY}" ]]; then
  log "较新版本源码安全副本保留在: ${CURRENT_SOURCE_SAFETY}"
fi
warn "数据库没有回退。升级前数据库备份为: ${PRE_UPGRADE_BACKUP:-<状态中未记录>}"
