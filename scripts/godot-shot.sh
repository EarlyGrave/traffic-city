#!/usr/bin/env bash
# Godot-клиент в облаке без GPU: Xvfb + OpenGL (Mesa llvmpipe, gl_compatibility).
#   scripts/godot-shot.sh install                       # скачать Godot 4.7.2 mono (SourceForge-зеркало) в $GODOT_DIR
#   scripts/godot-shot.sh shot [scenario] [frames]      # PNG последнего кадра (movie maker, фикс. 30 fps)
#   scripts/godot-shot.sh fps  [scenario] [frames]      # средний FPS за N кадров (--print-fps, vsync выкл.)
#   scripts/godot-shot.sh stats [scenario] [poses]      # счётчики рендера (draw calls, примитивы, память) по позам, JSON (docs/design/render.md §8)
#       poses: "overview mid close lod0" (по умолчанию; "lod0" - 30 м, единственная поза с LOD0 ТС и тенями); env: WARMUP (тики прогрева; 2400, для s100k/s1m 6000), STATS_FRAME (кадр съёма, 10),
#       PRESET (low|high, по умолчанию low - пресет гейта; идёт в имя файла)
#   scripts/godot-shot.sh gate                          # V9: stats по набору SCENARIOS (по умолчанию "s1k-peds s10k") + Sim.Headless render-compare
#       с content/render-budgets.json и baseline $RENDER_BASELINE (по умолчанию perf/render-baseline.json; в CI perf/render-baseline-ci.json;
#       нет файла - только бюджет); exit 1 при превышении бюджета low или росте счётчика > 10% (docs/design/render.md §8.3)
#   scripts/godot-shot.sh gate-update                   # те же замеры + Sim.Headless render-update-baseline в $RENDER_BASELINE (только осознанно, ADR-0007)
# Доп. аргументы клиента: GAME_ARGS="--cam X Z DIST [PITCH [YAW]] --warmup-ticks N --hud --ped-mode heat|agents"; пресет - env PRESET (не GAME_ARGS); имя PNG: OUT_NAME (по умолчанию = сценарий).
# Результаты: $OUT (по умолчанию perf/results/godot; gate - $OUT_GATE, по умолчанию perf/results/render): <scenario>.png, <scenario>-fps.txt, stats-<scenario>-<поза>.json.
# FPS здесь - программный рендер на CPU (llvmpipe), абсолют не равен GPU; годится для сравнения до/после.
set -euo pipefail
cd "$(dirname "$0")/.."
VER=4.7.2-stable
GODOT_DIR=${GODOT_DIR:-/opt/godot}
BIN=$GODOT_DIR/Godot_v${VER}_mono_linux_x86_64/Godot_v${VER}_mono_linux.x86_64
OUT=${OUT:-perf/results/godot}
RES=${RES:-1280x720}
cmd=${1:-}
scenario=${2:-s1k}
frames=${3:-}
poses=${3:-overview mid close lod0}

install() {
  local base=https://sourceforge.net/projects/godot-engine.mirror/files/$VER
  local z=Godot_v${VER}_mono_linux_x86_64.zip tmp
  tmp=$(mktemp -d)
  curl -fsSL -o "$tmp/$z" "$base/$z/download"
  curl -fsSL -o "$tmp/SUMS" "$base/SHA512-SUMS.txt/download"
  (cd "$tmp" && grep " $z\$" SUMS | sha512sum -c -)
  mkdir -p "$GODOT_DIR"
  unzip -q -o "$tmp/$z" -d "$GODOT_DIR"
  rm -rf "$tmp"
  echo "ok: $BIN"
}

need_godot() {
  [ -x "$BIN" ] || { echo "нет Godot: scripts/godot-shot.sh install" >&2; exit 1; }
}

run() { # run <extra godot args...>
  dotnet build game/TrafficCity.csproj -v q -nologo >&2
  (cd game && xvfb-run -a -s "-screen 0 ${RES}x24" "$BIN" --path . --rendering-driver opengl3 \
    --resolution "$RES" "$@" -- --scenario "$scenario" ${GAME_ARGS:-})
}

case $cmd in
  install) install ;;
  shot)
    need_godot
    mkdir -p "$OUT"
    d=$(mktemp -d)
    frames=${frames:-120}
    run --write-movie "$d/f.png" --fixed-fps 30 --quit-after "$frames" >/dev/null
    last=$(ls "$d" | tail -1)
    cp "$d/$last" "$OUT/${OUT_NAME:-$scenario}.png"
    rm -rf "$d"
    echo "$OUT/${OUT_NAME:-$scenario}.png (кадр $((frames - 1)))"
    ;;
  fps)
    need_godot
    mkdir -p "$OUT"
    frames=${frames:-600}
    run --disable-vsync --print-fps --quit-after "$frames" 2>&1 | grep '^Project FPS' > "$OUT/$scenario-fps.raw" || true
    # первые 2 секундных отсчёта - прогрев/загрузка, не считаем
    awk -v sc="$scenario" -v fr="$frames" -v res="$RES" 'NR>2{s+=$3;n++}
      END{printf "scenario=%s frames=%d fps_avg=%.1f (по %d секундным отсчётам, %s, llvmpipe)\n", sc, fr, s/n, n, res}' \
      "$OUT/$scenario-fps.raw" | tee "$OUT/$scenario-fps.txt"
    ;;
  stats)
    need_godot
    mkdir -p "$OUT"
    case $scenario in s100k|s1m) def_warm=6000 ;; *) def_warm=2400 ;; esac
    for pose in $poses; do
      f="$OUT/stats-${OUT_NAME:-$scenario}-$pose-${PRESET:-low}.json"
      # --fixed-fps 20: дельта кадра = 1 тик ядра, счётчики детерминированы; съём на кадре STATS_FRAME и выход
      f_abs=$(realpath -m "$f")
      GAME_ARGS="${GAME_ARGS:-} --render-preset ${PRESET:-low} --warmup-ticks ${WARMUP:-$def_warm} --pose $pose --render-stats $f_abs --stats-frame ${STATS_FRAME:-10}" \
        run --fixed-fps 20 --disable-vsync >/dev/null
      echo "$f"
      python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); m=d["deterministic"]; print("  %s %s: draw=%d prims=%d objs=%d shadow_draw=%d video_mem=%d MB tex=%d buf=%d instances=%d" % (d["scenario"], d["pose"], m["main"]["draw_calls"], m["main"]["primitives"], m["main"]["objects"], m["shadow"]["draw_calls"], m["memory_bytes"]["video"]>>20, m["memory_bytes"]["texture"], m["memory_bytes"]["buffer"], m["instances"]["total"]))' "$f"
    done
    ;;
  gate|gate-update)
    need_godot
    OUT=${OUT_GATE:-perf/results/render} # отдельный каталог: чистим только свои замеры
    export OUT
    mkdir -p "$OUT"
    rm -f "$OUT"/stats-*.json # без устаревших замеров других наборов
    for sc in ${SCENARIOS:-s1k-peds s10k}; do
      "$0" stats "$sc"
    done
    dotnet build src/Sim.Headless -c Release -v q -nologo >&2
    headless=(dotnet run --project src/Sim.Headless -c Release --no-build --)
    base=${RENDER_BASELINE:-perf/render-baseline.json}
    if [ "$cmd" = gate-update ]; then
      "${headless[@]}" render-update-baseline --from "$OUT" --baseline "$base"
    elif [ -f "$base" ]; then
      "${headless[@]}" render-compare --result "$OUT" --budget content/render-budgets.json --baseline "$base"
    else
      echo "нет baseline клиента $base - проверяю только бюджет (создать: scripts/godot-shot.sh gate-update)" >&2
      "${headless[@]}" render-compare --result "$OUT" --budget content/render-budgets.json
    fi
    ;;
  *) sed -n 2,16p "$0"; exit 2 ;;
esac
