//! Ядро симуляции traffic-city (Rust-пилот, ADR-0022, `docs/design/rust-pilot.md`).
//!
//! Правила портируемости: `clippy.toml` рядом с этим крейтом (аналог `BannedSymbols.txt` в C#).
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

pub mod util;
