# Формат сценариев и параметров ТС (JSON; сценарий `format_version: 2`, типы ТС — `1`)

Статус: принято (ADR-0002). Файлы в `scenarios/`: `s1k.json`, `s10k.json`, `s100k.json`,
`s1m.json`, `stress-1m.json` (ADR-0013), `vehicle-types.json`. Формат независим от языка: C#-хост (`Sim.Headless`, клиент) и
будущий Rust-хост читают одни и те же файлы и строят из них `ScenarioDesc`
(`docs/design/core-api.md` §3.1). **Ядро JSON не читает.**

## 1. Общие правила

- Кодировка UTF-8, ключи `snake_case`, порядок ключей не важен, неизвестные ключи — **ошибка**
  загрузчика (защита от опечаток; расширения — только через `format_version`).
- Единицы в файле человеческие: метры, секунды, **км/ч**, м/с²; загрузчик переводит в СИ и тики
  (`ticks = round(seconds * 20)`, `mps = kmh / 3.6`). Ядро не знает про км/ч.
- Все поля с дефолтом можно опустить; дефолты зафиксированы ниже и реализуются загрузчиком
  (не ядром), чтобы Rust- и C#-хосты дали одинаковый `ScenarioDesc`.
- Имена (типов ТС, зон) — строки только в JSON; загрузчик превращает их в индексы: индекс типа
  ТС = позиция в массиве `vehicle_types.types` (порядок в файле значим).
- Числа с плавающей точкой пишутся так, чтобы парсились в `float32` без неоднозначности
  (не более 7 значащих цифр).
- Версионирование: `format_version` — целое; загрузчик принимает только известные версии;
  несовместимое изменение (переименование/удаление поля, смена единиц) → `format_version + 1` и
  запись в этот документ. Добавление необязательного поля с дефолтом версию не меняет.

## 2. Файл сценария (`scenarios/sXXX.json`)

### 2.1 Схема (все секции)

```
{
  "format_version": 2,                  // 2: секция routing, max_plan_steps (ADR-0009); 1 больше не принимается
  "name": string,                       // информативно
  "seed": uint64 (как число или строка десятичная),
  "duration_s": number,                 // хост останавливает прогон; bench --ticks перекрывает
  "vehicle_types_file": string,         // относительный путь; дефолт "vehicle-types.json"

  "city": {                             // генератор; если есть секция "network" — "city.enabled" должно быть false
    "enabled": bool,                    // дефолт true
    "size_m": [w, h],                   // размер карты, м. Дефолт [3000, 3000]
    "scale": number,                    // множитель к size_m (удобно для лестницы). Дефолт 1.0
    "block_m": number,                  // шаг сетки улиц, м. Дефолт 150
    "arterial_every": int,              // каждая N-я линия сетки — артерия 2+2. Дефолт 4 (≈600 м)
    "arterial_speed_kmh": number,       // дефолт 60
    "street_speed_kmh": number,         // дефолт 40
    "lane_width_m": number,             // дефолт 3.5
    "irregular_nodes": int,             // число узлов, сдвинутых/слитых для нерегулярности. Дефолт round(12·scale²) — считает загрузчик, если ключ опущен; генератор сам не масштабирует
    "irregular_jitter_m": number,       // макс. сдвиг нерегулярного узла, м. Дефолт 40
    "node_jitter_m": number,            // фоновый джиттер всех внутренних узлов, м (0..0.45·block_m). Дефолт 0 — улицы и артерии прямые. PRNG-draw делается всегда
    "removed_street_share": number,     // доля удалённых внутренних улиц (дырки в сетке, 0..0.5). Дефолт 0.15
    "connector_samples": int,           // точек полилинии connector'а. Дефолт 8
    "signals": "arterial_x_arterial" | "all_arterial" | "none",   // где ставить светофоры. Дефолт "arterial_x_arterial"
    "minor_control": "yield" | "stop" | "right_hand",              // регулирование улица→артерия / улица×улица. Дефолт "yield" (улица×улица всегда right_hand)
    "signal_mode": "adaptive" | "fixed",                           // 2.5, ADR-0018. Дефолт "adaptive"
    "signal_coordination": "alternate" | "legacy" | "none",        // офсеты (intersections.md §6.7). Дефолт "alternate"; "legacy" — формула этапа 1
    "progression_speed_factor": number                             // скорость волны = arterial_speed · k. Дефолт 1.0
  },

  "network": {                          // явная сеть (тесты, ручные карты); взаимоисключима с city.enabled
    "nodes": [ { "id": string, "x": number, "z": number, "control": "none|yield|stop|signal|right_hand" } ],
    "segments": [ { "a": string, "b": string, "lanes_ab": int, "lanes_ba": int, "speed_kmh": number, "kind": "street|arterial" } ]
  },

  "vehicles": {
    "count": int,                       // целевое число живых машин (без автобусов); спавнер поддерживает
    "spawn_ramp_s": number,             // линейный разгон до count за это время. Дефолт 120
    "type_mix": { "<type>": weight }    // доли типов ТС для OD-поездок. Дефолт {"car": 1}
  },

  "demand": {
    "zone_m": number,                   // сторона квадратной зоны, м. Дефолт 500
    "model": "uniform" | "gravity",     // выбор OD-пары. Дефолт "gravity"
    "gravity_exponent": number,         // вес ∝ w_i·w_j / d^α, α. Дефолт 1.0
    "weight_jitter": number,            // веса зон = 1 ± U(-j, j) по PRNG Zones. Дефолт 0.5
    "external_share": number,           // доля поездок с началом ИЛИ концом на краю карты. Дефолт 0.3
    "min_trip_m": number,               // отбрасывать OD-пары ближе этого (по центрам зон). Дефолт 600
    "od_pairs": [ { "from": int, "to": int, "weight": number } ]   // необязательно: явные пары вместо модели
  },

  "transit": {
    "routes": int,                      // число автобусных маршрутов (генерируются по PRNG Transit). Дефолт 0
    "headway_s": number,                // плановый интервал. Дефолт 300
    "stops_per_route": [min, max],      // дефолт [8, 14]
    "stop_spacing_m": number,           // дефолт 400
    "stop_kind": "curb" | "bay" | "mixed",  // дефолт "mixed" (bay на артериях, curb на улицах)
    "bus_type": string,                 // имя типа ТС. Дефолт "bus"
    "dwell_base_s": number,             // стоянка без пассажиров. Дефолт 8
    "dwell_per_passenger_s": number,    // дефолт 1.5
    "passenger_rate_per_min": number,   // прибытие ожидающих на остановку, чел/мин. Дефолт 2
    "routes_explicit": [ { "stops": [ { "lane": int, "s": number, "kind": "curb|bay" } ], "headway_s": number } ]
  },

  "rules": {                            // → RulesDesc; все дефолты
    "min_physical_gap_m": 0.5,
    "stuck_threshold_s": 60,
    "stuck_speed_mps": 0.1,
    "gridlock_stuck_share": 0.05,
    "gridlock_min_stuck": 10,
    "gridlock_hold_s": 60,
    "mobil_every_ticks": 10,
    "signal_yellow_s": 3,
    "signal_all_red_s": 1,
    "yield_gap_s": 3.0,
    "max_yield_wait_s": 20,             // инверсия приоритета у ворот (intersections.md §5.4)
    "signal_min_green_s": 7, "signal_max_green_s": 24, "signal_gap_s": 2.5,          // Adaptive (intersections.md §6.5), 2.5
    "signal_arrow_trigger_s": 4, "signal_arrow_min_s": 3, "signal_arrow_max_s": 6, "signal_arrow_gap_s": 2,
    "exit_claim_s": 10                  // заявка на выезд (intersections.md §5.4 п. 3-bis)
  },

  "routing": {                          // → RoutingDesc (routing.md §9, ADR-0009)
    "levels": int,                      // уровней оверлея 1..4 (0 — только тесты: плоский A*). Дефолт 1
    "cell_m": number,                   // сторона ячейки уровня 1, м. Дефолт 1000
    "fanout": int,                      // ячеек уровня ℓ по стороне ячейки ℓ+1. Дефолт 4
    "window_steps": int,                // слот окна маршрута, шагов 8..32. Дефолт 16 (≤ 15 шагов = 8 сегментов)
    "pathfind_budget_per_tick": int,    // запросов на тик (перенесено из capacities). Дефолт 32
    "work_budget_per_tick": int,        // релаксаций запросов на тик. Дефолт 1500 · pathfind_budget_per_tick
    "query_work_cap": int,              // предел релаксаций одного запроса (иначе NoPath). Дефолт 0 = 4 · число направленных сегментов
    "customize_budget_per_tick": int,   // релаксаций волны кастомизации на тик. Дефолт 10000
    "customize_eps": number,            // порог изменения веса сегмента для «грязной» ячейки. Дефолт 0.05
    "cost_recalc_s": number,            // период пересчёта весов по загрузке (ADR-0006). Дефолт 30
    "timed_drift": number,              // порог дрейфа остатка стоимости для плановой перемаршрутизации. Дефолт 0.15
    "timed_drift_abs_s": number         // абсолютная добавка к порогу, с. Дефолт 20
  },

  "lod": {                              // → LodDesc (ADR-0013, sim-lod.md); секция необязательна, дефолт — LOD выключен
    "mode": "off" | "auto" | "all_meso",// off — всё micro (побитово как без секции); all_meso — диагностика/тесты. Дефолт "off"
    "cell_m": number,                   // сторона LOD-ячейки, м. Дефолт 1000
    "micro_budget": int,                // машин в micro-ячейках (мягкий предел контроллера). Дефолт 15000
    "eval_s": number,                   // период оценки уровней. Дефолт 5
    "hold_s": number,                   // micro-ячейка не понижается раньше. Дефолт 30
    "focus_margin_cells": int,          // кольцо ячеек вокруг фокуса. Дефолт 1
    "follow_selected": bool,            // ячейка выбранной машины — фокус. Дефолт true
    "focus_commands": bool,             // false — SetLodFocus(Focus) отклоняется, уровень только по состоянию. Дефолт true
    "congestion_share": number,         // доля стоящих ≥ 30 с в ячейке для «пробки». Дефолт 0.1
    "congestion_min": int,              // и не меньше стольких машин. Дефолт 10
    "meso_headway_scale": number,       // множитель T в meso (калибровка эквивалентности коридору, sim-lod.md §9). Дефолт 0.8
    "meso_accel_scale": number,         // множитель a в meso. Дефолт 1.0
    "focus":  [ { "x": number, "z": number, "radius_m": number } ],   // начальные слоты фокуса 0..3 (эмуляция камеры)
    "pinned": [ { "x": number, "z": number, "radius_m": number } ],   // начальные закрепления 4..7
    "tour": { "period_s": number, "radius_m": number, "path": [[x, z], ...] }  // только хост/bench: фокус слота 0 обходит
                                        // точки пути, сдвиг раз в period_s (команды SetLodFocus) — тест переходов
  },

  "capacities": {                       // → SimCapacities; обязательны max_vehicles и сетевые
    "max_vehicles": int,
    "max_nodes": int, "max_segments": int, "max_lanes": int, "max_lane_points": int,
    "max_zones": int, "max_bus_routes": int, "max_bus_stops": int, "max_bus_stops_per_route": 32,
    "max_routes": int,                  // слоты маршрутов: машины + поездки с построенным маршрутом
    "max_plan_steps": int,              // арена планов (int); дефолт max_routes · (12 + 8·levels)
    "max_pending_trips": int,
    "command_queue": 1024, "events": 4096,
    "max_signalized_nodes": int,
    "max_connectors": int,              // дефолт max_nodes * 16 (intersections.md §4)
    "max_conflicts": int                // дефолт max_connectors * 6
  }
}
```

Ёмкости задаются явно и с запасом (ядро не угадывает размер сети): если генератор не влез —
`Create` возвращает `NetworkTooLarge`, и запас нужно поднять. Оценка для сетки: при
`N = size/block` линий в каждом направлении узлов ≈ `(N+1)²`, сегментов ≈ `2·N·(N+1)`,
полос ≈ `сегменты × 2.5` (смесь 1+1 и 2+2) + connector'ы ≈ `узлы × 12`, точек полос ≈
`полосы_normal × 2 + connector'ы × connector_samples`. Берём ×1.5. Точные формулы и оценки по
лестнице — `docs/design/city-generator.md` §4; артериальные линии — `i % arterial_every ==
arterial_every / 2` (при дефолтах 5 на ось, 25 светофоров), периметр — улица; фоновый джиттер всех
узлов `node_jitter_m` (дефолт 0). Зон: `Zx·Zz + 2·(Zx+Zz)`, `Zx = ceil(w·scale / zone_m)` (s1k: 60).
`max_routes ≥ max_vehicles + routed_pending`: каждая машина и каждая поездка с построенным маршрутом держит слот (план +
окно `window_steps` шагов по 8 B, арена окон = `max_routes · window_steps`; `routing.md` §9.5); магистралей больше нет.
Нет слота — поездка ждёт (`RouteDeferred`). `max_plan_steps ≥ max_routes · avg_plan · 1.5`, `avg_plan = 12 + 8·levels` — по замеру H3 (`routing.md` §15): блок слота = длина стека за
окном + запас `8·levels`; средняя длина стека 4.5 (s1k, L1) / 7.5 (s10k, L1) / 10.2 (s100k, L2) / 5.6 (s10k, L3), p95 ≤ 18, максимум 24,
т.е. формула с запасом ×1.2–1.4 над средним блоком (запас съедают дыры до компактификации арены). Предел арены плана — `1<<28`.
Ключа `max_route_steps` больше нет (арена полных маршрутов удалена, загрузчик отклоняет неизвестные ключи).

### 2.2 Полный пример `scenarios/s1k.json`

```json
{
  "format_version": 2,
  "name": "s1k",
  "seed": 20261024,
  "duration_s": 1800,
  "vehicle_types_file": "vehicle-types.json",

  "city": {
    "enabled": true,
    "size_m": [3000, 3000],
    "scale": 1.0,
    "block_m": 150,
    "arterial_every": 4,
    "arterial_speed_kmh": 60,
    "street_speed_kmh": 40,
    "lane_width_m": 3.5,
    "irregular_nodes": 12,
    "irregular_jitter_m": 40,
    "node_jitter_m": 0,
    "removed_street_share": 0.15,
    "connector_samples": 8,
    "signals": "arterial_x_arterial",
    "minor_control": "yield"
  },

  "vehicles": {
    "count": 1000,
    "spawn_ramp_s": 120,
    "type_mix": { "car": 1.0 }
  },

  "demand": {
    "zone_m": 500,
    "model": "gravity",
    "gravity_exponent": 1.0,
    "weight_jitter": 0.5,
    "external_share": 0.3,
    "min_trip_m": 600
  },

  "transit": {
    "routes": 10,
    "headway_s": 300,
    "stops_per_route": [8, 14],
    "stop_spacing_m": 400,
    "stop_kind": "mixed",
    "bus_type": "bus",
    "dwell_base_s": 8,
    "dwell_per_passenger_s": 1.5,
    "passenger_rate_per_min": 2
  },

  "rules": {
    "min_physical_gap_m": 0.5,
    "stuck_threshold_s": 60,
    "stuck_speed_mps": 0.1,
    "gridlock_stuck_share": 0.05,
    "gridlock_min_stuck": 10,
    "gridlock_hold_s": 60,
    "mobil_every_ticks": 10,
    "signal_yellow_s": 3,
    "signal_all_red_s": 1,
    "yield_gap_s": 3.0,
    "max_yield_wait_s": 20
  },

  "routing": {
    "levels": 1,
    "cell_m": 1000,
    "fanout": 4,
    "window_steps": 16,
    "pathfind_budget_per_tick": 32,
    "work_budget_per_tick": 48000,
    "customize_budget_per_tick": 10000,
    "cost_recalc_s": 30
  },

  "capacities": {
    "max_vehicles": 1280,
    "max_nodes": 700,
    "max_segments": 1300,
    "max_lanes": 12000,
    "max_lane_points": 80000,
    "max_zones": 64,
    "max_bus_routes": 16,
    "max_bus_stops": 256,
    "max_bus_stops_per_route": 32,
    "max_routes": 4096,
    "max_plan_steps": 81920,
    "max_pending_trips": 2048,
    "command_queue": 1024,
    "events": 4096,
    "max_signalized_nodes": 64,
    "max_connectors": 11200,
    "max_conflicts": 67200
  }
}
```

Пояснения к s1k: 3×3 км при `block_m = 150` даёт 20×20 кварталов, ~440 узлов, ~840 сегментов;
артерии через каждые 600 м (6×6 линий) — 25 регулируемых перекрёстков артерия×артерия.
1 000 машин на ~300 полосо-км ≈ 3 машины/полосо-км — свободный поток, цель этапа 1 —
корректность и перф, не пробки. 10 маршрутов × ~4 автобуса (маршрут ~20 мин / интервал 5 мин)
≈ 40 автобусов — отсюда `max_vehicles = 1280` (запас ~20 %).

### 2.3 Лестница: что меняется

Решение владельца (2026-10-24, ADR-0013): s1k/s10k/s100k — **машин одновременно** (лестница движка); **s1m — город на
1M жителей** (≈ 80k машин одновременно + автобусы; позже пешеходы) — цель продукта; **stress-1m** — 1M машин, предел
движка без бюджета (бывший `s1m.json`, переименован). Карта s1m — 18×18 км (≈ 3 100 жит./км²).

| Поле | s1k | s10k | s100k | s1m (1M жителей) | stress-1m |
|------|-----|------|-------|-----|-----|
| назначение | регрессия, CI-бюджет | горячий путь, CI-бюджет | масштаб машин | город 1M жителей | предел движка |
| `city.scale` (карта) | 1.0 (3×3 км) | 2.0 (6×6) | 5.0 (15×15) | 6.0 (18×18) | 16.0 (48×48) |
| `city.irregular_nodes` | 12 | 48 | 300 | 432 | 3000 |
| `vehicles.count` | 1 000 | 10 000 | 100 000 | 80 000 | 1 000 000 |
| `transit.routes` | 10 | 40 | 250 | 400 (≈ 2 000 автобусов, оценка) | 0 |
| `vehicles.spawn_ramp_s` | 120 | 300 | 600 | 600 | 900 |
| `duration_s` | 1800 | 1800 | 1800 | 1800 | 600 |
| плотность, маш./полосо-км (оценка) | 3 | 8 | 13 | 7 | 13 |
| `lod.mode` | off | off (+ прогон `--lod all_meso` для цены meso) | auto | auto | auto |
| `lod.micro_budget` | — | — | 15 000 | 15 000 | 15 000 |
| `lod.focus` (эмуляция камеры в bench) | — | — | центр, r 1 500 м | центр, r 1 500 м | центр, r 1 500 м |
| доля micro / meso при полной загрузке | 100 / 0 | 100 / 0 | ≤ 15 / ≥ 85 % | ≤ 18 / ≥ 82 % | ≤ 1.5 / ≥ 98.5 % |
| **tick p95, бюджет** | **≤ 1 мс** | **≤ 4 мс** | **≤ 15 мс** | **≤ 15 мс** (предложение) | нет: p95, тиков/с, память — в отчёт |
| tick p95, оценка (`sim-lod.md` §6) | 0.48 (факт) | 4.1–4.9 → ≤ 4 (ADR-0016) | 11–14 | 10–13 | 60–80 |
| `capacities.max_vehicles` | 1 280 | 12 000 | 115 000 | 90 000 | 1 100 000 |
| `capacities.max_nodes / segments / lanes / lane_points` | 700 / 1 300 / 12 000 / 80 k | 2 800 / 5 200 / 48 k / 320 k | 17 k / 32 k / 300 k / 2 M | 25 k / 46 k / 440 k / 3 M | 170 k / 330 k / 3 M / 15 M |
| `capacities.max_routes / max_plan_steps` | 4 096 / 82 k | 32 k / 640 k | 250 k / 7 M | 180 k / 5 M | 1.3 M / 47 M (*) |
| `routing.levels` (ячейки 1 / 4 / 16 км) | 1 | 1 | 2 | 2 | 3 |
| `routing.work_budget_per_tick / customize_budget_per_tick` | 48 k / 10 k | 192 k / 20 k | 400 k / 100 k | 600 k / 150 k | 2 M / 2 M |
| `routing.cost_recalc_s` | 30 | 30 | 30 | 60 | 120 |
| `capacities.max_connectors / max_conflicts` | 11.2 k / 67 k | 45 k / 270 k | 270 k / 1.6 M | 390 k / 2.4 M | 2.7 M / 16 M |
| `capacities.max_pending_trips` | 2 048 | 16 k | 128 k | 128 k | 1 M |
| `routing.pathfind_budget_per_tick` | 32 | 128 | 512 | 512 | 2 048 |
| `capacities.max_zones` | 64 | 256 | 1 024 | 2 048 | 10 000 |
| `capacities.max_bus_routes / stops` | 16 / 256 | 64 / 1 024 | 320 / 4 096 | 512 / 6 144 | 16 / 256 |
| `capacities.max_signalized_nodes` (светофоров `A²`: 25 / 100 / 625 / 900 / 6 400) | 64 | 160 | 800 | 1 200 | 8 000 |

(*) stress-1m (ADR-0009): окна `1.3 M · 16 · 8 B` ≈ 166 МБ + план `47 M · 4 B` ≈ 187 МБ вместо ≈ 4 ГБ полных маршрутов;
`max_plan_steps` — формула подтверждена замером H3 для s1k–s100k. Ёмкости сети s1m — масштаб s100k × (6/5)² ≈ 1.44 с
округлением вверх (формулы `city-generator.md` §4; проверить `validate` и тестом генератора при создании файла, L5).

Всё остальное (`block_m`, скорости, `demand`, `rules`, типы ТС) **одинаково** на всей лестнице,
чтобы различия метрик объяснялись только размером и LOD. Плотность растёт с 3 до ~13 маш./полосо-км —
намеренно: у больших сценариев должны появляться очереди, иначе они не нагружают перекрёстки и
MOBIL; s1m реже (7) — средняя по большому городу, пробки локальны. `stress-1m` короче (10 мин) — он только фиксирует
предел. Бюджеты s1k/s10k — в CI (bench-gate); s100k и s1m — отчётный прогон верификатора с порогом.

## 3. Параметры ТС (`scenarios/vehicle-types.json`)

```json
{
  "format_version": 1,
  "types": [
    {
      "name": "car",
      "category": "car",
      "length_m": 4.5,
      "width_m": 1.8,
      "max_speed_kmh": 130,
      "speed_factor": 1.05,
      "idm": { "a": 1.0, "b": 1.5, "T": 1.2, "s0": 2.0, "delta": 4 },
      "mobil": { "politeness": 0.3, "threshold": 0.2, "b_safe": 4.0, "right_bias": 0.3, "duration_s": 2.0 }
    },
    {
      "name": "bus",
      "category": "bus",
      "length_m": 12.0,
      "width_m": 2.55,
      "max_speed_kmh": 80,
      "speed_factor": 0.95,
      "idm": { "a": 0.8, "b": 1.2, "T": 1.6, "s0": 2.5, "delta": 4 },
      "mobil": { "politeness": 0.5, "threshold": 0.3, "b_safe": 3.0, "right_bias": 0.5, "duration_s": 3.0 }
    }
  ]
}
```

Соответствие `VehicleTypeDesc`: `Length, Width, MaxSpeed(м/с), SpeedFactor, Accel=a,
ComfortDecel=b, HeadwayTime=T, MinGap=s0, Delta, Politeness, LaneChangeThreshold=threshold,
SafeDecel=b_safe, RightBias=right_bias, LaneChangeDurationTicks=round(duration_s·20)`.
Желаемая скорость на полосе: `v0 = min(MaxSpeed, SpeedFactor · lane.SpeedLimit)`.

### 3.1 Обоснование стартовых значений

IDM (Treiber, Hennecke, Helbing, *Phys. Rev. E* 62, 2000; Treiber & Kesting, *Traffic Flow
Dynamics*, Springer 2013, табл. 11.2 «городской трафик»: v0 = 54 км/ч, T = 1.0 с, s0 = 2 м,
a = 1.0 м/с², b = 1.5 м/с², δ = 4):

- `a = 1.0`, `b = 1.5`, `s0 = 2.0`, `delta = 4` — взяты из городского набора без изменений.
- `T = 1.2` вместо 1.0: небольшой запас на дискретизацию dt = 0.05 с и на жёсткий клэмп I6
  (core-api §7.6) — при T = 1.0 клэмп срабатывает чаще в плотных очередях, что искажает
  поведение. Пропускная способность полосы при T = 1.2 ≈ 2 400 маш./ч/полосу — реалистично
  для города.
- `speed_factor = 1.05`: лёгкое превышение лимита «в среднем»; разброс по водителям — этап 2.
- `max_speed_kmh = 130` у легковых фактически не ограничивает в городе (лимиты 40–60).

Автобус: `a = 0.8`, `b = 1.2` (тяжёлое ТС, комфорт пассажиров), `T = 1.6`, `s0 = 2.5`
(длина 12 м → больший запас), `max 80 км/ч`, `speed_factor 0.95`.

MOBIL (Kesting, Treiber, Helbing, *Transportation Research Record* 1999, 2007):
- `politeness p = 0.3` — в рекомендованном диапазоне 0.2–0.5 («реалистичный» водитель; p = 0
  эгоист, p = 1 альтруист); автобусу 0.5 — реже «втыкается».
- `threshold Δa_th = 0.2 м/с²` — рекомендовано 0.1–0.3; гистерезис против дрожания.
- `b_safe = 4.0 м/с²` — критерий безопасности `a_new_follower ≥ −b_safe` (рекомендовано ≤ 4;
  нормальное торможение b = 1.5, физический предел ~8).
- `right_bias Δa_bias = 0.3 м/с²` — асимметричное правило «держись правее» для правостороннего
  движения; у автобуса выше (остановки справа).
- `duration_s = 2.0` — длительность манёвра (визуальная и логическая, ghost в исходной полосе).

Все значения — стартовые; калибровка по метрикам (`avg_speed_kmh`, `stuck`, `hard_clamps`) —
после шага 3. Изменение значений не меняет формат.

## 4. Явная сеть (пример для тестов)

```json
{
  "format_version": 2,
  "name": "test-two-lanes-ring",
  "seed": 1,
  "duration_s": 60,
  "city": { "enabled": false },
  "network": {
    "nodes": [
      { "id": "n0", "x": 0, "z": 0, "control": "none" },
      { "id": "n1", "x": 500, "z": 0, "control": "none" },
      { "id": "n2", "x": 500, "z": 500, "control": "none" },
      { "id": "n3", "x": 0, "z": 500, "control": "none" }
    ],
    "segments": [
      { "a": "n0", "b": "n1", "lanes_ab": 2, "lanes_ba": 0, "speed_kmh": 50, "kind": "street" },
      { "a": "n1", "b": "n2", "lanes_ab": 2, "lanes_ba": 0, "speed_kmh": 50, "kind": "street" },
      { "a": "n2", "b": "n3", "lanes_ab": 2, "lanes_ba": 0, "speed_kmh": 50, "kind": "street" },
      { "a": "n3", "b": "n0", "lanes_ab": 2, "lanes_ba": 0, "speed_kmh": 50, "kind": "street" }
    ]
  },
  "vehicles": { "count": 50, "spawn_ramp_s": 10 },
  "demand": { "zone_m": 1000, "model": "uniform", "external_share": 0, "min_trip_m": 0 },
  "capacities": {
    "max_vehicles": 64, "max_nodes": 8, "max_segments": 8, "max_lanes": 64, "max_lane_points": 512,
    "max_zones": 4, "max_bus_routes": 1, "max_bus_stops": 1, "max_routes": 256, "max_plan_steps": 8192,
    "max_pending_trips": 64, "max_signalized_nodes": 1
  },
  "routing": { "levels": 1, "cell_m": 250, "pathfind_budget_per_tick": 8 }
}
```

Узлы с `control: "none"` и ровно одним входом/выходом соединяются connector'ами «прямо» 1:1 по
полосам — это режим шага 2 (без перекрёстков). Зоны при `city.enabled = false` строятся сеткой
`zone_m` поверх bounding box сети.

## 5. Выход бенча (для сверки C# ↔ Rust)

`perf/results/<имя>.json` пишет хост; поля `traffic.*` считаются из `SimCounters`/`SimStats`
(core-api §11) и должны совпадать между реализациями при одинаковом сценарии и числе тиков —
вместе с `state_hash` после последнего тика. Это и есть критерий «метрики один в один».
