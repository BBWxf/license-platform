#!/usr/bin/env bash
# Shared helpers for License Platform deployment operations.

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

readonly LP_PROJECT_NAME="license-platform"
LP_HOME="${LICENSE_PLATFORM_HOME:-/opt/license-platform}"
LP_CONFIG_DIR="${LICENSE_PLATFORM_CONFIG_DIR:-/etc/license-platform}"
LP_DATA_DIR="${LICENSE_PLATFORM_DATA_DIR:-/var/lib/license-platform}"
LP_BACKUP_DIR="${LICENSE_PLATFORM_BACKUP_DIR:-/var/backups/license-platform}"
LP_ENV_FILE="${LP_CONFIG_DIR}/deployment.env"
LP_COMPOSE_FILE="${LP_HOME}/compose.yml"

log() {
  printf '%s [license-platform] %s\n' "$(date --iso-8601=seconds)" "$*"
}

warn() {
  printf '%s [license-platform] WARNING: %s\n' "$(date --iso-8601=seconds)" "$*" >&2
}

fatal() {
  printf '%s [license-platform] ERROR: %s\n' "$(date --iso-8601=seconds)" "$*" >&2
  exit 1
}

require_root() {
  [[ ${EUID} -eq 0 ]] || fatal "此操作必须以 root 身份运行。"
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fatal "缺少必需命令: $1"
}

verify_root_0600_file() {
  local path="$1"
  [[ -f "${path}" && ! -L "${path}" ]] || fatal "文件缺失、不是普通文件或是符号链接: ${path}"
  [[ "$(stat -c '%u:%g:%a' "${path}")" == "0:0:600" ]] || fatal "文件必须为 root:root 0600: ${path}"
}

verify_runtime_secret_file() {
  local path="$1"
  local secrets_gid="${SECRETS_GID:-20001}"
  [[ -f "${path}" && ! -L "${path}" ]] || fatal "秘密文件缺失、不是普通文件或是符号链接: ${path}"
  [[ "$(stat -c '%u:%g:%a' "${path}")" == "0:${secrets_gid}:440" ]] || \
    fatal "秘密文件必须为 root:${secrets_gid} 0440: ${path}"
}

install_runtime_secret() {
  local source="$1"
  local target="$2"
  local secrets_gid="${SECRETS_GID:-20001}"
  install -o root -g "${secrets_gid}" -m 0440 "${source}" "${target}"
}

is_usable_bind_ipv4() {
  local value="$1" a b c d octet
  [[ "${value}" != *[[:space:]]* ]] || return 1
  [[ "${value}" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]] || return 1
  IFS='.' read -r a b c d <<<"${value}"
  for octet in "${a}" "${b}" "${c}" "${d}"; do
    [[ "${octet}" == 0 || "${octet}" =~ ^[1-9][0-9]{0,2}$ ]] || return 1
    ((10#${octet} <= 255)) || return 1
  done
  ((10#${a} >= 1 && 10#${a} <= 223 && 10#${a} != 127)) || return 1
  ! ((10#${a} == 169 && 10#${b} == 254))
}

is_local_ipv4() {
  local address="$1" interface="${2:-}" addresses
  local -a arguments=(-o -4 address show)
  is_usable_bind_ipv4 "${address}" || return 1
  [[ -z "${interface}" ]] || arguments+=(dev "${interface}")
  addresses="$(ip "${arguments[@]}")" || return 1
  [[ " ${addresses}" == *" inet ${address}/"* ]]
}

require_local_bind_ipv4() {
  is_usable_bind_ipv4 "$1" || fatal "本机监听地址必须是有效的具体单播 IPv4，不能使用通配、回环或链路本地地址。"
  require_command ip
  is_local_ipv4 "$1" || fatal "本机监听 IPv4 $1 未配置在本机接口上；请用 --bind-ip 指定实际本机地址。"
}

select_bind_ipv4() {
  local public="$1" explicit="${2:-}" saved="${3:-}" selected routes route lookup word
  local default_device="" device="" source="" default_device_count=0 device_count=0 source_count=0 index
  local -a lines=() words=()
  require_command ip
  if [[ -n "${explicit}" || -n "${saved}" ]]; then
    selected="${explicit:-${saved}}"
    require_local_bind_ipv4 "${selected}"
    printf '%s\n' "${selected}"
    return 0
  fi
  if is_local_ipv4 "${public}"; then
    printf '%s\n' "${public}"
    return 0
  fi

  routes="$(ip -o -4 route show default)" || fatal "无法读取默认路由；请用 --bind-ip 指定本机监听 IPv4。"
  while IFS= read -r route; do
    [[ -z "${route}" ]] || lines+=("${route}")
  done <<<"${routes}"
  ((${#lines[@]} == 1)) || fatal "没有唯一默认路由；请用 --bind-ip 指定本机监听 IPv4。"
  IFS=' ' read -r -a words <<<"${lines[0]}"
  for ((index = 0; index < ${#words[@]}; index++)); do
    word="${words[index]}"
    [[ "${word}" != nexthop ]] || fatal "检测到多路径默认路由；请用 --bind-ip 指定本机监听 IPv4。"
    if [[ "${word}" == dev ]]; then
      default_device="${words[index+1]:-}"
      ((default_device_count += 1))
    fi
  done
  ((default_device_count == 1)) && [[ -n "${default_device}" ]] || fatal "默认路由出口不明确；请用 --bind-ip 指定本机监听 IPv4。"

  lookup="$(ip -o -4 route get "${public}")" || fatal "无法确定公网地址的本机路由源；请用 --bind-ip 指定本机监听 IPv4。"
  lines=()
  while IFS= read -r route; do
    [[ -z "${route}" ]] || lines+=("${route}")
  done <<<"${lookup}"
  ((${#lines[@]} == 1)) || fatal "本机路由源存在歧义；请用 --bind-ip 指定本机监听 IPv4。"
  IFS=' ' read -r -a words <<<"${lines[0]}"
  for ((index = 0; index < ${#words[@]}; index++)); do
    case "${words[index]}" in
      dev) device="${words[index+1]:-}"; ((device_count += 1)) ;;
      src) source="${words[index+1]:-}"; ((source_count += 1)) ;;
      nexthop) fatal "本机路由源存在多路径；请用 --bind-ip 指定本机监听 IPv4。" ;;
    esac
  done
  ((device_count == 1 && source_count == 1)) && [[ "${device}" == "${default_device}" ]] || \
    fatal "无法唯一确定默认出口的本机 IPv4；请用 --bind-ip 指定。"
  is_local_ipv4 "${source}" "${device}" || fatal "默认路由源不是可用的本机单播 IPv4；请用 --bind-ip 指定。"
  warn "检测到 NAT/EIP：公网访问地址 ${public}，本机监听地址 ${source}。请另行核对云平台映射与安全组；本机健康不证明公网可达。"
  printf '%s\n' "${source}"
}

check_bind_port_available() {
  local address="$1" port="$2" protocol="$3" container_ids container_id project service bindings binding host_binding
  local listeners _state _recv _send local_address _peer own_binding=0
  require_local_bind_ipv4 "${address}"
  [[ "${port}" =~ ^[1-9][0-9]{0,4}$ ]] && ((port <= 65535)) || fatal "端口检查参数无效。"
  [[ "${protocol}" == tcp || "${protocol}" == udp ]] || fatal "端口检查协议无效。"
  require_command docker
  require_command ss
  container_ids="$(docker ps --filter "publish=${port}" --format '{{.ID}}')" || fatal "无法检查容器端口占用。"
  while IFS= read -r container_id; do
    [[ -n "${container_id}" ]] || continue
    project="$(docker inspect --format '{{index .Config.Labels "com.docker.compose.project"}}' "${container_id}" 2>/dev/null || true)"
    service="$(docker inspect --format '{{index .Config.Labels "com.docker.compose.service"}}' "${container_id}" 2>/dev/null || true)"
    bindings="$(docker port "${container_id}")" || fatal "无法检查容器 ${container_id} 的端口映射。"
    while IFS= read -r binding; do
      [[ "${binding%% -> *}" == *"/${protocol}" ]] || continue
      host_binding="${binding##* -> }"
      case "${host_binding}" in
        "${address}:${port}"|"0.0.0.0:${port}"|"[::]:${port}")
          if [[ "${project}" == "${LP_PROJECT_NAME}" && "${service}" == caddy ]]; then
            own_binding=1
          else
            fatal "目标地址的 ${protocol^^} ${port} 已由容器 ${container_id} 使用；不会停止或修改该容器。"
          fi
          ;;
      esac
    done <<<"${bindings}"
  done <<<"${container_ids}"

  if ((own_binding == 0)); then
    if [[ "${protocol}" == tcp ]]; then
      listeners="$(ss -H -ltn "sport = :${port}")" || fatal "无法检查主机 TCP 端口。"
    else
      listeners="$(ss -H -lun "sport = :${port}")" || fatal "无法检查主机 UDP 端口。"
    fi
    while IFS=' ' read -r _state _recv _send local_address _peer; do
      case "${local_address}" in
        "${address}:${port}"|"0.0.0.0:${port}"|"*:${port}"|"[::]:${port}")
          fatal "目标地址的主机 ${protocol^^} ${port} 已被占用；不会停止或修改现有进程。"
          ;;
      esac
    done <<<"${listeners}"
  fi
}

load_deployment_env() {
  verify_root_0600_file "${LP_ENV_FILE}"
  # The file is generated by install.sh, owned by root, and contains shell-safe values.
  # A legacy snapshot must not inherit a newer binding from this process.
  unset BIND_IPV4 PUBLIC_IPV4
  set -a
  # shellcheck disable=SC1090
  source "${LP_ENV_FILE}"
  set +a
  is_usable_bind_ipv4 "${PUBLIC_IPV4:-}" || fatal "deployment.env 中 PUBLIC_IPV4 无效或缺失。"
  BIND_IPV4="${BIND_IPV4:-${PUBLIC_IPV4:-}}"
  is_usable_bind_ipv4 "${BIND_IPV4}" || fatal "deployment.env 中 BIND_IPV4 或旧版 PUBLIC_IPV4 不是可用的本机监听地址。"
  export BIND_IPV4

  LP_HOME="${INSTALL_DIR:-${LP_HOME}}"
  LP_CONFIG_DIR="${CONFIG_DIR:-${LP_CONFIG_DIR}}"
  LP_DATA_DIR="${DATA_DIR:-${LP_DATA_DIR}}"
  LP_BACKUP_DIR="${BACKUP_DIR:-${LP_BACKUP_DIR}}"
  LP_ENV_FILE="${LP_CONFIG_DIR}/deployment.env"
  LP_COMPOSE_FILE="${LP_HOME}/compose.yml"

  [[ "${HTTPS_ENABLED:-false}" == "true" || "${HTTPS_ENABLED:-false}" == "false" ]] || fatal "HTTPS_ENABLED 必须是 true 或 false。"
  [[ "${ALLOW_INSECURE_HTTP:-true}" == "true" || "${ALLOW_INSECURE_HTTP:-true}" == "false" ]] || fatal "ALLOW_INSECURE_HTTP 必须是 true 或 false。"
  [[ "${HSTS_ENABLED:-false}" == "true" || "${HSTS_ENABLED:-false}" == "false" ]] || fatal "HSTS_ENABLED 必须是 true 或 false。"
  [[ "${SECRETS_GID:-20001}" =~ ^[0-9]+$ ]] \
    && ((10#${SECRETS_GID:-20001} >= 1 && 10#${SECRETS_GID:-20001} <= 2147483647)) \
    || fatal "SECRETS_GID 必须是 1 到 2147483647 的整数。"
  if [[ "${HTTPS_ENABLED:-false}" == "true" && "${ALLOW_INSECURE_HTTP:-true}" != "false" ]]; then
    fatal "HTTPS_ENABLED=true 时必须设置 ALLOW_INSECURE_HTTP=false。"
  fi
  if [[ "${HSTS_ENABLED:-false}" == "true" && "${HTTPS_ENABLED:-false}" != "true" ]]; then
    fatal "HSTS_ENABLED=true 时必须设置 HTTPS_ENABLED=true。"
  fi
}

compose() {
  local -a compose_files=(--file "${LP_COMPOSE_FILE}")
  local docker_config="${DOCKER_CONFIG:-/root/.docker}"
  if [[ "${docker_config}" == /root/.docker && ! -d "${docker_config}" ]]; then
    docker_config="${LP_DATA_DIR}/docker-config"
    install -d -o root -g root -m 0700 "${docker_config}"
  fi
  if [[ "${HTTPS_ENABLED:-false}" == "true" ]]; then
    [[ -f "${LP_HOME}/compose.https.yml" ]] || fatal "HTTPS_ENABLED=true，但缺少 ${LP_HOME}/compose.https.yml。"
    compose_files+=(--file "${LP_HOME}/compose.https.yml")
  fi
  DOCKER_CONFIG="${docker_config}" docker compose \
    --project-name "${LP_PROJECT_NAME}" \
    --env-file "${LP_ENV_FILE}" \
    "${compose_files[@]}" \
    "$@"
}

service_container_id() {
  compose ps --quiet "$1" 2>/dev/null | tr -d '\r' | tail -n 1
}

service_state() {
  local service="$1"
  local container_id
  container_id="$(service_container_id "${service}")"
  if [[ -z "${container_id}" ]]; then
    printf 'missing\n'
    return 0
  fi
  docker inspect --format '{{.State.Status}}' "${container_id}"
}

service_health() {
  local service="$1"
  local container_id
  container_id="$(service_container_id "${service}")"
  if [[ -z "${container_id}" ]]; then
    printf 'missing\n'
    return 0
  fi
  docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "${container_id}"
}

wait_for_service() {
  local service="$1"
  local timeout_seconds="${2:-120}"
  local deadline=$((SECONDS + timeout_seconds))
  local status

  while (( SECONDS < deadline )); do
    status="$(service_health "${service}")"
    case "${status}" in
      healthy|running)
        log "服务 ${service} 已就绪（${status}）。"
        return 0
        ;;
      unhealthy|exited|dead)
        compose logs --no-color --tail 100 "${service}" >&2 || true
        fatal "服务 ${service} 状态异常: ${status}"
        ;;
    esac
    sleep 2
  done

  compose logs --no-color --tail 100 "${service}" >&2 || true
  fatal "等待服务 ${service} 就绪超时（${timeout_seconds}s）。"
}

probe_public_health() {
  local scheme="$1"
  local host="$2"
  local address="$3"
  local timeout_seconds="${4:-15}"
  local port body_file status body

  [[ "${scheme}" == "http" || "${scheme}" == "https" ]] || return 2
  [[ -n "${host}" && "${host}" != */* && "${host}" != *:* ]] || return 2
  [[ -n "${address}" && "${address}" != *:* ]] || return 2
  [[ "${timeout_seconds}" =~ ^[0-9]+$ ]] && ((timeout_seconds >= 1)) || return 2

  port=80
  [[ "${scheme}" == "https" ]] && port=443
  body_file="$(mktemp "${TMPDIR:-/tmp}/license-platform-health.XXXXXX")"
  if ! status="$(curl --disable --noproxy '*' --silent --show-error \
      --connect-timeout "${timeout_seconds}" --max-time "${timeout_seconds}" \
      --request GET --output "${body_file}" --write-out '%{http_code}' \
      --proto '=http,https' --connect-to "${host}:${port}:${address}:${port}" \
      "${scheme}://${host}/health")"; then
    rm -f -- "${body_file}"
    return 1
  fi

  body="$(<"${body_file}")"
  rm -f -- "${body_file}"
  [[ "${status}" == "200" && "${body}" == '{"status":"healthy"}' ]]
}

validate_pinned_image() {
  local image="$1"
  local label="${2:-镜像}"
  [[ -n "${image}" ]] || fatal "${label}不能为空。"
  [[ "${image}" =~ ^[A-Za-z0-9._/:@-]+$ ]] || fatal "${label}包含空白或非法字符: ${image}"
  [[ "${image}" != *":latest" ]] || fatal "${label}禁止使用 latest: ${image}"

  if [[ "${image}" == *@sha256:* ]]; then
    [[ "${image}" =~ ^[A-Za-z0-9._/:-]+@sha256:[0-9a-fA-F]{64}$ ]] || fatal "${label}的 sha256 digest 引用无效: ${image}"
    return 0
  fi

  local final_component="${image##*/}"
  [[ "${final_component}" == *:* ]] || fatal "${label}必须包含明确版本标签或 sha256 digest: ${image}"
  local tag="${final_component##*:}"
  [[ "${tag}" =~ ^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$ ]] || fatal "${label}的版本标签无效: ${image}"
  [[ "${tag}" != "latest" ]] || fatal "${label}禁止使用 latest: ${image}"
}

prepare_app_data_permissions() {
  log "校正应用数据卷权限，长期应用进程仍以 UID/GID 1654 非 root 运行。"
  compose run --rm --no-deps \
    --user 0:0 \
    --cap-add CHOWN \
    --cap-add DAC_OVERRIDE \
    --cap-add FOWNER \
    --entrypoint /bin/sh \
    app -ec 'chown -R 1654:1654 /app/data && chmod 0700 /app/data'
}

run_migration() {
  local timeout_seconds="${MIGRATION_TIMEOUT_SECONDS:-600}"
  [[ "${timeout_seconds}" =~ ^[0-9]+$ ]] && ((timeout_seconds >= 30)) || fatal "MIGRATION_TIMEOUT_SECONDS 必须是不小于 30 的整数。"
  require_command timeout
  [[ -r "${LP_HOME}/lib/common.sh" ]] || fatal "缺少已安装的 common.sh，无法运行迁移。"

  log "运行数据库迁移（超时 ${timeout_seconds}s）。"
  if timeout --foreground --signal=TERM --kill-after=30s "${timeout_seconds}s" \
    bash -c 'source "$1"; load_deployment_env; compose run --rm --no-deps app --migrate' \
    _ "${LP_HOME}/lib/common.sh"; then
    return 0
  else
    local rc=$?
  fi
  if (( rc == 124 || rc == 137 )); then
    fatal "数据库迁移超时；不会继续启动应用。请检查迁移实现和数据库日志。"
  fi
  fatal "数据库迁移失败（退出码 ${rc}）；不会继续启动应用。"
}

latest_backup_archive() {
  local newest=""
  local candidate
  shopt -s nullglob
  for candidate in "${LP_BACKUP_DIR}"/license-platform-backup-*.tar.gz; do
    if [[ -z "${newest}" || "${candidate}" -nt "${newest}" ]]; then
      newest="${candidate}"
    fi
  done
  shopt -u nullglob
  printf '%s\n' "${newest}"
}

write_env_values() {
  local file="$1"
  shift
  (($# >= 2 && $# % 2 == 0)) || fatal "write_env_values 需要一个或多个键值对。"

  local key value line line_key temporary
  local -a keys=()
  declare -A replacements=()
  while (($# > 0)); do
    key="$1"
    value="$2"
    shift 2
    [[ "${key}" =~ ^[A-Z][A-Z0-9_]*$ ]] || fatal "deployment.env 键无效: ${key}"
    [[ "${value}" != *$'\n'* && "${value}" != *$'\r'* ]] || fatal "deployment.env 值不能包含换行。"
    [[ ! ${replacements["${key}"]+_} ]] || fatal "重复的 deployment.env 更新键: ${key}"
    keys+=("${key}")
    replacements["${key}"]="${value}"
  done

  temporary="$(mktemp "${file}.XXXXXX")" || fatal "无法创建 deployment.env 临时文件。"
  chmod 0600 "${temporary}" || {
    rm -f -- "${temporary}"
    fatal "无法设置 deployment.env 临时文件权限。"
  }
  if [[ -f "${file}" ]]; then
    while IFS= read -r line || [[ -n "${line}" ]]; do
      if [[ "${line}" == *=* ]]; then
        line_key="${line%%=*}"
        if [[ -n "${line_key}" && ${replacements["${line_key}"]+_} ]]; then
          continue
        fi
      fi
      printf '%s\n' "${line}" >>"${temporary}" || {
        rm -f -- "${temporary}"
        fatal "无法写入 deployment.env 临时文件。"
      }
    done <"${file}"
  fi

  for key in "${keys[@]}"; do
    printf '%s=%q\n' "${key}" "${replacements[${key}]}" >>"${temporary}" || {
      rm -f -- "${temporary}"
      fatal "无法写入 deployment.env 更新值。"
    }
  done
  chown root:root "${temporary}" || {
    rm -f -- "${temporary}"
    fatal "无法设置 deployment.env 临时文件所有者。"
  }
  mv -f -- "${temporary}" "${file}" || {
    rm -f -- "${temporary}"
    fatal "无法原子替换 deployment.env。"
  }
}

write_env_value() {
  write_env_values "$@"
}

# Call only after acquiring operation.lock. Only the matching recovery command
# may exempt its own marker; a symlink (including a dangling one) also blocks.
reject_pending_transactions() {
  local exempt="${1:-}" pending
  case "${exempt}" in
    ""|upgrade.pending|manual-rollback.pending|install.pending|https-switch.pending|hsts-enable.pending) ;;
    *) fatal "未知的事务恢复豁免: ${exempt}" ;;
  esac
  for pending in upgrade.pending manual-rollback.pending install.pending https-switch.pending hsts-enable.pending; do
    [[ "${pending}" != "${exempt}" ]] || continue
    [[ ! -e "${LP_DATA_DIR}/${pending}" && ! -L "${LP_DATA_DIR}/${pending}" ]] || \
      fatal "检测到未解决的事务 ${pending}；请先运行对应的 upgrade、rollback、install、enable-https 或 enable-hsts 命令恢复，不能交叉修改部署。"
  done
}

confirm_exact() {
  local supplied="$1"
  local expected="$2"
  local message="$3"
  [[ "${supplied}" == "${expected}" ]] || fatal "${message}（需要参数值: ${expected}）"
}
