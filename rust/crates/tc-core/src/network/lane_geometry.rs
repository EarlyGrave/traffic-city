//! Порт `Network/LaneGeometry.cs`: позиция/направление по полосе (без аллокаций).

use crate::util::Vec2;

use super::types::LaneData;

/// Позиция и единичное направление на расстоянии `s` (зажимается в [0, длина]). `point_hint` - индекс отрезка полилинии
/// внутри полосы (0..point_count-2), хранится у машины: поиск идёт от подсказки вперёд/назад.
pub fn evaluate(lanes: &[LaneData], points: &[Vec2], point_s: &[f32], lane: usize, s: f32, point_hint: &mut i32) -> (Vec2, Vec2) {
    let l = &lanes[lane];
    let first = l.first_point as usize;
    let last_piece = l.point_count - 2;
    let total = point_s[first + last_piece as usize + 1];
    let mut s = s;
    if s < 0.0 {
        s = 0.0;
    } else if s > total {
        s = total;
    }

    let mut h = *point_hint;
    if h < 0 {
        h = 0;
    } else if h > last_piece {
        h = last_piece;
    }
    while h < last_piece && s >= point_s[first + h as usize + 1] {
        h += 1;
    }
    while h > 0 && s < point_s[first + h as usize] {
        h -= 1;
    }
    *point_hint = h;

    let a = points[first + h as usize];
    let b = points[first + h as usize + 1];
    let sa = point_s[first + h as usize];
    let span = point_s[first + h as usize + 1] - sa;
    let t = if span > 1e-6_f32 { (s - sa) / span } else { 0.0 };
    let d = b - a;
    let pos = a + (d * t);
    let len = ((d.x * d.x) + (d.z * d.z)).sqrt();
    let dir = if len > 1e-9_f32 { d * (1.0_f32 / len) } else { Vec2::new(1.0, 0.0) };
    (pos, dir)
}
