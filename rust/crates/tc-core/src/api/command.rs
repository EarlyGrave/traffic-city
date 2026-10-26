//! Плоская команда фиксированного размера 64 Б (core-api §4.1): `kind` + `tag` + 56 байт полезной нагрузки.
//! Пилот интерпретирует только `SET_TARGET_VEHICLE_COUNT`; остальные виды учитываются счётчиком без эффекта.

pub const COMMAND_SIZE: usize = 64;
pub const PAYLOAD_SIZE: usize = 56;
/// `CommandKind.SetTargetVehicleCount`.
pub const KIND_SET_TARGET_VEHICLE_COUNT: i32 = 9;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(C)]
pub struct Command {
    pub kind: i32,
    pub tag: u32,
    pub payload: [u8; PAYLOAD_SIZE],
}

impl Default for Command {
    fn default() -> Self {
        Self {
            kind: 0,
            tag: 0,
            payload: [0; PAYLOAD_SIZE],
        }
    }
}

const _: () = assert!(size_of::<Command>() == COMMAND_SIZE);

impl Command {
    /// Первое `i32` полезной нагрузки (`SetTargetVehicleCountCmd.Count`).
    pub fn payload_i32(&self) -> i32 {
        i32::from_le_bytes([self.payload[0], self.payload[1], self.payload[2], self.payload[3]])
    }
}

/// Счётчики пилотного `Step` (`tc_pilot_counters`).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
#[repr(C)]
pub struct PilotCounters {
    pub commands_applied: i64,
    pub commands_dropped: i64,
}

const _: () = assert!(size_of::<PilotCounters>() == 16);
