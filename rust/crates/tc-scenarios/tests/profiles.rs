//! Каталог профилей дорог (ADR-0030, road-profiles.md §1, §3): те же проверки и виды ошибок, что у C#; эмиссия полос по профилю, `Mirrored`.

use std::fs;
use std::path::{Path, PathBuf};

use tc_core::api::desc::ScenarioDesc;
use tc_core::api::profiles::RoadProfileError;
use tc_core::network::types::{LaneKind, SEGMENT_MIRRORED};
use tc_core::network::{NetworkStore, RoadProfileStore};
use tc_core::sim::Sim;
use tc_scenarios::{LoadError, load_str, load_with, parse_road_profiles};

fn repo_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../../..")
}

fn fixture_text() -> String {
    fs::read_to_string(repo_root().join("tests/fixtures/road-profiles.fixture.json")).unwrap()
}

/// Каталог из одного профиля `p` (JSON элементов), ширина `width_m`.
fn one(width_m: &str, elements: &str) -> String {
    format!(
        r#"{{ "format_version": 1, "module_m": 8, "profiles": [ {{ "id": "p", "kind": "street", "width_m": {width_m}, "elements": [{elements}] }} ] }}"#
    )
}

const SW: &str = r#"{"type":"sidewalk","w":3}"#;
const VG: &str = r#"{"type":"verge","w":1.25}"#;
const CB: &str = r#"{"type":"curb","w":0.25}"#;
const LB: &str = r#"{"type":"lane","w":3.5,"dir":"backward"}"#;
const LF: &str = r#"{"type":"lane","w":3.5,"dir":"forward"}"#;
const MEDIAN3: &str = r#"{"type":"median","w":3}"#;

fn join(parts: &[&str]) -> String {
    parts.join(",")
}

fn kind_of(text: &str) -> Option<RoadProfileError> {
    parse_road_profiles(text).err().and_then(|e| e.profile_error)
}

fn approx(a: f32, b: f32) -> bool {
    (a - b).abs() < 1e-4
}

#[test]
fn fixture_catalog_loads() {
    let d = parse_road_profiles(&fixture_text()).unwrap();
    assert_eq!(d.module_mm, 8000);
    let ps = d.profiles.unwrap();
    let ids: Vec<&str> = ps.iter().map(|p| p.id.as_str()).collect();
    assert_eq!(ids, ["street_2", "oneway_2", "arterial_4", "arterial_6", "asym_1_2"]);
    assert_eq!(ps[2].width_mm, 24000);
    assert_eq!(ps[2].elements.len(), 11);
}

#[test]
fn valid_single_profile_loads() {
    assert!(parse_road_profiles(&one("16", &join(&[SW, VG, CB, LB, LF, CB, VG, SW]))).is_ok());
}

#[test]
fn validation_error_kinds_match_csharp() {
    let cases: [(&str, String, RoadProfileError); 9] = [
        (
            "sum != width_m",
            one("16", &join(&[SW, VG, CB, LB, LF, CB, VG])),
            RoadProfileError::BadWidth,
        ),
        (
            "не кратно module_m",
            one("12", &join(&[SW, CB, LB, LF, CB, SW])),
            RoadProfileError::BadWidth,
        ),
        (
            "полоса вне допуска",
            one(
                "16",
                &join(&[SW, VG, CB, r#"{"type":"lane","w":4.5,"dir":"backward"}"#, LF, CB, VG, SW]),
            ),
            RoadProfileError::ElementWidth,
        ),
        (
            "нет полос",
            one("16", &join(&[SW, VG, r#"{"type":"median","w":7.5}"#, VG, SW])),
            RoadProfileError::NoLane,
        ),
        (
            "forward левее backward",
            one("16", &join(&[SW, VG, CB, LF, LB, CB, VG, SW])),
            RoadProfileError::LaneOrder,
        ),
        (
            "полосы одного направления не подряд",
            one("24", &join(&[SW, VG, CB, LB, MEDIAN3, LB, CB, VG, SW])),
            RoadProfileError::LaneOrder,
        ),
        (
            "нет бордюра между полосой и газоном",
            one("16", &join(&[SW, r#"{"type":"verge","w":1.5}"#, LB, LF, CB, VG, SW])),
            RoadProfileError::NoCurb,
        ),
        (
            "два бордюра подряд",
            one("16", &join(&[SW, VG, CB, CB, LB, LF, CB, VG, SW])),
            RoadProfileError::DoubleCurb,
        ),
        (
            "пустой каталог",
            r#"{ "format_version": 1, "profiles": [] }"#.to_string(),
            RoadProfileError::NoProfiles,
        ),
    ];
    for (what, text, want) in cases {
        assert_eq!(kind_of(&text), Some(want), "{what}");
    }
}

#[test]
fn too_many_lanes_per_direction() {
    // 9 полос forward (3 м) в 32 м: 9 * 3 = 27 + 2 бордюра 0.5 + 2 тротуара 2 = 32
    let lane = r#"{"type":"lane","w":3,"dir":"forward"}"#;
    let lanes = [lane; 9].join(",");
    let curb = r#"{"type":"curb","w":0.5}"#;
    let sw = r#"{"type":"sidewalk","w":2}"#;
    assert_eq!(
        kind_of(&one("32", &format!("{sw},{curb},{lanes},{curb},{sw}"))),
        Some(RoadProfileError::TooManyLanes)
    );
}

#[test]
fn duplicate_ids_and_too_many_elements() {
    let el = join(&[SW, VG, CB, LB, LF, CB, VG, SW]);
    let p = format!(r#"{{ "id": "p", "kind": "street", "width_m": 16, "elements": [{el}] }}"#);
    let dup = format!(r#"{{ "format_version": 1, "profiles": [ {p}, {p} ] }}"#);
    assert_eq!(kind_of(&dup), Some(RoadProfileError::DuplicateId));

    let many = [r#"{"type":"verge","w":0}"#; 17].join(",");
    assert_eq!(kind_of(&one("16", &many)), Some(RoadProfileError::TooManyElements));
}

#[test]
fn loader_errors_are_not_profile_errors() {
    // дробные мм, неизвестный тип, dir у не-полосы, лишний ключ - ошибки загрузчика, а не валидации каталога
    let frac = one(
        "16",
        &join(&[SW, VG, CB, r#"{"type":"lane","w":3.5004,"dir":"backward"}"#, LF, CB, VG, SW]),
    );
    assert!(parse_road_profiles(&frac).is_err());
    assert_eq!(kind_of(&frac), None);
    assert!(parse_road_profiles(&one("16", r#"{"type":"nope","w":16}"#)).is_err());
    assert!(parse_road_profiles(&one("16", r#"{"type":"verge","w":16,"dir":"forward"}"#)).is_err());
    let extra = fixture_text().replacen("\"module_m\"", "\"extra\": 1, \"module_m\"", 1);
    assert!(parse_road_profiles(&extra).is_err());
}

const SCENARIO_HEAD: &str = r#"{ "format_version": 2, "seed": 1, "duration_s": 1, ROAD
  "city": { "enabled": false },
  "network": { "nodes": [ {"id":"a","x":0,"z":0,"control":"none"}, {"id":"b","x":100,"z":0,"control":"none"} ],
    "segments": [ SEGS ] },
  "vehicles": { "count": 0 },
  "capacities": { "max_vehicles": 8, "max_nodes": 4, "max_segments": 4, "max_lanes": 64, "max_lane_points": 512,
    "max_zones": 1, "max_bus_routes": 0, "max_bus_stops": 0, "max_routes": 10, "max_pending_trips": 8, "max_signalized_nodes": 0 } }"#;

const ROAD: &str = r#""road_profiles_file": "x.json","#;

fn scenario(road: &str, segs: &str) -> String {
    SCENARIO_HEAD.replace("ROAD", road).replace("SEGS", segs)
}

fn load_fixture(text: &str) -> Result<ScenarioDesc, LoadError> {
    let fx = fixture_text();
    load_with(text, &mut |_p: &str| Ok(fx.clone()))
}

#[test]
fn catalog_requires_reader_and_profile_keys_require_catalog() {
    let with_cat = scenario(ROAD, r#"{"a":"a","b":"b","profile":"street_2","speed_kmh":40}"#);
    assert!(
        load_str(&with_cat).is_err(),
        "load_str без читателя файлов не может открыть каталог"
    );
    assert!(load_fixture(&with_cat).is_ok());

    let no_cat = scenario("", r#"{"a":"a","b":"b","profile":"street_2","speed_kmh":40}"#);
    assert!(load_str(&no_cat).is_err(), "profile без каталога");
    let no_cat = scenario(
        "",
        r#"{"a":"a","b":"b","mirrored":true,"lanes_ab":1,"lanes_ba":1,"speed_kmh":40,"kind":"street"}"#,
    );
    assert!(load_str(&no_cat).is_err(), "mirrored без каталога");

    // с каталогом профиль обязателен; явные lanes/kind обязаны совпасть
    let missing = scenario(
        ROAD,
        r#"{"a":"a","b":"b","lanes_ab":1,"lanes_ba":1,"speed_kmh":40,"kind":"street"}"#,
    );
    assert!(load_fixture(&missing).is_err());
    let bad_lanes = scenario(ROAD, r#"{"a":"a","b":"b","profile":"street_2","lanes_ab":2,"speed_kmh":40}"#);
    assert!(load_fixture(&bad_lanes).is_err());
    let bad_kind = scenario(ROAD, r#"{"a":"a","b":"b","profile":"street_2","kind":"arterial","speed_kmh":40}"#);
    assert!(load_fixture(&bad_kind).is_err());
}

fn z_of(n: &NetworkStore, lane: usize) -> f32 {
    n.lane_points[n.lanes[lane].first_point as usize].z
}

#[test]
fn mirrored_swaps_lane_counts_and_mirrors_geometry() {
    let plain = load_fixture(&scenario(ROAD, r#"{"a":"a","b":"b","profile":"asym_1_2","speed_kmh":50}"#)).unwrap();
    let mirr = load_fixture(&scenario(
        ROAD,
        r#"{"a":"a","b":"b","profile":"asym_1_2","mirrored":true,"speed_kmh":50}"#,
    ))
    .unwrap();
    let (p, m) = (plain.network.segments[0], mirr.network.segments[0]);
    assert_eq!((p.lanes_ab, p.lanes_ba, p.profile, p.mirrored), (2, 1, 5, false));
    assert_eq!((m.lanes_ab, m.lanes_ba, m.profile, m.mirrored), (1, 2, 5, true));

    let sp = Sim::create(&plain).unwrap();
    let sm = Sim::create(&mirr).unwrap();
    let (np, nm) = (sp.network(), sm.network());
    assert_eq!(np.segments[0].flags & SEGMENT_MIRRORED, 0);
    assert_eq!(nm.segments[0].flags & SEGMENT_MIRRORED, SEGMENT_MIRRORED);
    assert_eq!((np.segments[0].profile, nm.segments[0].profile), (4, 4));
    for n in [np, nm] {
        for l in &n.lanes[..n.lane_count as usize] {
            let want = if l.kind == LaneKind::Normal { 3.5 } else { 0.0 }; // у connector'ов Width = 0
            assert!(approx(l.width, want), "ширина полосы {}", l.width);
        }
    }

    // Ось по +X, «вправо» = +z. asym_1_2 слева направо: backward 3.5, median 2, forward 3.5 x2, края +-6.25 от оси:
    // центры полос: backward -4.5, forward 1.0 и 4.5 (LaneNo 0 - правая).
    let want_plain = [4.5_f32, 1.0, -4.5];
    let want_mirr = [4.5_f32, -4.5, -1.0];
    for (i, w) in want_plain.iter().enumerate() {
        assert!(approx(z_of(np, i), *w), "plain lane {i}: {}", z_of(np, i));
    }
    for (i, w) in want_mirr.iter().enumerate() {
        assert!(approx(z_of(nm, i), *w), "mirrored lane {i}: {}", z_of(nm, i));
    }
    // Зеркало побитово: backward-полоса plain (A->B №0 у mirrored) - то же смещение с обратным знаком.
    assert_eq!(z_of(np, 2).to_bits(), (-z_of(nm, 0)).to_bits());
}

#[test]
fn derived_curb_and_walk_values() {
    let d = parse_road_profiles(&fixture_text()).unwrap();
    let s = RoadProfileStore::new(&d);
    assert_eq!((s.curb_l_mm[0], s.curb_r_mm[0]), (3500, 3500)); // street_2
    assert_eq!((s.curb_l_mm[2], s.curb_r_mm[2]), (8000, 8000)); // arterial_4: кромка 8 м (road-profiles.md §3.2)
    assert_eq!((s.curb_l_mm[4], s.curb_r_mm[4]), (6250, 6250)); // asym_1_2
    assert_eq!((s.walk_l_mm[0], s.walk_r_mm[0]), (3000, 3000));
    assert_eq!((s.lanes_fwd[4], s.lanes_bwd[4]), (2, 1));
    assert!(approx(s.half_road_m(2), 8.0));
    assert!(approx(s.half_road_m(4), 6.25));
}
