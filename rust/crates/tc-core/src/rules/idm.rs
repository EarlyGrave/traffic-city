//! Intelligent Driver Model (Treiber, Hennecke, Helbing 2000). Порт `Sim.Core/Rules/Idm.cs` - чистые функции без состояния.
//!
//! `a = amax * (1 - (v/v0)^delta - (s*/gap)^2)`, `s* = s0 + max(0, v*T + v*dv / (2*sqrt(amax*b)))`, `dv = v - vLeader`.
//! Порядок операций и клампы повторяют C# построчно (rust-pilot.md §6.1-6.2): без FMA, без `f32::max/min`, `sqrt` корректно округлён.

use crate::util::detmath::powf_system;

/// Физический предел торможения, м/с².
pub const MAX_BRAKE: f32 = 9.0;
/// «Бесконечный» зазор для свободной дороги (интерактивный член обнуляется).
pub const FREE_GAP: f32 = 1e6;

const MIN_GAP: f32 = 0.01;
const MIN_V0: f32 = 0.1;

/// Желаемая скорость на полосе: `min(max_speed, speed_factor * lane_limit)`.
pub fn desired_speed(max_speed: f32, speed_factor: f32, lane_limit: f32) -> f32 {
    let v = speed_factor * lane_limit;
    if v < max_speed { v } else { max_speed }
}

/// Ускорение на свободной дороге (без лидера).
pub fn free_accel(v: f32, v0: f32, amax: f32, delta: f32) -> f32 {
    let a = amax * (1.0 - pow(v / (if v0 < MIN_V0 { MIN_V0 } else { v0 }), delta));
    if a < -MAX_BRAKE { -MAX_BRAKE } else { a }
}

/// Ускорение при лидере на расстоянии `gap` (бампер-в-бампер, м) со сближением `dv`.
#[allow(clippy::too_many_arguments)] // сигнатура зеркалит C# `Idm.Accel`
pub fn accel(v: f32, v0: f32, gap: f32, dv: f32, amax: f32, b: f32, headway: f32, s0: f32, delta: f32) -> f32 {
    if gap >= FREE_GAP {
        return free_accel(v, v0, amax, delta);
    }
    let gap = if gap < MIN_GAP { MIN_GAP } else { gap };

    let s_star = (v * headway) + (v * dv / (2.0 * (amax * b).sqrt()));
    let s_star = s0 + (if s_star > 0.0 { s_star } else { 0.0 });
    let r = s_star / gap;
    let a = amax * (1.0 - pow(v / (if v0 < MIN_V0 { MIN_V0 } else { v0 }), delta) - (r * r));
    if a < -MAX_BRAKE { -MAX_BRAKE } else { a }
}

/// Показатель 4 (по умолчанию) и 2 - умножениями (не зависит от libm); остальные - системный `powf` как `MathF.Pow` (§6.3).
fn pow(x: f32, delta: f32) -> f32 {
    if delta == 4.0 {
        let x2 = x * x;
        return x2 * x2;
    }
    if delta == 2.0 {
        return x * x;
    }
    powf_system(x, delta)
}
