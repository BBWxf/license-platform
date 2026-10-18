#!/usr/bin/env bash
# Sourced only by networking.test.mjs after a disposable copy of common.sh.
# ip/ss/curl return synthetic fixture data; their external-command fallbacks and
# every deployment/system entry point are blocked, never forwarded to the host.
export PATH="${FIXTURE_ROOT}/blocked:${PATH}"

fixture_record() {
  local name="$1"
  shift
  printf '%s\0' "$@" >>"${FIXTURE_ROOT}/${name}.args"
  printf '\0' >>"${FIXTURE_ROOT}/${name}.args"
}

fixture_blocked() {
  printf '%s\n' "$*" >>"${FIXTURE_ROOT}/unexpected-commands"
  printf 'Host operation blocked by networking fixture: %s\n' "$*" >&2
  return 97
}

for fixture_command in compose systemctl sudo install flock iptables nft ufw apt-get yum dnf service reboot shutdown; do
  eval "${fixture_command}() { fixture_blocked ${fixture_command} \"\$@\"; }"
done
unset fixture_command
# Only the restore-legacy fixture may exercise common.sh's atomic env writer.
# Its ownership/mode operations are modeled, not executed, and only for a new
# regular temporary file immediately inside this fixture's config directory.
fixture_env_temporary() {
  [[ "${FIXTURE_ALLOW_ENV_WRITE:-0}" == 1 && "$1" == "${FIXTURE_ROOT}/config/deployment.env."* \
    && "${1%/*}" == "${FIXTURE_ROOT}/config" && -f "$1" && ! -L "$1" ]]
}
chmod() {
  if [[ "$#" == 2 && "$1" == 0600 ]] && fixture_env_temporary "$2"; then return 0; fi
  fixture_blocked chmod "$@"
}
chown() {
  if [[ "$#" == 2 && "$1" == root:root ]] && fixture_env_temporary "$2"; then return 0; fi
  fixture_blocked chown "$@"
}
require_root() { fixture_blocked require_root "$@"; }
verify_root_0600_file() {
  [[ "$1" == "${FIXTURE_ROOT}/config/deployment.env" && -f "$1" && ! -L "$1" ]] \
    || fatal 'unsafe networking fixture environment path'
}

ip() {
  fixture_record ip "$@"
  if [[ "$#" == 4 && "$1" == -o && "$2" == -4 && "$3" == address && "$4" == show ]]; then
    command cat -- "${FIXTURE_ROOT}/addresses"
    return "${IP_ADDRESS_EXIT:-0}"
  elif [[ "$#" == 6 && "$1" == -o && "$2" == -4 && "$3" == address && "$4" == show && "$5" == dev ]]; then
    [[ "$6" =~ ^[A-Za-z0-9_.:-]+$ ]] || return 95
    if [[ -f "${FIXTURE_ROOT}/addresses.$6" ]]; then command cat -- "${FIXTURE_ROOT}/addresses.$6"; fi
    return "${IP_ADDRESS_EXIT:-0}"
  elif [[ "$#" == 5 && "$1" == -o && "$2" == -4 && "$3" == route && "$4" == show && "$5" == default ]]; then
    command cat -- "${FIXTURE_ROOT}/default-route"
    return "${IP_DEFAULT_EXIT:-0}"
  elif [[ "$#" == 5 && "$1" == -o && "$2" == -4 && "$3" == route && "$4" == get && "$5" == "${FIXTURE_PUBLIC_IPV4}" ]]; then
    command cat -- "${FIXTURE_ROOT}/route-get"
    return "${IP_ROUTE_GET_EXIT:-0}"
  fi
  fixture_blocked ip "$@"
}

# Read-only Docker API model for the shared port preflight. This is a shell
# function, not an executable; command docker / any mutating verb is blocked.
docker() {
  fixture_record docker "$@"
  if [[ "$#" == 5 && "$1" == ps && "$2" == --filter && "$3" =~ ^publish=[0-9]+$ && "$4" == --format && "$5" == '{{.ID}}' ]]; then
    command cat -- "${FIXTURE_ROOT}/containers"
    return "${DOCKER_PS_EXIT:-0}"
  elif [[ "$#" == 4 && "$1" == inspect && "$2" == --format && "$4" == fixture-container ]]; then
    case "$3" in
      '{{index .Config.Labels "com.docker.compose.project"}}') command cat -- "${FIXTURE_ROOT}/container-project" ;;
      '{{index .Config.Labels "com.docker.compose.service"}}') command cat -- "${FIXTURE_ROOT}/container-service" ;;
      *) fixture_blocked docker "$@"; return 97 ;;
    esac
    return "${DOCKER_INSPECT_EXIT:-0}"
  elif [[ "$#" == 2 && "$1" == port && "$2" == fixture-container ]]; then
    command cat -- "${FIXTURE_ROOT}/container-ports"
    return "${DOCKER_PORT_EXIT:-0}"
  fi
  fixture_blocked docker "$@"
}

ss() {
  fixture_record ss "$@"
  command cat -- "${FIXTURE_ROOT}/listeners"
  return "${SS_EXIT:-0}"
}

curl() {
  fixture_record curl "$@"
  local output='' headers='' argument
  while (($#)); do
    argument="$1"
    shift
    case "${argument}" in
      --output|-o)
        (($#)) || return 95
        output="$1"
        shift
        ;;
      --dump-header|-D)
        (($#)) || return 95
        headers="$1"
        shift
        ;;
    esac
  done
  [[ "${output}" == "${FIXTURE_ROOT}/tmp/"* && -f "${output}" && ! -L "${output}" ]] \
    || { fixture_blocked curl-output "${output}"; return 97; }
  if [[ -n "${headers}" ]]; then
    [[ "${headers}" == "${FIXTURE_ROOT}/tmp/"* && -f "${headers}" && ! -L "${headers}" ]] \
      || { fixture_blocked curl-header "${headers}"; return 97; }
    command cat -- "${FIXTURE_ROOT}/curl-headers" >"${headers}"
  fi
  command cat -- "${FIXTURE_ROOT}/curl-body" >"${output}"
  printf '%s' "${CURL_STATUS:-200}"
  return "${CURL_EXIT:-0}"
}
