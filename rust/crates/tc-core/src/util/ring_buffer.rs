//! FIFO кольцевой буфер фиксированной ёмкости. Порт `Util/RingBuffer.cs`. Не растёт; полный буфер отвергает запись.

#[derive(Clone, Debug)]
pub struct RingBuffer<T: Copy + Default> {
    items: Vec<T>,
    head: usize,
    count: usize,
}

impl<T: Copy + Default> RingBuffer<T> {
    /// Единственная аллокация; `capacity > 0`.
    pub fn new(capacity: usize) -> Self {
        debug_assert!(capacity > 0);
        Self {
            items: vec![T::default(); capacity],
            head: 0,
            count: 0,
        }
    }

    pub fn capacity(&self) -> usize {
        self.items.len()
    }

    pub fn len(&self) -> usize {
        self.count
    }

    pub fn is_empty(&self) -> bool {
        self.count == 0
    }

    pub fn is_full(&self) -> bool {
        self.count == self.items.len()
    }

    /// `false` = буфер полон, элемент отброшен.
    pub fn try_enqueue(&mut self, item: T) -> bool {
        if self.count == self.items.len() {
            return false;
        }
        let mut tail = self.head + self.count;
        if tail >= self.items.len() {
            tail -= self.items.len();
        }
        self.items[tail] = item;
        self.count += 1;
        true
    }

    pub fn try_dequeue(&mut self) -> Option<T> {
        if self.count == 0 {
            return None;
        }
        let item = self.items[self.head];
        self.head += 1;
        if self.head == self.items.len() {
            self.head = 0;
        }
        self.count -= 1;
        Some(item)
    }

    pub fn try_peek(&self) -> Option<T> {
        if self.count == 0 { None } else { Some(self.items[self.head]) }
    }

    /// Физический индекс элемента по порядку очереди (0 = голова).
    fn index_of(&self, offset: usize) -> usize {
        debug_assert!(offset < self.count);
        let i = self.head + offset;
        if i >= self.items.len() { i - self.items.len() } else { i }
    }

    /// Элемент по порядку очереди (0 = голова), для хэша и дампа.
    pub fn at(&self, offset: usize) -> &T {
        &self.items[self.index_of(offset)]
    }

    /// Заменяет элемент по порядку очереди (0 = голова).
    pub fn set(&mut self, offset: usize, item: T) {
        let i = self.index_of(offset);
        self.items[i] = item;
    }

    /// Удаляет элемент по порядку очереди, сдвигая хвост к голове (O(len - offset)); порядок остальных сохраняется.
    pub fn remove_at(&mut self, offset: usize) {
        let cap = self.items.len();
        let mut i = self.index_of(offset);
        for _ in offset..self.count - 1 {
            let mut next = i + 1;
            if next == cap {
                next = 0;
            }
            self.items[i] = self.items[next];
            i = next;
        }
        self.count -= 1;
    }

    pub fn clear(&mut self) {
        self.head = 0;
        self.count = 0;
    }
}
