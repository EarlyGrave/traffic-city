# Rust-пилот ядра (спецификация, этап 4.6)

ADR: `docs/adr/0022-rust-pilot.md`. Связанные: `core-api.md` §8 (PCG32), §9 (хэш, float), §12 (C ABI); ADR-0017 (воркеры);
`save-format.md` §2 (XXH64); `scenario-format.md` §2.1, §4 (явная сеть). Цель — **проверить правила портируемости** второй реализацией,
а не перенести ядро. Клиент не переезжает. Задачи R1–R8 — §10.

## 0. Цель, границы, критерий успеха
- В пилоте: `Util` (PCG32, FNV-1a 64, XXH64, `Vec2`, float-хелперы), разбор подмножества JSON сценария, построение явной сети
  (`Network/NetworkBuilder*.cs`, `LaneGeometry.cs`, `NetworkStore.cs`, `NetworkTypes.cs`, построение `Rules/Intersections/ConnectorStore`),
  сетевой хэш, C ABI с пилотным `Step`.
- Вне пилота: генератор города, зоны, транспорт, светофоры (планы), фазы тика, IDM/MOBIL (кроме векторов R7), маршрутизация, LOD, сейвы, пешеходы
  (ADR-0020: сеть пешеходов производная — сетевой хэш её не включает), воркеры.
- Успех: (1) все golden-файлы §5 совпадают в C# и Rust побитово (кроме `headings_hash`, §6.3); (2) `ffi-smoke` в одном процессе получает
  от Rust тот же `network_hash`, что C#, и те же размеры struct; (3) CI `rust` зелёный; (4) отчёт R8 — список нарушений правил портируемости,
  найденных при порте (каждое → строка в `lessons/rust.md` или правка `core-api.md` §9).

## 1. Окружение (проверено 2026-10-25, облачный контейнер)
| Что | Факт |
|---|---|
| Тулчейн | Уже установлен: `/root/.cargo/bin/{cargo,rustc,rustup}`, `stable-x86_64-unknown-linux-gnu` = **rustc/cargo 1.97.0**, профиль `minimal` + `clippy`, `rustfmt`. Текущий stable в канале — 1.99.1 |
| apt (Ubuntu 24.04) | `cargo`/`rustc` 1.75.0 (кандидат, не установлен) и версионные `rustc-1.74…1.83` — без edition 2024 (нужен ≥ 1.85). Не использовать |
| Прокси | `index.crates.io/config.json` → 200; `static.crates.io` → 200 (крейт скачан пробой `cargo run`, `CARGO_HTTP_CAINFO=/root/.ccr/ca-bundle.crt` уже задан); `static.rust-lang.org` (rustup-init, `channel-rust-*.toml`) → 200; `sh.rustup.rs` → **403** |
| Если тулчейна нет | `curl -sSfO https://static.rust-lang.org/rustup/dist/x86_64-unknown-linux-gnu/rustup-init && sh`-установка из файла (не `curl sh.rustup.rs`) |
| Закрепление | `rust/rust-toolchain.toml` `channel = "1.97.0"`: rustup поставит отдельный тулчейн `1.97.0` (≈ 70 МБ, minimal), даже если `stable` той же версии — один раз на сессию; без файла `rustup update` молча сменит версию |
| .NET рядом | `/usr/lib/dotnet`: рантаймы 8.0.32 и 10.0.13 |

Замер float (этот контейнер, 1 млн пар аргументов из одного LCG, хэш FNV по битам; проба в scratchpad, не в репо):
| Функция | C# net8 / net10 `MathF` | Rust `f32::` (std) | Rust крейт `libm` |
|---|---|---|---|
| `atan2` | `f620e9d929472cfe` / тот же | тот же | отличается в 2 776 из 1 000 000 |
| `sin` | `3444917517ea70b6` / тот же | тот же | отличается в 13 534 из 1 000 000 |
| `max(-0,+0)`, `max(+0,-0)`, `min(-0,+0)`, `min(+0,-0)` | `+0, +0, -0, -0` | те же (но в документации Rust — «не определено») | — |
| `(int)3e9f`, `(int)NaN` | net8: `int.MinValue`, `int.MinValue`; **net10: `int.MaxValue`, `0`** | `as i32`: `i32::MAX`, `0` | — |

## 2. Граница C ABI (`crates/tc-ffi`, заголовок `rust/crates/tc-ffi/include/tc_sim.h`, пишется руками)
```c
#define TC_ABI_VERSION 1u
uint32_t tc_abi_version(void);
int32_t  tc_struct_size(int32_t which);            // размеры struct ниже (TC_SZ_*), для сверки с C#; -1 = неизвестно
typedef struct tc_sim tc_sim;                      // opaque
int32_t  tc_sim_create(const tc_scenario_desc* desc, const tc_runtime_options* opts, tc_sim** out); // CreateStatus (core-api §3), <0 — TC_ERR_*
void     tc_sim_destroy(tc_sim* sim);              // NULL — no-op; повторный вызов на том же указателе — UB (C# держит SafeHandle)
int32_t  tc_sim_step(tc_sim* sim, int32_t ticks);  // 0 = ok; TC_ERR_ARG при ticks < 1; TC_ERR_POISONED
int32_t  tc_sim_enqueue(tc_sim* sim, const tc_command* cmd); // 1 = принята, 0 = очередь полна (счётчик++), <0 — ошибка
int64_t  tc_sim_tick(const tc_sim* sim);
const tc_vehicle_render_data* tc_sim_vehicles(const tc_sim* sim, int32_t* out_len); // [MaxVehicles], в пилоте нули
const tc_vec2*  tc_sim_lane_points(const tc_sim* sim, int32_t* out_len);           // [LanePointCount] — реальная геометрия
uint64_t tc_sim_network_hash(const tc_sim* sim);   // §5.2
void     tc_sim_pilot_counters(const tc_sim* sim, tc_pilot_counters* out); // commands_applied, commands_dropped (int64)
```
Коды: `TC_ERR_NULL = -1` (NULL-аргумент), `TC_ERR_ARG = -2`, `TC_ERR_PANIC = -3`, `TC_ERR_POISONED = -4`, `TC_ERR_ABI = -5` (поле `struct_size` desc не совпало).

**Владение памятью.** Вход (`desc` и его массивы, `opts`, `cmd`) — память вызывающего: ядро читает только внутри вызова и всё копирует.
Выход (`ptr + len`) — память ядра, только чтение, валиден до следующего `tc_sim_step`/`tc_sim_destroy`; `len` пишется всегда (0 при ошибке, указатель NULL).
`tc_sim*` создаётся `Box::into_raw`, освобождается только `tc_sim_destroy` (`Box::from_raw`). Ядро не хранит указателей вызывающего. Аллокатор не
пересекает границу: хост никогда не освобождает память ядра и наоборот.

**Ошибки и паники.** Каждая `extern "C"` — тонкая обёртка: проверка NULL → `catch_unwind(AssertUnwindSafe(..))` → код. Паника → `TC_ERR_PANIC`,
экземпляр помечается «отравленным» (дальнейшие вызовы — `TC_ERR_POISONED`/no-op, `destroy` работает). `panic = "abort"` не используем: игра не
должна падать от бага ядра. `tc-core` в пилотном `Step` паник не допускает (индексы из проверенных при `Create` диапазонов).

**Потоки.** `tc_sim` однопоточный для хоста (core-api §3): `Send`, не `Sync`; вызовы одного экземпляра не пересекаются. `tc_runtime_options { int32 struct_size; int32 worker_threads; }` —
в пилоте `worker_threads` принимается и игнорируется (> 1 → без ошибки, работает 1).

**Раскладка.** Все struct — `#[repr(C)]`, поля фиксированной ширины, явные `_pad`, `const _: () = assert!(size_of::<T>() == N)` в Rust.
`bool` → `u8`; enum на границе — целое, в ядро — через `TryFrom` (невалидное значение → `CreateStatus`, никогда не `transmute`).
`tc_scenario_desc` = `struct_size` (первым полем, сверка → `TC_ERR_ABI`), `seed: u64`, `tc_capacities` (все `int32` в порядке объявления
`SimCapacities`), `const tc_node_desc* nodes; int32 node_count; const tc_segment_desc* segments; int32 segment_count;` + параметры построителя,
которые C# берёт для явной сети (см. `WorldBuilder.BuildNetwork`/`NetworkOptions`; R4 перечисляет точно). `tc_node_desc { f32 x, z; u8 control; u8 _pad[3]; }`
(12 Б), `tc_segment_desc { i32 a, b, lanes_ab, lanes_ba; f32 speed_limit; u8 kind; u8 _pad[3]; }` (24 Б). `tc_command` — 64 Б (`core-api` §4.1): `i32 kind` + полезная
нагрузка байтами; пилотный `Step` применяет `SetTargetVehicleCount` (только запоминает), остальные — счётчик `commands_applied` без эффекта.
`tc_vehicle_render_data` — 32 Б, раскладка `VehicleRenderData` (core-api §5.1). C# `Sim.Core` для пилота не обязан быть blittable на уровне `ScenarioDesc`:
`ffi-smoke` переводит C#-desc в свои зеркальные struct.

**Уточнения R6.** Параметры построителя (`NetworkOptions.Default`) зашиты в обеих реализациях и через ABI не передаются; `tc_scenario_desc` = 144 Б (64-бит), `tc_capacities` = 96 Б. Заголовок — `rust/crates/tc-ffi/include/tc_sim.h`; `tc_sim_test_panic` — только с фичей `test-panic`. `tc_sim_create`: 0 = ok, > 0 = `CreateStatus`, < 0 = `TC_ERR_*`.

Пилотный `Step(n)`: `n` раз — применить очередь команд (FIFO, ёмкость `CommandQueueCapacity`, кольцо как `Util/RingBuffer`), `Tick++`. Больше ничего.

## 3. Workspace `rust/`
```
rust/
  Cargo.toml            # [workspace] members = crates/*, resolver = "3"; [workspace.package] edition = "2024", rust-version = "1.97"
  Cargo.lock            # в git
  rust-toolchain.toml   # channel = "1.97.0", components = ["clippy", "rustfmt"], profile = "minimal"
  rustfmt.toml          # max_width = 140 (как .editorconfig C#), остальное по умолчанию
  allowed-crates.txt    # пусто: сторонних крейтов нет (ADR-0022 п.5)
  crates/
    tc-core/            # lib; #![forbid(unsafe_code)]; 0 зависимостей; без std::fs/io/time/env/thread (clippy.toml в каталоге крейта)
      clippy.toml
      src/lib.rs, util/{pcg32,fnv1a64,xxhash64,vec2,detmath,ring_buffer,fixed_hash_map}.rs,
          api/{desc,status,command,snapshot}.rs, network/{store,types,builder,connectors,lane_geometry,hash}.rs,
          intersections/connector_store.rs, sim.rs (пилотный фасад: create/step/enqueue/спаны)
    tc-scenarios/       # lib: &str (JSON) -> ScenarioDesc; свой ридер JSON; 0 зависимостей
      src/{json.rs, loader.rs}; tests/golden.rs (читает ../../tests/golden/rust-pilot и ../../scenarios)
    tc-ffi/             # crate-type = ["cdylib", "rlib"]; unsafe только здесь; include/tc_sim.h
  ffi-smoke/            # C# net10.0 консоль, вне traffic-city.sln: [DllImport("tc_ffi")], сверка размеров и network_hash
scripts/rust-check.sh   # fmt --check + clippy -D warnings + test + проверка зависимостей (то же, что CI)
```
Соответствие C# ↔ Rust: `internal sealed class` хранилища → `pub(crate) struct` с `Vec<T>` фиксированной длины (выделяются в `create`, не растут);
handle → `#[derive(Copy)] struct { index: i32, generation: i32 }`; `NetworkStatus`/`CreateStatus` → `#[repr(i32)] enum` с теми же числами;
`IConnectorRules` → `trait` с одной реализацией (статическая диспетчеризация). Имена — snake_case от C#-имён, чтобы `grep` находил пару.
Профили: `[profile.dev] overflow-checks = true`, `[profile.release] overflow-checks = false, lto = "thin", codegen-units = 1`, `[profile.test]` наследует dev.

## 4. Почему без сторонних крейтов
| Кандидат | Зачем | Почему нет |
|---|---|---|
| `serde_json` (+`serde`, `itoa`, `ryu`, `memchr`) | JSON | 4–5 крейтов; без `float_roundtrip` разбор `f64` не всегда корректно округлён — ломает §6.4. Свой ридер ≈ 300 строк |
| `rand_pcg` | PCG32 | 20 строк, вектора общие с C# (`util.json`) |
| `libm` | кросс-ОС sin/cos/atan2 | расходится с C# `MathF` (§1); в пилоте трансцендентных в хэше нет |
| `rayon` | воркеры | воркеры вне пилота; набросок §8 — на `std::thread` + атомиках, без крейта |
| `cbindgen` | заголовок | заголовок ~60 строк, размеры сверяет `ffi-smoke` |
Правило: новый крейт — ADR с причиной, транзитивным деревом (`cargo tree`) и лицензиями; запись в `allowed-crates.txt`; `scripts/rust-check.sh` падает на
любой пакет из `cargo metadata --format-version 1` с `source != null`, которого нет в списке.

## 5. Кросс-языковой golden
### 5.1 Файлы (`tests/golden/rust-pilot/`, пишет только `Sim.Headless golden`, отдельным коммитом)
- `util.json` — вектора: PCG32 (`seed`, `stream` из набора {0, 1, 42, 2^63+5} × {0, 1, 6, 2^62}: первые 16 `NextUInt`, состояние после 1 000,
  `NextFloat01` (биты), `NextBelow(7)`, `NextRange(-3.5, 12.25)` (биты) по 16 шт.); FNV-1a 64 (пустой, `Add(byte)`×N, `Add(int/uint/long/ulong/float)` по 8 значений
  включая `-0f`, `NaN`, `±∞`); XXH64 (данные `b[i] = (i·31 + 7) & 0xFF`, длины 0, 1, 3, 4, 7, 8, 31, 32, 33, 63, 64, 100, 1 000, 4 097; seed 0 и `0x9E3779B97F4A7C15`;
  разом и потоково кусками 1/7/32 Б); `min/max` (§6.2) на парах из {−0, +0, 1, −1, NaN, +∞, −∞}; `float→int` на значениях из диапазона.
- `<имя>.json` на сценарий: `{ "golden_version": 1, "scenario": "<путь от корня>", "load_ok": true, "create_status": 0, "desc_hash": "…",
  "network_hash": "…", "conflicts_hash": "…", "headings_hash": "…", "counts": { "nodes", "segments", "lanes", "lane_points", "segment_points", "arms", "lane_next", "connectors", "conflicts" } }`.
  Хэши — **строки** hex из 16 знаков (u64 в JSON-числе теряет точность). Если `load_ok: false` — остальное отсутствует (тексты ошибок не сравниваются).
- Сценарии: `scenarios/demo-grid.json` и фикстуры `tests/golden/rust-pilot/scenarios/`: `net-skew` (узлы с неточными в двоичном виде координатами
  `123.456`, `-0.1`, `1e-3`; диагонали, острые углы), `net-star5` (узел с 5 рукавами, 1–4 полосы), `net-oneway` (`lanes_ba: 0`, тупик → разворот), `net-signal`
  (узлы `signal`/`stop`/`yield`/`right_hand`), `net-big` (решётка 12×12 с `speed_kmh` 30/50/70 — проверка `kmh/3.6` в double), ошибочные: `err-snap` (узлы ближе SnapRadius →
  `InvalidNetwork`), `err-short` (сегмент короче минимума), `err-caps` (`max_lane_points` мал → `NetworkTooLarge`), `err-json` (висячая запятая → `load_ok: false`).
  Все фикстуры — `city.enabled: false`, `demand.target_vehicles: 0` (или минимальные секции, которые требует C#-загрузчик).
- **Профили дорог (RP3, ADR-0030, road-profiles.md §8).** Фикстура `net-profiles` берёт каталог `tests/fixtures/road-profiles.fixture.json`
  через `road_profiles_file` и строит `street_2`, `oneway_2`, `arterial_4`, `arterial_6` и `asym_1_2` дважды: вдоль профиля и с `mirrored: true`.
  Ошибочные `err-profile-*` (`width`, `nocurb`, `lane-order`, `lane-width`: каталоги в `tests/golden/rust-pilot/profiles/`; `mm`: дробные мм;
  `ref`: неизвестный профиль; `lanes`: `lanes_ba` не сходится с профилем) дают `load_ok: false`. Если каталог разобран, но не прошёл валидацию,
  в golden есть `"profile_error": "<RoadProfileError>"` (BadWidth, NoCurb, LaneOrder, ElementWidth…): Rust сверяет вид ошибки с C#.
  Порядок хэшей в режиме профилей: в `network_hash` после connector'ов добавляется раздел с меткой `0x464F5250` (`FORP`): каталог, `Segment.Profile` каждого
  сегмента, `Lane.Width` каждой полосы; в `desc_hash` после `corner_radius` — метка, каталог, `Profile`+`Mirrored` сегментов и два нуля
  (`city.profiles` вне пилота). **Без каталога оба раздела не добавляются**: прежние golden не менялись. Загрузчик Rust по-прежнему без ввода-вывода:
  `load_with(text, читатель файлов)` получает путь `road_profiles_file` как в JSON, `load_str` для таких сценариев — ошибка. Каталог вне C ABI:
  `ffi-smoke` такие сценарии пропускает, сверяет их только `cargo test`.

### 5.2 `network_hash` (C#: `Simulation.ComputeNetworkHash()`, новый; Rust: `network::hash`)
FNV-1a 64 (`core-api` §9: `Add` little-endian, float — битовый образ, enum — базовым типом, `bool` — байтом), после построения сети и
`ConnectorStore` (до зон, транспорта, планов светофоров — их поля в состоянии «после построения сети»). Порядок:
1. `Add(1)` — версия сетевого хэша; счётчики `NodeCount, SegmentCount, LaneCount, LanePointCount, SegmentPointCount, NodeArmCount, LaneNextCount, Connectors.Count, ConflictsUsed`.
2. `Nodes[0..NodeCount)`: все поля `NodeData` в порядке объявления (`Position.X, Position.Z, Elevation, Radius, Control, FirstArm, ArmCount, FirstConnector, ConnectorCount, Signal, Zone, Generation`).
3. `Segments[0..SegmentCount)`: все поля `SegmentData` в порядке объявления (включая `Reserved`).
4. `Lanes[0..LaneCount)`: все поля `LaneData` в порядке объявления; затем `LaneLength[i]`, `LaneGateNode[i]`.
5. `NodeArms, ArmFlags, ArmDir (X, Z)` по `[0..NodeArmCount)`; `LaneNext[0..LaneNextCount)`.
6. `SegmentPoints (X, Z)` `[0..SegmentPointCount)`; `LanePoints (X, Z)` и `LanePointS` `[0..LanePointCount)`.
7. `ConnectorStore` по `[0..Count)`: `Lane, Node, InLane, OutLane, Length, InLength, LocalBit, Maneuver, FromArm, Flags`.
Не входят: `LanePieceHeading` (→ `headings_hash`: те же правила, массив `[0..LanePointCount)`), служебные индексы построения (`IncHead/IncNext/CellNext/Cells`),
`NodeTicket`, `OutReserved` (состояние тика), зоны и `Bus*`.
`conflicts_hash` — отдельно: по connector'ам `FirstConflict, ConflictCount, ConflictMask`, затем `Conflicts[0..ConflictsUsed)` по полям `ConflictEntry`.
`desc_hash` — по разобранному JSON до `Create`: `Seed`, `SimCapacities` (все поля по порядку), число узлов и по узлу `X, Z, Control`, число сегментов и по сегменту
`A, B, LanesAB, LanesBA, SpeedLimit, Kind`, затем параметры построителя (§2).
**Уточнения R2 (реализация `Metrics/NetworkHash.cs`, обязательны для порта).** Элементы массивов хэшируются поэлементно (все поля элемента, затем следующий элемент):
`Lanes[i]` → поля `LaneData`, `LaneLength[i]`, `LaneGateNode[i]`; рукава `i` → `NodeArms[i]` (int), `ArmFlags[i]` (byte), `ArmDir[i].X`, `.Z`; `SegmentPoints[i]` → `X, Z`; `LanePoints[i]` → `X, Z, LanePointS[i]`;
connector `i` → поля раздела 7 в перечисленном порядке; `LaneNext[i]` — int. Целые ширины: `byte`-поля — `Add(byte)`, enum `NodeControl` — `Add(int)`, `RoadKind/LaneKind` — `Add(byte)`, `ulong` — 8 байт LE, `ushort` — 2 байта LE (младший первым; в `Fnv1a64` перегрузки нет).
Для явной сети `NodeData.Signal` и `NodeData.Zone` на момент хэша равны `-1` (тесты проверяют), `Elevation = 0`. `Version = 1` — первое `Add(int)` каждого из четырёх хэшей.
`headings_hash`: `Add(1)`, `Add(LanePointCount)`, затем `Add(float)` для `LanePieceHeading[0..LanePointCount)`. `conflicts_hash`: `Add(1)`, `Add(Connectors.Count)`, `Add(ConflictsUsed)`, по connector'ам `FirstConflict, ConflictCount` (int), `ConflictMask` (ulong), затем по записям `OtherLocal, Flags` (byte), `SMineCm, SOtherCm, Reserved` (ushort).
`desc_hash`: `Add(1)`, `Seed` (ulong), `SimCapacities` — 24 int-поля в порядке объявления (`MaxVehicles … SkimObsCapacity`, значения как в разобранном desc, до дефолтов `Create`), число узлов (int), по узлу `X, Z` (float), `Control` (int); число сегментов, по сегменту `A, B, LanesAB, LanesBA` (int), `SpeedLimit` (float), `Kind` (byte); затем `NetworkOptions.Default`: `LaneWidth, SnapRadius, NodeSetback` (float), `ConnectorSamples` (int), `MinLaneLength` (float); затем действующий `CornerRadius` явной сети (float, ADR-0028: `network.corner_radius_m`, 0 в JSON → 0, по умолчанию 6). ADR-0028 перегенерировал golden без `golden_version++` (тот же номер у `detlogit.json`); `headings_hash` снят на Windows — облаку перегенерировать на Linux.
Файл сценария при `create_status != 0` содержит `load_ok`, `create_status`, `desc_hash` (без `network_hash`/`conflicts_hash`/`headings_hash`/`counts`). `util.json`: PCG32 (16 комбинаций, `NextInt(-5, 100)`), `fnv1a64`, `xxh64`, `minmax`, `float_int`, `denormal`; u64 — hex-строки.

**Уточнения R3 (реализация `tc-scenarios`).** `desc_hash`, `ScenarioDesc`, `SimCapacities`, `NetworkOptions::DEFAULT` — в `tc-core::api::desc`; загрузчик `tc_scenarios::load_str(&str)` (без I/O).
Из golden-каталога сценарными считаются файлы с ключом `scenario` (`detlogit.json` и др. — нет). `create_status` в R3 не сверяется (нужен `Sim::create`, R4);
`load_ok: false` у `err-json` — синтаксис. Подмножество и границы — ADR-0022 «Реализация R3».

**Уточнения R4 (реализация `tc-core::{network,intersections,sim}`).** `network_hash` включает `ConflictsUsed` (шапка) и `ConflictGroup` полос. **R5 выполнен:** конфликты строятся (`ConnectorStore::{compute_pairs, write_rows, compute_rel, recompute_rel}`, `ConflictEntry`), обход `Sim::set_conflicts_used_for_golden` и подстановка в тесте удалены, `counts.conflicts` и `conflicts_hash` сверяются со всеми golden.
`ConnectorStore` R4 = регистрация (`Register`, `BuildAll`); пары - R5. `Sim::create` повторяет порядок `WorldBuilder.Create`: ёмкости (`InvalidCapacity`) -> узлы (snap/индекс != i -> `InvalidNetwork`)
-> сегменты -> `BuildAllConnectors` -> `ConnectorStore.BuildAll`; `NetworkStatus >= NodesFull` -> `NetworkTooLarge`, остальное -> `InvalidNetwork`.
`headings_hash` (системный atan2) на Linux x64 совпал со всеми golden-сценариями; тест не краснеет при расхождении, если не задано `TC_STRICT_HEADINGS=1`.

Изменение порядка/состава — правка этого раздела, `golden_version++`, перегенерация golden.

### 5.3 Как сверяется
- C#: `tests/Sim.Core.Tests/Network/RustPilotGoldenTests.cs` — для каждого `*.json` в golden грузит сценарий, считает хэши, сравнивает с файлом
  (C#-изменение, сдвигающее байты сети, краснит тест; обновление — `dotnet run --project src/Sim.Headless -- golden --out tests/golden/rust-pilot`).
- Rust: `crates/tc-scenarios/tests/golden.rs` — то же из тех же файлов; `crates/tc-core` — unit-тесты `util.json`.
- В одном процессе: `ffi-smoke` — C#-загрузчик → `tc_scenario_desc` → `tc_sim_create` → `tc_sim_network_hash` == `sim.ComputeNetworkHash()`;
  `tc_struct_size(*) == Marshal.SizeOf<T>()`; `tc_sim_step(100)` → `tc_sim_tick == 100`; переполнение очереди → `0` и `commands_dropped`.

## 6. Float- и integer-детерминизм (обе стороны)
### 6.1 Разрешено в хэшируемом коде
`+ − × ÷` скаляров `f32` в фиксированном порядке (без переассоциации: `a + b + c` = `(a + b) + c` в обоих языках), `sqrt` (IEEE, корректно округлён:
`MathF.Sqrt` ↔ `f32::sqrt`), `abs`, `floor`, `ceil`, `MathF.Round(v, AwayFromZero)` ↔ `f32::round`, `MathF.Round(v)` (ToEven) ↔ `f32::round_ties_even`,
сравнения, `int ↔ float` в диапазоне. Константы: `1f / 3f` в C# сворачивается компилятором в IEEE-результат, в Rust `1.0f32 / 3.0` — тот же; литералы с
одинаковой десятичной записью дают одинаковый `f32`.
### 6.2 Ловушки, которые порт обязан повторить
| Что | C# | Rust | Правило |
|---|---|---|---|
| FMA | RyuJIT не сливает `a*b+c` сам; `MathF.FusedMultiplyAdd` запрещён (§9) | LLVM без fast-math не сливает; `f32::mul_add` запрещён (`clippy.toml`); подсказку `clippy::suboptimal_flops` (nursery) **не включать и не применять** | без FMA |
| Смешение типов | `float * double` → `double` молча (`kmh / 3.6`) | неявных преобразований нет | в порте явно `(x as f64 / 3.6) as f32` там, где C# считает в double; ревью сверяет типы выражений построчно |
| min/max | `MathF.Max`: NaN-пропагирующий, `+0 > −0` | `f32::max`: NaN игнорирует, знак нуля не определён | только `detmath::max_net/min_net`, повторяют .NET: `max`: если `x != y` → `x` при NaN(x), иначе `y < x ? x : y`; если равны → `y` с минусом в знаке ? `x` : `y`. `min`: если `x != y` → `x` при NaN(x), иначе `x < y ? x : y`; если равны → `x` с минусом ? `x` : `y` |
| float→int вне диапазона | net8: `int.MinValue`; net10: насыщение | `as`: насыщение, NaN → 0 | в C# (с 3.1c) любое `(int)/(long)` от float/double в Sim.Core идёт через `Util/DetMath.ToIntSat/ToLongSat` (насыщение, NaN → 0, усечение к нулю - ровно Rust `as`); голый `(int)x` допустим только после явного клампа с `!(x > 0f)`-проверкой. В Rust - просто `as i32/i64`; тесты `DetMathTests` дают векторы для `util.json` |
| Переполнение целых | `unchecked` по умолчанию — обёртка | debug — паника | PRNG/хэши/билеты — `wrapping_*`; где переполнения не ждём — паника в тестах ловит баг |
| Сдвиги | счётчик маскируется (`& 31`/`& 63`) | ≥ ширины — паника | `wrapping_shl/shr` или `rotate_*` (PCG: `rotate_right`) |
| Деление целых | к нулю; `/0` — исключение | к нулю; `/0` — паника | делители проверены при `Create` |
| Порядок итерации | свои `FixedHashMap`, списки | `HashMap` std — случайный | `HashMap/HashSet` запрещены; порт `FixedHashMap` и цепочек `CellNext` один в один (первое найденное зависит от порядка) |
| Сортировки | вручную вставками (рукава по `PseudoAngle`) | `sort_unstable` даёт другой порядок равных | переносить тот же алгоритм, не звать `sort*` |
| Денормалы | FTZ/DAZ не включены | так же | хост (Godot, драйверы) может выставить MXCSR в процессе — тест на денормал в `util.json` ловит |
| NaN в состоянии | хэш по битам | так же | NaN в хэшируемых данных — ошибка валидации, не данные |
| Векторизация | JIT не векторизует скалярный код с float-редукцией | LLVM без reassoc векторизует только побитово-эквивалентное | ок; ручные SIMD-интринсики — запрещены в обоих |

### 6.3 Трансцендентные функции и `libm`
- `MathF.Atan2/Sin/Cos/Pow/Exp` в .NET → функции C-рантайма ОС (glibc / UCRT / libSystem); Rust `f32::atan2` и т.п. → тот же системный libm.
  На Linux x64 совпадение замерено (§1). Между ОС не совпадают **и в самом C#**: сейчас трансцендентные влияют на состояние в генераторе города
  (`CityGenerator` cos/sin сдвига узлов, `GridPlan` atan2 → проверка `MinArmAngle` → топология), в `TransitGenerator` (atan2 курсов) и в `Idm.Pow` при `delta ∉ {2, 4}`.
  То есть «один seed → один город» уже сейчас верно только в пределах ОС. В сети пилота трансцендентное — только `LanePieceHeading` (вне хэша).
- Варианты (вопрос владельцу 1): (а) оставить системный libm — детерминизм «в пределах ОС», мультиплеера/реплеев между ОС нет; (б) `DetMath` —
  один и тот же код (порт musl `atan2f/sinf/cosf/powf`, только `+ − × ÷ sqrt`) в C# (`Util/DetMath.cs`) и Rust (`util/detmath.rs`, без крейта): побитово везде,
  цена — ~400 строк × 2, медленнее системных в 1.5–3 раза (оценка, не замер), генератор/IDM меняют golden один раз; (в) убрать трансцендентные из
  хэшируемых путей (псевдоугол уже так сделан в `PseudoAngle`; `cos/sin` сдвига — по таблице из PRNG) — дёшево, но по месту.
### 6.4 Числа из JSON
C#: `JsonElement.TryGetDouble` (корректное округление .NET Core 3.0+) → `(float)` (round-to-nearest-even) — **двойное округление** намеренно
входит в контракт. Rust: токен числа → `str::parse::<f64>()` (корректно округляет) → `as f32`. Целые поля — как `TryGetInt32`: токен без `.`/`e`/`E`,
в диапазоне `i32`, иначе ошибка. `seed` — `u64` числом или десятичной строкой (как `JsonObjectReader.ULong`). Скорости `Mps(kmh) = (float)(kmh / 3.6)` — в `f64`.
`desc_hash` в golden отделяет расхождение загрузчика от расхождения построителя.

## 7. Правила портируемости в Rust (аналог `BannedSymbols.txt`)
`crates/tc-core/clippy.toml`: `disallowed-types` = `std::collections::{HashMap, HashSet}`, `std::time::{Instant, SystemTime}`, `std::fs::File`;
`disallowed-methods` = `std::time::Instant::now`, `std::time::SystemTime::now`, `std::thread::spawn`, `std::fs::*` (read, write, read_to_string),
`std::env::var`, `f32::{mul_add, max, min, sin, cos, tan, atan2, atan, asin, acos, powf, powi, exp, ln, log2, log10}` (и `f64::` то же).
Единственное разрешение — `detmath::atan2_system` с `#[allow(clippy::disallowed_methods)]` и комментарием (`LanePieceHeading`).
`lib.rs` tc-core: `#![forbid(unsafe_code)]`, `#![deny(clippy::print_stdout, clippy::print_stderr, clippy::dbg_macro, clippy::unwrap_used, clippy::expect_used, clippy::panic)]`
(в тестах — `#[cfg_attr(test, allow(...))]`). Горячий путь (пилотный `step`) — без `Vec::push`/`Box::new`/`String`/`format!`: ёмкости из `create` (тест: глобальный
считающий аллокатор в интеграционном тесте `tc-core`, 0 аллокаций на 1 000 `step` — аналог 0 B/тик).

## 8. Набросок: воркеры и Godot (не делать в пилоте)
- Воркеры (ADR-0017): `WorkerPool` → `W−1` потоков `std::thread::Builder` (создание в `create`, `join` в `drop`), чанки по `AtomicUsize::fetch_add`, задание —
  `&dyn Fn(Range<usize>, &mut WorkerAcc)` без аллокаций; выходные массивы фазы — `split_at_mut` по чанкам или `UnsafeCell`-срезы с инвариантом «пишет только [i]»
  (тогда `unsafe` в `tc-core` — отдельный ADR). Аккумуляторы — `i64` по воркеру, сумма по номеру воркера. `rayon` не нужен.
- Godot, путь A (рекомендуется первым): C#-клиент остаётся, `Sim.Core` за интерфейсом `ISimBackend` с двумя реализациями — прямой C# и `[DllImport("tc_ffi")]`
  поверх §2; снапшот читается `ReadOnlySpan<T>` по указателю (`new ReadOnlySpan<T>(ptr, len)`), MultiMesh-буфер заполняется так же, как сейчас.
  Без gdext и без новых зависимостей; переключатель — в настройках клиента.
- Путь B (если C#-клиент уходит): крейт `tc-godot` (`cdylib`, зависимость `godot` = gdext — ADR, версия под Godot 4.7 проверяется тогда) с `#[derive(GodotClass)] struct TrafficSim { sim: tc_core::Sim }`,
  `#[func] step(ticks)`, `#[func] fill_multimesh(rid)` — Rust пишет `PackedFloat32Array` (12 float на экземпляр) и зовёт `RenderingServer::multimesh_set_buffer`;
  копия на кадр одна (как сейчас в C#-клиенте). `tc-core` от gdext не зависит.

## 9. CI (`.github/workflows/ci.yml`, новый job `rust`, параллельно `build-test-bench`)
`ubuntu-latest` (rustup предустановлен; `rust-toolchain.toml` ставит 1.97.0 + компоненты), `working-directory: rust`:
`cargo fmt --all --check` → `cargo clippy --workspace --all-targets --locked -- -D warnings` → `cargo test --workspace --locked` →
`cargo build --release -p tc-ffi --locked` → `bash ../scripts/rust-check.sh --deps-only` → `actions/setup-dotnet` (по `global.json`) →
`dotnet run --project ffi-smoke -c Release -- --lib target/release/libtc_ffi.so --golden ../tests/golden/rust-pilot`.
Кэш — `actions/cache` на `rust/target` по хэшу `Cargo.lock` + `rust-toolchain.toml`. arm64-раннер (`ubuntu-24.04-arm`) — позже, вопрос 3.
Хуки Claude Code (`.claude/settings.json`) про `.rs` не знают — до решения владельца (вопрос 4) исполнитель сам гоняет `bash scripts/rust-check.sh`.

## 10. Задачи
Все — sonnet, кроме R8. Исполнитель: `scripts/rust-check.sh` (Rust) и `dotnet build/test --filter` своей области (C#), без бенчей.
Rust-задачи не трогают C#-файлы и наоборот; R2 трогает `Simulation.cs` одной строкой — **после мержа волны P (пешеходы)**.

| № | Что | Файлы | Приёмка / проверки | Зависит |
|---|---|---|---|---|
| R1 | Workspace + `Util` + правила: `Cargo.toml`, toolchain, fmt, `clippy.toml`, `allowed-crates.txt`, `scripts/rust-check.sh`; `tc-core::util`: `Pcg32` (`create/from_state/next_u32/next_below/next_int/next_float01/next_range`), `Fnv1a64`, `XxHash64` (разом и потоково), `Vec2`, `detmath` (`max_net/min_net`, `atan2_system`), `RingBuffer`, `FixedHashMap` (порт C#) | `rust/**` (кроме ffi-smoke), `scripts/rust-check.sh` | `rust-check.sh` зелёный; unit-тесты на вектора, перенесённые из C#-тестов `Util` (PCG против референса C — те же числа); `cargo metadata` без сторонних пакетов; запрещённый метод в `tc-core` → clippy красный (проверить и откатить) | — |
| R2 | C#: `Simulation.ComputeNetworkHash()` + внутренние `conflicts`/`headings`/`desc` хэши (§5.2), команда `Sim.Headless golden --out <dir>` (вектора `util.json` + файл на сценарий), фикстуры §5.1, тест `RustPilotGoldenTests`, сгенерированные golden-файлы; строка в `core-api.md` §3/§9 | `src/Sim.Core/Metrics/NetworkHash.cs`, `Api/Simulation.cs` (1 метод), `src/Sim.Headless/` (команда), `tests/golden/rust-pilot/**`, `tests/Sim.Core.Tests/Network/RustPilotGoldenTests.cs`, `docs/design/core-api.md` | build -warnaserror, тест зелёный; хэш состояния и сейвы не изменились (существующие тесты детерминизма/сейвов зелёные); ручная порча одного числа в golden → тест красный | после P-волны |
| R3 | `tc-scenarios`: ридер JSON (RFC 8259, без комментариев/висячих запятых, глубина ≤ 64, `\u`-суррогаты, числа токеном), загрузчик подмножества (§6.4; неизвестные ключи в разбираемых объектах — ошибка, как C#; прочие секции пропускаются), `desc_hash` | `rust/crates/tc-scenarios/**` | `desc_hash` и `load_ok` совпадают со всеми golden; unit-тесты ридера (экранирование, `-0`, `1e-3`, `0.1`+`0.2` → биты как `double.Parse`, глубина, обрезанный ввод — ошибка без паники) | R1, R2 |
| R4 | `tc-core::network` + построение connector'ов: порт `NetworkStore`, `NetworkTypes`, `LaneGeometry`, `NetworkBuilder`(+`.Connectors`), `ConnectorStore` без конфликтов; `network_hash`, `headings_hash`; `Sim::create` для явной сети (ёмкости и `CreateStatus` как `WorldBuilder`) | `rust/crates/tc-core/src/{network,intersections,api,sim.rs}` | `network_hash`, `counts`, `create_status` совпадают со всеми golden; `headings_hash` — совпадает на Linux (если нет — отчёт, не блокер); 0 аллокаций в `step`; при расхождении — бисекция по разделам §5.2 (временный отладочный дамп по разделам в тесте, не в API) | R1, R2, R3 |
| R5 | Конфликты: `ConnectorStore.BuildConflicts`/`BuildAll` (пересечения полилиний, `SMineCm/SOtherCm`, маски), `conflicts_hash` | `rust/crates/tc-core/src/intersections/**` | `conflicts_hash` совпадает со всеми golden | R4 |
| R6 | `tc-ffi` + `include/tc_sim.h` (§2) + `rust/ffi-smoke` (C#, `[DllImport]`, `SafeHandle`, зеркальные struct) | `rust/crates/tc-ffi/**`, `rust/ffi-smoke/**` | `ffi-smoke` на всех golden-сценариях: размеры struct, `network_hash` == C#, `create_status` == C#, `step(100)`, переполнение очереди, NULL-аргументы → коды, тестовая паника (cfg-фича `test-panic`) → `TC_ERR_PANIC`, затем `TC_ERR_POISONED`, `destroy` ок; `dotnet format rust/ffi-smoke --verify-no-changes` | R4 (R5 — для `conflicts`) |
| R7 | (опц.) IDM-вектора: чистая функция `Idm` C# → Rust; `util.json` + 10 000 наборов аргументов из PCG (скорости, зазоры, параметры типов `vehicle-types.json`, `delta` ∈ {4, 2}) → биты ускорения | `rust/crates/tc-core/src/rules/idm.rs`; генерация в команде `golden` (C#, 1 файл) | побитово на всех наборах; `delta ∉ {2,4}` (`MathF.Pow`) — отдельный счётчик расхождений, не гейт | R1, R2 |
| R8 | haiku-верификатор + отчёт: полный `rust-check.sh`, `dotnet test`, `ffi-smoke`, время CI-job; сводка расхождений, найденных в R3–R7, и их причин → `docs/REPORT-rust-pilot.md` (оркестратор), строки в `lessons/rust.md`, правки `core-api.md` §9 | отчёт | цифры только из прогонов | R1–R6 (R7 — если делали) |
| R-CI | job `rust` в `ci.yml` (§9) — вместе с R1 (fmt/clippy/test) и дополнить в R6 (ffi-smoke) | `.github/workflows/ci.yml` | job зелёный в PR | R1, R6 |

Параллельно можно: R1 ‖ R2 (разные языки и файлы). R3 → R4 → R5 последовательно (одни модули), R6 после R4, R7 после R1+R2 параллельно R3–R5.
Оценка объёма: R1 ≈ 500 строк Rust, R2 ≈ 400 C#, R3 ≈ 500, R4 ≈ 1 300, R5 ≈ 450, R6 ≈ 400 Rust + 300 C#.

## 11. Открытые вопросы владельцу
| № | Вопрос | Рекомендация | Трейдофф |
|---|---|---|---|
| 1 | Трансцендентная математика (§6.3): системный libm, общий `DetMath` в обоих языках или убрать из хэшируемых путей? | Сейчас (а) + не допускать новых трансцендентных в хэш; перед мультиплеером/реплеями между ОС или переездом генератора на Rust — (б) | (б) ~800 строк, медленнее, один раз меняет golden и города всех seed; (а) — «один seed → один город» только в пределах ОС (уже так) |
| 2 | Свой JSON-ридер или `serde_json` (`float_roundtrip`)? | Свой (0 зависимостей, контроль чисел) | ~300 строк и тесты на свою ответственность; `serde_json` — 5 крейтов в дереве и ADR |
| 3 | arm64 в CI (`ubuntu-24.04-arm`) для проверки x64 ↔ arm64? | Да, после R6, только `cargo test` + golden C# | +1 job; на arm64 libm другой → `headings_hash` может разойтись (это и узнаем) |
| 4 | Хуки Claude Code для `.rs` (PostToolUse `cargo check`, Stop — `rust-check.sh`)? | Да, после R1 (правка `.claude/` — владелец/оркестратор) | +время на каждой правке `.rs`; без хука агент может забыть проверку |
| 5 | Чинить в C# приведение float→int вне диапазона (net8 ≠ net10)? | Да, отдельной задачей: аудит `(int)`/`(ushort)` от float в `Sim.Core`, кламп до приведения | правка C# может сдвинуть хэш, если такие значения реально встречаются (тогда это баг, а не поведение) |
