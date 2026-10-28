//! Сверка `tc-core::util` с C# побитово: вектора в `vectors/mod.rs` сгенерированы из `Sim.Core/Util` (R1; общий golden `util.json` - R2).

mod vectors;

use tc_core::util::detmath::{max_net, min_net};
use tc_core::util::{FixedHashMap, Fnv1a64, Pcg32, RingBuffer, RngStream, Vec2, XxHash64};
use vectors::*;

fn xxh_data() -> Vec<u8> {
    (0..4097usize).map(|i| ((i * 31 + 7) & 0xFF) as u8).collect()
}

#[test]
fn pcg32_matches_reference_demo_values() {
    // pcg32-demo: seed 42, stream 54.
    let expected = [0xa15c02b7, 0x7b47f409, 0xba1d3330, 0x83d2f293, 0xbfa4784b, 0xcbed606e];
    let mut rng = Pcg32::create(42, 54);
    for e in expected {
        assert_eq!(rng.next_u32(), e);
    }
}

#[test]
fn pcg32_matches_csharp_vectors() {
    assert_eq!(PCG_VECTORS.len(), 16);
    for v in PCG_VECTORS {
        let ctx = format!("seed={:#x} stream={:#x}", v.seed, v.stream);

        let mut r = Pcg32::create(v.seed, v.stream);
        for (i, e) in v.first16.iter().enumerate() {
            assert_eq!(r.next_u32(), *e, "{ctx} next_u32[{i}]");
        }
        for _ in 16..1000 {
            r.next_u32();
        }
        assert_eq!(r.state(), v.state_after_1000, "{ctx} state");
        assert_eq!(r.inc(), v.inc, "{ctx} inc");

        let mut r = Pcg32::create(v.seed, v.stream);
        for (i, e) in v.float01_bits.iter().enumerate() {
            assert_eq!(r.next_float01().to_bits(), *e, "{ctx} float01[{i}]");
        }
        let mut r = Pcg32::create(v.seed, v.stream);
        for (i, e) in v.below7.iter().enumerate() {
            assert_eq!(r.next_below(7), *e, "{ctx} below7[{i}]");
        }
        let mut r = Pcg32::create(v.seed, v.stream);
        for (i, e) in v.range_bits.iter().enumerate() {
            assert_eq!(r.next_range(-3.5, 12.25).to_bits(), *e, "{ctx} range[{i}]");
        }
        let mut r = Pcg32::create(v.seed, v.stream);
        for (i, e) in v.int_range.iter().enumerate() {
            assert_eq!(r.next_int(-5, 100), *e, "{ctx} int_range[{i}]");
        }
    }
}

#[test]
fn pcg32_from_state_and_stream() {
    let mut a = Pcg32::create(7, RngStream::Routing as u64);
    assert_eq!(a, Pcg32::for_stream(7, RngStream::Routing));
    a.next_u32();
    let mut c = Pcg32::from_state(a.state(), a.inc());
    assert_eq!(a.next_u32(), c.next_u32());
    assert_eq!(RngStream::Routing as u64, 6);
    assert_eq!(RngStream::CityGen as u64, 1);
}

#[test]
fn fnv1a64_matches_csharp_vectors() {
    assert_eq!(Fnv1a64::new().value(), FNV_EMPTY);
    assert_eq!(Fnv1a64::default().value(), Fnv1a64::OFFSET);

    let mut h = Fnv1a64::new();
    for b in FNV_BYTES {
        h.add_u8(b);
    }
    assert_eq!(h.value(), FNV_BYTES_HASH);
    let mut h = Fnv1a64::new();
    h.add_bytes(&FNV_BYTES);
    assert_eq!(h.value(), FNV_BYTES_HASH);

    for (i, v) in FNV_INTS.iter().enumerate() {
        let mut h = Fnv1a64::new();
        h.add_i32(*v);
        assert_eq!(h.value(), FNV_INT_HASHES[i], "i32[{i}]");
        let mut h = Fnv1a64::new();
        h.add_u32(*v as u32);
        assert_eq!(h.value(), FNV_UINT_HASHES[i], "u32[{i}]");
    }
    for (i, v) in FNV_LONGS.iter().enumerate() {
        let mut h = Fnv1a64::new();
        h.add_i64(*v);
        assert_eq!(h.value(), FNV_LONG_HASHES[i], "i64[{i}]");
        let mut h = Fnv1a64::new();
        h.add_u64(*v as u64);
        assert_eq!(h.value(), FNV_ULONG_HASHES[i], "u64[{i}]");
    }
    for (i, bits) in FNV_FLOAT_BITS.iter().enumerate() {
        let mut h = Fnv1a64::new();
        h.add_f32(f32::from_bits(*bits));
        assert_eq!(h.value(), FNV_FLOAT_HASHES[i], "f32[{i}] bits={bits:#x}");
    }
}

#[test]
fn xxh64_matches_csharp_vectors_oneshot_and_streaming() {
    let data = xxh_data();
    for (si, seed) in XXH_SEEDS.iter().enumerate() {
        for (li, len) in XXH_LENS.iter().enumerate() {
            let want = XXH_HASHES[si][li];
            assert_eq!(XxHash64::hash(&data[..*len], *seed), want, "oneshot seed={seed:#x} len={len}");
            for chunk in [1usize, 7, 32] {
                let mut s = XxHash64::new(*seed);
                for c in data[..*len].chunks(chunk) {
                    s.append(c);
                }
                assert_eq!(s.value(), want, "stream chunk={chunk} seed={seed:#x} len={len}");
            }
        }
    }
}

#[test]
fn xxh64_known_empty_values() {
    // Публичные эталоны XXH64 (xxhash.com): пустая строка.
    assert_eq!(XxHash64::hash(&[], 0), 0xEF46DB3751D8E999);
}

#[test]
fn xxh64_value_does_not_consume_state() {
    let data = xxh_data();
    let mut s = XxHash64::new(0);
    s.append(&data[..100]);
    let mid = s.value();
    assert_eq!(mid, XxHash64::hash(&data[..100], 0));
    s.append(&data[100..1000]);
    assert_eq!(s.value(), XxHash64::hash(&data[..1000], 0));
}

#[test]
fn min_max_match_dotnet_mathf() {
    assert_eq!(MINMAX.len(), 49);
    for (x, y, mx, mn) in MINMAX {
        let (xf, yf) = (f32::from_bits(*x), f32::from_bits(*y));
        assert_eq!(max_net(xf, yf).to_bits(), *mx, "max({x:#x}, {y:#x})");
        assert_eq!(min_net(xf, yf).to_bits(), *mn, "min({x:#x}, {y:#x})");
    }
}

#[test]
fn min_max_signed_zero_semantics() {
    assert_eq!(max_net(-0.0, 0.0).to_bits(), 0.0f32.to_bits());
    assert_eq!(max_net(0.0, -0.0).to_bits(), 0.0f32.to_bits());
    assert_eq!(min_net(-0.0, 0.0).to_bits(), (-0.0f32).to_bits());
    assert_eq!(min_net(0.0, -0.0).to_bits(), (-0.0f32).to_bits());
    assert!(max_net(f32::NAN, 1.0).is_nan());
    assert!(max_net(1.0, f32::NAN).is_nan());
    assert!(min_net(f32::NAN, 1.0).is_nan());
    assert!(min_net(1.0, f32::NAN).is_nan());
}

#[test]
fn vec2_ops() {
    let a = Vec2::new(1.5, -2.0);
    let b = Vec2::new(0.25, 4.0);
    assert_eq!(a + b, Vec2::new(1.75, 2.0));
    assert_eq!(a - b, Vec2::new(1.25, -6.0));
    assert_eq!(a * 2.0, Vec2::new(3.0, -4.0));
    assert_eq!(std::mem::size_of::<Vec2>(), 8);
}

#[test]
fn fixed_hash_map_matches_csharp_trace() {
    let mut m = FixedHashMap::new(64);
    for (i, (op, key, val, res)) in MAP_TRACE.iter().enumerate() {
        match op {
            0 => assert_eq!(i32::from(m.try_set(*key, *val)), *res, "op#{i} set"),
            1 => assert_eq!(i32::from(m.remove(*key)), *res, "op#{i} remove"),
            _ => assert_eq!(m.get(*key).unwrap_or(i32::MIN), *res, "op#{i} get"),
        }
    }
    assert_eq!(m.len(), MAP_FINAL_COUNT);
}

#[test]
fn fixed_hash_map_basics() {
    let mut m = FixedHashMap::new(2);
    assert!(m.try_set(1, 1));
    assert!(m.try_set(2, 2));
    assert!(!m.try_set(3, 3));
    assert!(m.try_set(2, 22));
    assert_eq!(m.get(2), Some(22));
    assert!(m.remove(1));
    assert!(!m.remove(1));
    assert!(!m.contains_key(1));
    assert_eq!(m.len(), 1);
    m.clear();
    assert!(m.is_empty());
}

#[test]
fn ring_buffer_fifo_wrap_and_remove() {
    let mut r: RingBuffer<i32> = RingBuffer::new(4);
    assert!(r.is_empty());
    for i in 0..4 {
        assert!(r.try_enqueue(i));
    }
    assert!(r.is_full());
    assert!(!r.try_enqueue(99));
    assert_eq!(r.try_dequeue(), Some(0));
    assert_eq!(r.try_dequeue(), Some(1));
    assert!(r.try_enqueue(4));
    assert!(r.try_enqueue(5)); // обёртка: физически [4, 5, 2, 3]
    assert_eq!(r.try_peek(), Some(2));
    assert_eq!(*r.at(0), 2);
    assert_eq!(*r.at(3), 5);
    r.set(1, 30);
    r.remove_at(1); // убираем 30
    assert_eq!(r.len(), 3);
    assert_eq!((*r.at(0), *r.at(1), *r.at(2)), (2, 4, 5));
    r.remove_at(2);
    r.remove_at(0);
    assert_eq!(r.try_dequeue(), Some(4));
    assert_eq!(r.try_dequeue(), None);
    assert_eq!(r.capacity(), 4);
    r.clear();
    assert!(r.is_empty());
}
