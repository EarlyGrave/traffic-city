//! Загрузчик сценариев Rust-пилота (ADR-0022, `docs/design/rust-pilot.md` §5.1, §6.4, §10 R3):
//! JSON-текст -> `tc_core::api::desc::ScenarioDesc`. Свой JSON-ридер, 0 сторонних зависимостей, без файлового ввода-вывода.
#![forbid(unsafe_code)]
#![deny(
    clippy::print_stdout,
    clippy::print_stderr,
    clippy::dbg_macro,
    clippy::unwrap_used,
    clippy::expect_used,
    clippy::panic
)]
#![cfg_attr(
    test,
    allow(
        clippy::print_stdout,
        clippy::print_stderr,
        clippy::dbg_macro,
        clippy::unwrap_used,
        clippy::expect_used,
        clippy::panic
    )
)]

pub mod json;
pub mod loader;

pub use loader::{LoadError, load_str};
pub use tc_core::api::desc::desc_hash;
