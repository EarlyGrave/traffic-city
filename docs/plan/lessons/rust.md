# Грабли: Rust-пилот: FFI, конфликты, golden

Новый урок — дописать в конец файла; если правило важное для всех — строку в README.md.

## Rust-пилот: конфликты и FFI (R5, R6)
- **`cargo build -p tc-ffi --locked` падает, пока `Cargo.lock` не обновлён:** новый член workspace меняет lock, закоммитить его вместе с крейтом (CI гоняет `--locked`).
- **Паника в `extern "C"`-обёртке без `catch_unwind` — UB/abort:** `panic = "abort"` не включён намеренно; проверка — `tc_sim_test_panic` (фича `test-panic`, отдельный `--target-dir`, чтобы не перетирать обычный `.so`).
- **`&mut` из `*const` через `cast_mut` — UB:** обёртки разведены на `guard` (`&TcSim`) и `guard_mut` (`&mut TcSim`) по указателю того же вида, что в сигнатуре C.
- **Pre-commit при коммите в worktree идёт ≈ 14 мин даже для Rust-правок (полный `dotnet test`):** Rust-коммиты складывать в один.
