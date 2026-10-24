#!/usr/bin/env bash
# Stop: если изменены .cs — dotnet test + bench-smoke s1k + compare. Провал -> exit 2.
set -u
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
hook_init
[ "$HOOK_STOP_ACTIVE" = 1 ] && exit 0

root=$(hook_repo_root)
cd "$root" 2>/dev/null || exit 0

changed=$( { git diff --name-only HEAD 2>/dev/null; git ls-files --others --exclude-standard 2>/dev/null; } | tr -d '\r')
printf '%s\n' "$changed" | grep -q '\.cs$' || exit 0

if ! _have dotnet; then
  echo "stop-verify: dotnet не найден, проверка пропущена." >&2
  exit 0
fi

fail() {
  printf 'stop-verify: %s\n' "$1" >&2
  [ -n "${2:-}" ] && printf '%s\n' "$2" | tail -n 60 >&2
  exit 2
}

if [ ! -f traffic-city.sln ]; then
  echo "stop-verify: traffic-city.sln нет, dotnet test пропущен." >&2
  exit 0
fi
out=$(dotnet test traffic-city.sln -nologo -v q 2>&1); rc=$?
if [ $rc -ne 0 ]; then
  fail "dotnet test провалился (exit $rc):" "$(printf '%s\n' "$out" | tr -d '\r' | grep -E 'error|Failed|failed|Assert' | awk '!seen[$0]++' | tail -n 40)"
fi

if [ ! -f scenarios/s1k.json ] || [ ! -f src/Sim.Headless/Sim.Headless.csproj ]; then
  echo "stop-verify: bench/compare пропущены (нужны scenarios/s1k.json, src/Sim.Headless)." >&2
  exit 0
fi
# Параметры bench и повтор при шуме p95 - в scripts/bench-gate.sh (единая точка для хука, CI и рук).
# Облачный контейнер: эталон perf/baseline.json (ADR-0007); нет его - абсолютный бюджет p95 <= 1 мс и 0 аллокаций.
if [ ! -f scripts/bench-gate.sh ]; then
  echo "stop-verify: scripts/bench-gate.sh нет, bench/compare пропущены." >&2
  exit 0
fi
if [ -f perf/baseline.json ]; then
  out=$(bash scripts/bench-gate.sh --out perf/results/s1k-smoke.json --baseline perf/baseline.json 2>&1); rc=$?
  [ $rc -ne 0 ] && fail "регрессия производительности vs perf/baseline.json (bench-gate exit $rc):" "$out"
else
  out=$(bash scripts/bench-gate.sh --out perf/results/s1k-smoke.json --budget-p95-ms 1.0 2>&1); rc=$?
  [ $rc -ne 0 ] && fail "бюджет s1k нарушен: p95 > 1.0 мс или аллокации (bench-gate exit $rc):" "$out"
fi
exit 0
