//! Сверка с C#-golden (`tests/golden/rust-pilot/*.json`, rust-pilot.md §5.3): `load_ok` и `desc_hash` на каждом сценарии.
//! `create_status`, `network_hash` и т.д. сверяет R4+ (нужен `Sim::create`).

use std::fs;
use std::path::{Path, PathBuf};

use tc_scenarios::json::{self, Json};
use tc_scenarios::{desc_hash, load_str};

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

#[test]
fn desc_hash_and_load_ok_match_csharp_golden() {
    let files = golden_files();
    assert!(files.len() >= 10, "ожидали demo-grid + 9 фикстур, нашли {}", files.len());
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

        let text = fs::read_to_string(repo_root().join(scenario)).unwrap_or_else(|e| panic!("{name}: {scenario}: {e}"));
        let loaded = load_str(&text);
        assert_eq!(
            loaded.is_ok(),
            *load_ok,
            "{name}: load_ok (ошибка загрузчика: {:?})",
            loaded.as_ref().err()
        );
        if let Ok(desc) = loaded {
            let Some(Json::Str(expected)) = field(&g, "desc_hash") else {
                panic!("{name}: нет desc_hash")
            };
            assert_eq!(format!("{:016x}", desc_hash(&desc)), *expected, "{name}: desc_hash");
        } else {
            assert!(field(&g, "desc_hash").is_none(), "{name}: load_ok=false, но desc_hash есть");
        }
        checked.push(name);
    }
    assert_eq!(checked.len(), 10, "сценарных golden: {checked:?}");
    for must in ["demo-grid.json", "err-json.json", "err-caps.json", "net-big.json", "net-skew.json"] {
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
