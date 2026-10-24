#!/usr/bin/env bash
# PreToolUse (Edit|Write): Sim.Core не знает про Godot; perf/baseline.json только через update-baseline.
set -u
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
hook_init
[ -z "$HOOK_FILE" ] && exit 0
rel=$(hook_rel_path "$HOOK_FILE")

case $rel in
  perf/baseline.json)
    hook_block "perf/baseline.json нельзя править вручную: используйте 'dotnet run --project src/Sim.Headless -c Release -- update-baseline'." ;;
esac

case $rel in
  src/Sim.Core/*|*/src/Sim.Core/*)
    if printf '%s' "$HOOK_TEXT" | grep -Eq 'using[[:space:]]+Godot|Godot\.'; then
      hook_block "в src/Sim.Core/** запрещены 'using Godot' и ссылки на 'Godot.' (ядро симуляции не зависит от движка). Файл: $rel"
    fi ;;
esac
exit 0
