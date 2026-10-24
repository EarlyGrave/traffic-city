#!/usr/bin/env bash
# Общие функции хуков Claude Code. Подключается через `source`.
# Работает в Linux и Git Bash (Windows). jq -> python -> grep/sed fallback.

_have() { command -v "$1" >/dev/null 2>&1; }

_pybin() {
  local p
  for p in python3 python; do
    if _have "$p" && "$p" -c 'import sys' >/dev/null 2>&1; then echo "$p"; return 0; fi
  done
  return 1
}

_PY_SCRIPT='
import sys, json
d = json.load(sys.stdin)
m = sys.argv[1]
ti = d.get("tool_input") or {}
if m == "file":
    print(ti.get("file_path") or ti.get("notebook_path") or "")
elif m == "text":
    parts = [ti.get(k) for k in ("content", "new_string", "new_source")]
    parts += [e.get("new_string") for e in (ti.get("edits") or []) if isinstance(e, dict)]
    print("\n".join(p for p in parts if isinstance(p, str)))
elif m == "cmd":
    print(ti.get("command") or "")
elif m == "agent":
    print(1 if d.get("agent_id") else 0)
elif m == "stop":
    print(1 if d.get("stop_hook_active") is True else 0)
'

# Читает stdin в HOOK_INPUT и разбирает поля:
# HOOK_FILE, HOOK_TEXT, HOOK_CMD, HOOK_HAS_AGENT (0/1), HOOK_STOP_ACTIVE (0/1)
hook_init() {
  HOOK_INPUT=$(cat)
  HOOK_FILE=""; HOOK_TEXT=""; HOOK_CMD=""; HOOK_HAS_AGENT=0; HOOK_STOP_ACTIVE=0
  local py
  if [ -z "${HOOK_NO_JQ:-}" ] && _have jq && printf '%s' "$HOOK_INPUT" | jq -e . >/dev/null 2>&1; then
    HOOK_FILE=$(printf '%s' "$HOOK_INPUT" | jq -r '.tool_input.file_path // .tool_input.notebook_path // empty' | tr -d '\r')
    HOOK_TEXT=$(printf '%s' "$HOOK_INPUT" | jq -r '[.tool_input.content, .tool_input.new_string, .tool_input.new_source, (.tool_input.edits[]? | .new_string)] | map(select(type == "string")) | join("\n")')
    HOOK_CMD=$(printf '%s' "$HOOK_INPUT" | jq -r '.tool_input.command // empty')
    [ "$(printf '%s' "$HOOK_INPUT" | jq -r 'if ((.agent_id // "") != "") then 1 else 0 end' | tr -d '\r')" = 1 ] && HOOK_HAS_AGENT=1
    [ "$(printf '%s' "$HOOK_INPUT" | jq -r 'if .stop_hook_active == true then 1 else 0 end' | tr -d '\r')" = 1 ] && HOOK_STOP_ACTIVE=1
  elif [ -z "${HOOK_NO_PY:-}" ] && py=$(_pybin) && printf '%s' "$HOOK_INPUT" | "$py" -c 'import sys,json; json.load(sys.stdin)' >/dev/null 2>&1; then
    HOOK_FILE=$(printf '%s' "$HOOK_INPUT" | "$py" -c "$_PY_SCRIPT" file | tr -d '\r')
    HOOK_TEXT=$(printf '%s' "$HOOK_INPUT" | "$py" -c "$_PY_SCRIPT" text)
    HOOK_CMD=$(printf '%s' "$HOOK_INPUT" | "$py" -c "$_PY_SCRIPT" cmd)
    [ "$(printf '%s' "$HOOK_INPUT" | "$py" -c "$_PY_SCRIPT" agent | tr -d '\r')" = 1 ] && HOOK_HAS_AGENT=1
    [ "$(printf '%s' "$HOOK_INPUT" | "$py" -c "$_PY_SCRIPT" stop | tr -d '\r')" = 1 ] && HOOK_STOP_ACTIVE=1
  else
    # Грубый fallback: без разбора JSON.
    HOOK_FILE=$(printf '%s' "$HOOK_INPUT" | grep -o '"\(file_path\|notebook_path\)"[[:space:]]*:[[:space:]]*"\([^"\\]\|\\.\)*"' | head -n1 \
      | sed 's/^[^:]*:[[:space:]]*"//; s/"$//; s/\\\\/\\/g')
    HOOK_TEXT=$HOOK_INPUT
    HOOK_CMD=$(printf '%s' "$HOOK_INPUT" | grep -o '"command"[[:space:]]*:[[:space:]]*"\([^"\\]\|\\.\)*"' | head -n1 \
      | sed 's/^[^:]*:[[:space:]]*"//; s/"$//; s/\\"/"/g; s/\\n/\n/g; s/\\\\/\\/g')
    printf '%s' "$HOOK_INPUT" | grep -q '"agent_id"[[:space:]]*:[[:space:]]*"[^"]' && HOOK_HAS_AGENT=1
    printf '%s' "$HOOK_INPUT" | grep -q '"stop_hook_active"[[:space:]]*:[[:space:]]*true' && HOOK_STOP_ACTIVE=1
  fi
  return 0
}

# Блокировка: сообщение в stderr, exit 2.
hook_block() {
  printf 'BLOCKED: %s\n' "$*" >&2
  exit 2
}

# Прямые слеши; C:\x и C:/x -> /c/x
norm_slashes() {
  local p=${1//\\//}
  if [[ $p =~ ^([A-Za-z]):(/.*)?$ ]]; then
    local d=${BASH_REMATCH[1]}
    p="/${d,,}${BASH_REMATCH[2]}"
  fi
  printf '%s' "$p"
}

# Убирает ., .. и двойные слеши из абсолютного пути.
collapse_path() {
  local IFS=/ part
  local -a out=()
  set -f
  for part in $1; do
    case $part in
      ''|.) ;;
      ..) if [ ${#out[@]} -gt 0 ]; then unset 'out[${#out[@]}-1]'; fi ;;
      *) out+=("$part") ;;
    esac
  done
  set +f
  printf '/%s' "${out[*]:-}"
}

# Каталог проекта (нормализованный).
hook_project_dir() {
  local d=${CLAUDE_PROJECT_DIR:-}
  [ -z "$d" ] && d=$(git rev-parse --show-toplevel 2>/dev/null)
  [ -z "$d" ] && d=$PWD
  collapse_path "$(norm_slashes "$d")"
}

# Путь относительно корня git-репозитория файла (учитывает worktree),
# иначе относительно $CLAUDE_PROJECT_DIR, иначе нормализованный абсолютный.
hook_rel_path() {
  local p dir root proj
  p=$(norm_slashes "$1")
  case $p in
    /*) ;;
    *) p="$(norm_slashes "$PWD")/$p" ;;
  esac
  p=$(collapse_path "$p")
  dir=$p
  while [ -n "$dir" ] && [ "$dir" != "/" ] && [ ! -d "$dir" ]; do dir=${dir%/*}; done
  [ -z "$dir" ] && dir=/
  root=$(git -C "$dir" rev-parse --show-toplevel 2>/dev/null | tr -d '\r')
  if [ -n "$root" ]; then
    root=$(collapse_path "$(norm_slashes "$root")")
    if [ "$root" != "/" ] && [[ $p == "$root"/* ]]; then printf '%s' "${p#"$root"/}"; return 0; fi
  fi
  proj=$(hook_project_dir)
  if [ "$proj" != "/" ] && [[ $p == "$proj"/* ]]; then printf '%s' "${p#"$proj"/}"; return 0; fi
  printf '%s' "$p"
}

# Корень git-репозитория текущего cwd, fallback $CLAUDE_PROJECT_DIR.
hook_repo_root() {
  local r
  r=$(git rev-parse --show-toplevel 2>/dev/null | tr -d '\r')
  [ -z "$r" ] && r=${CLAUDE_PROJECT_DIR:-$PWD}
  printf '%s' "$r"
}
