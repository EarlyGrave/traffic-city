#!/usr/bin/env bash
# Самопроверка хуков: фейковые JSON-входы, проверка exit-кодов.
set -u
HERE=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
export CLAUDE_PROJECT_DIR
CLAUDE_PROJECT_DIR=$(cd "$HERE/../.." && pwd)
P=$CLAUDE_PROJECT_DIR

FAILS=0; TOTAL=0

# t <имя> <ожидаемый exit> <хук> <json> [каталог запуска]
t() {
  local name=$1 want=$2 hook=$3 json=$4 cwd=${5:-$P} got
  TOTAL=$((TOTAL + 1))
  got=$(cd "$cwd" && printf '%s' "$json" | bash "$HERE/$hook" >/dev/null 2>&1; echo $?)
  if [ "$got" = "$want" ]; then echo "PASS  $name (exit $got)"; else echo "FAIL  $name (ожидали $want, получили $got)"; FAILS=$((FAILS + 1)); fi
}

W='"tool_name":"Write"'; E='"tool_name":"Edit"'
GODOT_C='using Godot;\nclass A {}'

echo "== guard-core =="
t "Write using Godot в src/Sim.Core -> block" 2 guard-core.sh "{$W,\"agent_id\":\"a1\",\"tool_input\":{\"file_path\":\"$P/src/Sim.Core/Foo.cs\",\"content\":\"$GODOT_C\"}}"
t "Edit using Godot в src/Sim.Core -> block" 2 guard-core.sh "{$E,\"agent_id\":\"a1\",\"tool_input\":{\"file_path\":\"$P/src/Sim.Core/Foo.cs\",\"old_string\":\"x\",\"new_string\":\"$GODOT_C\"}}"
t "MultiEdit edits[].new_string Godot. -> block" 2 guard-core.sh "{\"tool_name\":\"MultiEdit\",\"tool_input\":{\"file_path\":\"$P/src/Sim.Core/Foo.cs\",\"edits\":[{\"old_string\":\"a\",\"new_string\":\"b\"},{\"old_string\":\"c\",\"new_string\":\"var n = Godot.Vector2.Zero;\"}]}}"
t "относительный путь src/Sim.Core -> block" 2 guard-core.sh "{$W,\"tool_input\":{\"file_path\":\"src/Sim.Core/Foo.cs\",\"content\":\"$GODOT_C\"}}"
t "Windows-путь C:\\ в src/Sim.Core -> block" 2 guard-core.sh "{$W,\"tool_input\":{\"file_path\":\"C:\\\\proj\\\\src\\\\Sim.Core\\\\Foo.cs\",\"content\":\"$GODOT_C\"}}" /tmp
t "тот же текст в game/Foo.cs -> allow" 0 guard-core.sh "{$W,\"tool_input\":{\"file_path\":\"$P/game/Foo.cs\",\"content\":\"$GODOT_C\"}}"
t "чистый код в src/Sim.Core -> allow" 0 guard-core.sh "{$W,\"tool_input\":{\"file_path\":\"$P/src/Sim.Core/Foo.cs\",\"content\":\"namespace X { class A {} }\"}}"
t "worktree: путь вне проекта, но src/Sim.Core -> block" 2 guard-core.sh "{$W,\"tool_input\":{\"file_path\":\"$P/.claude/worktrees/w1/src/Sim.Core/Foo.cs\",\"content\":\"$GODOT_C\"}}"
t "правка perf/baseline.json -> block" 2 guard-core.sh "{$E,\"tool_input\":{\"file_path\":\"$P/perf/baseline.json\",\"old_string\":\"1\",\"new_string\":\"2\"}}"
t "perf/results/x.json -> allow" 0 guard-core.sh "{$W,\"tool_input\":{\"file_path\":\"$P/perf/results/x.json\",\"content\":\"{}\"}}"

echo "== guard-orchestrator =="
t "оркестратор пишет src/x.cs -> block" 2 guard-orchestrator.sh "{$W,\"tool_input\":{\"file_path\":\"$P/src/x.cs\",\"content\":\"a\"}}"
t "оркестратор пишет docs/plan/a.md -> allow" 0 guard-orchestrator.sh "{$W,\"tool_input\":{\"file_path\":\"$P/docs/plan/a.md\",\"content\":\"a\"}}"
t "оркестратор пишет CLAUDE.md -> allow" 0 guard-orchestrator.sh "{$W,\"tool_input\":{\"file_path\":\"$P/CLAUDE.md\",\"content\":\"a\"}}"
t "оркестратор: src/Sim.Core/CLAUDE.md -> block" 2 guard-orchestrator.sh "{$W,\"tool_input\":{\"file_path\":\"$P/src/Sim.Core/CLAUDE.md\",\"content\":\"a\"}}"
t "оркестратор: .claude/settings.json -> block" 2 guard-orchestrator.sh "{$W,\"tool_input\":{\"file_path\":\"$P/.claude/settings.json\",\"content\":\"a\"}}"
t "оркестратор: путь ../ побег из docs -> block" 2 guard-orchestrator.sh "{$W,\"tool_input\":{\"file_path\":\"$P/docs/../src/x.cs\",\"content\":\"a\"}}"
t "оркестратор: внешний абсолютный путь -> block" 2 guard-orchestrator.sh "{$W,\"tool_input\":{\"file_path\":\"/etc/passwd\",\"content\":\"a\"}}"
t "субагент (agent_id) пишет src/x.cs -> allow" 0 guard-orchestrator.sh "{$W,\"agent_id\":\"agent-1\",\"tool_input\":{\"file_path\":\"$P/src/x.cs\",\"content\":\"a\"}}"

echo "== guard-bash =="
B() { printf '{"tool_name":"Bash","tool_input":{"command":"%s"}}' "$1"; }
t "git push origin main -> block" 2 guard-bash.sh "$(B 'git push origin main')"
t "git push origin master -> block" 2 guard-bash.sh "$(B 'git push origin master')"
t "git push --force origin x -> block" 2 guard-bash.sh "$(B 'git push --force origin x')"
t "git push -f origin x -> block" 2 guard-bash.sh "$(B 'git push origin x -f')"
t "git push --force-with-lease -> block" 2 guard-bash.sh "$(B 'git push --force-with-lease')"
t "git push origin +x -> block" 2 guard-bash.sh "$(B 'git push origin +claude/x')"
t "git push origin HEAD:main -> block" 2 guard-bash.sh "$(B 'git push origin HEAD:main')"
t "git push origin HEAD:refs/heads/main -> block" 2 guard-bash.sh "$(B 'git push origin HEAD:refs/heads/main')"
t "git push origin :main -> block" 2 guard-bash.sh "$(B 'git push origin :main')"
t "git push origin +main -> block" 2 guard-bash.sh "$(B 'git push origin +main')"
t "git push --delete origin main -> block" 2 guard-bash.sh "$(B 'git push --delete origin main')"
t "git branch -D main -> block" 2 guard-bash.sh "$(B 'git branch -D main')"
t "git branch -d master -> block" 2 guard-bash.sh "$(B 'git branch -d master')"
t "git branch --delete main -> block" 2 guard-bash.sh "$(B 'git branch --delete main')"
t "git status && git push origin HEAD:main -> block" 2 guard-bash.sh "$(B 'git status && git push origin HEAD:main')"
t "цепочка через ; и | -> block" 2 guard-bash.sh "$(B 'echo hi; git log | cat; git push origin main')"
t "git -C . push origin main -> block" 2 guard-bash.sh "$(B 'git -C . push origin main')"
t "bash -c 'git push origin main' -> block" 2 guard-bash.sh "$(B 'bash -c \"git push origin main\"')"
t "git push -u origin claude/abc -> allow" 0 guard-bash.sh "$(B 'git push -u origin claude/abc')"
t "git push origin checkpoint/2026-10-24-x -> allow" 0 guard-bash.sh "$(B 'git push origin checkpoint/2026-10-24-x')"
t "git log main -> allow" 0 guard-bash.sh "$(B 'git log main')"
t "git diff main...HEAD -> allow" 0 guard-bash.sh "$(B 'git diff main...HEAD')"
t "git checkout main -> allow" 0 guard-bash.sh "$(B 'git checkout main')"
t "git merge main -> allow" 0 guard-bash.sh "$(B 'git merge main')"
t "git branch -D claude/x -> allow" 0 guard-bash.sh "$(B 'git branch -D claude/x')"
t "git commit -m 'fix: main' -> allow" 0 guard-bash.sh "$(B 'git commit -m \"fix: main branch\"')"

# bare push / reset --hard зависят от текущей ветки: временный репозиторий
TMP=$(mktemp -d 2>/dev/null || echo "/tmp/th.$$")
mkdir -p "$TMP/onmain" "$TMP/onfeat"
( cd "$TMP/onmain" && git init -q -b main . && git -c user.email=a@b -c user.name=t commit -q --allow-empty -m init ) >/dev/null 2>&1
( cd "$TMP/onfeat" && git init -q -b claude/feat . && git -c user.email=a@b -c user.name=t commit -q --allow-empty -m init ) >/dev/null 2>&1
t "голый git push на main -> block" 2 guard-bash.sh "$(B 'git push')" "$TMP/onmain"
t "git push origin на main -> block" 2 guard-bash.sh "$(B 'git push origin')" "$TMP/onmain"
t "git push origin HEAD на main -> block" 2 guard-bash.sh "$(B 'git push origin HEAD')" "$TMP/onmain"
t "git reset --hard на main -> block" 2 guard-bash.sh "$(B 'git reset --hard HEAD~1')" "$TMP/onmain"
t "голый git push на feature-ветке -> allow" 0 guard-bash.sh "$(B 'git push')" "$TMP/onfeat"
t "git push origin HEAD на feature -> allow" 0 guard-bash.sh "$(B 'git push origin HEAD')" "$TMP/onfeat"
t "git reset --hard на feature -> allow" 0 guard-bash.sh "$(B 'git reset --hard HEAD~1')" "$TMP/onfeat"
rm -rf "$TMP"

echo "== stop-verify =="
t "stop_hook_active=true -> exit 0" 0 stop-verify.sh '{"hook_event_name":"Stop","stop_hook_active":true}'

echo "== post-build / session-start (не-.cs и не-облако) =="
t "post-build: не .cs -> allow" 0 post-build.sh "{$W,\"tool_input\":{\"file_path\":\"$P/docs/a.md\",\"content\":\"a\"}}"
t "session-start вне облака -> exit 0" 0 session-start.sh '{"hook_event_name":"SessionStart"}'

echo
echo "Итого: $((TOTAL - FAILS))/$TOTAL PASS"
[ "$FAILS" -eq 0 ] || { echo "FAILED: $FAILS"; exit 1; }
exit 0
