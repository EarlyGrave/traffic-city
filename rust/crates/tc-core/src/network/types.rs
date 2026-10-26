//! Порт `Network/NetworkTypes.cs` и публичных структур сети (`Api/NetworkData.cs`).
//! Поля и их порядок - ровно как в C# (порядок входит в `network_hash`, rust-pilot.md §5.2).

use crate::api::desc::{NodeControl, RoadKind};
use crate::api::status::CreateStatus;
use crate::util::Vec2;

/// Результат операций построения сети. Исключений нет: ёмкости и некорректные аргументы - статусы.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i32)]
pub enum NetworkStatus {
    Ok = 0,
    InvalidArgument = 1,
    SegmentTooShort = 2,
    TooManyArms = 3,
    TooManyConnectors = 4,
    NodeAlreadyBuilt = 5,
    NodesFull = 6,
    SegmentsFull = 7,
    LanesFull = 8,
    PointsFull = 9,
    ConnectorsFull = 10,
    ConflictsFull = 11,
}

impl NetworkStatus {
    /// Отображение на статус `Sim::create` (`NetworkStatusExtensions.ToCreateStatus`): >= `NodesFull` - `NetworkTooLarge`.
    pub fn to_create_status(self) -> CreateStatus {
        match self {
            NetworkStatus::Ok => CreateStatus::Ok,
            s if (s as i32) >= (NetworkStatus::NodesFull as i32) => CreateStatus::NetworkTooLarge,
            _ => CreateStatus::InvalidNetwork,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum LaneKind {
    Normal = 0,
    Connector = 1,
    BusBay = 2,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum Maneuver {
    Straight = 0,
    Right = 1,
    Left = 2,
    UTurn = 3,
}

impl Maneuver {
    pub const ALL: [Maneuver; 4] = [Maneuver::Straight, Maneuver::Right, Maneuver::Left, Maneuver::UTurn];
}

#[derive(Clone, Copy, Debug, PartialEq)]
#[repr(C)]
pub struct NodeData {
    pub position: Vec2,
    pub elevation: f32,
    pub radius: f32,
    pub control: NodeControl,
    pub first_arm: i32,
    pub arm_count: i32,
    pub first_connector: i32,
    pub connector_count: i32,
    pub signal: i32,
    pub zone: i32,
    pub generation: i32,
}

impl NodeData {
    pub const EMPTY: NodeData = NodeData {
        position: Vec2::new(0.0, 0.0),
        elevation: 0.0,
        radius: 0.0,
        control: NodeControl::None,
        first_arm: 0,
        arm_count: 0,
        first_connector: 0,
        connector_count: 0,
        signal: 0,
        zone: 0,
        generation: 0,
    };
}

#[derive(Clone, Copy, Debug, PartialEq)]
#[repr(C)]
pub struct SegmentData {
    pub node_a: i32,
    pub node_b: i32,
    pub length: f32,
    pub speed_limit: f32,
    pub kind: RoadKind,
    pub lanes_ab: u8,
    pub lanes_ba: u8,
    pub reserved: u8,
    pub first_lane_ab: i32,
    pub first_lane_ba: i32,
    pub first_point: i32,
    pub point_count: i32,
    pub generation: i32,
}

impl SegmentData {
    pub const EMPTY: SegmentData = SegmentData {
        node_a: 0,
        node_b: 0,
        length: 0.0,
        speed_limit: 0.0,
        kind: RoadKind::Street,
        lanes_ab: 0,
        lanes_ba: 0,
        reserved: 0,
        first_lane_ab: 0,
        first_lane_ba: 0,
        first_point: 0,
        point_count: 0,
        generation: 0,
    };
}

#[derive(Clone, Copy, Debug, PartialEq)]
#[repr(C)]
pub struct LaneData {
    pub kind: LaneKind,
    pub lane_no: u8,
    pub direction: u8,
    pub flags: u8,
    pub segment: i32,
    pub node: i32,
    pub from_lane: i32,
    pub to_lane: i32,
    pub length: f32,
    pub speed_limit: f32,
    pub first_point: i32,
    pub point_count: i32,
    pub left: i32,
    pub right: i32,
    pub first_next: i32,
    pub next_count: i32,
    pub conflict_group: i32,
    pub generation: i32,
}

impl LaneData {
    pub const EMPTY: LaneData = LaneData {
        kind: LaneKind::Normal,
        lane_no: 0,
        direction: 0,
        flags: 0,
        segment: 0,
        node: 0,
        from_lane: 0,
        to_lane: 0,
        length: 0.0,
        speed_limit: 0.0,
        first_point: 0,
        point_count: 0,
        left: 0,
        right: 0,
        first_next: 0,
        next_count: 0,
        conflict_group: 0,
        generation: 0,
    };
}

/// Контекст решения «какая полоса входа в какую полосу выхода» для одного манёвра.
#[derive(Clone, Copy, Debug)]
pub struct ManeuverContext {
    pub maneuver: Maneuver,
    pub in_lane_count: i32,
    pub out_lane_count: i32,
    pub control: NodeControl,
    pub in_kind: RoadKind,
    pub out_kind: RoadKind,
    /// true - повторный проход для полосы, которой основные правила не дали ни одного connector'а.
    pub relaxed: bool,
}

/// Правила манёвров (`IConnectorRules`): номер выходной полосы или -1, если манёвр с этой входной полосы запрещён.
pub trait ConnectorRules {
    fn target_lane(&self, ctx: &ManeuverContext, in_lane_no: i32) -> i32;
}

/// Правила по умолчанию (intersections.md §2.2).
#[derive(Clone, Copy, Debug, Default)]
pub struct DefaultConnectorRules;

impl ConnectorRules for DefaultConnectorRules {
    fn target_lane(&self, ctx: &ManeuverContext, in_lane_no: i32) -> i32 {
        let last = ctx.out_lane_count - 1;
        match ctx.maneuver {
            Maneuver::Straight => {
                if in_lane_no < last {
                    in_lane_no
                } else {
                    last
                }
            }
            Maneuver::UTurn | Maneuver::Left => {
                if ctx.relaxed || in_lane_no == ctx.in_lane_count - 1 {
                    last
                } else {
                    -1
                }
            }
            Maneuver::Right => {
                if ctx.relaxed || in_lane_no == 0 {
                    0
                } else {
                    -1
                }
            }
        }
    }
}
