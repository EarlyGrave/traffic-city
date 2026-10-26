//! Порт `Rules/Intersections` (пилот: только `ConnectorStore`).

pub mod connector_store;

pub use connector_store::{ConflictEntry, ConflictKind, ConflictRel, ConnectorStore};
