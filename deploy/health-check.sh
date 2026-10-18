#!/usr/bin/env bash
# Verify container state and the internal/public health endpoints.

set -Eeuo pipefail
IFS=$'\n\t'
umask 077

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
COMMON_PATH="${SCRIPT_DIR}/../lib/common.sh"
[[ -r "${COMMON_PATH}" ]] || COMMON_PATH="${SCRIPT_DIR}/lib/common.sh"
# shellcheck source=deploy/lib/common.sh
source "${COMMON_PATH}"

case "${1:-}" in
  ""|--local-install) ;;
  -h|--help)
    printf '用法: sudo licensectl health\n'
    exit 0
    ;;
  *) fatal "未知参数: $1" ;;
esac

require_root
require_command docker
load_deployment_env
require_local_bind_ipv4 "${BIND_IPV4}"

failed=0
for service in db app; do
  status="$(service_health "${service}")"
  if [[ "${status}" != "healthy" ]]; then
    warn "${service} 健康状态不是 healthy: ${status}"
    failed=1
  else
    log "${service}: healthy"
  fi
done

caddy_status="$(service_state caddy)"
if [[ "${caddy_status}" != "running" ]]; then
  warn "caddy 运行状态异常: ${caddy_status}"
  failed=1
else
  log "caddy: running"
fi

if compose exec --no-TTY caddy sh -c 'command -v wget >/dev/null 2>&1'; then
  if ! compose exec --no-TTY caddy wget -q -T 10 -O /dev/null http://app:8080/health; then
    warn "Caddy 容器无法访问 app:8080/health。"
    failed=1
  else
    log "内部应用健康端点可访问。"
  fi

  if [[ "${HTTPS_ENABLED:-false}" == "false" ]]; then
    if ! compose exec --no-TTY caddy wget -q -T 10 -O /dev/null \
      --header="Host: ${PUBLIC_IPV4}" http://127.0.0.1/health; then
      warn "Caddy 的 HTTP 反向代理健康请求失败。"
      failed=1
    else
      log "Caddy HTTP 反向代理路径可访问。"
    fi
  fi
else
  warn "Caddy 镜像不含 wget，跳过容器内 HTTP 探测；将依赖主机 curl 和容器健康状态。"
fi

if [[ "${HTTPS_ENABLED:-false}" == "true" ]]; then
  PUBLIC_HEALTH_SCHEME="https"
  PUBLIC_HEALTH_HOST="${CADDY_SITE_ADDRESS#https://}"
  PUBLIC_HEALTH_HOST="${PUBLIC_HEALTH_HOST%/}"
  PUBLIC_HEALTH_URL="https://${PUBLIC_HEALTH_HOST}/health"
else
  PUBLIC_HEALTH_SCHEME="http"
  PUBLIC_HEALTH_HOST="${PUBLIC_IPV4}"
  PUBLIC_HEALTH_URL="http://${PUBLIC_HEALTH_HOST}/health"
fi

if command -v curl >/dev/null 2>&1; then
  if ! probe_public_health \
      "${PUBLIC_HEALTH_SCHEME}" "${PUBLIC_HEALTH_HOST}" "${BIND_IPV4}" 10; then
    warn "本机入口必须直接返回 200 和精确健康正文，检查失败: ${PUBLIC_HEALTH_URL}（连接 ${BIND_IPV4}）"
    failed=1
  else
    log "本机入口严格健康检查通过: ${PUBLIC_HEALTH_URL}（连接 ${BIND_IPV4}）；公网映射与安全组需另行核验。"
  fi
else
  warn "主机未安装 curl，无法执行禁用代理和本地配置的严格入口检查。"
  failed=1
fi

compose ps
if [[ "${HTTPS_ENABLED:-false}" == "false" ]]; then
  warn "当前入口是公网明文 HTTP；健康检查通过也不代表传输安全。"
fi
(( failed == 0 )) || fatal "健康检查失败。"
log "健康检查通过。"
