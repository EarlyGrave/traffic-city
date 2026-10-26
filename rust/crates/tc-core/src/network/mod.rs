//! Порт `Sim.Core/Network`: хранилище, построитель сети и connector'ов, сетевые хэши (rust-pilot.md §5.2).

pub mod builder;
pub mod connectors;
pub mod hash;
pub mod lane_geometry;
pub mod store;
pub mod types;

pub use builder::NetworkBuilder;
pub use store::NetworkStore;
pub use types::{
    ConnectorRules, DefaultConnectorRules, LaneData, LaneKind, Maneuver, ManeuverContext, NetworkStatus, NodeData, SegmentData,
};
