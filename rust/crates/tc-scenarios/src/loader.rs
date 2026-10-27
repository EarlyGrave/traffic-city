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
use tc_core::api::profiles::{self, RoadElemDesc, RoadElemKind, RoadLaneDir, RoadProfileDesc, RoadProfileError, RoadProfilesDesc};

use crate::json::{self, Json};

/// Ошибка загрузки; `path` - JSONPath-подобный путь (`$.network.nodes[2].x`), `message` - причина. Тексты не сверяются с C#,
/// а вид ошибки каталога профилей (`profile_error`) - те же значения, что в C# (`RoadProfileError`).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct LoadError {
    pub path: String,
    pub message: String,
    /// Задан, если каталог профилей разобран, но не прошёл валидацию (road-profiles.md §1 «Валидация»).
    pub profile_error: Option<RoadProfileError>,
}

impl LoadError {
    fn new(path: &str, message: impl Into<String>) -> Self {
        Self {
            path: path.to_string(),
            message: message.into(),
            profile_error: None,
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

/// Читатель внешних файлов сценария: путь как в JSON (`road_profiles_file`) -> текст файла или причина отказа.
/// Загрузчик сам ввода-вывода не делает; относительные пути разрешает вызывающий (от каталога сценария, как C# `readRelativeFile`).
pub type FileReader<'a> = &'a mut dyn FnMut(&str) -> Result<String, String>;

/// Разбирает текст сценария без доступа к файлам: сценарий с `road_profiles_file` даёт ошибку (нужен `load_with`).
/// `Ok` - подмножество разобрано (соответствует `load_ok: true` в golden), `Err` - `load_ok: false`.
pub fn load_str(text: &str) -> Res<ScenarioDesc> {
    load_with(text, &mut |_| Err("file access is not available (use load_with)".to_string()))
}

/// Как `load_str`, но внешний каталог профилей (`road_profiles_file`, ADR-0030) читает `read_file`.
pub fn load_with(text: &str, read_file: FileReader<'_>) -> Res<ScenarioDesc> {
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
    let road_profiles = load_road_profiles(&mut root, read_file)?;

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
    let network = parse_network(&mut net_obj, &road_profiles)?;

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

    Ok(ScenarioDesc {
        seed,
        capacities,
        network,
        road_profiles,
    })
}

const PROFILES_FORMAT_VERSION: i32 = 1;
const DEFAULT_MODULE_MM: i32 = 8000;

/// Метры -> целые мм; нецелое число мм - ошибка (ширины в каталоге целые мм, road-profiles.md §1). Как C# `Mm`.
fn mm(meters: f64, path: &str) -> Res<i32> {
    let v = meters * 1000.0;
    let rounded = v.round(); // от нуля при половине, как MidpointRounding.AwayFromZero
    if (v - rounded).abs() > 1e-6 || rounded > 1_000_000.0 {
        return Err(LoadError::new(
            path,
            format!("value {meters} is not a whole number of millimetres (or too large)"),
        ));
    }
    Ok(rounded as i32)
}

/// Читает `road_profiles_file` (путь передаётся читателю как есть); ключа нет - режим legacy (`profiles = None`).
fn load_road_profiles(root: &mut Obj<'_>, read_file: FileReader<'_>) -> Res<RoadProfilesDesc> {
    if !root.has("road_profiles_file") {
        return Ok(RoadProfilesDesc::default());
    }
    let file = root.str_("road_profiles_file", None)?;
    let text = read_file(&file).map_err(|e| LoadError::new("$.road_profiles_file", e))?;
    parse_road_profiles(&text).map_err(|e| LoadError {
        path: format!("{file}: {}", e.path),
        message: e.message,
        profile_error: e.profile_error,
    })
}

/// Разбор текста каталога профилей (`scenarios/road-profiles.json`, `tests/fixtures/road-profiles.fixture.json`) с валидацией ядра.
/// Те же проверки и тот же вид ошибки, что у C# `ScenarioLoader.ParseRoadProfiles`.
pub fn parse_road_profiles(text: &str) -> Res<RoadProfilesDesc> {
    let doc = json::parse(text).map_err(|e| LoadError::new("$", format!("road profiles: invalid JSON: {e}")))?;
    let mut root = Obj::new(&doc, "$")?;
    match root.get("format_version") {
        None => return Err(root.missing("format_version")),
        Some(v) => {
            let ver = int_value(v, "$.format_version", i32::MIN, i32::MAX)?;
            if ver != PROFILES_FORMAT_VERSION {
                return Err(LoadError::new(
                    "$.format_version",
                    format!("unsupported version {ver}, loader supports {PROFILES_FORMAT_VERSION}"),
                ));
            }
        }
    }
    let module_path = root.child("module_m");
    let module_mm = match root.get("module_m") {
        None => DEFAULT_MODULE_MM,
        Some(v) => mm(num_value(v, &module_path, 0.001, f64::INFINITY)?, &module_path)?,
    };
    let items = root.req_arr("profiles")?;
    let pp = root.child("profiles");
    root.finish()?;

    let mut profiles = Vec::with_capacity(items.len());
    for (i, item) in items.iter().enumerate() {
        let p = format!("{pp}[{i}]");
        let mut r = Obj::new(item, &p)?;
        let id = r.str_("id", None)?;
        let kind = match r.str_("kind", None)?.as_str() {
            "street" => RoadKind::Street,
            "arterial" => RoadKind::Arterial,
            _ => return Err(LoadError::new(&r.child("kind"), "expected \"street\" or \"arterial\"")),
        };
        let wpath = r.child("width_m");
        let width_mm = mm(r.num("width_m", None, 0.001, f64::INFINITY)?, &wpath)?;
        let els = r.req_arr("elements")?;
        let ep = r.child("elements");
        r.finish()?;

        let mut elements = Vec::with_capacity(els.len());
        for (e, ej) in els.iter().enumerate() {
            let q = format!("{ep}[{e}]");
            let mut er = Obj::new(ej, &q)?;
            let kind = match er.str_("type", None)?.as_str() {
                "sidewalk" => RoadElemKind::Sidewalk,
                "verge" => RoadElemKind::Verge,
                "curb" => RoadElemKind::Curb,
                "lane" => RoadElemKind::Lane,
                "median" => RoadElemKind::Median,
                "bike" => RoadElemKind::Bike,
                "parking" => RoadElemKind::Parking,
                "tram" => RoadElemKind::Tram,
                "bus_bay" => RoadElemKind::BusBay,
                _ => return Err(LoadError::new(&er.child("type"), "unknown element type")),
            };
            let w_path = er.child("w");
            let w_mm = mm(er.num("w", None, 0.0, f64::INFINITY)?, &w_path)?;
            let dir = if kind == RoadElemKind::Lane {
                match er.str_("dir", None)?.as_str() {
                    "forward" => RoadLaneDir::Forward,
                    "backward" => RoadLaneDir::Backward,
                    _ => return Err(LoadError::new(&er.child("dir"), "expected \"forward\" or \"backward\"")),
                }
            } else if er.has("dir") {
                return Err(LoadError::new(&er.child("dir"), "allowed only for \"lane\" elements"));
            } else {
                RoadLaneDir::Forward
            };
            er.finish()?;
            elements.push(RoadElemDesc { kind, dir, width_mm: w_mm });
        }
        profiles.push(RoadProfileDesc {
            id,
            kind,
            width_mm,
            elements,
        });
    }

    let desc = RoadProfilesDesc {
        module_mm,
        profiles: Some(profiles),
    };
    if let Err((err, bad)) = profiles::validate(&desc) {
        let at = match (bad, desc.profiles.as_ref()) {
            (b, Some(ps)) if b >= 0 => format!("{pp}[{b}] (\"{}\")", ps[b as usize].id),
            _ => pp,
        };
        return Err(LoadError {
            path: at,
            message: format!("invalid road profile ({})", err.name()),
            profile_error: Some(err),
        });
    }
    Ok(desc)
}

/// Поля сегмента явной сети при каталоге (C# `ParseSegmentProfile`): `profile` (id) обязателен, `mirrored` по умолчанию false;
/// `lanes_ab`/`lanes_ba`/`kind` выводятся из профиля, а если заданы - обязаны совпасть. `Ok(None)` - каталога нет (поля читает прежний код).
fn parse_segment_profile(s: &mut Obj<'_>, profiles: &RoadProfilesDesc) -> Res<Option<SegmentProfileFields>> {
    let Some(ps) = &profiles.profiles else {
        for key in ["profile", "mirrored"] {
            if s.has(key) {
                return Err(LoadError::new(&s.child(key), "requires $.road_profiles_file"));
            }
        }
        return Ok(None);
    };

    let id = s.str_("profile", None)?;
    let Some(p) = ps.iter().position(|x| x.id == id) else {
        return Err(LoadError::new(&s.child("profile"), format!("unknown road profile '{id}'")));
    };
    let mirrored = s.bool_("mirrored", false)?;
    let pd = &ps[p];
    let count = |dir: RoadLaneDir| pd.elements.iter().filter(|e| e.kind == RoadElemKind::Lane && e.dir == dir).count() as i32;
    let (fwd, bwd) = (count(RoadLaneDir::Forward), count(RoadLaneDir::Backward));
    let (lanes_ab, lanes_ba) = if mirrored { (bwd, fwd) } else { (fwd, bwd) };
    if s.has("lanes_ab") && s.int("lanes_ab", None, i32::MIN, i32::MAX)? != lanes_ab {
        return Err(LoadError::new(
            &s.child("lanes_ab"),
            format!("does not match profile '{id}' ({lanes_ab})"),
        ));
    }
    if s.has("lanes_ba") && s.int("lanes_ba", None, i32::MIN, i32::MAX)? != lanes_ba {
        return Err(LoadError::new(
            &s.child("lanes_ba"),
            format!("does not match profile '{id}' ({lanes_ba})"),
        ));
    }
    if s.has("kind") {
        let want = if pd.kind == RoadKind::Street { "street" } else { "arterial" };
        if s.str_("kind", None)? != want {
            return Err(LoadError::new(&s.child("kind"), format!("does not match profile '{id}'")));
        }
    }
    Ok(Some(SegmentProfileFields {
        profile: p as i32 + 1,
        mirrored,
        lanes_ab,
        lanes_ba,
        kind: pd.kind,
    }))
}

struct SegmentProfileFields {
    profile: i32,
    mirrored: bool,
    lanes_ab: i32,
    lanes_ba: i32,
    kind: RoadKind,
}

fn parse_network(r: &mut Obj<'_>, profiles: &RoadProfilesDesc) -> Res<NetworkDesc> {
    let nodes_j = r.req_arr("nodes")?;
    let segs_j = r.req_arr("segments")?;
    // ADR-0028: 0 в JSON - скругление выключено (в desc -1), как C# ParseNetwork.
    let corner = r.num("corner_radius_m", Some(6.0), 0.0, 50.0)?;
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
        if let Some(pf) = parse_segment_profile(&mut s, profiles)? {
            let speed_limit = mps(s.num("speed_kmh", None, 1.0, f64::INFINITY)?);
            s.finish()?;
            segments.push(SegmentDesc {
                a,
                b,
                lanes_ab: pf.lanes_ab,
                lanes_ba: pf.lanes_ba,
                speed_limit,
                kind: pf.kind,
                profile: pf.profile,
                mirrored: pf.mirrored,
            });
            continue;
        }
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
            profile: 0,
            mirrored: false,
        });
    }

    let corner_radius = if corner == 0.0 { -1.0 } else { corner as f32 };
    Ok(NetworkDesc {
        nodes,
        segments,
        corner_radius,
    })
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
