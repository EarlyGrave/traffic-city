//! Точка/вектор в плоскости земли (X, Z); Y - высота - хранится отдельно. Порт `Util/Vec2.cs`.

use core::ops::{Add, Mul, Sub};

#[derive(Clone, Copy, Debug, Default, PartialEq)]
#[repr(C)]
pub struct Vec2 {
    pub x: f32,
    pub z: f32,
}

impl Vec2 {
    pub const fn new(x: f32, z: f32) -> Self {
        Self { x, z }
    }
}

impl Add for Vec2 {
    type Output = Vec2;
    fn add(self, o: Vec2) -> Vec2 {
        Vec2::new(self.x + o.x, self.z + o.z)
    }
}

impl Sub for Vec2 {
    type Output = Vec2;
    fn sub(self, o: Vec2) -> Vec2 {
        Vec2::new(self.x - o.x, self.z - o.z)
    }
}

impl Mul<f32> for Vec2 {
    type Output = Vec2;
    fn mul(self, k: f32) -> Vec2 {
        Vec2::new(self.x * k, self.z * k)
    }
}
