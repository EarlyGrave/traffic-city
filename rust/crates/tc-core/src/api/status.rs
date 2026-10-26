//! `CreateStatus` (core-api §10): коды - как в C# `Sim.Core.Api.CreateStatus`.

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i32)]
pub enum CreateStatus {
    Ok = 0,
    /// Ёмкость <= 0 или > лимита реализации.
    InvalidCapacity = 1,
    /// Явная сеть не влезла в ёмкости.
    NetworkTooLarge = 2,
    InvalidVehicleType = 3,
    /// Ссылка на несуществующий узел, нулевая длина сегмента, прилипание узлов и т.п.
    InvalidNetwork = 4,
    InvalidTransit = 5,
    InvalidDemand = 6,
    NotSupported = 7,
    InvalidPeds = 8,
    InvalidCity = 9,
}
