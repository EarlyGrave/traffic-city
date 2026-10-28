//! Каталог профилей дорог (road-profiles.md §1, ADR-0030): плоские данные и валидация. Порт `Api/RoadProfileDesc.cs`.
//!
//! Ширины - целые мм; `profiles = None` в `RoadProfilesDesc` - режим legacy (прежняя геометрия, хэши побитово прежние).

use super::desc::RoadKind;

/// Тип элемента сечения. Значения - как в C# `RoadElemKind` (входят в хэш).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum RoadElemKind {
    Sidewalk = 0,
    Verge = 1,
    Curb = 2,
    Lane = 3,
    Median = 4,
    Bike = 5,
    Parking = 6,
    Tram = 7,
    BusBay = 8,
}

/// Направление полосы относительно направления рисования профиля (A->B).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum RoadLaneDir {
    Forward = 0,
    Backward = 1,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct RoadElemDesc {
    pub kind: RoadElemKind,
    /// Только у `Lane`; у остальных `Forward` и не читается.
    pub dir: RoadLaneDir,
    pub width_mm: i32,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct RoadProfileDesc {
    pub id: String,
    pub kind: RoadKind,
    /// Заявленная ширина: сумма элементов = `width_mm` = `k * module_mm`.
    pub width_mm: i32,
    /// Слева направо по оси A->B.
    pub elements: Vec<RoadElemDesc>,
}

/// `profiles = None` - режим legacy.
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct RoadProfilesDesc {
    pub module_mm: i32,
    /// `None` - режим legacy; индекс профиля = порядок в файле.
    pub profiles: Option<Vec<RoadProfileDesc>>,
}

impl RoadProfilesDesc {
    /// `true` - режим профилей (каталог задан).
    pub fn enabled(&self) -> bool {
        self.profiles.is_some()
    }
}

/// Причина отказа валидации каталога (одинаково в C# и Rust). Имена вариантов - как у C# `RoadProfileError`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum RoadProfileError {
    None = 0,
    NoProfiles,
    TooManyProfiles,
    TooManyElements,
    DuplicateId,
    BadModule,
    BadWidth,
    ElementWidth,
    NoLane,
    TooManyLanes,
    LaneOrder,
    NoCurb,
    DoubleCurb,
    UnknownKind,
}

impl RoadProfileError {
    /// Имя варианта (как `ToString()` у C#-enum; сообщения загрузчика и golden).
    pub fn name(self) -> &'static str {
        match self {
            Self::None => "None",
            Self::NoProfiles => "NoProfiles",
            Self::TooManyProfiles => "TooManyProfiles",
            Self::TooManyElements => "TooManyElements",
            Self::DuplicateId => "DuplicateId",
            Self::BadModule => "BadModule",
            Self::BadWidth => "BadWidth",
            Self::ElementWidth => "ElementWidth",
            Self::NoLane => "NoLane",
            Self::TooManyLanes => "TooManyLanes",
            Self::LaneOrder => "LaneOrder",
            Self::NoCurb => "NoCurb",
            Self::DoubleCurb => "DoubleCurb",
            Self::UnknownKind => "UnknownKind",
        }
    }
}

pub const MAX_PROFILES: usize = 64;
pub const MAX_ELEMENTS: usize = 16;
pub const MAX_LANES_PER_DIR: i32 = 8;
pub const MAX_MODULES: i32 = 8;

/// Допуск ширины элемента, мм (road-profiles.md §1, таблица типов): `(min, max)` включительно.
pub fn width_range(kind: RoadElemKind) -> (i32, i32) {
    match kind {
        RoadElemKind::Sidewalk => (1500, 64000),
        RoadElemKind::Curb => (0, 500),
        RoadElemKind::Lane => (3000, 4000),
        RoadElemKind::Bike | RoadElemKind::Parking | RoadElemKind::Tram | RoadElemKind::BusBay => (1000, 4000),
        RoadElemKind::Verge | RoadElemKind::Median => (0, 64000),
    }
}

/// Проверка каталога. `Err((ошибка, индекс первого плохого профиля))`; индекс -1 - ошибка уровня каталога.
pub fn validate(d: &RoadProfilesDesc) -> Result<(), (RoadProfileError, i32)> {
    let Some(ps) = d.profiles.as_ref().filter(|p| !p.is_empty()) else {
        return Err((RoadProfileError::NoProfiles, -1));
    };
    if ps.len() > MAX_PROFILES {
        return Err((RoadProfileError::TooManyProfiles, -1));
    }
    if d.module_mm <= 0 {
        return Err((RoadProfileError::BadModule, -1));
    }
    for (i, p) in ps.iter().enumerate() {
        let at = i as i32;
        let e = validate_one(p, d.module_mm);
        if e != RoadProfileError::None {
            return Err((e, at));
        }
        if ps[..i].iter().any(|q| q.id == p.id) {
            return Err((RoadProfileError::DuplicateId, at));
        }
    }
    Ok(())
}

fn is_ground(k: RoadElemKind) -> bool {
    matches!(k, RoadElemKind::Sidewalk | RoadElemKind::Verge)
}

fn is_lane_and_ground(a: RoadElemKind, b: RoadElemKind) -> bool {
    (a == RoadElemKind::Lane && is_ground(b)) || (b == RoadElemKind::Lane && is_ground(a))
}

fn validate_one(p: &RoadProfileDesc, module_mm: i32) -> RoadProfileError {
    if p.id.is_empty() {
        return RoadProfileError::DuplicateId;
    }
    let el = &p.elements;
    if el.is_empty() || el.len() > MAX_ELEMENTS {
        return RoadProfileError::TooManyElements;
    }
    if p.width_mm <= 0 || p.width_mm % module_mm != 0 || p.width_mm / module_mm > MAX_MODULES {
        return RoadProfileError::BadWidth;
    }

    let mut sum: i64 = 0;
    let (mut fwd, mut bwd) = (0_i32, 0_i32);
    let mut last_lane: i32 = -1; // индекс последнего элемента-полосы
    let mut last_dir: i32 = -1; // его направление
    let mut saw_forward = false;
    for (i, e) in el.iter().enumerate() {
        let (min, max) = width_range(e.kind);
        if e.width_mm < min || e.width_mm > max {
            return RoadProfileError::ElementWidth;
        }
        sum += i64::from(e.width_mm);
        if e.kind == RoadElemKind::Curb && i > 0 && el[i - 1].kind == RoadElemKind::Curb {
            return RoadProfileError::DoubleCurb;
        }
        if i > 0 && is_lane_and_ground(el[i - 1].kind, e.kind) {
            return RoadProfileError::NoCurb;
        }
        if e.kind != RoadElemKind::Lane {
            continue;
        }
        let dir = e.dir as i32;
        if dir == 0 {
            fwd += 1;
            saw_forward = true;
        } else {
            bwd += 1;
            if saw_forward {
                return RoadProfileError::LaneOrder; // backward правее forward
            }
        }
        if last_lane >= 0 && last_dir == dir && last_lane != i as i32 - 1 {
            return RoadProfileError::LaneOrder; // полосы одного направления не подряд
        }
        last_lane = i as i32;
        last_dir = dir;
    }

    if sum != i64::from(p.width_mm) {
        return RoadProfileError::BadWidth;
    }
    if fwd + bwd == 0 {
        return RoadProfileError::NoLane;
    }
    if fwd > MAX_LANES_PER_DIR || bwd > MAX_LANES_PER_DIR {
        RoadProfileError::TooManyLanes
    } else {
        RoadProfileError::None
    }
}
