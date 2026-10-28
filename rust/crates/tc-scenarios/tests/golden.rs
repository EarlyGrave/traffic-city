//! Сверка с C#-golden (`tests/golden/rust-pilot/*.json`, rust-pilot.md §5.3): `load_ok`, `desc_hash` (R3), `create_status`,
//! `network_hash`, `counts` (R4), `conflicts_hash` (R5) на каждом сценарии; `headings_hash` - отдельным тестом (системный atan2, не блокирует).

use std::fs;
use std::path::{Path, PathBuf};

use tc_core::api::status::CreateStatus;
use tc_core::sim::Sim;
use tc_scenarios::json::{self, Json};
use tc_scenarios::{LoadError, desc_hash, load_str, load_with};

/// Загрузка сценария из файла репозитория: внешние файлы (`road_profiles_file`) читаются от каталога сценария, как C# `ScenarioLoader.Load`.
fn load_scenario(scenario: &str) -> Result<tc_core::api::desc::ScenarioDesc, LoadError> {
    let path = repo_root().join(scenario);
    let dir = path.parent().unwrap().to_path_buf();
    let text = fs::read_to_string(&path).unwrap_or_else(|e| panic!("{scenario}: {e}"));
    load_with(&text, &mut |rel: &str| {
        fs::read_to_string(dir.join(rel)).map_err(|e| format!("{rel}: {e}"))
    })
}

fn repo_root() -> PathBuf {
    // rust/crates/tc-scenarios -> корень репозитория
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..")
}

fn golden_dir() -> PathBuf {
    repo_root().join("tests/golden/rust-pilot")
}

fn field<'a>(obj: &'a Json, key: &str) -> Option<&'a Json> {
    match obj {
        Json::Obj(f) => f.iter().find(|(k, _)| k == key).map(|(_, v)| v),
        _ => None,
    }
}

fn golden_files() -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = fs::read_dir(golden_dir())
        .unwrap()
        .map(|e| e.unwrap().path())
        .filter(|p| p.extension().is_some_and(|e| e == "json") && p.file_name().is_some_and(|n| n != "util.json"))
        .collect();
    v.sort();
    v
}

fn hex(g: &Json, key: &str) -> Option<String> {
    match field(g, key) {
        Some(Json::Str(s)) => Some(s.clone()),
        _ => None,
    }
}

fn count(g: &Json, key: &str) -> i32 {
    let Some(c) = field(g, "counts") else { panic!("нет counts") };
    field(c, key).and_then(Json::as_i32).unwrap_or_else(|| panic!("counts.{key}"))
}

fn expected_status(g: &Json) -> i32 {
    field(g, "create_status").and_then(Json::as_i32).expect("create_status")
}

/// `create_status`, `network_hash`, `conflicts_hash`, `counts` против golden.
fn check_create(name: &str, desc: &tc_core::api::desc::ScenarioDesc, g: &Json) {
    let status = expected_status(g);
    match Sim::create(desc) {
        Err(st) => {
            assert_eq!(st as i32, status, "{name}: create_status (Rust: {st:?})");
            assert!(field(g, "network_hash").is_none(), "{name}: ошибка create, но network_hash есть");
        }
        Ok(sim) => {
            assert_eq!(
                status,
                CreateStatus::Ok as i32,
                "{name}: Rust создал сеть, golden ждёт статус {status}"
            );
            let c = sim.counts();
            let want = [
                ("nodes", c.nodes),
                ("segments", c.segments),
                ("lanes", c.lanes),
                ("lane_points", c.lane_points),
                ("segment_points", c.segment_points),
                ("arms", c.arms),
                ("lane_next", c.lane_next),
                ("connectors", c.connectors),
                ("conflicts", c.conflicts),
            ];
            for (key, got) in want {
                assert_eq!(got, count(g, key), "{name}: counts.{key}");
            }
            let expected = hex(g, "network_hash").unwrap_or_else(|| panic!("{name}: нет network_hash"));
            assert_eq!(format!("{:016x}", sim.network_hash()), expected, "{name}: network_hash");
            let expected = hex(g, "conflicts_hash").unwrap_or_else(|| panic!("{name}: нет conflicts_hash"));
            assert_eq!(format!("{:016x}", sim.conflicts_hash()), expected, "{name}: conflicts_hash");
        }
    }
}

#[test]
fn desc_hash_and_load_ok_match_csharp_golden() {
    let files = golden_files();
    assert!(files.len() >= 19, "ожидали demo-grid + 18 фикстур, нашли {}", files.len());
    let mut checked: Vec<String> = Vec::new();
    for f in files {
        let name = f.file_name().unwrap().to_string_lossy().into_owned();
        let g = json::parse(&fs::read_to_string(&f).unwrap()).unwrap_or_else(|e| panic!("{name}: golden: {e}"));
        assert_eq!(
            field(&g, "golden_version").and_then(Json::as_i32),
            Some(1),
            "{name}: golden_version"
        );
        // Не-сценарные golden (detlogit.json и т.п.) без ключа scenario сверяют другие тесты.
        let Some(Json::Str(scenario)) = field(&g, "scenario") else {
            continue;
        };
        let Some(Json::Bool(load_ok)) = field(&g, "load_ok") else {
            panic!("{name}: нет load_ok")
        };

        let loaded = load_scenario(scenario);
        assert_eq!(
            loaded.is_ok(),
            *load_ok,
            "{name}: load_ok (ошибка загрузчика: {:?})",
            loaded.as_ref().err()
        );
        // Вид ошибки каталога профилей (RoadProfileError) - тот же, что у C#; ключа в golden нет - ошибка не из валидации каталога.
        let want_kind = match field(&g, "profile_error") {
            Some(Json::Str(s)) => Some(s.as_str()),
            _ => None,
        };
        assert_eq!(
            loaded.as_ref().err().and_then(|e| e.profile_error).map(|k| k.name()),
            want_kind,
            "{name}: profile_error"
        );
        if let Ok(desc) = loaded {
            let Some(Json::Str(expected)) = field(&g, "desc_hash") else {
                panic!("{name}: нет desc_hash")
            };
            assert_eq!(format!("{:016x}", desc_hash(&desc)), *expected, "{name}: desc_hash");
            check_create(&name, &desc, &g);
        } else {
            assert!(field(&g, "desc_hash").is_none(), "{name}: load_ok=false, но desc_hash есть");
        }
        checked.push(name);
    }
    assert_eq!(checked.len(), 18, "сценарных golden: {checked:?}");
    for must in [
        "demo-grid.json",
        "err-json.json",
        "err-caps.json",
        "net-big.json",
        "net-skew.json",
        "net-profiles.json",
        "err-profile-width.json",
        "err-profile-nocurb.json",
        "err-profile-lane-order.json",
        "err-profile-lane-width.json",
        "err-profile-mm.json",
        "err-profile-ref.json",
        "err-profile-lanes.json",
    ] {
        assert!(checked.iter().any(|n| n == must), "{must} не проверен");
    }
}

#[test]
fn corrupted_hash_is_detected() {
    let text = fs::read_to_string(repo_root().join("scenarios/demo-grid.json")).unwrap();
    let a = load_str(&text).unwrap();
    let mut b = a.clone();
    b.network.nodes[0].x = f32::from_bits(b.network.nodes[0].x.to_bits() ^ 1);
    assert_ne!(desc_hash(&a), desc_hash(&b));
}

/// `headings_hash` (atan2 системного libm): на Linux x64 ожидается совпадение с C#, на других ОС - нет (rust-pilot.md §6.3);
/// расхождение сообщается, но тест не красный, если не задано `TC_STRICT_HEADINGS=1`.
#[test]
fn headings_hash_matches_csharp_golden_on_linux() {
    let strict = std::env::var("TC_STRICT_HEADINGS").is_ok_and(|v| v == "1");
    let mut mismatches: Vec<String> = Vec::new();
    for f in golden_files() {
        let name = f.file_name().unwrap().to_string_lossy().into_owned();
        let g = json::parse(&fs::read_to_string(&f).unwrap()).unwrap();
        let (Some(Json::Str(scenario)), Some(expected)) = (field(&g, "scenario"), hex(&g, "headings_hash")) else {
            continue;
        };
        let desc = load_scenario(scenario).unwrap();
        let sim = Sim::create(&desc).unwrap();
        let got = format!("{:016x}", sim.headings_hash());
        if got != expected {
            mismatches.push(format!("{name}: rust {got} != c# {expected}"));
        }
    }
    if !mismatches.is_empty() {
        eprintln!("headings_hash расходится (не блокер, §6.3): {mismatches:?}");
        assert!(!strict || !cfg!(target_os = "linux"), "headings_hash: {mismatches:?}");
    }
}
