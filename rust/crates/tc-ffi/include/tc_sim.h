/* tc_sim.h - C ABI Rust-пилота ядра traffic-city (ADR-0022, docs/design/rust-pilot.md §2).
 * Пишется руками, держать в синхроне с crates/tc-ffi/src/lib.rs; размеры сверяет tc_struct_size() и rust/ffi-smoke.
 * Только 64-битные платформы (указатели в tc_scenario_desc). Порядок байт - little-endian. */
#ifndef TC_SIM_H
#define TC_SIM_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define TC_ABI_VERSION 1u

/* Коды ошибок (< 0). Результаты tc_sim_create >= 0 - CreateStatus ядра (0 = ok). */
#define TC_ERR_NULL     (-1) /* NULL-аргумент */
#define TC_ERR_ARG      (-2) /* неверное значение аргумента */
#define TC_ERR_PANIC    (-3) /* паника внутри ядра; экземпляр отравлен */
#define TC_ERR_POISONED (-4) /* экземпляр отравлен предыдущей паникой; destroy работает */
#define TC_ERR_ABI      (-5) /* struct_size не совпал */

/* Идентификаторы для tc_struct_size. */
#define TC_SZ_SCENARIO_DESC       0
#define TC_SZ_NODE_DESC           1
#define TC_SZ_SEGMENT_DESC        2
#define TC_SZ_RUNTIME_OPTIONS     3
#define TC_SZ_COMMAND             4
#define TC_SZ_VEHICLE_RENDER_DATA 5
#define TC_SZ_VEC2                6
#define TC_SZ_PILOT_COUNTERS      7
#define TC_SZ_CAPACITIES          8

/* Значения control / kind - как NodeControl / RoadKind в Sim.Core. */
#define TC_CONTROL_NONE 0
#define TC_CONTROL_YIELD 1
#define TC_CONTROL_STOP 2
#define TC_CONTROL_SIGNAL 3
#define TC_CONTROL_RIGHT_HAND_RULE 4
#define TC_ROAD_STREET 0
#define TC_ROAD_ARTERIAL 1

/* Вид команды SetTargetVehicleCount (CommandKind = 9); payload: int32 count с offset 0. */
#define TC_CMD_SET_TARGET_VEHICLE_COUNT 9

/* Ёмкости (SimCapacities): 24 x int32, порядок объявления. 96 байт. */
typedef struct tc_capacities {
    int32_t max_vehicles, max_nodes, max_segments, max_lanes, max_lane_points;
    int32_t max_zones, max_bus_routes, max_bus_stops, max_bus_stops_per_route, max_routes;
    int32_t max_plan_steps, max_pending_trips, command_queue_capacity, event_capacity;
    int32_t max_signalized_nodes, max_connectors, max_conflicts;
    int32_t max_peds, max_residents, max_ped_path_steps;
    int32_t max_lots, max_buildings, max_city_residents, skim_obs_capacity;
} tc_capacities;

typedef struct tc_node_desc {   /* 12 байт */
    float x, z;
    uint8_t control;
    uint8_t _pad[3];
} tc_node_desc;

typedef struct tc_segment_desc { /* 24 байта */
    int32_t a, b, lanes_ab, lanes_ba;
    float speed_limit;           /* м/с */
    uint8_t kind;
    uint8_t _pad[3];
} tc_segment_desc;

/* Описание явной сети. Параметры построителя (NetworkOptions.Default) зашиты в ядре и не передаются.
 * 144 байта на 64-битных платформах. struct_size - первым полем, обязан быть равен tc_struct_size(TC_SZ_SCENARIO_DESC). */
typedef struct tc_scenario_desc {
    int32_t struct_size;
    int32_t _pad0;
    uint64_t seed;
    tc_capacities capacities;
    const tc_node_desc* nodes;
    int32_t node_count;
    int32_t _pad1;
    const tc_segment_desc* segments;
    int32_t segment_count;
    int32_t _pad2;
} tc_scenario_desc;

typedef struct tc_runtime_options { /* 8 байт */
    int32_t struct_size;
    int32_t worker_threads;         /* пилот: принимается и игнорируется */
} tc_runtime_options;

typedef struct tc_command {         /* 64 байта (core-api §4.1) */
    int32_t kind;
    uint32_t tag;
    uint8_t payload[56];
} tc_command;

typedef struct tc_vehicle_render_data { /* 32 байта (core-api §5.1) */
    float x, y, z;
    float heading;
    float speed;
    int32_t index;
    uint16_t generation;
    uint8_t type;
    uint8_t flags;
    float lateral_offset;
} tc_vehicle_render_data;

typedef struct tc_vec2 { float x, z; } tc_vec2; /* 8 байт */

typedef struct tc_pilot_counters {  /* 16 байт */
    int64_t commands_applied;
    int64_t commands_dropped;
} tc_pilot_counters;

typedef struct tc_sim tc_sim;       /* opaque */

uint32_t tc_abi_version(void);
/* Размер struct по TC_SZ_*; -1 = неизвестный идентификатор. */
int32_t tc_struct_size(int32_t which);

/* Создание. 0 = ok (*out заполнен); > 0 = CreateStatus ошибки; < 0 = TC_ERR_*. При ошибке *out = NULL. opts может быть NULL.
 * desc, opts и массивы desc - память вызывающего; ядро копирует всё внутри вызова. */
int32_t tc_sim_create(const tc_scenario_desc* desc, const tc_runtime_options* opts, tc_sim** out);
/* NULL - no-op; повторный вызов на том же указателе - UB. Работает на отравленном экземпляре. */
void tc_sim_destroy(tc_sim* sim);

/* 0 = ok; TC_ERR_ARG при ticks < 1; TC_ERR_POISONED / TC_ERR_PANIC / TC_ERR_NULL. */
int32_t tc_sim_step(tc_sim* sim, int32_t ticks);
/* 1 = принята, 0 = очередь полна (commands_dropped++), < 0 = ошибка. */
int32_t tc_sim_enqueue(tc_sim* sim, const tc_command* cmd);
/* Текущий тик; -1 = NULL, -4 = отравлен. */
int64_t tc_sim_tick(const tc_sim* sim);

/* Снапшоты: ptr + len. Память ядра, только чтение, валидна до следующего tc_sim_step / tc_sim_destroy.
 * *out_len пишется всегда (0 и NULL при ошибке). */
const tc_vehicle_render_data* tc_sim_vehicles(const tc_sim* sim, int32_t* out_len);  /* [max_vehicles], в пилоте нули */
const tc_vec2* tc_sim_lane_points(const tc_sim* sim, int32_t* out_len);              /* [lane_point_count] */

/* FNV-1a 64 по построенной сети (rust-pilot.md §5.2); 0 при NULL/отравлении. */
uint64_t tc_sim_network_hash(const tc_sim* sim);
void tc_sim_pilot_counters(const tc_sim* sim, tc_pilot_counters* out);

#ifdef TC_TEST_PANIC
/* Только в сборке с фичей test-panic: вызывает панику внутри обёртки -> TC_ERR_PANIC, далее TC_ERR_POISONED. */
int32_t tc_sim_test_panic(tc_sim* sim);
#endif

#ifdef __cplusplus
}
#endif

#endif /* TC_SIM_H */
