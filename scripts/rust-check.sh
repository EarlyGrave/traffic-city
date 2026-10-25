#!/usr/bin/env bash
# Проверка Rust-пилота (docs/design/rust-pilot.md §3, §4, §9): fmt, clippy -D warnings, test, сторонние крейты.
# Те же шаги, что в CI job `rust`. Запуск из любого каталога.
#   bash scripts/rust-check.sh              # всё
#   bash scripts/rust-check.sh --deps-only  # только проверка сторонних зависимостей (cargo metadata)
#   bash scripts/rust-check.sh --no-test    # fmt + clippy + deps (быстро, для хука)
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root/rust"

if ! command -v cargo >/dev/null 2>&1; then
  [ -x /root/.cargo/bin/cargo ] && export PATH="/root/.cargo/bin:$PATH"
fi
command -v cargo >/dev/null 2>&1 || { echo "rust-check: cargo не найден (см. docs/design/rust-pilot.md §1)" >&2; exit 1; }

mode=all
case "${1:-}" in
  --deps-only) mode=deps ;;
  --no-test) mode=notest ;;
  "") ;;
  *) echo "rust-check: неизвестный аргумент $1" >&2; exit 2 ;;
esac

# Любой пакет с source != null (т.е. из реестра/git) обязан быть в allowed-crates.txt.
check_deps() {
  local meta allowed bad
  meta=$(cargo metadata --format-version 1 --locked)
  allowed=$(grep -v '^[[:space:]]*#' allowed-crates.txt | tr -d '\r' | sed 's/[[:space:]]//g' | grep -v '^$' || true)
  if command -v jq >/dev/null 2>&1; then
    bad=$(printf '%s' "$meta" | jq -r '.packages[] | select(.source != null) | .name')
  else
    bad=$(printf '%s' "$meta" | python3 -I -c 'import sys,json; [print(p["name"]) for p in json.load(sys.stdin)["packages"] if p.get("source") is not None]')
  fi
  local fail=0 name
  for name in $bad; do
    if ! printf '%s\n' "$allowed" | grep -qx "$name"; then
      echo "rust-check: сторонний крейт '$name' не в rust/allowed-crates.txt (нужен ADR, rust-pilot.md §4)" >&2
      fail=1
    fi
  done
  [ "$fail" = 0 ] || exit 1
  echo "rust-check: сторонние зависимости: нет вне allowed-crates.txt"
}

if [ "$mode" = deps ]; then
  check_deps
  exit 0
fi

echo "== cargo fmt --check =="
cargo fmt --all --check
echo "== cargo clippy -D warnings =="
cargo clippy --workspace --all-targets --locked -- -D warnings
if [ "$mode" = all ]; then
  echo "== cargo test =="
  cargo test --workspace --locked
fi
echo "== deps =="
check_deps
echo "rust-check: OK"
