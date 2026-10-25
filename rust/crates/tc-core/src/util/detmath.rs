//! Float-хелперы с семантикой .NET (`docs/design/rust-pilot.md` §6.2-6.3).

/// `MathF.Max`: NaN-пропагирующий, `+0 > -0`.
pub fn max_net(x: f32, y: f32) -> f32 {
    if x != y {
        if !x.is_nan() {
            return if y < x { x } else { y };
        }
        return x;
    }
    if y.is_sign_negative() { x } else { y }
}

/// `MathF.Min`: NaN-пропагирующий, `-0 < +0`.
pub fn min_net(x: f32, y: f32) -> f32 {
    if x != y {
        if !x.is_nan() {
            return if x < y { x } else { y };
        }
        return x;
    }
    if x.is_sign_negative() { x } else { y }
}

/// `MathF.Atan2` через системный libm. Единственное разрешённое место трансцендентной функции (`LanePieceHeading`, вне хэша);
/// на Linux x64 побитово совпадает с C# (rust-pilot.md §1), между ОС - нет (§6.3).
#[allow(clippy::disallowed_methods)] // единственное исключение из clippy.toml: системный atan2 как у MathF.Atan2
pub fn atan2_system(y: f32, x: f32) -> f32 {
    y.atan2(x)
}
