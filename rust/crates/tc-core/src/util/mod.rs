//! Порт `Sim.Core/Util`: PRNG, хэши, вектор, float-хелперы, фиксированные контейнеры.

pub mod detmath;
pub mod fixed_hash_map;
pub mod fnv1a64;
pub mod pcg32;
pub mod ring_buffer;
pub mod vec2;
pub mod xxhash64;

pub use fixed_hash_map::FixedHashMap;
pub use fnv1a64::Fnv1a64;
pub use pcg32::{Pcg32, RngStream};
pub use ring_buffer::RingBuffer;
pub use vec2::Vec2;
pub use xxhash64::XxHash64;
