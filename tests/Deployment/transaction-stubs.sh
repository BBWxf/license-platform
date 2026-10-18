#!/usr/bin/env bash
# Sourced only in disposable transaction fixtures; never invokes host services.
# External fallbacks are fail-closed sentinels as well as function overrides.
export PATH="${FIXTURE_ROOT}/blocked:${PATH}"
require_root() { :; }
require_command() { :; }
verify_root_0600_file() { [[ -f "$1" && ! -L "$1" ]] || fatal "unsafe fixture state: $1"; }
chown() { :; }
chmod() { :; }
flock() { :; }
sync() { :; }
install() {
  local -a args=()
  local directory=false
  while (($#)); do
    case "$1" in
      -o|-g|-m) shift 2 ;;
      -d) directory=true; shift ;;
      *) args+=("$1"); shift ;;
    esac
  done
  if [[ "${directory}" == true ]]; then command mkdir -p -- "${args[@]}";
  else
    command cp -- "${args[@]}" || return
    if [[ "${CRASH_POINT:-}" == managed-files && "${args[1]}" == *home/lib/common.sh ]]; then kill -KILL "$$"; fi
  fi
}
cp() {
  local -a args=()
  local arg
  for arg in "$@"; do
    if [[ "$arg" == -a ]]; then args+=(-R); else args+=("$arg"); fi
  done
  command cp "${args[@]}"
}
# Windows Git Bash may implement ln -s as a file copy. Model that one managed
# link explicitly there; POSIX fixtures continue to exercise real symlinks.
ln() {
  if [[ "${FIXTURE_FAKE_LINKS:-0}" == 1 ]]; then
    [[ "$1" == -s && "$3" == "${FIXTURE_ROOT}/sbin/licensectl" ]] || return 95
    printf '%s\n' "$2" >"$3"
    : >"$3.fixture-link"
  else command ln "$@"; fi
}
readlink() {
  if [[ "${FIXTURE_FAKE_LINKS:-0}" == 1 && -f "$2.fixture-link" ]]; then
    command cat "$2"
  else command readlink "$@"; fi
}
date() {
  if [[ "${1:-}" == '--utc' ]]; then printf '%s\n' "${FIXTURE_TIMESTAMP:-20261018T120000.123456789Z}";
  else printf '%s\n' '2026-10-18T12:00:00Z'; fi
}
fixture_log() { printf '%s\n' "$*" >>"${FIXTURE_ROOT}/actions"; }
docker() {
  fixture_log "image-stub $*"
  if [[ "$1" == inspect ]]; then printf '%s\n' 'sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; fi
}
service_container_id() { printf 'fixture-%s\n' "$1"; }
latest_backup_archive() { printf '%s/backup.tar.gz\n' "${FIXTURE_ROOT}"; }
wait_for_service() { fixture_log "wait $*"; }
prepare_app_data_permissions() { fixture_log permissions; }
run_migration() { fixture_log migration; [[ "${FAIL_MIGRATION:-0}" != 1 ]]; }
compose() {
  fixture_log "compose $*"
  if [[ "${CRASH_POINT:-}" == stop && "$1" == stop ]]; then kill -KILL "$$"; fi
  if [[ "${FAIL_RECOVERY:-0}" == 1 && "${APP_IMAGE:-}" == *rollback:* ]]; then return 31; fi
  if [[ "${FAIL_SWITCH:-0}" == 1 && "$1" == up && "${APP_IMAGE:-}" == license-platform-rollback:* ]]; then return 32; fi
  return 0
}
systemctl() {
  fixture_log "timer-stub $*"
  case "$1" in
    is-enabled) command cat "${FIXTURE_ROOT}/timer.enabled" ;;
    is-active) command cat "${FIXTURE_ROOT}/timer.active" ;;
    show)
      if [[ -f "${FIXTURE_ROOT}/systemd/license-platform-backup.timer" ]]; then printf 'loaded\n'; else printf 'not-found\n'; fi ;;
    stop) printf 'inactive\n' >"${FIXTURE_ROOT}/timer.active" ;;
    disable) printf 'disabled\n' >"${FIXTURE_ROOT}/timer.enabled" ;;
    start) printf 'active\n' >"${FIXTURE_ROOT}/timer.active" ;;
    enable)
      if [[ "${FAIL_TIMER_ENABLE:-0}" == 1 && "${2:-}" == --now ]]; then return 33; fi
      printf 'enabled\n' >"${FIXTURE_ROOT}/timer.enabled"
      [[ "${2:-}" != --now ]] || printf 'active\n' >"${FIXTURE_ROOT}/timer.active"
      return 0 ;;
    daemon-reload) : ;;
    *) return 94 ;;
  esac
}
mv() {
  local -a operands=()
  local arg source target
  for arg in "$@"; do [[ "$arg" == -* ]] || operands+=("$arg"); done
  source="${operands[0]}"; target="${operands[1]}"
  command mv "$@" || return
  case "${CRASH_POINT:-}" in
    source-to-safety)
      if [[ "$source" == "${LP_HOME}/source" && "$target" == *source.before-manual-rollback-* ]]; then kill -KILL "$$"; fi ;;
    old-to-source)
      if [[ "$source" == *source.before-20261017* && "$target" == "${LP_HOME}/source" ]]; then kill -KILL "$$"; fi ;;
    recovery-source-to-backup)
      if [[ "$source" == "${LP_HOME}/source" && "$target" == *source.before-20261017* ]]; then kill -KILL "$$"; fi ;;
    install-old-to-backup)
      if [[ "$source" == "${DEFAULT_INSTALL_DIR}/source" && "$target" == *source.before-install-* ]]; then kill -KILL "$$"; fi ;;
    install-stage-to-source)
      if [[ "$source" == *source.stage.* && "$target" == "${DEFAULT_INSTALL_DIR}/source" ]]; then kill -KILL "$$"; fi ;;
    env-switch)
      if [[ "$target" == "${LP_ENV_FILE}" ]] && command grep -q '^APP_IMAGE=license-platform-rollback:' "$target"; then kill -KILL "$$"; fi ;;
  esac
}
rm() {
  if [[ "${CRASH_POINT:-}" == commit ]]; then
    local arg
    for arg in "$@"; do
      if [[ "$arg" == *manual-rollback.pending || "$arg" == *install.pending ]]; then kill -KILL "$$"; fi
    done
  fi
  if [[ "${FIXTURE_FAKE_LINKS:-0}" == 1 ]]; then
    local target
    for target in "$@"; do
      if [[ "$target" == "${FIXTURE_ROOT}/sbin/licensectl" ]]; then command rm -f -- "$target.fixture-link"; fi
    done
  fi
  command rm "$@"
}
