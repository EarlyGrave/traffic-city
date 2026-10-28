//! Сверка `tc_core::rules::idm` с C# побитово (R7): `util.json` -> `idm` (10 000 наборов, delta in {4, 2}, гейт);
//! `idm-pow.json` -> `idm_other_delta` (delta вне {2, 4}, `MathF.Pow` = системный libm): отдельный счётчик расхождений, не гейт
//! (строго только при `TC_STRICT_HEADINGS=1`, как `headings_hash`).

use std::fs;
use std::path::{Path, PathBuf};

use tc_core::rules::idm;
use tc_scenarios::json::{self, Json};

fn golden(name: &str) -> Json {
    let path: PathBuf = Path::new(env!("CARGO_MANIFEST_DIR"))
        .join("../../../tests/golden/rust-pilot")
        .join(name);
    json::parse(&fs::read_to_string(&path).unwrap()).unwrap_or_else(|e| panic!("{name}: {e}"))
}

fn field<'a>(obj: &'a Json, key: &str) -> &'a Json {
    match obj {
        Json::Obj(f) => f
            .iter()
            .find(|(k, _)| k == key)
            .map(|(_, v)| v)
            .unwrap_or_else(|| panic!("нет ключа {key}")),
        _ => panic!("не объект"),
    }
}

/// Результат сверки: число строк и индексы расходящихся по `v0` / `a`.
struct Outcome {
    rows: usize,
    bad_v0: Vec<usize>,
    bad_a: Vec<usize>,
}

fn check(section: &Json) -> Outcome {
    let Json::Arr(rows) = field(section, "rows") else { panic!("rows") };
    let mut out = Outcome {
        rows: rows.len(),
        bad_v0: Vec::new(),
        bad_a: Vec::new(),
    };
    for (i, row) in rows.iter().enumerate() {
        let Json::Arr(c) = row else { panic!("строка {i}") };
        assert_eq!(c.len(), 13, "строка {i}");
        let f = |k: usize| {
            f32::from_bits(
                c[k].as_u64()
                    .and_then(|v| u32::try_from(v).ok())
                    .unwrap_or_else(|| panic!("строка {i}[{k}]")),
            )
        };
        let bits = |k: usize| c[k].as_u64().unwrap_or_else(|| panic!("строка {i}[{k}]")) as u32;
        let (amax, b, t, s0, delta, max_speed, factor, limit, v, gap, dv) =
            (f(0), f(1), f(2), f(3), f(4), f(5), f(6), f(7), f(8), f(9), f(10));
        let v0 = idm::desired_speed(max_speed, factor, limit);
        if v0.to_bits() != bits(11) {
            out.bad_v0.push(i);
        }
        // Ускорение считаем от v0 из C# (отдельный сбой v0 не маскирует сверку формулы).
        let a = idm::accel(v, f(11), gap, dv, amax, b, t, s0, delta);
        if a.to_bits() != bits(12) {
            out.bad_a.push(i);
        }
    }
    out
}

#[test]
fn idm_gated_vectors_are_bit_identical() {
    let g = golden("util.json");
    let o = check(field(&g, "idm"));
    assert_eq!(o.rows, 10_000);
    assert!(
        o.bad_v0.is_empty(),
        "desired_speed: {} расхождений, первые {:?}",
        o.bad_v0.len(),
        &o.bad_v0[..o.bad_v0.len().min(5)]
    );
    assert!(
        o.bad_a.is_empty(),
        "accel: {} расхождений, первые {:?}",
        o.bad_a.len(),
        &o.bad_a[..o.bad_a.len().min(5)]
    );
}

#[test]
fn idm_other_delta_mismatch_counter() {
    let g = golden("idm-pow.json");
    let o = check(field(&g, "idm_other_delta"));
    assert_eq!(o.rows, 1_000);
    println!(
        "idm delta not in {{2,4}} (powf): rows={} mismatches v0={} a={}",
        o.rows,
        o.bad_v0.len(),
        o.bad_a.len()
    );
    // desired_speed не зависит от libm - всегда обязан совпасть.
    assert!(o.bad_v0.is_empty(), "desired_speed: {} расхождений", o.bad_v0.len());
    if std::env::var("TC_STRICT_HEADINGS").is_ok_and(|v| v == "1") {
        assert!(o.bad_a.is_empty(), "accel (powf): {} расхождений из {}", o.bad_a.len(), o.rows);
    }
}
