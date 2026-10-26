//! Unit-тесты загрузчика подмножества (дефолты, границы подмножества, ошибки).

use tc_core::api::desc::{NodeControl, RoadKind};
use tc_scenarios::load_str;

const BASE: &str = r#"{
  "format_version": 2, "name": "t", "seed": SEED, "duration_s": 10,
  "city": { "enabled": false },
  "network": {
    "nodes": [ {"id":"a","x":0,"z":0,"control":"none"}, {"id":"b","x":123.456,"z":-0.1,"control":"stop"} ],
    "segments": [ {"a":"a","b":"b","lanes_ab":1,"lanes_ba":0,"speed_kmh":50,"kind":"arterial"} ]
  },
  "vehicles": { "count": 0 },
  EXTRA
  "capacities": { "max_vehicles": 8, "max_nodes": 4, "max_segments": 4, "max_lanes": 16, "max_lane_points": 64,
    "max_zones": 1, "max_bus_routes": 0, "max_bus_stops": 0, "max_routes": 10, "max_pending_trips": 8,
    "max_signalized_nodes": 0 CAPS }
}"#;

fn build(seed: &str, extra: &str, caps: &str) -> String {
    BASE.replace("SEED", seed).replace("EXTRA", extra).replace("CAPS", caps)
}

#[test]
fn loads_minimal_scenario_with_defaults() {
    let d = load_str(&build("1", "", "")).unwrap();
    assert_eq!(d.seed, 1);
    let c = d.capacities;
    assert_eq!(
        (c.max_bus_stops_per_route, c.command_queue_capacity, c.event_capacity),
        (32, 1024, 4096)
    );
    assert_eq!(c.max_plan_steps, 10 * (12 + 8)); // routing по умолчанию: levels = 1
    assert_eq!((c.max_connectors, c.max_conflicts), (64, 384));
    assert_eq!((c.max_peds, c.max_lots, c.skim_obs_capacity), (0, 0, 0));
    assert_eq!(d.network.nodes[1].x, 123.456f32);
    assert_eq!(d.network.nodes[1].z, -0.1f32);
    assert_eq!(d.network.nodes[1].control, NodeControl::Stop);
    assert_eq!(d.network.segments[0].speed_limit, (50.0f64 / 3.6) as f32);
    assert_eq!(d.network.segments[0].kind, RoadKind::Arterial);
}

#[test]
fn routing_levels_change_plan_steps_default() {
    let d = load_str(&build("1", r#""routing": { "levels": 3 },"#, "")).unwrap();
    assert_eq!(d.capacities.max_plan_steps, 10 * (12 + 8 * 3));
    let d = load_str(&build("1", r#""routing": { "levels": 0 },"#, "")).unwrap();
    assert_eq!(d.capacities.max_plan_steps, 10 * 20);
}

#[test]
fn seed_as_number_or_decimal_string() {
    assert_eq!(load_str(&build("18446744073709551615", "", "")).unwrap().seed, u64::MAX);
    assert_eq!(load_str(&build("\"18446744073709551615\"", "", "")).unwrap().seed, u64::MAX);
    for bad in [
        "\"+5\"",
        "\" 5\"",
        "\"\"",
        "\"0x5\"",
        "-1",
        "1.0",
        "1e2",
        "\"18446744073709551616\"",
        "true",
    ] {
        assert!(load_str(&build(bad, "", "")).is_err(), "seed {bad}");
    }
}

#[test]
fn rejects_unknown_and_duplicate_keys_and_out_of_subset() {
    assert!(load_str(&build("1", r#""bogus": 1,"#, "")).is_err());
    assert!(load_str(&build("1", "", r#", "max_vehicles": 9"#)).is_err());
    assert!(load_str(&build("1", "", r#", "bogus": 9"#)).is_err());
    assert!(load_str(&build("1", "", r#", "max_peds": 9"#)).is_err());
    assert!(load_str(&build("1", "", r#", "max_lots": 9"#)).is_err());
    assert!(load_str(&build("1", r#""calendar": {},"#, "")).is_err());
    assert!(load_str(&build("1", r#""peds": { "count": 1 },"#, "")).is_err());
    assert!(load_str(&build("1", r#""demand": { "model": "residents" },"#, "")).is_err());
    assert!(load_str(&build("1", r#""demand": { "model": "bogus" },"#, "")).is_err());
    assert!(load_str(&build("1", r#""routing": { "levels": 5 },"#, "")).is_err());
    // Допустимые в подмножестве секции пропускаются
    let skipped = r#""peds": { "enabled": false }, "transit": { "x": 1 }, "rules": {}, "lod": [1],"#;
    assert!(load_str(&build("1", skipped, "")).is_ok());
    assert!(load_str(&build("1", r#""demand": { "model": "uniform", "zone_m": 200 },"#, "")).is_ok());
}

#[test]
fn rejects_city_generator_and_bad_versions() {
    let s = build("1", "", "");
    assert!(load_str(&s.replace(r#""enabled": false"#, r#""enabled": true"#)).is_err());
    assert!(load_str(&s.replace(r#""city": { "enabled": false },"#, "")).is_err());
    assert!(load_str(&s.replace(r#""format_version": 2"#, r#""format_version": 3"#)).is_err());
    assert!(load_str(&s.replace(r#""format_version": 2,"#, "")).is_err());
    assert!(load_str(&s.replace(r#""format_version": 2"#, r#""format_version": 2.0"#)).is_err());
}

#[test]
fn network_validation() {
    let s = build("1", "", "");
    assert!(load_str(&s.replace(r#""lanes_ba":0"#, r#""lanes_ba":17"#)).is_err());
    assert!(load_str(&s.replace(r#""lanes_ab":1,"lanes_ba":0"#, r#""lanes_ab":0,"lanes_ba":0"#)).is_err());
    assert!(load_str(&s.replace(r#""speed_kmh":50"#, r#""speed_kmh":0.5"#)).is_err());
    assert!(load_str(&s.replace(r#""kind":"arterial""#, r#""kind":"road""#)).is_err());
    assert!(load_str(&s.replace(r#""b":"b""#, r#""b":"zzz""#)).is_err());
    assert!(load_str(&s.replace(r#""id":"b""#, r#""id":"a""#)).is_err());
    assert!(load_str(&s.replace(r#""control":"stop""#, r#""control":"roundabout""#)).is_err());
    assert!(load_str(&s.replace(r#""x":123.456"#, r#""x":1e999"#)).is_err());
    assert!(load_str(&s.replace(r#""max_vehicles": 8"#, r#""max_vehicles": 0"#)).is_err());
}

#[test]
fn syntax_errors_are_load_errors() {
    let s = build("1", "", "");
    assert!(load_str(&s.replace(r#""count": 0 }"#, r#""count": 0, }"#)).is_err());
    assert!(load_str(&s[..s.len() / 2]).is_err());
    assert!(load_str("").is_err());
    assert!(load_str("[]").is_err());
}
