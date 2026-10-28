#!/usr/bin/env bash
# PostToolUse (Edit|Write, *.rs / Cargo.toml / clippy.toml под rust/): cargo clippy -D warnings по workspace rust/; при ошибке exit 2.
# clippy включает cargo check, плюс правила портируемости из crates/tc-core/clippy.toml (docs/design/rust-pilot.md §7).
set -u
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
hook_init
[ -z "$HOOK_FILE" ] && exit 0
case $HOOK_FILE in
  *.rs|*/Cargo.toml|*/clippy.toml|*/rust-toolchain.toml|*/rustfmt.toml) ;;
  *) exit 0 ;;
esac

if ! _have cargo; then
  [ -x /root/.cargo/bin/cargo ] && export PATH="/root/.cargo/bin:$PATH"
fi
if ! _have cargo; then
  echo "post-rust: cargo не найден, проверка пропущена." >&2
  exit 0
fi

p=$(norm_slashes "$HOOK_FILE")
case $p in /*) ;; *) p="$(norm_slashes "$PWD")/$p" ;; esac
p=$(collapse_path "$p")
dir=${p%/*}
while [ -n "$dir" ] && [ ! -d "$dir" ]; do dir=${dir%/*}; done
rdir=""
while [ -n "$dir" ]; do
  if [ -f "$dir/Cargo.toml" ] && [ -f "$dir/rust-toolchain.toml" ]; then rdir=$dir; break; fi
  dir=${dir%/*}
done
if [ -z "$rdir" ]; then
  echo "post-rust: workspace rust/ для $HOOK_FILE не найден, проверка пропущена." >&2
  exit 0
fi

cd "$rdir" || exit 0
# --locked: без сети и без тихой смены Cargo.lock; нет lock-файла - пусть cargo создаст.
args=(--workspace --all-targets)
[ -f Cargo.lock ] && args+=(--locked)
out=$(cargo clippy "${args[@]}" -- -D warnings 2>&1); rc=$?
if [ $rc -ne 0 ]; then
  {
    echo "post-rust: cargo clippy в $rdir провалился (exit $rc):"
    printf '%s\n' "$out" | tr -d '\r' | grep -vE '^\s*(Compiling|Checking|Downloaded|Downloading|Locking|Updating)' | tail -n 60
  } >&2
  exit 2
fi
exit 0
