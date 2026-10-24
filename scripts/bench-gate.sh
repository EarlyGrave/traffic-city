#!/usr/bin/env bash
# Единая точка bench-smoke s1k + compare: Stop-хук, CI и руки используют ОДНИ параметры (lessons.md, ADR-0007).
#   scripts/bench-gate.sh --out perf/results/s1k.json (--baseline perf/baseline.json | --budget-p95-ms 1.0) [--attempts 3]
# Шум p95 в общем 4-vCPU контейнере ~±10-15% (см. docs/REPORT-stage2.md, шаг 2.1), допуск compare 10%:
# провал по p95 повторяем до --attempts раз (независимые процессы), провал только если не прошла ни одна попытка.
# Аллокации и несовместимая рампа (ramp_complete) - ошибка сразу, без повторов.
set -u

BENCH_ARGS=(--scenario s1k --ticks 2000 --warmup-ticks 2400 --repeat 3 --warmup-until-full)

out=perf/results/s1k.json; baseline=""; budget=""; attempts=3
while [ $# -gt 0 ]; do
  case "$1" in
    --out) out=$2; shift 2 ;;
    --baseline) baseline=$2; shift 2 ;;
    --budget-p95-ms) budget=$2; shift 2 ;;
    --attempts) attempts=$2; shift 2 ;;
    *) echo "bench-gate: неизвестный аргумент '$1'" >&2; exit 2 ;;
  esac
done
if [ -z "$baseline" ] && [ -z "$budget" ]; then
  echo "bench-gate: нужен --baseline <файл> или --budget-p95-ms <мс>" >&2; exit 2
fi
mkdir -p "$(dirname "$out")"
run=(dotnet run --project src/Sim.Headless -c Release --)

rc=1
for i in $(seq 1 "$attempts"); do
  b=$("${run[@]}" bench "${BENCH_ARGS[@]}" --out "$out" 2>&1); brc=$?
  if [ $brc -ne 0 ]; then
    printf 'bench-gate: bench провалился (exit %s):\n%s\n' "$brc" "$b" >&2; exit 2
  fi
  if [ -n "$baseline" ]; then
    c=$("${run[@]}" compare --result "$out" --baseline "$baseline" 2>&1); rc=$?
  else
    c=$("${run[@]}" compare --result "$out" --budget-p95-ms "$budget" 2>&1); rc=$?
  fi
  printf 'bench-gate: попытка %s/%s\n%s\n%s\n' "$i" "$attempts" "$b" "$c"
  [ $rc -eq 0 ] && exit 0
  # повтор имеет смысл только для шума p95
  if printf '%s' "$c" | grep -qE 'ALLOCATIONS|RAMP MISMATCH' || [ $rc -ne 1 ]; then break; fi
done
exit $rc
