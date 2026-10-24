#!/usr/bin/env bash
# PreToolUse (Edit|Write): основная сессия (нет agent_id) пишет только docs/** и CLAUDE.md.
set -u
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
hook_init
[ "$HOOK_HAS_AGENT" = 1 ] && exit 0
[ -z "$HOOK_FILE" ] && exit 0
rel=$(hook_rel_path "$HOOK_FILE")
case $rel in
  docs/*|CLAUDE.md) exit 0 ;;
esac
hook_block "оркестратор не пишет код — делегируй субагенту. Основной сессии разрешены только docs/** и CLAUDE.md (попытка записи: $rel)."
