//! XXH64. Порт `Util/XxHash64.cs`: потоковая, `append` любыми кусками даёт тот же результат, что и одиночный `hash`. Не аллоцирует.

const P1: u64 = 11_400_714_785_074_694_791;
const P2: u64 = 14_029_467_366_897_019_727;
const P3: u64 = 1_609_587_929_392_839_161;
const P4: u64 = 9_650_029_242_287_828_579;
const P5: u64 = 2_870_177_450_012_600_261;

#[derive(Clone, Copy, Debug)]
pub struct XxHash64 {
    v1: u64,
    v2: u64,
    v3: u64,
    v4: u64,
    seed: u64,
    total: u64,
    buffered: usize,
    buf: [u8; 32],
}

fn read_u64(s: &[u8]) -> u64 {
    let mut a = [0u8; 8];
    a.copy_from_slice(&s[..8]);
    u64::from_le_bytes(a)
}

fn read_u32(s: &[u8]) -> u32 {
    let mut a = [0u8; 4];
    a.copy_from_slice(&s[..4]);
    u32::from_le_bytes(a)
}

fn round(acc: u64, input: u64) -> u64 {
    acc.wrapping_add(input.wrapping_mul(P2)).rotate_left(31).wrapping_mul(P1)
}

fn merge(h: u64, v: u64) -> u64 {
    (h ^ round(0, v)).wrapping_mul(P1).wrapping_add(P4)
}

impl XxHash64 {
    pub fn new(seed: u64) -> Self {
        Self {
            v1: seed.wrapping_add(P1).wrapping_add(P2),
            v2: seed.wrapping_add(P2),
            v3: seed,
            v4: seed.wrapping_sub(P1),
            seed,
            total: 0,
            buffered: 0,
            buf: [0; 32],
        }
    }

    /// Хэш куска одним вызовом.
    pub fn hash(data: &[u8], seed: u64) -> u64 {
        let mut s = Self::new(seed);
        s.append(data);
        s.value()
    }

    pub fn append(&mut self, mut data: &[u8]) {
        self.total = self.total.wrapping_add(data.len() as u64);

        if self.buffered > 0 {
            let take = (32 - self.buffered).min(data.len());
            self.buf[self.buffered..self.buffered + take].copy_from_slice(&data[..take]);
            self.buffered += take;
            data = &data[take..];
            if self.buffered < 32 {
                return;
            }
            let block = self.buf;
            self.stripe(&block);
            self.buffered = 0;
        }

        while data.len() >= 32 {
            self.stripe(data);
            data = &data[32..];
        }

        if !data.is_empty() {
            self.buf[..data.len()].copy_from_slice(data);
            self.buffered = data.len();
        }
    }

    /// Хэш всех добавленных байт; состояние не меняется (можно продолжать добавлять).
    pub fn value(&self) -> u64 {
        let mut h;
        if self.total >= 32 {
            h = self
                .v1
                .rotate_left(1)
                .wrapping_add(self.v2.rotate_left(7))
                .wrapping_add(self.v3.rotate_left(12))
                .wrapping_add(self.v4.rotate_left(18));
            h = merge(h, self.v1);
            h = merge(h, self.v2);
            h = merge(h, self.v3);
            h = merge(h, self.v4);
        } else {
            h = self.seed.wrapping_add(P5);
        }

        h = h.wrapping_add(self.total);

        let mut tail = &self.buf[..self.buffered];
        while tail.len() >= 8 {
            h ^= round(0, read_u64(tail));
            h = h.rotate_left(27).wrapping_mul(P1).wrapping_add(P4);
            tail = &tail[8..];
        }

        if tail.len() >= 4 {
            h ^= u64::from(read_u32(tail)).wrapping_mul(P1);
            h = h.rotate_left(23).wrapping_mul(P2).wrapping_add(P3);
            tail = &tail[4..];
        }

        for &b in tail {
            h ^= u64::from(b).wrapping_mul(P5);
            h = h.rotate_left(11).wrapping_mul(P1);
        }

        h ^= h >> 33;
        h = h.wrapping_mul(P2);
        h ^= h >> 29;
        h = h.wrapping_mul(P3);
        h ^= h >> 32;
        h
    }

    fn stripe(&mut self, s: &[u8]) {
        self.v1 = round(self.v1, read_u64(s));
        self.v2 = round(self.v2, read_u64(&s[8..]));
        self.v3 = round(self.v3, read_u64(&s[16..]));
        self.v4 = round(self.v4, read_u64(&s[24..]));
    }
}
