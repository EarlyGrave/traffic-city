#!/usr/bin/env bash
# Godot-клиент в облаке без GPU: Xvfb + OpenGL (Mesa llvmpipe, gl_compatibility).
#   scripts/godot-shot.sh install                       # скачать Godot 4.7.2 mono (SourceForge-зеркало) в $GODOT_DIR
#   scripts/godot-shot.sh shot [scenario] [frames]      # PNG последнего кадра (movie maker, фикс. 30 fps)
#   scripts/godot-shot.sh fps  [scenario] [frames]      # средний FPS за N кадров (--print-fps, vsync выкл.)
# Доп. аргументы клиента: GAME_ARGS="--cam X Z DIST [PITCH [YAW]] --warmup-ticks N --hud --ped-mode heat|agents"; имя PNG: OUT_NAME (по умолчанию = сценарий).
# Результаты: $OUT (по умолчанию perf/results/godot): <scenario>.png, <scenario>-fps.txt.
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
  *) sed -n 2,6p "$0"; exit 2 ;;
esac
