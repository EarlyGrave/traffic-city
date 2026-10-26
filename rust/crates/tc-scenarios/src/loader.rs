//! Загрузчик подмножества сценария: JSON -> `ScenarioDesc` (rust-pilot.md §5.1, §6.4, §10 R3).
//!
//! Подмножество: `format_version: 2`, `city.enabled = false`, явная сеть, ёмкости. Как C# `ScenarioLoader`:
//! неизвестные ключи в разбираемых объектах (корень, `network`, узлы, сегменты, `capacities`) - ошибка; дубликаты ключей - ошибка;
//! дефолты ёмкостей и диапазоны те же. Файлы типов ТС не читаются (загрузчик без I/O): `vehicle_types_file` - только строка.
//! Секции `city`, `vehicles`, `demand`, `peds`, `routing` разбираются выборочно (нужные ядру пилота ключи), остальные ключи в них
//! пропускаются; `transit`, `rules`, `lod` пропускаются целиком. Режимы вне подмножества (`city.enabled != false`, `peds.enabled`,
//! `demand.model = "residents"`) - ошибка `unsupported`, а не молчаливый пропуск.

use std::fmt;

use tc_core::api::desc::{NetworkDesc, NodeControl, NodeDesc, RoadKind, ScenarioDesc, SegmentDesc, SimCapacities};

use crate::json::{self, Json};

/// Ошибка загрузки; `path` - JSONPath-подобный путь (`$.network.nodes[2].x`), `message` - причина. Тексты не сверяются с C#.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LoadError {
    pub path: String,
    pub message: String,
}

impl LoadError {
    fn new(path: &str, message: impl Into<String>) -> Self {
        Self {
            path: path.to_string(),
            message: message.into(),
        }
    }
}

impl fmt::Display for LoadError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{}: {}", self.path, self.message)
    }
}

impl std::error::Error for LoadError {}

type Res<T> = Result<T, LoadError>;

const SUPPORTED_FORMAT_VERSION: i32 = 2;
const MAX_ROUTING_LEVELS: i32 = 4;
const MAX_ARENA_STEPS: i64 = 1 << 28;
const PED_CAPACITY_KEYS: [&str; 3] = ["max_peds", "max_residents", "max_ped_path_steps"];
const CITY_CAPACITY_KEYS: [&str; 4] = ["max_lots", "max_buildings", "max_city_residents", "skim_obs_capacity"];
const TOWN_SECTION_KEYS: [&str; 5] = ["calendar", "zoning", "residents", "economy", "choice"];

/// Читатель объекта с учётом прочитанных ключей (`JsonObjectReader`).
struct Obj<'a> {
    fields: &'a [(String, Json)],
    used: Vec<bool>,
    path: String,
}

impl<'a> Obj<'a> {
    fn new(v: &'a Json, path: &str) -> Res<Self> {
        let Json::Obj(fields) = v else {
            return Err(LoadError::new(path, "expected object"));
        };
        for (i, (k, _)) in fields.iter().enumerate() {
            if fields[..i].iter().any(|(k2, _)| k2 == k) {
                return Err(LoadError::new(&format!("{path}.{k}"), "duplicate key"));
            }
        }
        Ok(Self {
            fields,
            used: vec![false; fields.len()],
            path: path.to_string(),
        })
    }

    fn child(&self, key: &str) -> String {
        format!("{}.{}", self.path, key)
    }

    fn get(&mut self, key: &str) -> Option<&'a Json> {
        let i = self.fields.iter().position(|(k, _)| k == key)?;
        self.used[i] = true;
        Some(&self.fields[i].1)
    }

    fn has(&self, key: &str) -> bool {
        self.fields.iter().any(|(k, _)| k == key)
    }

    fn missing(&self, key: &str) -> LoadError {
        LoadError::new(&self.child(key), "required key is missing")
    }

    fn finish(&self) -> Res<()> {
        for (i, (k, _)) in self.fields.iter().enumerate() {
            if !self.used[i] {
                return Err(LoadError::new(&self.child(k), "unknown key"));
            }
        }
        Ok(())
    }

    fn opt_obj(&mut self, key: &str) -> Res<Option<Obj<'a>>> {
        let path = self.child(key);
        match self.get(key) {
            None => Ok(None),
            Some(v) => Ok(Some(Obj::new(v, &path)?)),
        }
    }

    fn req_arr(&mut self, key: &str) -> Res<&'a [Json]> {
        let path = self.child(key);
        match self.get(key) {
            None => Err(self.missing(key)),
            Some(Json::Arr(a)) => Ok(a),
            Some(_) => Err(LoadError::new(&path, "expected array")),
        }
    }

    fn str_(&mut self, key: &str, def: Option<&str>) -> Res<String> {
        let path = self.child(key);
        match self.get(key) {
            None => def.map(str::to_string).ok_or_else(|| self.missing(key)),
            Some(Json::Str(s)) => Ok(s.clone()),
            Some(_) => Err(LoadError::new(&path, "expected string")),
        }
    }

    fn bool_(&mut self, key: &str, def: bool) -> Res<bool> {
        let path = self.child(key);
        match self.get(key) {
            None => Ok(def),
            Some(Json::Bool(b)) => Ok(*b),
            Some(_) => Err(LoadError::new(&path, "expected boolean")),
        }
    }

    /// Число (`double`); `def = None` - обязательное.
    fn num(&mut self, key: &str, def: Option<f64>, min: f64, max: f64) -> Res<f64> {
        let path = self.child(key);
        match self.get(key) {
            None => def.ok_or_else(|| self.missing(key)),
            Some(v) => num_value(v, &path, min, max),
        }
    }

    fn int(&mut self, key: &str, def: Option<i32>, min: i32, max: i32) -> Res<i32> {
        let path = self.child(key);
        match self.get(key) {
            None => def.ok_or_else(|| self.missing(key)),
            Some(v) => int_value(v, &path, min, max),
        }
    }

    /// `uint64` - числом или десятичной строкой (только цифры).
    fn ulong(&mut self, key: &str) -> Res<u64> {
        let path = self.child(key);
        match self.get(key) {
            None => Err(self.missing(key)),
            Some(Json::Str(s)) => {
                if !s.is_empty() && s.bytes().all(|b| b.is_ascii_digit()) {
                    s.parse::<u64>()
                        .map_err(|_| LoadError::new(&path, "expected unsigned 64-bit integer (decimal string)"))
                } else {
                    Err(LoadError::new(&path, "expected unsigned 64-bit integer (decimal string)"))
                }
            }
            Some(v) => v.as_u64().ok_or_else(|| LoadError::new(&path, "expected unsigned 64-bit integer")),
        }
    }
}

fn num_value(v: &Json, path: &str, min: f64, max: f64) -> Res<f64> {
    let x = v.as_f64().ok_or_else(|| LoadError::new(path, "expected number"))?;
    if x < min || x > max {
        return Err(LoadError::new(path, format!("value {x} out of range [{min}, {max}]")));
    }
    Ok(x)
}

fn int_value(v: &Json, path: &str, min: i32, max: i32) -> Res<i32> {
    let x = v.as_i32().ok_or_else(|| LoadError::new(path, "expected integer"))?;
    if x < min || x > max {
        return Err(LoadError::new(path, format!("value {x} out of range [{min}, {max}]")));
    }
    Ok(x)
}

/// `Mps(kmh) = (float)(kmh / 3.6)`: деление в `f64`, затем одно округление до `f32`.
fn mps(kmh: f64) -> f32 {
    (kmh / 3.6) as f32
}

/// Разбирает текст сценария. `Ok` - подмножество разобрано (соответствует `load_ok: true` в golden), `Err` - `load_ok: false`.
pub fn load_str(text: &str) -> Res<ScenarioDesc> {
    let doc = json::parse(text).map_err(|e| LoadError::new("$", format!("invalid JSON: {e}")))?;
    let mut root = Obj::new(&doc, "$")?;

    let version = match root.get("format_version") {
        None => return Err(root.missing("format_version")),
        Some(v) => int_value(v, "$.format_version", i32::MIN, i32::MAX)?,
    };
    if version != SUPPORTED_FORMAT_VERSION {
        return Err(LoadError::new(
            "$.format_version",
            format!("unsupported version {version}, this loader supports {SUPPORTED_FORMAT_VERSION}"),
        ));
    }

    root.str_("name", Some(""))?;
    let seed = root.ulong("seed")?;
    root.num("duration_s", None, 0.0, f64::INFINITY)?;
    root.str_("vehicle_types_file", Some(""))?;

    // city: генератор вне подмножества, нужна явная сеть.
    let city_enabled = match root.opt_obj("city")? {
        None => true,
        Some(mut c) => c.bool_("enabled", true)?,
    };
    if city_enabled {
        return Err(LoadError::new(
            "$.city.enabled",
            "unsupported: the subset needs city.enabled = false (explicit network)",
        ));
    }
    let mut net_obj = root.opt_obj("network")?.ok_or_else(|| root.missing("network"))?;
    let network = parse_network(&mut net_obj)?;

    // vehicles: обязательная секция; остальное (type_mix и т.д.) относится к типам ТС и пропускается.
    let mut veh = root.opt_obj("vehicles")?.ok_or_else(|| root.missing("vehicles"))?;
    veh.int("count", None, 0, i32::MAX)?;
    veh.num("spawn_ramp_s", Some(120.0), 0.0, f64::INFINITY)?;

    if let Some(mut d) = root.opt_obj("demand")? {
        let model = d.str_("model", Some("gravity"))?;
        match model.as_str() {
            "gravity" | "uniform" => {}
            "residents" => return Err(LoadError::new("$.demand.model", "unsupported: demand.model = \"residents\"")),
            _ => {
                return Err(LoadError::new(
                    "$.demand.model",
                    "expected \"uniform\", \"gravity\" or \"residents\"",
                ));
            }
        }
    }
    if let Some(mut p) = root.opt_obj("peds")?
        && p.bool_("enabled", true)?
    {
        return Err(LoadError::new("$.peds", "unsupported: enabled pedestrians"));
    }
    root.get("transit");
    root.get("rules");
    root.get("lod");
    let levels = match root.opt_obj("routing")? {
        None => 1,
        Some(mut r) => r.int("levels", Some(1), 0, MAX_ROUTING_LEVELS)?,
    };
    for key in TOWN_SECTION_KEYS {
        if root.has(key) {
            return Err(LoadError::new(&root.child(key), "allowed only with demand.model = \"residents\""));
        }
    }

    let mut cap_obj = root.opt_obj("capacities")?.ok_or_else(|| root.missing("capacities"))?;
    let capacities = parse_capacities(&mut cap_obj, levels)?;
    root.finish()?;

    Ok(ScenarioDesc { seed, capacities, network })
}

fn parse_network(r: &mut Obj<'_>) -> Res<NetworkDesc> {
    let nodes_j = r.req_arr("nodes")?;
    let segs_j = r.req_arr("segments")?;
    r.finish()?;

    let np = r.child("nodes");
    let mut ids: Vec<&str> = Vec::with_capacity(nodes_j.len());
    let mut nodes = Vec::with_capacity(nodes_j.len());
    for (i, nj) in nodes_j.iter().enumerate() {
        let p = format!("{np}[{i}]");
        let mut n = Obj::new(nj, &p)?;
        let id = match n.get("id") {
            None => return Err(n.missing("id")),
            Some(Json::Str(s)) => s.as_str(),
            Some(_) => return Err(LoadError::new(&n.child("id"), "expected string")),
        };
        if ids.contains(&id) {
            return Err(LoadError::new(&n.child("id"), format!("duplicate node id '{id}'")));
        }
        ids.push(id);
        let x = n.num("x", None, f64::NEG_INFINITY, f64::INFINITY)? as f32;
        let z = n.num("z", None, f64::NEG_INFINITY, f64::INFINITY)? as f32;
        let control = match n.str_("control", None)?.as_str() {
            "none" => NodeControl::None,
            "yield" => NodeControl::Yield,
            "stop" => NodeControl::Stop,
            "signal" => NodeControl::Signal,
            "right_hand" => NodeControl::RightHandRule,
            _ => {
                return Err(LoadError::new(
                    &n.child("control"),
                    "expected \"none\", \"yield\", \"stop\", \"signal\" or \"right_hand\"",
                ));
            }
        };
        n.finish()?;
        nodes.push(NodeDesc { x, z, control });
    }

    let sp = r.child("segments");
    let mut segments = Vec::with_capacity(segs_j.len());
    for (i, sj) in segs_j.iter().enumerate() {
        let p = format!("{sp}[{i}]");
        let mut s = Obj::new(sj, &p)?;
        let a = node_ref(&mut s, "a", &ids)?;
        let b = node_ref(&mut s, "b", &ids)?;
        let lanes_ab = s.int("lanes_ab", None, 0, 16)?;
        let lanes_ba = s.int("lanes_ba", None, 0, 16)?;
        if lanes_ab + lanes_ba == 0 {
            return Err(LoadError::new(&p, "lanes_ab + lanes_ba must be > 0"));
        }
        let speed_limit = mps(s.num("speed_kmh", None, 1.0, f64::INFINITY)?);
        let kind = match s.str_("kind", None)?.as_str() {
            "street" => RoadKind::Street,
            "arterial" => RoadKind::Arterial,
            _ => return Err(LoadError::new(&s.child("kind"), "expected \"street\" or \"arterial\"")),
        };
        s.finish()?;
        segments.push(SegmentDesc {
            a,
            b,
            lanes_ab,
            lanes_ba,
            speed_limit,
            kind,
        });
    }

    Ok(NetworkDesc { nodes, segments })
}

fn node_ref(s: &mut Obj<'_>, key: &str, ids: &[&str]) -> Res<i32> {
    let id = s.str_(key, None)?;
    match ids.iter().position(|x| *x == id) {
        Some(i) => Ok(i as i32),
        None => Err(LoadError::new(&s.child(key), format!("unknown node id '{id}'"))),
    }
}

fn parse_capacities(r: &mut Obj<'_>, routing_levels: i32) -> Res<SimCapacities> {
    let mut c = SimCapacities {
        max_vehicles: r.int("max_vehicles", None, 1, i32::MAX)?,
        max_nodes: r.int("max_nodes", None, 1, i32::MAX)?,
        max_segments: r.int("max_segments", None, 1, i32::MAX)?,
        max_lanes: r.int("max_lanes", None, 1, i32::MAX)?,
        max_lane_points: r.int("max_lane_points", None, 1, i32::MAX)?,
        max_zones: r.int("max_zones", None, 1, i32::MAX)?,
        max_bus_routes: r.int("max_bus_routes", None, 0, i32::MAX)?,
        max_bus_stops: r.int("max_bus_stops", None, 0, i32::MAX)?,
        max_bus_stops_per_route: r.int("max_bus_stops_per_route", Some(32), 1, i32::MAX)?,
        max_routes: r.int("max_routes", None, 1, i32::MAX)?,
        max_pending_trips: r.int("max_pending_trips", None, 1, i32::MAX)?,
        command_queue_capacity: r.int("command_queue", Some(1024), 1, i32::MAX)?,
        event_capacity: r.int("events", Some(4096), 1, i32::MAX)?,
        max_signalized_nodes: r.int("max_signalized_nodes", None, 0, i32::MAX)?,
        ..SimCapacities::default()
    };
    // max_plan_steps: дефолт max_routes * (12 + 8 * levels), предел арены 1 << 28 (как в C#).
    let plan_default = (i64::from(c.max_routes) * (12 + 8 * i64::from(routing_levels.max(1)))).min(MAX_ARENA_STEPS);
    c.max_plan_steps = r.int("max_plan_steps", Some(plan_default as i32), 1, MAX_ARENA_STEPS as i32)?;
    let conn_default = c
        .max_nodes
        .checked_mul(16)
        .ok_or_else(|| LoadError::new(&r.child("max_connectors"), "default overflows int"))?;
    c.max_connectors = r.int("max_connectors", Some(conn_default), 1, i32::MAX)?;
    let confl_default = c
        .max_connectors
        .checked_mul(6)
        .ok_or_else(|| LoadError::new(&r.child("max_conflicts"), "default overflows int"))?;
    c.max_conflicts = r.int("max_conflicts", Some(confl_default), 1, i32::MAX)?;

    // Жители-пешеходы и режим жителей вне подмножества: их ключи ёмкостей недопустимы (как в C# при выключенных секциях).
    for key in PED_CAPACITY_KEYS {
        if r.has(key) {
            return Err(LoadError::new(&r.child(key), "allowed only with an enabled $.peds section"));
        }
    }
    for key in CITY_CAPACITY_KEYS {
        if r.has(key) {
            return Err(LoadError::new(&r.child(key), "allowed only with demand.model = \"residents\""));
        }
    }
    r.finish()?;
    Ok(c)
}
