//! Снапшот ТС для клиента (core-api §5.1): 32 байта, раскладка C# `VehicleRenderData`.

#[derive(Clone, Copy, Debug, Default, PartialEq)]
#[repr(C)]
pub struct VehicleRenderData {
    pub x: f32,
    pub y: f32,
    pub z: f32,
    pub heading: f32,
    pub speed: f32,
    pub index: i32,
    pub generation: u16,
    pub kind: u8,
    pub flags: u8,
    pub lateral_offset: f32,
}

const _: () = assert!(size_of::<VehicleRenderData>() == 32);
