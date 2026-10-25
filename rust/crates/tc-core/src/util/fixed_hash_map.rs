//! Открытая хэш-таблица `i64 -> i32` фиксированной ёмкости. Порт `Util/FixedHashMap.cs` один в один:
//! линейное пробирование, удаление со сдвигом назад (Knuth, алгоритм R), размер = степень двойки >= 2 * `max_entries`.
//! Не растёт: `try_set` возвращает `false`, когда достигнут `max_entries`.

#[derive(Clone, Debug)]
pub struct FixedHashMap {
    keys: Vec<i64>,
    values: Vec<i32>,
    used: Vec<bool>,
    mask: usize,
    count: usize,
    max_entries: usize,
}

/// Фиксированный смешивающий хэш (SplitMix64 finalizer), не зависит от платформы.
fn hash(key: i64) -> usize {
    let mut x = key as u64;
    x ^= x >> 30;
    x = x.wrapping_mul(0xbf58_476d_1ce4_e5b9);
    x ^= x >> 27;
    x = x.wrapping_mul(0x94d0_49bb_1331_11eb);
    x ^= x >> 31;
    (x & 0x7fff_ffff) as usize
}

impl FixedHashMap {
    pub fn new(max_entries: usize) -> Self {
        debug_assert!(max_entries > 0);
        let mut size = 4usize;
        while size < max_entries * 2 {
            size <<= 1;
        }
        Self {
            keys: vec![0; size],
            values: vec![0; size],
            used: vec![false; size],
            mask: size - 1,
            count: 0,
            max_entries,
        }
    }

    pub fn max_entries(&self) -> usize {
        self.max_entries
    }

    pub fn len(&self) -> usize {
        self.count
    }

    pub fn is_empty(&self) -> bool {
        self.count == 0
    }

    pub fn get(&self, key: i64) -> Option<i32> {
        let mut i = hash(key) & self.mask;
        while self.used[i] {
            if self.keys[i] == key {
                return Some(self.values[i]);
            }
            i = (i + 1) & self.mask;
        }
        None
    }

    pub fn contains_key(&self, key: i64) -> bool {
        self.get(key).is_some()
    }

    /// Вставка или обновление. `false` = таблица полна (новый ключ не добавлен).
    pub fn try_set(&mut self, key: i64, value: i32) -> bool {
        let mut i = hash(key) & self.mask;
        while self.used[i] {
            if self.keys[i] == key {
                self.values[i] = value;
                return true;
            }
            i = (i + 1) & self.mask;
        }
        if self.count >= self.max_entries {
            return false;
        }
        self.used[i] = true;
        self.keys[i] = key;
        self.values[i] = value;
        self.count += 1;
        true
    }

    pub fn remove(&mut self, key: i64) -> bool {
        let mut i = hash(key) & self.mask;
        while self.used[i] {
            if self.keys[i] == key {
                self.remove_at(i);
                return true;
            }
            i = (i + 1) & self.mask;
        }
        false
    }

    pub fn clear(&mut self) {
        self.used.fill(false);
        self.count = 0;
    }

    fn remove_at(&mut self, mut hole: usize) {
        let mut j = hole;
        loop {
            j = (j + 1) & self.mask;
            if !self.used[j] {
                break;
            }
            let ideal = hash(self.keys[j]) & self.mask;
            let movable = if hole <= j {
                ideal <= hole || ideal > j
            } else {
                ideal <= hole && ideal > j
            };
            if movable {
                self.keys[hole] = self.keys[j];
                self.values[hole] = self.values[j];
                hole = j;
            }
        }
        self.used[hole] = false;
        self.count -= 1;
    }
}
