#!/usr/bin/env bash
# SessionStart: в облаке ($CLAUDE_CODE_REMOTE=true) убедиться, что есть .NET SDK из global.json.
# Никогда не валит сессию: всегда exit 0.
set -u
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

log() { printf 'session-start: %s\n' "$*" >&2; }

main() {
  [ "${CLAUDE_CODE_REMOTE:-}" = true ] || return 0
  local root=${CLAUDE_PROJECT_DIR:-$PWD}
  local gj="$root/global.json"
  [ -f "$gj" ] || return 0

  local ver
  ver=$(grep -o '"version"[[:space:]]*:[[:space:]]*"[^"]*"' "$gj" | head -n1 | sed 's/.*:[[:space:]]*"//; s/"$//')
  if [ -z "$ver" ]; then log "версия SDK в global.json не найдена"; return 0; fi
  local mm=${ver%.*}   # 9.0.100 -> 9.0
  case $mm in *.*) ;; *) mm=$ver ;; esac

  export PATH="$HOME/.dotnet:$PATH"
  export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

  have_sdk() { _have dotnet && dotnet --list-sdks 2>/dev/null | grep -q "^${mm//./\\.}\."; }

  if have_sdk; then
    log "SDK $mm уже установлен"
    write_env
    return 0
  fi

  log "SDK $mm не найден, пробую dotnet-install.sh"
  local tmp; tmp=$(mktemp 2>/dev/null || echo "/tmp/dotnet-install.$$.sh")
  if _have curl && curl -fsSL --max-time 60 https://dot.net/v1/dotnet-install.sh -o "$tmp" 2>/dev/null \
     && (cd "$root" && bash "$tmp" --jsonfile global.json --install-dir "$HOME/.dotnet") >&2 2>&1; then
    :
  else
    log "dotnet-install.sh недоступен или упал"
  fi
  rm -f "$tmp" 2>/dev/null

  if ! have_sdk; then
    log "пробую apt-get install dotnet-sdk-$mm"
    local sudo=""
    if [ "$(id -u 2>/dev/null)" != 0 ]; then _have sudo && sudo=sudo; fi
    if _have apt-get; then
      { $sudo apt-get update -qq; $sudo apt-get install -y -qq "dotnet-sdk-$mm"; } >&2 2>&1 \
        || log "apt-get install dotnet-sdk-$mm не удался"
    else
      log "apt-get не найден"
    fi
  fi

  if have_sdk; then log "SDK $mm готов"; else log "не удалось установить SDK $mm"; fi
  write_env
  return 0
}

write_env() {
  if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
    {
      echo "export PATH=\"$HOME/.dotnet:\$PATH\""
      echo "export DOTNET_ROOT=\"$HOME/.dotnet\""
    } >> "$CLAUDE_ENV_FILE" 2>/dev/null || log "не удалось записать $CLAUDE_ENV_FILE"
  fi
}

main || log "ошибка (игнорируется)"
exit 0
