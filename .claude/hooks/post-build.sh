#!/usr/bin/env bash
# PostToolUse (Edit|Write, *.cs): быстрая сборка ближайшего проекта; при ошибке exit 2.
set -u
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
hook_init
[ -z "$HOOK_FILE" ] && exit 0
case $HOOK_FILE in *.cs) ;; *) exit 0 ;; esac

if ! _have dotnet; then
  echo "post-build: dotnet не найден, сборка пропущена." >&2
  exit 0
fi

p=$(norm_slashes "$HOOK_FILE")
case $p in /*) ;; *) p="$(norm_slashes "$PWD")/$p" ;; esac
p=$(collapse_path "$p")
dir=${p%/*}
while [ -n "$dir" ] && [ ! -d "$dir" ]; do dir=${dir%/*}; done
csproj=""
while [ -n "$dir" ]; do
  found=$(find "$dir" -maxdepth 1 -name '*.csproj' 2>/dev/null | head -n1)
  if [ -n "$found" ]; then csproj=$found; break; fi
  dir=${dir%/*}
done
if [ -z "$csproj" ]; then
  echo "post-build: *.csproj для $HOOK_FILE не найден, сборка пропущена." >&2
  exit 0
fi
case $csproj in
  */game/*.csproj)
    if [ "${HOOK_BUILD_GAME:-0}" != 1 ]; then
      echo "post-build: Godot-проект ($csproj) пропущен (нужен Godot.NET.Sdk; HOOK_BUILD_GAME=1 включает сборку)." >&2
      exit 0
    fi ;;
esac

csdir=${csproj%/*}
args=(-nologo -v q -warnaserror)
out=""; rc=0
if [ -f "$csdir/obj/project.assets.json" ]; then
  out=$(dotnet build "$csproj" "${args[@]}" --no-restore 2>&1); rc=$?
  if [ $rc -ne 0 ] && printf '%s' "$out" | grep -Eq 'NETSDK1004|NETSDK1005|NETSDK1064|NU1[0-9]{3}'; then
    out=$(dotnet build "$csproj" "${args[@]}" 2>&1); rc=$?
  fi
else
  out=$(dotnet build "$csproj" "${args[@]}" 2>&1); rc=$?
fi

if [ $rc -ne 0 ]; then
  {
    echo "post-build: сборка $csproj провалилась (exit $rc):"
    msgs=$(printf '%s\n' "$out" | tr -d '\r' | grep -E 'error|warning' | awk '!seen[$0]++' | tail -n 40)
    [ -z "$msgs" ] && msgs=$(printf '%s\n' "$out" | tail -n 40)
    printf '%s\n' "$msgs"
  } >&2
  exit 2
fi
exit 0
