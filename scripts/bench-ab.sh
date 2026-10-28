#!/usr/bin/env bash
# A/B бенча s1k: голова (текущее дерево) против базового коммита на ТОЙ ЖЕ машине (ADR-0031).
#   scripts/bench-ab.sh --base <sha> [--out-dir perf/results/ab] [--runs 3] [--budget-p95-ms 1.0]
# Прогоны вперемешку B1 H1 B2 H2 ... (отдельные процессы, параметры из scripts/bench-args.sh).
# Провал: медиана p95 головы > медианы базы x 1.10 И min(голова) > max(база); аллокации головы; разный ramp_complete;
# p95 головы > бюджета. База не собралась/не запустилась -> предупреждение и гейт по бюджету через bench-gate.sh.
set -u
# shellcheck source=scripts/bench-args.sh
source "$(dirname "${BASH_SOURCE[0]}")/bench-args.sh"

base=""; out_dir=perf/results/ab; runs=3; budget=1.0
while [ $# -gt 0 ]; do
  case "$1" in
    --base) base=$2; shift 2 ;;
    --out-dir) out_dir=$2; shift 2 ;;
    --runs) runs=$2; shift 2 ;;
    --budget-p95-ms) budget=$2; shift 2 ;;
    *) echo "bench-ab: неизвестный аргумент '$1'" >&2; exit 2 ;;
  esac
done
[ -n "$base" ] || { echo "bench-ab: нужен --base <sha>" >&2; exit 2; }

head_dir=$(pwd)
mkdir -p "$out_dir"; out_dir=$(cd "$out_dir" && pwd)
base_dir=$(mktemp -u "${TMPDIR:-/tmp}/bench-ab-base.XXXXXX")
cleanup() { git -C "$head_dir" worktree remove --force "$base_dir" >/dev/null 2>&1; rm -rf "$base_dir"; git -C "$head_dir" worktree prune; }
trap cleanup EXIT

fallback() {
  echo "bench-ab: ПРЕДУПРЕЖДЕНИЕ: $1; гейт по бюджету ${budget} мс (без A/B)." >&2
  cd "$head_dir" && bash scripts/bench-gate.sh --out "$out_dir/head-fallback.json" --budget-p95-ms "$budget"
  exit $?
}

if ! git -C "$head_dir" worktree add --detach "$base_dir" "$base" >/dev/null 2>&1; then
  fallback "базовый коммит '$base' недоступен"
fi
echo "bench-ab: база $(git -C "$base_dir" rev-parse --short HEAD), голова $(git -C "$head_dir" rev-parse --short HEAD)"

if ! (cd "$base_dir" && dotnet build src/Sim.Headless -c Release -v q -nologo >"$out_dir/base-build.log" 2>&1); then
  tail -n 20 "$out_dir/base-build.log" >&2
  fallback "база не собирается"
fi
(cd "$head_dir" && dotnet build src/Sim.Headless -c Release -v q -nologo) || { echo "bench-ab: голова не собирается" >&2; exit 2; }

run_side() { # <dir> <out.json>
  (cd "$1" && dotnet run --no-build --project src/Sim.Headless -c Release -- bench "${BENCH_ARGS[@]}" --out "$2" >"$2.log" 2>&1)
}

for i in $(seq 1 "$runs"); do
  run_side "$base_dir" "$out_dir/base-$i.json" || { tail -n 20 "$out_dir/base-$i.json.log" >&2; fallback "прогон базы $i провалился"; }
  run_side "$head_dir" "$out_dir/head-$i.json" || { tail -n 20 "$out_dir/head-$i.json.log" >&2; echo "bench-ab: прогон головы $i провалился" >&2; exit 2; }
done

python3 - "$out_dir" "$runs" "$budget" <<'PY'
import json, statistics, sys
d, n, budget = sys.argv[1], int(sys.argv[2]), float(sys.argv[3])
def load(side, i):
    p = json.load(open(f"{d}/{side}-{i}.json"))["perf"]
    return dict(p95=p["tick_ms"]["p95"], p50=p["tick_ms"]["p50"], alloc=p.get("alloc_bytes_per_tick", 0), ramp=p.get("ramp_complete"))
rows = {s: [load(s, i) for i in range(1, n + 1)] for s in ("base", "head")}
print(f"{'run':>3} {'side':<5} {'p95 ms':>9} {'p50 ms':>9} {'alloc B/tick':>13} {'ramp':>6}")
for i in range(n):
    for s in ("base", "head"):
        r = rows[s][i]
        print(f"{i+1:>3} {s:<5} {r['p95']:>9.4f} {r['p50']:>9.4f} {r['alloc']:>13} {str(r['ramp']):>6}")
bp = [r["p95"] for r in rows["base"]]; hp = [r["p95"] for r in rows["head"]]
mb, mh = statistics.median(bp), statistics.median(hp)
print(f"median p95: base {mb:.4f}  head {mh:.4f}  ratio {mh/mb:.3f}   min head {min(hp):.4f}  max base {max(bp):.4f}")
fails = []
if mh > mb * 1.10 and min(hp) > max(bp):
    fails.append("регрессия p95: медиана головы > базы x1.10 и прогоны не пересекаются")
if any(r["alloc"] > 0 for r in rows["head"]):
    fails.append("аллокации на тик у головы")
if {r["ramp"] for r in rows["head"]} != {r["ramp"] for r in rows["base"]} or len({r["ramp"] for r in rows["head"]}) != 1:
    fails.append("ramp_complete различается")
if mh > budget:
    fails.append(f"медиана p95 головы {mh:.4f} > бюджета {budget} мс")
if max(hp) > budget:
    print(f"warning: max p95 головы {max(hp):.4f} > бюджета {budget} мс (медиана в бюджете)")
if fails:
    print("VERDICT: FAIL - " + "; ".join(fails)); sys.exit(1)
print("VERDICT: PASS")
PY
