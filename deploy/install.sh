#!/usr/bin/env bash
# Idempotent installer for Ubuntu/Debian hosts.

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
SOURCE_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd -P)"
# shellcheck source=deploy/lib/common.sh
source "${SCRIPT_DIR}/lib/common.sh"

MODE=""
PUBLIC_IPV4=""
ACCEPT_INSECURE_HTTP=0
APP_IMAGE_CLI=""
BUILD_LOCAL=1

readonly DEFAULT_INSTALL_DIR="/opt/license-platform"
readonly DEFAULT_CONFIG_DIR="/etc/license-platform"
readonly DEFAULT_DATA_DIR="/var/lib/license-platform"
readonly DEFAULT_BACKUP_DIR="/var/backups/license-platform"
readonly DEFAULT_APP_IMAGE="license-platform:0.1.0-local"
readonly DEFAULT_POSTGRES_IMAGE="postgres:18.4-bookworm"
readonly DEFAULT_CADDY_IMAGE="caddy:2.10.2-alpine"
readonly DEFAULT_DOTNET_SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0.108-bookworm-slim"
readonly DEFAULT_DOTNET_ASPNET_IMAGE="mcr.microsoft.com/dotnet/aspnet:10.0.8-bookworm-slim"
readonly DEFAULT_SECRETS_GID="20001"
readonly DEFAULT_SYSTEMD_DIR="/etc/systemd/system"
readonly DEFAULT_SBIN_LINK="/usr/local/sbin/licensectl"

usage() {
  cat <<'USAGE'
用法:
  sudo ./deploy/install.sh \
    --mode http-ip \
    --public-ip <服务器固定公网IPv4> \
    --accept-insecure-http \
    [--app-image <仓库/镜像:明确版本|仓库/镜像@sha256:digest>]

说明:
  不传 --app-image 时，从当前源码构建 license-platform:0.1.0-local。
  传入 --app-image 时，拉取预构建镜像；禁止隐式标签和 latest。
  当前仅支持 http-ip。必须显式接受明文 HTTP 风险。
USAGE
}

while (($# > 0)); do
  case "$1" in
    --mode)
      (($# >= 2)) || fatal "--mode 缺少值"
      MODE="$2"
      shift 2
      ;;
    --public-ip)
      (($# >= 2)) || fatal "--public-ip 缺少值"
      PUBLIC_IPV4="$2"
      shift 2
      ;;
    --accept-insecure-http)
      ACCEPT_INSECURE_HTTP=1
      shift
      ;;
    --app-image)
      (($# >= 2)) || fatal "--app-image 缺少值"
      APP_IMAGE_CLI="$2"
      BUILD_LOCAL=0
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      usage >&2
      fatal "未知参数: $1"
      ;;
  esac
done

require_root

[[ "${MODE}" == "http-ip" ]] || fatal "必须指定 --mode http-ip；当前不支持其他模式。"
(( ACCEPT_INSECURE_HTTP == 1 )) || fatal "http-ip 必须同时指定 --accept-insecure-http。"
[[ -n "${PUBLIC_IPV4}" ]] || fatal "必须指定 --public-ip。"

cat >&2 <<'WARNING'
================================================================================
严重安全警告：你选择的是公网明文 HTTP 模式。
此模式不提供传输加密、完整性保护或服务端身份认证。后台口令、许可证、API
令牌和业务数据可能被网络中的第三方窃听或篡改。它不能被描述为安全部署。
应尽快配置域名和 HTTPS；在此之前请使用防火墙、VPN/专线和来源地址白名单降低暴露。
================================================================================
WARNING

validate_public_ipv4() {
  local value="$1"
  local a b c d octet
  [[ "${value}" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || fatal "不是有效 IPv4: ${value}"
  IFS='.' read -r a b c d <<<"${value}"
  for octet in "${a}" "${b}" "${c}" "${d}"; do
    ((10#${octet} >= 0 && 10#${octet} <= 255)) || fatal "不是有效 IPv4: ${value}"
  done

  ((10#${a} >= 1 && 10#${a} <= 223)) || fatal "不是可用公网单播 IPv4: ${value}"
  ((10#${a} != 10)) || fatal "--public-ip 不能是 RFC1918 私网地址: ${value}"
  ! ((10#${a} == 100 && 10#${b} >= 64 && 10#${b} <= 127)) || fatal "--public-ip 不能是运营商级 NAT 地址: ${value}"
  ! ((10#${a} == 127)) || fatal "--public-ip 不能是回环地址: ${value}"
  ! ((10#${a} == 169 && 10#${b} == 254)) || fatal "--public-ip 不能是链路本地地址: ${value}"
  ! ((10#${a} == 172 && 10#${b} >= 16 && 10#${b} <= 31)) || fatal "--public-ip 不能是 RFC1918 私网地址: ${value}"
  ! ((10#${a} == 192 && 10#${b} == 168)) || fatal "--public-ip 不能是 RFC1918 私网地址: ${value}"
  ! ((10#${a} == 192 && 10#${b} == 0 && 10#${c} == 2)) || fatal "--public-ip 不能是文档保留地址: ${value}"
  ! ((10#${a} == 198 && 10#${b} == 51 && 10#${c} == 100)) || fatal "--public-ip 不能是文档保留地址: ${value}"
  ! ((10#${a} == 203 && 10#${b} == 0 && 10#${c} == 113)) || fatal "--public-ip 不能是文档保留地址: ${value}"
  ! ((10#${a} == 198 && (10#${b} == 18 || 10#${b} == 19))) || fatal "--public-ip 不能是基准测试保留地址: ${value}"
}

check_os() {
  [[ -r /etc/os-release ]] || fatal "无法识别操作系统（缺少 /etc/os-release）。"
  # shellcheck disable=SC1091
  source /etc/os-release
  local family=" ${ID:-} ${ID_LIKE:-} "
  [[ "${family}" == *" ubuntu "* || "${family}" == *" debian "* ]] || \
    fatal "仅支持 Ubuntu/Debian，当前为 ${PRETTY_NAME:-unknown}。"
  log "操作系统检查通过: ${PRETTY_NAME:-${ID}}"
}

check_resources() {
  local mem_kib=0 key value unit
  while IFS=' ' read -r key value unit; do
    if [[ "${key}" == "MemTotal:" ]]; then
      mem_kib="${value}"
      break
    fi
  done </proc/meminfo
  (( mem_kib >= 1572864 )) || fatal "可用物理内存规格不足 1.5 GiB；目标至少为 2 GiB。"
  if (( mem_kib < 1966080 )); then
    warn "检测到内存略低于 2 GiB（$((mem_kib / 1024)) MiB）；可能发生内存压力。"
  fi

  local available_kib
  available_kib="$(df -Pk /var/lib | awk 'NR==2 {print $4}')"
  [[ "${available_kib}" =~ ^[0-9]+$ ]] || fatal "无法读取磁盘可用空间。"
  (( available_kib >= 5242880 )) || fatal "磁盘可用空间不足 5 GiB。"
  if (( available_kib < 10485760 )); then
    warn "磁盘可用空间低于建议的 10 GiB；请监控镜像、数据库和备份增长。"
  fi
  log "资源检查通过: 内存 $((mem_kib / 1024)) MiB，磁盘可用 $((available_kib / 1024 / 1024)) GiB。"
}

check_docker() {
  require_command docker
  docker info >/dev/null 2>&1 || fatal "Docker daemon 不可用。"
  docker compose version >/dev/null 2>&1 || fatal "需要 Docker Compose v2（docker compose）。"
  log "Docker 与 Compose 检查通过。"
}

check_systemd() {
  require_command systemctl
  [[ -d /run/systemd/system ]] || fatal "当前系统未由 systemd 管理，无法创建已授权的每日备份 timer。"
  systemctl is-system-running >/dev/null 2>&1 || {
    local state
    state="$(systemctl is-system-running 2>/dev/null || true)"
    [[ "${state}" == "degraded" ]] || fatal "systemd 未运行（状态: ${state:-unknown}）。"
    warn "systemd 当前为 degraded；会继续，但必须检查失败的系统单元。"
  }
  log "systemd 检查通过。"
}

check_public_ip_is_local() {
  require_command ip
  if ! ip -o -4 address show | grep -Fq " ${PUBLIC_IPV4}/"; then
    fatal "公网 IPv4 ${PUBLIC_IPV4} 未配置在本机接口上；为避免绑定到错误接口，安装已停止。NAT 场景请先把固定地址正确映射/配置到主机。"
  fi
}

check_port_80() {
  local container_id project service binding host_binding own_binding=0
  while IFS= read -r container_id; do
    [[ -n "${container_id}" ]] || continue
    project="$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "${container_id}" 2>/dev/null || true)"
    service="$(docker inspect --format '{{index .Config.Labels "com.docker.compose.service"}}' "${container_id}" 2>/dev/null || true)"
    while IFS= read -r binding; do
      [[ -n "${binding}" ]] || continue
      host_binding="${binding##* -> }"
      case "${host_binding}" in
        "${PUBLIC_IPV4}:80"|"0.0.0.0:80"|"[::]:80")
          if [[ "${project}" == "license-platform" && "${service}" == "caddy" ]]; then
            own_binding=1
          else
            fatal "目标地址的 TCP 80 已由现有容器 ${container_id} 使用；安装不会停止或修改该容器。"
          fi
          ;;
      esac
    done < <(docker port "${container_id}" 2>/dev/null || true)
  done < <(docker ps --filter publish=80 --format '{{.ID}}')

  require_command ss
  if (( own_binding == 0 )); then
    while IFS=' ' read -r _state _recv_q _send_q local_address _peer; do
      case "${local_address}" in
        "${PUBLIC_IPV4}:80"|"0.0.0.0:80"|"*:80"|"[::]:80")
          fatal "目标地址的主机 TCP 80 已被占用；安装不会停止或修改现有进程/容器。"
          ;;
      esac
    done < <(ss -H -ltn 'sport = :80')
  fi
  log "端口 80 检查通过。"
}

generate_base64_secret() {
  local bytes="$1"
  if command -v openssl >/dev/null 2>&1; then
    openssl rand -base64 "${bytes}" | tr -d '\n'
  else
    require_command base64
    require_command dd
    dd if=/dev/urandom bs=1 count="${bytes}" status=none | base64 | tr -d '\n'
  fi
}

create_secret_if_missing() {
  local path="$1"
  local bytes="$2"
  local secrets_gid="${SECRETS_GID_VALUE:-${DEFAULT_SECRETS_GID}}"
  [[ ! -L "${path}" ]] || fatal "拒绝写入符号链接形式的秘密文件: ${path}"
  if [[ ! -e "${path}" ]]; then
    local temporary
    temporary="$(mktemp "${path}.XXXXXX")"
    generate_base64_secret "${bytes}" >"${temporary}"
    printf '\n' >>"${temporary}"
    chown root:"${secrets_gid}" "${temporary}"
    chmod 0440 "${temporary}"
    mv "${temporary}" "${path}"
    log "已生成秘密文件 ${path}。"
  fi
  [[ -f "${path}" ]] || fatal "秘密路径不是普通文件: ${path}"
  chown root:"${secrets_gid}" "${path}"
  chmod 0440 "${path}"
}

check_local_source_contract() {
  local web_root="${SOURCE_ROOT}/src/LicensePlatform.Web"
  local missing=()
  require_command grep

  grep -r --include='*.cs' -Fq -- '--migrate' "${web_root}" || missing+=("--migrate 一次性迁移入口")
  grep -r --include='*.cs' -Fq -- '--admin-reset' "${web_root}" || missing+=("--admin-reset 管理员密码重置入口")
  grep -r --include='*.cs' -Fq -- '/health' "${web_root}" || missing+=("GET /health 健康端点")
  grep -r --include='*.cs' -Fq -- 'AllowInsecureHttp' "${web_root}" || missing+=("明文 HTTP 的显式开关处理")
  grep -r --include='*.cs' -Fq -- 'MasterEncryptionKeyFile' "${web_root}" || missing+=("主解密密钥文件读取")
  grep -r --include='*.cs' -Fq -- 'CardLookupKeyFile' "${web_root}" || missing+=("查询/HMAC 密钥文件读取")

  if (( ${#missing[@]} > 0 )); then
    printf '当前源码缺少部署运行契约证据:\n' >&2
    printf '  - %s\n' "${missing[@]}" >&2
    fatal "为避免迁移挂起或不安全启动，本地构建已停止。请先在 src 中实现契约（本脚本不会修改 src），或使用经过兼容验证的 --app-image。"
  fi
}

INSTALL_SOURCE_STAGE=""
INSTALL_SOURCE_BACKUP=""
INSTALL_SOURCE_PREVIOUS="false"
INSTALL_SOURCE_PROMOTED=0

prepare_source_stage() {
  [[ -d "${SOURCE_ROOT}/src" && ! -L "${SOURCE_ROOT}/src" ]] || fatal "本地源码 src 必须是普通目录而不是符号链接。"
  [[ -f "${SOURCE_ROOT}/Dockerfile" && ! -L "${SOURCE_ROOT}/Dockerfile" ]] || fatal "本地构建缺少普通文件 Dockerfile。"
  INSTALL_SOURCE_STAGE="$(mktemp -d "${DEFAULT_INSTALL_DIR}/source.stage.XXXXXX")"
  if (( INSTALL_TRANSACTION_ACTIVE == 1 )); then
    write_env_value "${INSTALL_TRANSACTION_DIR}/state.env" SOURCE_STAGE "${INSTALL_SOURCE_STAGE}"
  fi
  cp -a -- "${SOURCE_ROOT}/src" "${INSTALL_SOURCE_STAGE}/src"

  local root_file
  for root_file in Dockerfile global.json Directory.Build.props Directory.Build.targets Directory.Packages.props .dockerignore; do
    if [[ -f "${SOURCE_ROOT}/${root_file}" && ! -L "${SOURCE_ROOT}/${root_file}" ]]; then
      install -o root -g root -m 0644 "${SOURCE_ROOT}/${root_file}" "${INSTALL_SOURCE_STAGE}/${root_file}"
    fi
  done
  chown -R root:root "${INSTALL_SOURCE_STAGE}"
  chmod -R u=rwX,go=rX "${INSTALL_SOURCE_STAGE}"
}

promote_source_stage() {
  local target="${DEFAULT_INSTALL_DIR}/source"
  [[ -n "${INSTALL_SOURCE_STAGE}" && -d "${INSTALL_SOURCE_STAGE}" && ! -L "${INSTALL_SOURCE_STAGE}" ]] || fatal "本地源码 staging 不可用。"
  if [[ -z "${INSTALL_SOURCE_BACKUP}" ]]; then
    INSTALL_SOURCE_BACKUP="${DEFAULT_INSTALL_DIR}/source.before-install-$(date --utc +%Y%m%dT%H%M%S.%NZ)"
  fi
  [[ ! -e "${INSTALL_SOURCE_BACKUP}" && ! -L "${INSTALL_SOURCE_BACKUP}" ]] || fatal "安装源码回滚路径已存在。"
  if [[ -e "${target}" || -L "${target}" ]]; then
    [[ -d "${target}" && ! -L "${target}" ]] || fatal "受管源码路径不是安全目录。"
    INSTALL_SOURCE_PREVIOUS="true"
  fi
  if (( INSTALL_TRANSACTION_ACTIVE == 1 )); then
    write_env_value "${INSTALL_TRANSACTION_DIR}/state.env" SOURCE_SWITCH_STARTED true
    sync -f "${INSTALL_TRANSACTION_DIR}/state.env"
  fi
  # Set this before the first rename, so failure between the two renames is
  # recoverable even on an initial install without a durable rerun journal.
  INSTALL_SOURCE_PROMOTED=1
  if [[ "${INSTALL_SOURCE_PREVIOUS}" == "true" ]]; then
    mv -- "${target}" "${INSTALL_SOURCE_BACKUP}"
  fi
  mv -- "${INSTALL_SOURCE_STAGE}" "${target}"
  INSTALL_SOURCE_STAGE=""
}

restore_install_source() {
  local target="${DEFAULT_INSTALL_DIR}/source"
  if (( INSTALL_SOURCE_PROMOTED == 1 )); then
    if [[ "${INSTALL_SOURCE_PREVIOUS}" == "true" ]]; then
      if [[ -e "${INSTALL_SOURCE_BACKUP}" || -L "${INSTALL_SOURCE_BACKUP}" ]]; then
        [[ -d "${INSTALL_SOURCE_BACKUP}" && ! -L "${INSTALL_SOURCE_BACKUP}" ]] || return 1
        if [[ -e "${target}" || -L "${target}" ]]; then
          [[ -d "${target}" && ! -L "${target}" ]] || return 1
          rm -rf -- "${target}" || return 1
        fi
        mv -- "${INSTALL_SOURCE_BACKUP}" "${target}" || return 1
      else
        # The first rename never happened, or recovery already restored it.
        [[ -d "${target}" && ! -L "${target}" ]] || return 1
      fi
    elif [[ -e "${target}" || -L "${target}" ]]; then
      [[ -d "${target}" && ! -L "${target}" ]] || return 1
      rm -rf -- "${target}" || return 1
    fi
  fi
  if [[ -n "${INSTALL_SOURCE_STAGE}" && ( -e "${INSTALL_SOURCE_STAGE}" || -L "${INSTALL_SOURCE_STAGE}" ) ]]; then
    [[ -d "${INSTALL_SOURCE_STAGE}" && ! -L "${INSTALL_SOURCE_STAGE}" ]] || return 1
    rm -rf -- "${INSTALL_SOURCE_STAGE}" || return 1
  fi
}

install_managed_files() {
  local managed_path
  for managed_path in \
    "${DEFAULT_INSTALL_DIR}" \
    "${DEFAULT_INSTALL_DIR}/bin" \
    "${DEFAULT_INSTALL_DIR}/lib" \
    "${DEFAULT_INSTALL_DIR}/docs" \
    "${DEFAULT_INSTALL_DIR}/source" \
    "${DEFAULT_CONFIG_DIR}" \
    "${DEFAULT_CONFIG_DIR}/secrets" \
    "${DEFAULT_DATA_DIR}" \
    "${DEFAULT_DATA_DIR}/rollback" \
    "${DEFAULT_BACKUP_DIR}"; do
    [[ ! -L "${managed_path}" ]] || fatal "拒绝使用符号链接形式的受管目录: ${managed_path}"
  done

  install -d -o root -g root -m 0750 \
    "${DEFAULT_INSTALL_DIR}" \
    "${DEFAULT_INSTALL_DIR}/bin" \
    "${DEFAULT_INSTALL_DIR}/lib" \
    "${DEFAULT_INSTALL_DIR}/docs" \
    "${DEFAULT_CONFIG_DIR}" \
    "${DEFAULT_CONFIG_DIR}/secrets" \
    "${DEFAULT_DATA_DIR}" \
    "${DEFAULT_DATA_DIR}/rollback" \
    "${DEFAULT_BACKUP_DIR}"
  chmod 0700 "${DEFAULT_CONFIG_DIR}" "${DEFAULT_CONFIG_DIR}/secrets" "${DEFAULT_DATA_DIR}" "${DEFAULT_DATA_DIR}/rollback" "${DEFAULT_BACKUP_DIR}"

  install -o root -g root -m 0644 "${SCRIPT_DIR}/compose.yml" "${DEFAULT_INSTALL_DIR}/compose.yml"
  install -o root -g root -m 0644 "${SCRIPT_DIR}/compose.https.yml" "${DEFAULT_INSTALL_DIR}/compose.https.yml"
  install -o root -g root -m 0644 "${SCRIPT_DIR}/Caddyfile.https" "${DEFAULT_INSTALL_DIR}/Caddyfile.https"
  install -o root -g root -m 0644 "${SCRIPT_DIR}/lib/common.sh" "${DEFAULT_INSTALL_DIR}/lib/common.sh"

  local script
  for script in backup.sh restore.sh upgrade.sh rollback.sh health-check.sh; do
    install -o root -g root -m 0750 "${SCRIPT_DIR}/${script}" "${DEFAULT_INSTALL_DIR}/bin/${script}"
  done
  install -o root -g root -m 0750 "${SCRIPT_DIR}/licensectl" "${DEFAULT_INSTALL_DIR}/licensectl"
  if [[ -e "${DEFAULT_SBIN_LINK}" || -L "${DEFAULT_SBIN_LINK}" ]]; then
    [[ -L "${DEFAULT_SBIN_LINK}" && "$(readlink -f "${DEFAULT_SBIN_LINK}")" == "${DEFAULT_INSTALL_DIR}/licensectl" ]] || \
      fatal "${DEFAULT_SBIN_LINK} 已存在且不属于本部署；不会覆盖。"
  else
    ln -s "${DEFAULT_INSTALL_DIR}/licensectl" "${DEFAULT_SBIN_LINK}"
  fi

  if [[ -d "${SOURCE_ROOT}/docs" && ! -L "${SOURCE_ROOT}/docs" ]]; then
    local docs_stage
    docs_stage="$(mktemp -d "${DEFAULT_INSTALL_DIR}/docs.stage.XXXXXX")"
    cp -a "${SOURCE_ROOT}/docs/." "${docs_stage}/"
    chown -R root:root "${docs_stage}"
    chmod -R u=rwX,go=rX "${docs_stage}"
    # Never merge into an old docs tree containing symlinks or stale files.
    [[ -d "${DEFAULT_INSTALL_DIR}/docs" && ! -L "${DEFAULT_INSTALL_DIR}/docs" ]] || fatal "受管文档目录不安全。"
    rm -rf -- "${DEFAULT_INSTALL_DIR}/docs"
    mv -- "${docs_stage}" "${DEFAULT_INSTALL_DIR}/docs"
  fi

  if [[ -L "${DEFAULT_CONFIG_DIR}/Caddyfile" ]]; then
    fatal "拒绝使用符号链接形式的 Caddyfile。"
  elif [[ ! -e "${DEFAULT_CONFIG_DIR}/Caddyfile" ]]; then
    install -o root -g root -m 0644 "${SCRIPT_DIR}/Caddyfile" "${DEFAULT_CONFIG_DIR}/Caddyfile"
  elif [[ -f "${DEFAULT_CONFIG_DIR}/Caddyfile" ]]; then
    chown root:root "${DEFAULT_CONFIG_DIR}/Caddyfile"
    chmod 0644 "${DEFAULT_CONFIG_DIR}/Caddyfile"
    log "保留现有 ${DEFAULT_CONFIG_DIR}/Caddyfile（幂等安装不会覆盖运维修改）。"
  else
    fatal "${DEFAULT_CONFIG_DIR}/Caddyfile 不是普通文件。"
  fi

}

INSTALL_TRANSACTION_ACTIVE=0
INSTALL_TRANSACTION_DIR=""
INSTALL_PENDING_FILE="${DEFAULT_DATA_DIR}/install.pending"
INSTALL_TRANSACTION_TARGETS=(
  "${DEFAULT_INSTALL_DIR}/compose.yml"
  "${DEFAULT_INSTALL_DIR}/compose.https.yml"
  "${DEFAULT_INSTALL_DIR}/Caddyfile.https"
  "${DEFAULT_INSTALL_DIR}/licensectl"
  "${DEFAULT_INSTALL_DIR}/bin"
  "${DEFAULT_INSTALL_DIR}/lib"
  "${DEFAULT_INSTALL_DIR}/docs"
  "${DEFAULT_CONFIG_DIR}/deployment.env"
  "${DEFAULT_CONFIG_DIR}/Caddyfile"
  "${DEFAULT_SYSTEMD_DIR}/license-platform-backup.service"
  "${DEFAULT_SYSTEMD_DIR}/license-platform-backup.timer"
)

install_state_value() {
  local file="$1" key="$2" line
  line="$(grep -E "^${key}=[A-Za-z0-9_./:@-]*$" "${file}" || true)"
  [[ -n "${line}" && "${line}" != *$'\n'* ]] || fatal "安装事务缺少或重复安全字段: ${key}"
  printf '%s\n' "${line#*=}"
}

check_install_managed_paths() {
  local path
  for path in "${DEFAULT_INSTALL_DIR}" "${DEFAULT_INSTALL_DIR}/bin" "${DEFAULT_INSTALL_DIR}/lib" \
    "${DEFAULT_INSTALL_DIR}/docs" "${DEFAULT_INSTALL_DIR}/source" "${DEFAULT_CONFIG_DIR}" \
    "${DEFAULT_CONFIG_DIR}/secrets" "${DEFAULT_DATA_DIR}/rollback" "${DEFAULT_BACKUP_DIR}"; do
    [[ ! -L "${path}" && ( ! -e "${path}" || -d "${path}" ) ]] || fatal "受管目录不安全: ${path}"
  done
  [[ -d "${DEFAULT_SYSTEMD_DIR}" && ! -L "${DEFAULT_SYSTEMD_DIR}" ]] || fatal "systemd 单元目录不安全。"
  for path in "${INSTALL_TRANSACTION_TARGETS[@]}"; do
    case "${path}" in
      "${DEFAULT_INSTALL_DIR}/bin"|"${DEFAULT_INSTALL_DIR}/lib"|"${DEFAULT_INSTALL_DIR}/docs")
        [[ ! -L "${path}" && ( ! -e "${path}" || -d "${path}" ) ]] || fatal "受管目录不安全: ${path}"
        ;;
      *) [[ ! -L "${path}" && ( ! -e "${path}" || -f "${path}" ) ]] || fatal "受管文件不安全: ${path}" ;;
    esac
  done
  # install(1) follows existing file symlinks; reject those before overwriting.
  for path in common.sh; do
    [[ ! -L "${DEFAULT_INSTALL_DIR}/lib/${path}" ]] || fatal "受管库文件不能是符号链接。"
  done
  for path in backup.sh restore.sh upgrade.sh rollback.sh health-check.sh; do
    [[ ! -L "${DEFAULT_INSTALL_DIR}/bin/${path}" ]] || fatal "受管脚本不能是符号链接。"
  done
  if [[ -e "${DEFAULT_SBIN_LINK}" || -L "${DEFAULT_SBIN_LINK}" ]]; then
    [[ -L "${DEFAULT_SBIN_LINK}" && "$(readlink -f "${DEFAULT_SBIN_LINK}")" == "${DEFAULT_INSTALL_DIR}/licensectl" ]] || fatal "运维入口已存在且不属于本部署。"
  fi
}

begin_install_transaction() (
  set -Eeuo pipefail
  local directory="$1" timestamp="${1##*/}" index=0 target service container image
  local timer_enabled timer_active link_present=false source_present=false
  timestamp="${timestamp#install-}"
  load_deployment_env
  [[ "${LP_HOME}" == "${DEFAULT_INSTALL_DIR}" && "${LP_CONFIG_DIR}" == "${DEFAULT_CONFIG_DIR}" \
    && "${LP_DATA_DIR}" == "${DEFAULT_DATA_DIR}" && "${LP_BACKUP_DIR}" == "${DEFAULT_BACKUP_DIR}" \
    && "${SOURCE_DIR}" == "${DEFAULT_INSTALL_DIR}/source" ]] || fatal "安装器重跑只支持默认受管路径。"
  "${LP_HOME}/bin/health-check.sh"
  for service in app db caddy; do
    container="$(service_container_id "${service}")"
    [[ -n "${container}" ]] || fatal "缺少 ${service} 容器，无法建立安装重跑基线。"
    image="$(docker inspect --format '{{.Image}}' "${container}")"
    [[ "${image}" =~ ^sha256:[0-9a-f]{64}$ ]] || fatal "无法固定 ${service} 的当前镜像。"
    docker image tag "${image}" "license-platform-install-${service}-rollback:${timestamp,,}"
  done
  timer_enabled="$(systemctl is-enabled license-platform-backup.timer 2>/dev/null || true)"
  timer_active="$(systemctl is-active license-platform-backup.timer 2>/dev/null || true)"
  [[ "${timer_enabled}" == "enabled" || "${timer_enabled}" == "disabled" || "${timer_enabled}" == "not-found" ]] || fatal "备份 timer enable 状态不受支持，拒绝修改: ${timer_enabled}"
  [[ "${timer_active}" == "active" || "${timer_active}" == "inactive" ]] || fatal "备份 timer runtime 状态不受支持，拒绝修改: ${timer_active}"
  [[ ! -L "${DEFAULT_SBIN_LINK}" ]] || link_present=true
  [[ ! -d "${DEFAULT_INSTALL_DIR}/source" ]] || source_present=true
  LICENSE_PLATFORM_OPERATION_LOCK_HELD=1 "${LP_HOME}/bin/backup.sh" >/dev/null
  install -d -o root -g root -m 0700 "${directory}/files"
  for target in "${INSTALL_TRANSACTION_TARGETS[@]}"; do
    if [[ -e "${target}" ]]; then
      cp -a -- "${target}" "${directory}/files/${index}"
    else
      : >"${directory}/files/${index}.absent"
      chmod 0600 "${directory}/files/${index}.absent"
    fi
    ((index += 1))
  done
  cat >"${directory}/state.env" <<STATE
STATE_VERSION=1
STATUS=PREPARED
TIMER_ENABLED=${timer_enabled}
TIMER_ACTIVE=${timer_active}
SYSTEMD_TOUCHED=false
LINK_PRESENT=${link_present}
PREVIOUS_SOURCE_PRESENT=${source_present}
SOURCE_STAGE=
SOURCE_BACKUP=${DEFAULT_INSTALL_DIR}/source.before-install-${timestamp}
SOURCE_SWITCH_STARTED=false
APP_RECOVERY_IMAGE=license-platform-install-app-rollback:${timestamp,,}
DB_RECOVERY_IMAGE=license-platform-install-db-rollback:${timestamp,,}
CADDY_RECOVERY_IMAGE=license-platform-install-caddy-rollback:${timestamp,,}
STATE
  chown root:root "${directory}/state.env"
  chmod 0600 "${directory}/state.env"
  local pending_temporary
  pending_temporary="$(mktemp "${INSTALL_PENDING_FILE}.XXXXXX")"
  printf '%s\n' "${directory}" >"${pending_temporary}"
  chown root:root "${pending_temporary}"
  chmod 0600 "${pending_temporary}"
  mv -f -- "${pending_temporary}" "${INSTALL_PENDING_FILE}"
  sync -f "${INSTALL_PENDING_FILE}"
)

recover_install_transaction() (
  set -Eeuo pipefail
  local directory="$1" name="${1##*/}" journal="${1}/state.env" index=0 target snapshot
  local timer_enabled timer_active systemd_touched link_present source_switch app_image db_image caddy_image
  [[ "${directory}" == "${DEFAULT_DATA_DIR}/rollback/${name}" && "${name}" =~ ^install-[0-9]{8}T[0-9]{6}\.[0-9]{9}Z$ \
    && -d "${directory}" && ! -L "${directory}" && -d "${directory}/files" && ! -L "${directory}/files" ]] || fatal "安装事务目录不安全。"
  verify_root_0600_file "${journal}"
  [[ "$(install_state_value "${journal}" STATE_VERSION)" == "1" ]] || fatal "不支持的安装事务版本。"
  timer_enabled="$(install_state_value "${journal}" TIMER_ENABLED)"
  timer_active="$(install_state_value "${journal}" TIMER_ACTIVE)"
  systemd_touched="$(install_state_value "${journal}" SYSTEMD_TOUCHED)"
  link_present="$(install_state_value "${journal}" LINK_PRESENT)"
  INSTALL_SOURCE_PREVIOUS="$(install_state_value "${journal}" PREVIOUS_SOURCE_PRESENT)"
  INSTALL_SOURCE_STAGE="$(install_state_value "${journal}" SOURCE_STAGE)"
  INSTALL_SOURCE_BACKUP="$(install_state_value "${journal}" SOURCE_BACKUP)"
  source_switch="$(install_state_value "${journal}" SOURCE_SWITCH_STARTED)"
  [[ "${timer_enabled}" == "enabled" || "${timer_enabled}" == "disabled" || "${timer_enabled}" == "not-found" ]] || fatal "timer enable 状态无效。"
  [[ "${timer_active}" == "active" || "${timer_active}" == "inactive" ]] || fatal "timer runtime 状态无效。"
  for snapshot in "${systemd_touched}" "${link_present}" "${INSTALL_SOURCE_PREVIOUS}" "${source_switch}"; do
    [[ "${snapshot}" == "true" || "${snapshot}" == "false" ]] || fatal "安装事务布尔字段无效。"
  done
  [[ -z "${INSTALL_SOURCE_STAGE}" || ( "${INSTALL_SOURCE_STAGE}" == "${DEFAULT_INSTALL_DIR}/source.stage."* \
    && "${INSTALL_SOURCE_STAGE##*/}" =~ ^source\.stage\.[A-Za-z0-9]+$ ) ]] || fatal "安装 staging 路径不安全。"
  [[ "${INSTALL_SOURCE_BACKUP}" == "${DEFAULT_INSTALL_DIR}/source.before-${name}" ]] || fatal "安装源码备份路径不安全。"
  app_image="$(install_state_value "${journal}" APP_RECOVERY_IMAGE)"
  db_image="$(install_state_value "${journal}" DB_RECOVERY_IMAGE)"
  caddy_image="$(install_state_value "${journal}" CADDY_RECOVERY_IMAGE)"
  local timestamp="${name#install-}"
  [[ "${app_image}" == "license-platform-install-app-rollback:${timestamp,,}" \
    && "${db_image}" == "license-platform-install-db-rollback:${timestamp,,}" \
    && "${caddy_image}" == "license-platform-install-caddy-rollback:${timestamp,,}" ]] || fatal "安装安全镜像名无效。"
  for snapshot in "${app_image}" "${db_image}" "${caddy_image}"; do
    docker image inspect "${snapshot}" >/dev/null 2>&1 || fatal "安装安全镜像已不存在。"
  done
  check_install_managed_paths
  # Check the entire snapshot before changing anything. A missing snapshot is
  # not evidence that the target was originally absent.
  for target in "${INSTALL_TRANSACTION_TARGETS[@]}"; do
    snapshot="${directory}/files/${index}"
    if [[ -e "${snapshot}.absent" || -L "${snapshot}.absent" ]]; then
      verify_root_0600_file "${snapshot}.absent"
      [[ ! -e "${snapshot}" && ! -L "${snapshot}" ]] || fatal "安装快照状态冲突。"
    else
      [[ ! -L "${snapshot}" && ( -f "${snapshot}" || -d "${snapshot}" ) ]] || fatal "安装快照缺失或不安全。"
    fi
    ((index += 1))
  done
  if [[ "${systemd_touched}" == "true" ]]; then
    local timer_load_state
    timer_load_state="$(systemctl show --property=LoadState --value license-platform-backup.timer)"
    [[ "${timer_load_state}" == "loaded" || "${timer_load_state}" == "not-found" ]] || fatal "备份 timer 无法安全恢复。"
    if [[ "${timer_load_state}" == "loaded" ]]; then
      systemctl stop license-platform-backup.timer
      # Remove installer-created enablement before putting old unit files back.
      systemctl disable license-platform-backup.timer
    fi
  fi
  INSTALL_SOURCE_PROMOTED=0
  [[ "${source_switch}" != "true" ]] || INSTALL_SOURCE_PROMOTED=1
  restore_install_source
  index=0
  for target in "${INSTALL_TRANSACTION_TARGETS[@]}"; do
    snapshot="${directory}/files/${index}"
    if [[ -d "${target}" ]]; then
      rm -rf -- "${target}"
    elif [[ -e "${target}" ]]; then
      rm -f -- "${target}"
    fi
    if [[ -e "${snapshot}" ]]; then
      cp -a -- "${snapshot}" "${target}"
    fi
    ((index += 1))
  done
  if [[ "${link_present}" == "true" ]]; then
    [[ -L "${DEFAULT_SBIN_LINK}" ]] || ln -s "${DEFAULT_INSTALL_DIR}/licensectl" "${DEFAULT_SBIN_LINK}"
  elif [[ -L "${DEFAULT_SBIN_LINK}" ]]; then
    rm -f -- "${DEFAULT_SBIN_LINK}"
  fi
  LP_HOME="${DEFAULT_INSTALL_DIR}"
  LP_CONFIG_DIR="${DEFAULT_CONFIG_DIR}"
  LP_DATA_DIR="${DEFAULT_DATA_DIR}"
  LP_ENV_FILE="${DEFAULT_CONFIG_DIR}/deployment.env"
  write_env_values "${LP_ENV_FILE}" APP_IMAGE "${app_image}" POSTGRES_IMAGE "${db_image}" CADDY_IMAGE "${caddy_image}"
  unset APP_IMAGE APP_SOURCE_MODE SOURCE_DIR HTTPS_ENABLED ALLOW_INSECURE_HTTP HSTS_ENABLED
  load_deployment_env
  compose config --quiet
  compose stop caddy app
  compose up --detach --force-recreate db
  wait_for_service db 180
  compose up --detach --force-recreate app caddy
  wait_for_service app 180
  wait_for_service caddy 120
  "${LP_HOME}/bin/health-check.sh"
  if [[ "${systemd_touched}" == "true" ]]; then
    systemctl daemon-reload
    if [[ "${timer_enabled}" == "enabled" ]]; then
      systemctl enable license-platform-backup.timer
    fi
    if [[ "${timer_active}" == "active" ]]; then
      systemctl start license-platform-backup.timer
    fi
  fi
  write_env_value "${journal}" STATUS RECOVERED
  sync -f "${DEFAULT_INSTALL_DIR}"
  sync -f "${DEFAULT_CONFIG_DIR}"
  sync -f "${DEFAULT_SYSTEMD_DIR}"
  sync -f "${journal}"
)

validate_public_ipv4 "${PUBLIC_IPV4}"
check_os
check_resources
check_docker
check_systemd
require_command curl
require_command flock
require_command grep
require_command sync
[[ ! -L "${DEFAULT_DATA_DIR}" ]] || fatal "拒绝使用符号链接形式的数据目录: ${DEFAULT_DATA_DIR}"
install -d -o root -g root -m 0700 "${DEFAULT_DATA_DIR}"
exec 8>"${DEFAULT_DATA_DIR}/operation.lock"
flock --nonblock 8 || fatal "已有安装、恢复、升级或其他独占运维任务正在运行。"
LP_DATA_DIR="${DEFAULT_DATA_DIR}"
reject_pending_transactions install.pending
if [[ -e "${INSTALL_PENDING_FILE}" || -L "${INSTALL_PENDING_FILE}" ]]; then
  verify_root_0600_file "${INSTALL_PENDING_FILE}"
  INTERRUPTED_INSTALL_DIR="$(<"${INSTALL_PENDING_FILE}")"
  warn "检测到中断的安装重跑；先恢复先前受管文件、配置、源码、镜像和 timer。"
  set +e
  recover_install_transaction "${INTERRUPTED_INSTALL_DIR}"
  INSTALL_RECOVERY_RC=$?
  set -e
  if (( INSTALL_RECOVERY_RC == 0 )); then
    rm -f -- "${INSTALL_PENDING_FILE}"
    sync -f "${DEFAULT_DATA_DIR}"
    fatal "中断的安装重跑已恢复并通过严格健康检查；请重新发起安装。"
  fi
  fatal "安装重跑恢复失败；事务标记保留，不能假定部署健康。"
fi
check_install_managed_paths
check_public_ip_is_local
check_port_80

APP_IMAGE="${DEFAULT_APP_IMAGE}"
APP_SOURCE_MODE="local"
HTTPS_ENABLED_VALUE="false"
ALLOW_INSECURE_HTTP_VALUE="true"
HSTS_ENABLED_VALUE="false"
CADDY_SITE_ADDRESS_VALUE="http://${PUBLIC_IPV4}"
POSTGRES_IMAGE_VALUE="${DEFAULT_POSTGRES_IMAGE}"
CADDY_IMAGE_VALUE="${DEFAULT_CADDY_IMAGE}"
DOTNET_SDK_IMAGE_VALUE="${DEFAULT_DOTNET_SDK_IMAGE}"
DOTNET_ASPNET_IMAGE_VALUE="${DEFAULT_DOTNET_ASPNET_IMAGE}"
SECRETS_GID_VALUE="${DEFAULT_SECRETS_GID}"
if [[ -e "${DEFAULT_CONFIG_DIR}/deployment.env" || -L "${DEFAULT_CONFIG_DIR}/deployment.env" ]]; then
  verify_root_0600_file "${DEFAULT_CONFIG_DIR}/deployment.env"
  # Preserve the selected image and source mode without allowing the old file to
  # overwrite this invocation's validated PUBLIC_IPV4 or other shell variables.
  mapfile -t existing_selection < <(
    # shellcheck disable=SC1091
    source "${DEFAULT_CONFIG_DIR}/deployment.env"
    printf '%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n' \
      "${APP_IMAGE:-${DEFAULT_APP_IMAGE}}" \
      "${APP_SOURCE_MODE:-local}" \
      "${HTTPS_ENABLED:-false}" \
      "${ALLOW_INSECURE_HTTP:-true}" \
      "${HSTS_ENABLED:-false}" \
      "${CADDY_SITE_ADDRESS:-http://${PUBLIC_IPV4}}" \
      "${POSTGRES_IMAGE:-${DEFAULT_POSTGRES_IMAGE}}" \
      "${CADDY_IMAGE:-${DEFAULT_CADDY_IMAGE}}" \
      "${DOTNET_SDK_IMAGE:-${DEFAULT_DOTNET_SDK_IMAGE}}" \
      "${DOTNET_ASPNET_IMAGE:-${DEFAULT_DOTNET_ASPNET_IMAGE}}" \
      "${SECRETS_GID:-${DEFAULT_SECRETS_GID}}"
  )
  APP_IMAGE="${existing_selection[0]:-${DEFAULT_APP_IMAGE}}"
  APP_SOURCE_MODE="${existing_selection[1]:-local}"
  HTTPS_ENABLED_VALUE="${existing_selection[2]:-false}"
  ALLOW_INSECURE_HTTP_VALUE="${existing_selection[3]:-true}"
  HSTS_ENABLED_VALUE="${existing_selection[4]:-false}"
  CADDY_SITE_ADDRESS_VALUE="${existing_selection[5]:-http://${PUBLIC_IPV4}}"
  POSTGRES_IMAGE_VALUE="${existing_selection[6]:-${DEFAULT_POSTGRES_IMAGE}}"
  CADDY_IMAGE_VALUE="${existing_selection[7]:-${DEFAULT_CADDY_IMAGE}}"
  DOTNET_SDK_IMAGE_VALUE="${existing_selection[8]:-${DEFAULT_DOTNET_SDK_IMAGE}}"
  DOTNET_ASPNET_IMAGE_VALUE="${existing_selection[9]:-${DEFAULT_DOTNET_ASPNET_IMAGE}}"
  SECRETS_GID_VALUE="${existing_selection[10]:-${DEFAULT_SECRETS_GID}}"
  if [[ "${HTTPS_ENABLED_VALUE}" == "false" ]]; then
    CADDY_SITE_ADDRESS_VALUE="http://${PUBLIC_IPV4}"
  fi
  if [[ "${APP_SOURCE_MODE}" == "remote" ]]; then
    warn "检测到旧版 APP_SOURCE_MODE=remote；本次迁移为 image。"
    APP_SOURCE_MODE="image"
  fi
  if [[ "${APP_SOURCE_MODE}" == "image" ]]; then
    BUILD_LOCAL=0
  fi
fi
if [[ -n "${APP_IMAGE_CLI}" ]]; then
  APP_IMAGE="${APP_IMAGE_CLI}"
  APP_SOURCE_MODE="image"
fi
[[ "${APP_SOURCE_MODE}" == "local" || "${APP_SOURCE_MODE}" == "image" ]] || fatal "deployment.env 中 APP_SOURCE_MODE 无效。"
[[ "${HTTPS_ENABLED_VALUE}" == "true" || "${HTTPS_ENABLED_VALUE}" == "false" ]] || fatal "deployment.env 中 HTTPS_ENABLED 无效。"
[[ "${HSTS_ENABLED_VALUE}" == "true" || "${HSTS_ENABLED_VALUE}" == "false" ]] || fatal "deployment.env 中 HSTS_ENABLED 无效。"
[[ "${SECRETS_GID_VALUE}" =~ ^[0-9]+$ ]] \
  && ((10#${SECRETS_GID_VALUE} >= 1 && 10#${SECRETS_GID_VALUE} <= 2147483647)) \
  || fatal "deployment.env 中 SECRETS_GID 无效。"
if [[ "${HTTPS_ENABLED_VALUE}" == "true" ]]; then
  ALLOW_INSECURE_HTTP_VALUE="false"
else
  ALLOW_INSECURE_HTTP_VALUE="true"
  HSTS_ENABLED_VALUE="false"
fi
validate_pinned_image "${APP_IMAGE}" "应用镜像"
validate_pinned_image "${POSTGRES_IMAGE_VALUE}" "PostgreSQL 镜像"
validate_pinned_image "${CADDY_IMAGE_VALUE}" "Caddy 镜像"
validate_pinned_image "${DOTNET_SDK_IMAGE_VALUE}" ".NET SDK 镜像"
validate_pinned_image "${DOTNET_ASPNET_IMAGE_VALUE}" ".NET ASP.NET 镜像"

if (( BUILD_LOCAL == 1 )); then
  [[ -f "${SOURCE_ROOT}/src/LicensePlatform.Web/LicensePlatform.Web.csproj" ]] || \
    fatal "本地构建缺少目标项目 src/LicensePlatform.Web/LicensePlatform.Web.csproj；请补全源码或使用 --app-image。"
  check_local_source_contract
fi

INSTALL_COMPLETED=0
install_exit_handler() {
  local rc=$? recovery_rc
  trap - EXIT INT TERM
  if (( rc != 0 && INSTALL_COMPLETED == 0 )); then
    if (( INSTALL_TRANSACTION_ACTIVE == 1 )); then
      warn "安装重跑失败；正在恢复先前部署。数据库迁移不会自动降级。"
      set +e
      recover_install_transaction "${INSTALL_TRANSACTION_DIR}"
      recovery_rc=$?
      set -e
      if (( recovery_rc == 0 )); then
        rm -f -- "${INSTALL_PENDING_FILE}"
        sync -f "${DEFAULT_DATA_DIR}"
        warn "先前受管文件、配置、源码、镜像和 timer 已恢复并通过严格健康检查。"
      else
        warn "安装重跑恢复失败；事务标记保留，不能假定部署健康。"
        exit 2
      fi
    elif ! restore_install_source; then
      warn "首次安装失败，且本地源码 staging 无法自动恢复；不会伪报回滚成功。"
      exit 2
    fi
  fi
  exit "${rc}"
}
trap install_exit_handler EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

if [[ -f "${DEFAULT_CONFIG_DIR}/deployment.env" ]]; then
  INSTALL_TIMESTAMP="$(date --utc +%Y%m%dT%H%M%S.%NZ)"
  INSTALL_TRANSACTION_DIR="${DEFAULT_DATA_DIR}/rollback/install-${INSTALL_TIMESTAMP}"
  [[ ! -e "${INSTALL_TRANSACTION_DIR}" && ! -L "${INSTALL_TRANSACTION_DIR}" ]] || fatal "安装事务目录已存在。"
  install -d -o root -g root -m 0700 "${INSTALL_TRANSACTION_DIR}"
  begin_install_transaction "${INSTALL_TRANSACTION_DIR}"
  INSTALL_TRANSACTION_ACTIVE=1
  INSTALL_SOURCE_BACKUP="${DEFAULT_INSTALL_DIR}/source.before-install-${INSTALL_TIMESTAMP}"
  INSTALL_SOURCE_PREVIOUS="$(install_state_value "${INSTALL_TRANSACTION_DIR}/state.env" PREVIOUS_SOURCE_PRESENT)"
  # Quiesce the old timer before replacing its executable files or units.
  write_env_value "${INSTALL_TRANSACTION_DIR}/state.env" SYSTEMD_TOUCHED true
  sync -f "${INSTALL_TRANSACTION_DIR}/state.env"
  if [[ "$(install_state_value "${INSTALL_TRANSACTION_DIR}/state.env" TIMER_ACTIVE)" == "active" ]]; then
    systemctl stop license-platform-backup.timer
  fi
fi

install_managed_files

if (( BUILD_LOCAL == 1 )); then
  prepare_source_stage
fi

create_secret_if_missing "${DEFAULT_CONFIG_DIR}/secrets/postgres_password" 32
create_secret_if_missing "${DEFAULT_CONFIG_DIR}/secrets/card_lookup_key" 32
create_secret_if_missing "${DEFAULT_CONFIG_DIR}/secrets/master_encryption_key" 64
create_secret_if_missing "${DEFAULT_CONFIG_DIR}/secrets/admin_initial_password" 24

INSTALL_ENV_TEMPORARY="$(mktemp "${DEFAULT_CONFIG_DIR}/deployment.env.XXXXXX")"
cat >"${INSTALL_ENV_TEMPORARY}" <<ENV
INSTALL_DIR=${DEFAULT_INSTALL_DIR}
CONFIG_DIR=${DEFAULT_CONFIG_DIR}
DATA_DIR=${DEFAULT_DATA_DIR}
BACKUP_DIR=${DEFAULT_BACKUP_DIR}
SOURCE_DIR=${DEFAULT_INSTALL_DIR}/source
PUBLIC_IPV4=${PUBLIC_IPV4}
CADDY_SITE_ADDRESS=${CADDY_SITE_ADDRESS_VALUE}
HTTPS_ENABLED=${HTTPS_ENABLED_VALUE}
ALLOW_INSECURE_HTTP=${ALLOW_INSECURE_HTTP_VALUE}
HSTS_ENABLED=${HSTS_ENABLED_VALUE}
SECRETS_GID=${SECRETS_GID_VALUE}
APP_IMAGE=${APP_IMAGE}
APP_SOURCE_MODE=${APP_SOURCE_MODE}
POSTGRES_IMAGE=${POSTGRES_IMAGE_VALUE}
CADDY_IMAGE=${CADDY_IMAGE_VALUE}
DOTNET_SDK_IMAGE=${DOTNET_SDK_IMAGE_VALUE}
DOTNET_ASPNET_IMAGE=${DOTNET_ASPNET_IMAGE_VALUE}
POSTGRES_DB=license_platform
POSTGRES_USER=license_platform
BACKUP_RETENTION_DAYS=14
MIGRATION_TIMEOUT_SECONDS=600
APP_MEMORY_LIMIT=640m
APP_CPU_LIMIT=1.00
DB_MEMORY_LIMIT=640m
DB_CPU_LIMIT=0.80
CADDY_MEMORY_LIMIT=128m
CADDY_CPU_LIMIT=0.20
ENV
if (( INSTALL_TRANSACTION_ACTIVE == 1 )); then
  # Preserve operational tuning and unknown future keys on a successful rerun;
  # only the explicitly managed selection is updated atomically.
  install -o root -g root -m 0600 "${DEFAULT_CONFIG_DIR}/deployment.env" "${INSTALL_ENV_TEMPORARY}"
  write_env_values "${INSTALL_ENV_TEMPORARY}" \
    INSTALL_DIR "${DEFAULT_INSTALL_DIR}" CONFIG_DIR "${DEFAULT_CONFIG_DIR}" \
    DATA_DIR "${DEFAULT_DATA_DIR}" BACKUP_DIR "${DEFAULT_BACKUP_DIR}" \
    SOURCE_DIR "${DEFAULT_INSTALL_DIR}/source" PUBLIC_IPV4 "${PUBLIC_IPV4}" \
    CADDY_SITE_ADDRESS "${CADDY_SITE_ADDRESS_VALUE}" HTTPS_ENABLED "${HTTPS_ENABLED_VALUE}" \
    ALLOW_INSECURE_HTTP "${ALLOW_INSECURE_HTTP_VALUE}" HSTS_ENABLED "${HSTS_ENABLED_VALUE}" \
    SECRETS_GID "${SECRETS_GID_VALUE}" APP_IMAGE "${APP_IMAGE}" APP_SOURCE_MODE "${APP_SOURCE_MODE}" \
    POSTGRES_IMAGE "${POSTGRES_IMAGE_VALUE}" CADDY_IMAGE "${CADDY_IMAGE_VALUE}" \
    DOTNET_SDK_IMAGE "${DOTNET_SDK_IMAGE_VALUE}" DOTNET_ASPNET_IMAGE "${DOTNET_ASPNET_IMAGE_VALUE}"
fi
chown root:root "${INSTALL_ENV_TEMPORARY}"
chmod 0600 "${INSTALL_ENV_TEMPORARY}"
mv -f -- "${INSTALL_ENV_TEMPORARY}" "${DEFAULT_CONFIG_DIR}/deployment.env"

LP_HOME="${DEFAULT_INSTALL_DIR}"
LP_CONFIG_DIR="${DEFAULT_CONFIG_DIR}"
LP_DATA_DIR="${DEFAULT_DATA_DIR}"
LP_BACKUP_DIR="${DEFAULT_BACKUP_DIR}"
LP_ENV_FILE="${DEFAULT_CONFIG_DIR}/deployment.env"
LP_COMPOSE_FILE="${DEFAULT_INSTALL_DIR}/compose.yml"
# Export the persisted selection, rather than allowing inherited shell values
# to silently override deployment.env through Compose's environment precedence.
load_deployment_env

compose config --quiet
log "拉取固定版本基础服务镜像。"
compose pull db caddy

if (( BUILD_LOCAL == 1 )); then
  log "从全新 staging 源码构建应用镜像 ${APP_IMAGE}，不会保留旧版本已删除文件。"
  SOURCE_DIR="${INSTALL_SOURCE_STAGE}" compose build --pull app
  promote_source_stage
else
  log "拉取应用镜像 ${APP_IMAGE}。"
  compose pull app
fi
prepare_app_data_permissions

log "启动 PostgreSQL（不会停止或修改其他 Compose 项目/容器）。"
compose up --detach db
wait_for_service db 180
run_migration

log "启动应用与 Caddy。"
compose up --detach app caddy
wait_for_service app 180
wait_for_service caddy 120
"${DEFAULT_INSTALL_DIR}/bin/health-check.sh" --local-install

install -o root -g root -m 0644 "${SCRIPT_DIR}/systemd/license-platform-backup.service" "${DEFAULT_SYSTEMD_DIR}/license-platform-backup.service"
install -o root -g root -m 0644 "${SCRIPT_DIR}/systemd/license-platform-backup.timer" "${DEFAULT_SYSTEMD_DIR}/license-platform-backup.timer"
systemctl daemon-reload
systemctl enable --now license-platform-backup.timer

if (( INSTALL_TRANSACTION_ACTIVE == 1 )); then
  write_env_value "${INSTALL_TRANSACTION_DIR}/state.env" STATUS SUCCEEDED
  sync -f "${DEFAULT_INSTALL_DIR}"
  sync -f "${DEFAULT_CONFIG_DIR}"
  sync -f "${DEFAULT_SYSTEMD_DIR}"
  sync -f "${INSTALL_TRANSACTION_DIR}/state.env"
  rm -f -- "${INSTALL_PENDING_FILE}"
fi
# Retain the old source and transaction snapshots. Deleting a required backup
# before committing the marker would make crash recovery impossible.
INSTALL_COMPLETED=1
sync -f "${DEFAULT_DATA_DIR}"
trap - EXIT INT TERM
log "安装完成。每日备份 timer 已创建并启用。"
log "初始后台密码仅保存在 ${DEFAULT_CONFIG_DIR}/secrets/admin_initial_password（父目录 root-only，文件 root:${SECRETS_GID_VALUE} 0440，仅供容器补充组只读挂载）。首次登录后请立即轮换。"
if [[ "${HTTPS_ENABLED_VALUE}" == "false" ]]; then
  warn "当前仍是公网明文 HTTP，不安全。访问地址: http://${PUBLIC_IPV4}"
else
  log "保留现有 HTTPS 配置；请核验证书和入口: ${CADDY_SITE_ADDRESS_VALUE}"
fi
