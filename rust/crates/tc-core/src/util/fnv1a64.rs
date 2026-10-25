//! FNV-1a 64. Порт `Util/Fnv1a64.cs`. Многобайтные значения - little-endian; float - по битовому образу.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Fnv1a64 {
    h: u64,
}

impl Fnv1a64 {
    pub const OFFSET: u64 = 14_695_981_039_346_656_037;
    pub const PRIME: u64 = 1_099_511_628_211;

    pub fn new() -> Self {
        Self { h: Self::OFFSET }
    }

    pub fn value(&self) -> u64 {
        self.h
    }

    pub fn add_u8(&mut self, b: u8) {
        self.h = (self.h ^ u64::from(b)).wrapping_mul(Self::PRIME);
    }

    pub fn add_i32(&mut self, v: i32) {
        for b in v.to_le_bytes() {
            self.add_u8(b);
        }
    }

    pub fn add_u32(&mut self, v: u32) {
        self.add_i32(v as i32);
    }

    pub fn add_i64(&mut self, v: i64) {
        for b in v.to_le_bytes() {
            self.add_u8(b);
        }
    }

    pub fn add_u64(&mut self, v: u64) {
        self.add_i64(v as i64);
    }

    pub fn add_f32(&mut self, v: f32) {
        self.add_i32(v.to_bits() as i32);
    }

    pub fn add_bytes(&mut self, bytes: &[u8]) {
        for &b in bytes {
            self.add_u8(b);
        }
    }
}

impl Default for Fnv1a64 {
    fn default() -> Self {
        Self::new()
    }
}
