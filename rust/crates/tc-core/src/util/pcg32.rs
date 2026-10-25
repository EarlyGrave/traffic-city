//! PCG32 XSH-RR (pcg-random.org). Порт `Util/Pcg32.cs`, `Util/RngStream.cs`.

const MULT: u64 = 6_364_136_223_846_793_005;

/// Номера независимых подпотоков PCG32 (core-api §8). Значения фиксированы навсегда.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u64)]
pub enum RngStream {
    CityGen = 1,
    Zones = 2,
    Spawn = 3,
    Behaviour = 4,
    Transit = 5,
    Routing = 6,
}

/// 64-бит состояние, нечётный 64-бит инкремент (поток).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Pcg32 {
    state: u64,
    inc: u64, // всегда нечётный
}

impl Pcg32 {
    /// `pcg32_srandom_r`: `inc = (stream << 1) | 1; state = 0; step; state += seed; step`.
    pub fn create(seed: u64, stream: u64) -> Self {
        let mut rng = Self {
            state: 0,
            inc: (stream << 1) | 1,
        };
        rng.next_u32();
        rng.state = rng.state.wrapping_add(seed);
        rng.next_u32();
        rng
    }

    /// Подпоток подсистемы от общего seed сценария.
    pub fn for_stream(seed: u64, stream: RngStream) -> Self {
        Self::create(seed, stream as u64)
    }

    /// Восстановление из сейва: состояние и инкремент как были (инкремент обязан быть нечётным).
    pub fn from_state(state: u64, inc: u64) -> Self {
        Self { state, inc }
    }

    pub fn state(&self) -> u64 {
        self.state
    }

    pub fn inc(&self) -> u64 {
        self.inc
    }

    /// `pcg32_random_r`.
    pub fn next_u32(&mut self) -> u32 {
        let old = self.state;
        self.state = old.wrapping_mul(MULT).wrapping_add(self.inc);
        let xorshifted = (((old >> 18) ^ old) >> 27) as u32;
        let rot = (old >> 59) as u32;
        xorshifted.rotate_right(rot)
    }

    /// `[0, bound)` умножением-сдвигом; смещение <= 2^-32, принято ради портируемости.
    pub fn next_below(&mut self, bound: u32) -> u32 {
        ((u64::from(self.next_u32()) * u64::from(bound)) >> 32) as u32
    }

    /// `[min_inclusive, max_exclusive)`; как в C#, разность считается в `i32` с обёрткой.
    pub fn next_int(&mut self, min_inclusive: i32, max_exclusive: i32) -> i32 {
        let span = max_exclusive.wrapping_sub(min_inclusive) as u32;
        min_inclusive.wrapping_add(self.next_below(span) as i32)
    }

    /// `[0, 1)`, 24 бита мантиссы.
    pub fn next_float01(&mut self) -> f32 {
        (self.next_u32() >> 8) as f32 * (1.0_f32 / 16_777_216.0_f32)
    }

    pub fn next_range(&mut self, min: f32, max: f32) -> f32 {
        // Два отдельных действия без FMA: min + ((max - min) * f).
        min + ((max - min) * self.next_float01())
    }
}
