# Формат сохранения и версия мира (спецификация, шаг 2.6b)

Статус: предложено (ADR-0012). Источник истины для `src/Sim.Core/Save/` и хостового конверта файла. Связано:
`core-api.md` §0 (I1–I5), §3, §4, §9, §12; `scenario-format.md` §1 (правила версий JSON); `sim-lod.md` §10 п.3; ADR-0017 п.4.

## 0. Инварианты сейва

| # | Инвариант | Проверка (§9) |
|---|---|---|
| S1 | Сейв — снимок **между** `Step`; ядро не читает файлы: байты уходят в `Span<byte>` / `SaveSink` хоста | API §6, анализатор (`System.IO` запрещён) |
| S2 | Самодостаточен: для `Load` не нужен сценарий, генератор, файлы типов ТС | загрузка корпуса без `scenarios/` |
| S3 | Побитовое продолжение: `Load(Save(w))` продолжается теми же хэшами, что `w` | T2, T3 |
| S4 | Канонический: одно состояние → одни байты (C# и Rust); `Save(Load(Save(w))) == Save(w)` побайтно | T1 |
| S5 | Любой вход (обрезанный, испорченный, чужой) → код ошибки, не исключение и не падение; после `Ok` — `Step` безопасен | T7 |
| S6 | `Save` в готовый буфер — 0 Б; после `Load` — 0 Б/тик в стационаре | T8 |
| S7 | Число потоков, `RenderView`, `OnPhaseEnd`, `SimWork` в сейв не входят; сейв при 1 потоке грузится при 2 и наоборот | T3 |

## 1. Что сохраняется

Правило: **сохраняется всё, что следующий тик читает раньше, чем перезаписывает** (+ параметры). Не сохраняются:
**производные** (чистая функция сохранённого, независимая от истории — `Load` строит их тем же кодом, что `Create`/`BuildRoad`),
**scratch** (живёт внутри тика), **выходы для клиента** (снапшоты, события). Сомнение → сохраняем. Ошибку классификации
ловит T1/T2 (повторный сейв не совпал / разошёлся хэш продолжения).

| Хранилище | Сохраняется | Производное / не сохраняется |
|---|---|---|
| Параметры `World` | `Seed`, `Caps`, `Rules`, `Routing`, `Lod`, `Demand` (скаляры, `Zones`, `OdPairs`), `TransitCfg`, `Types`, `City.LaneWidth/ConnectorSamples/Enabled` (нужны `NetworkBuilder` для `BuildRoad`) | генератор (`CityGenDesc` кроме этих полей), `CityInfo`, `SignalPlanSeeds`, `GenTransit` (только `Create`) |
| Время, PRNG, управление | `Tick`, 6 × `Pcg32 (State, Inc)`, `TargetVehicles`, `Selected`, `GridlockStreak`, `RecalcPending`, `Net.Version` | `SnapshotTick` (= `Tick`), `EventCount`, `EventsOverflowed`, `DebugDisableExitCheck` (тестовый) |
| `SimCounters` | все поля | `SimWork` (профилирование) |
| `NetworkStore` статика | `Nodes`, `Segments`, `Lanes` (все поля), `NodeArms`, `ArmFlags`, `ArmDir`, `LaneNext`, `SegmentPoints`, `LanePoints`, `LanePointS`, `Zones`, `ZoneLanes`, `BusStops`, `BusRoutes`, `BusRouteStops`, счётчики `*Count` | `LanePieceHeading`, `LaneLength`, `LaneGateNode`, `IncHead/IncNext`, `CellNext`, `Cells` (вставка в порядке индекса = исходной, пока нет сноса) |
| `NetworkStore` динамика | `NodeTicket`, `OutReserved`, `BusStops.Waiting/LastArrivalTick`, `BusRoutes.ActiveBuses/LastDispatchTick` | — |
| `ConnectorStore` | `Count`, `ConflictsUsed`, `Lane`, `Node`, `InLane`, `OutLane`, `LocalBit`, `Maneuver`, `FromArm`, `Flags`, `FirstConflict`, `ConflictCount`, `ConflictMask`, `Conflicts` — раскладка зависит от истории `BuildRoad` | `Length`, `InLength` |
| `SignalStore` + выходы фазы 4 | все массивы до `Count` (включая `PhaseMask`, `TicksLeft`), `SignalStates`, `SignalCount`, `LaneSignals` (1 Б/полосу — дешевле, чем доказывать чистоту) | — |
| `VehicleStore` | `Pool` (поколения, free-list **в LIFO-порядке**, `HighWater`), `Lane, S, Speed, Accel, Length, Type, DesiredSpeed, Ahead, Behind, GhostLane, GhostAhead, GhostBehind, LaneChangeDir, LaneChangeTicks, NextMobilTick, Route, RouteGen, RouteCursor, DestZone, DestLane, DestS, NextConn, GateState, GrantConnector, Ticket, WaitTicks, PointHint, Lateral, SpawnTick, StillTicks, Flags, BusRoute, BusNextStop, DwellTicks, Passengers` (124 Б/слот) | `ClampLeadGap/ClampFlags/ClampGateGap` (scratch 5→6), `RouteDirty` (после `Load` = 1 у живых: пересчёт входа `AssignConnectors` тот же) |
| `LaneOccupancy` | `LaneHead`, `LaneTail`, `LaneCount` | — |
| Очереди | `PendingTrips` (FIFO + `_unrouted`), `Rerouting`, `RefillQueue`, `Commands` (не применённые команды — 64 Б сырые, раскладка `core-api.md` §4 и так C ABI) — в **логическом** порядке, `head` при загрузке = 0 | — |
| `RouteStore` | `Pool`, `WinCount, PlanFirst, PlanCount, PlanCap, PlanCost, PlanTick`, окна живых слотов (`WinCount` шагов), `PlanArena[0..PlanUsed)`, `Compactions`. Окна и планы **не** производные: распакованы при старых весах | `_compactKeys/_compactItems` |
| `RouteGraph` | `SegCost` (веса по загрузке на момент пересчёта), `WeightsVersion` | топология (`ArcFirst/To/Cost/Maneuver`, `StartNode/EndNode/EndPos`, `VMax`): `Rebuild()`, затем вернуть `SegCost` |
| `Overlay` | параметры (проверка), раскладка (`LevelNx/Nz`, `CellBase`, `Entry*/Exit*First/Count/Cap`, `MatrixFirst`, `Entries`, `Exits`, построенные узлы/DirSeg, `_failed`), `Matrix` (до занятой длины), `Dirty`, `CustomizedSegCost`, `WeightsVersion`, `WaveActive/Cell/Row`, `Disabled`, `Rebuilds`. Матрицы посреди волны — смесь старых и новых весов, пересчитать нельзя | `NodeCellX/Z`, `CutLevel`, `LevelDiv`, `EntryIdx/ExitIdx` |
| Метрики фазы 9 | `SegmentLoads` (читает фаза 3 следующего тика), `NodeThroughput`, `BusRouteStats` | — |
| Спавнер, ворота, воркеры | — | `SpawnerData` (из зон и типов), `GateScratch`, `Behaviour/Integrate/SnapshotJob`, `Pool` |
| Снапшоты, события | — | `Render[2]`: после `Load` фаза 10 заполняет текущий, `PreviousVehicles` = текущий; `Events` пуст |
| LOD (ADR-0013, когда появится) | по машине `Lod`, `MesoAllow`, `MesoFresh`; по ячейке `CellLevel`, `CellHoldUntil`, `CellCongStreak`; `Focus[0..7]`, `LodEvalRequested`, `LastLodEvalTick` | структура ячеек (`LaneCell` и т.п.) |

**Мёртвые слоты** (машины, маршруты, блоки арены вне живых планов, хвосты `PlanCap > PlanCount`) пишутся **каноническими
значениями** — теми, что получает свежий слот (ссылки −1, остальное 0), кроме поколения. Так байты сейва не зависят от мусора.
Если `Spawn` не переинициализирует какое-то поле свежего слота — это баг, который всплывёт в T2: тогда поле чинится в ядре.

## 2. Контейнер (сырой, его пишет и читает ядро)

Все числа little-endian, `float` — битовый образ IEEE-754 (NaN-payload и −0 сохраняются). Платформа big-endian → `NotSupported`.

```
Заголовок, 64 Б
  0  magic[8]        "TCSAVE\r\n"             // \r\n ловит порчу текстовым режимом
  8  u16 container_version = 1
 10  u16 header_size = 64
 12  u32 world_version                         // §4
 16  i64 tick
 24  u64 state_hash                            // core-api §9 на момент Save
 32  u32 hash_version                          // версия порядка §9 (растёт при правке §9)
 36  u32 block_count
 40  u64 total_length                          // весь контейнер с подвалом — быстрый признак обрезки
 48  u16 writer (1 = C#, 2 = Rust), u16 reserved, u32 writer_version (major<<20 | minor<<10 | patch)
 56  u64 xxh64(заголовок[0..56) + оглавление)
Оглавление: block_count × 32 Б, порядок = порядок блоков в файле
  u32 id (FourCC, байты ASCII как в имени: 'V','E','H','C')
  u16 schema_version
  u16 flags          // бит0 critical; бит1 host (блок хоста/мода); бит2 kept (переписан из чужого сейва без разбора)
  u64 offset         // от начала файла, кратно 8
  u64 length         // длина полезной нагрузки без трейлера
Блок: полезная нагрузка, затем u64 xxh64(нагрузки), затем нули до кратности 8
Подвал, 16 Б: magic "TCEND\0\0\0", u64 xxh64(всего файла до подвала)
```

**Нагрузка блока ядра** — набор именованных столбцов (SoA):
```
u32 column_count, u32 reserved = 0
column_count × 24 Б, по возрастанию column_id:
  u32 column_id     // номер столбца внутри блока (§3; выбывший id не переиспользуется)
  u8  type          // 1 u8, 2 i8, 3 u16, 4 i16, 5 u32, 6 i32, 7 u64, 8 i64, 9 f32, 10 f64, 11 raw (запись elem_size байт)
  u8  elem_size     // байт на элемент (для 1–10 — проверяется)
  u16 reserved = 0
  u64 offset        // от начала нагрузки, кратно 8
  u64 count         // элементов
данные столбцов подряд, каждый с нулевым выравниванием до 8
```
Скаляр — столбец длины 1. `bool` — `u8` 0/1. Enum — его базовый тип. `Vec2` — два столбца `…X`, `…Z`. Строки — `raw`/`u8`
(UTF-8, ядро их не разбирает). Struct'ы C# в файл не копируются — только по полям (формат не зависит от `Pack`/порядка полей
и от рефакторинга AoS → SoA). Блоки хоста — нагрузка произвольная (ядро её не разбирает).

Канон: блоки ядра в порядке таблицы §3, затем блоки хоста в порядке передачи, затем `kept`; столбцы по возрастанию id;
все reserved и паддинги — нули. Выравнивание 8 даёт Rust-читателю `bytemuck`-срезы без копии (и mmap).

Раскладка плотная (реализация S1): первый блок начинается сразу за оглавлением, каждый следующий — сразу за предыдущим
(`offset = конец предыдущего`), подвал — сразу за последним; столбцы внутри блока тоже без дыр. Дыра, перекрытие, повтор id
блока → `InvalidReference` (оглавление) / `BadColumn` (каталог); смещение или длина за концом файла → `Truncated`; неизвестные
биты `flags`, `kept`+`critical`, ненулевые reserved/паддинги, неизвестный `writer` → `InvalidValue` (паддинги столбцов → `BadColumn`).
Байты за `total_length` читатель игнорирует. Блоки `host`/`kept` непрозрачны; неизвестный блок без `critical` читатель
превращает в `kept` (флаг ставится сам), поэтому `Load → Save` переписывает его побайтно. `SaveSource` читается целиком в
буфер (растёт по мере поступления байт, не по заявленной `total_length`), предел одного массива .NET (~2 ГБ) → `CapacityExceeded`.

Проверки при чтении (до любой аллокации по данным файла): magic, версии, `header_size`, `total_length ≤` длины входа
(иначе `Truncated`), xxh64 заголовка+оглавления, смещения кратны 8 / внутри файла / по возрастанию / без перекрытий,
xxh64 каждого блока и подвала, каталог столбцов (тип, `elem_size`, `offset + count·elem_size ≤ length`, id по возрастанию).
Аллокация по `count` — только после сверки с длиной блока (защита от «count = 2^60»).

## 3. Блоки `world_version = 1`

`C` — critical. Столбцы — поля в порядке перечисления, `column_id` = порядковый номер с 1. Точные списки фиксирует S2 в
приложении A этого файла (таблица `блок / id / имя / тип / длина / дефолт`) одновременно с кодом `Save/SaveSchema.cs` —
единственным реестром id в коде; тест сверяет реестр с приложением.

| FourCC | C | Строк / длина | Содержимое |
|---|---|---|---|
| `PARM` | C | 1 (скаляры) | `Seed`; поля `SimCapacities`, `RulesDesc`, `RoutingDesc`, `LodDesc`, скаляры `DemandDesc`/`TransitDesc`, `LaneWidth`, `ConnectorSamples`, `CityEnabled` |
| `DEMD` | C | зоны / OD-пары | `DemandDesc.Zones`, `DemandDesc.OdPairs` по полям |
| `VTYP` | C | типы ТС | `VehicleTypeDesc` по полям — симуляционные параметры ассетов (§5) |
| `MANI` | C | записи | манифест ассетов (§5) |
| `WRLD` | C | 1 + 6 | `Tick`, `NetVersion`, `TargetVehicles`, `SelectedIndex/Gen`, `GridlockStreak`, `RecalcPending`, `RngState[6]`, `RngInc[6]` (порядок `RngStream`) |
| `CNTR` | C | 1 | `SimCounters` по полям (новый счётчик = новый столбец, дефолт 0) |
| `NETW` | C | 1 | счётчики `NetworkStore` (`NodeCount` … `BusRouteStopCount`) |
| `NNOD` | C | `NodeCount` | `NodeData` по полям |
| `NSEG` | C | `SegmentCount` | `SegmentData` по полям |
| `NLAN` | C | `LaneCount` | `LaneData` по полям |
| `NARM` | C | `NodeArmCount` | `NodeArms`, `ArmFlags`, `ArmDirX/Z` |
| `NLNX` | C | `LaneNextCount` | `LaneNext` |
| `NPTS` | C | точки | `SegmentPointsX/Z` [`SegmentPointCount`], `LanePointsX/Z`, `LanePointS` [`LanePointCount`] |
| `NZON` | C | `ZoneCount` | `ZoneData` по полям, `ZoneLanes` |
| `NBUS` | C | остановки / маршруты | статические поля `BusStopData`/`BusRouteData`, `BusRouteStops` |
| `CONN` | C | `Count` / `ConflictsUsed` | §1, `Conflicts` по полям (`OtherLocal, Flags, SMineCm, SOtherCm`) |
| `SIGS` | C | `Count` | `SignalStore` (все массивы до `Count`, `GreenTicks`/`PhaseMask` — `Count·8`), `SignalStates`, `SignalCount` |
| `NDYN` | C | `NodeCount` | `NodeTicket`, `NodeThroughput` |
| `LDYN` | C | `LaneCount` | `LaneHead`, `LaneTail`, `LaneCount`, `OutReserved`, `LaneSignals` |
| `SDYN` | C | `SegmentCount` | `SegmentLoads` по полям |
| `BDYN` | C | остановки / маршруты | `Waiting`, `LastArrivalTick`; `ActiveBuses`, `LastDispatchTick`; `BusRouteStats` по полям |
| `VEHC` | C | `HighWater` | `HighWater`, `FreeList` (снизу вверх), `Generation`, поля §1 |
| `TRIP` | C | очередь | `PendingTrip` по полям в порядке FIFO, `Unrouted` |
| `QUES` | C | очереди | `Rerouting (Index, Generation, ReadyTick, Kind)`, `Refill (Index, Generation)`, `Commands` (`raw`, 64 Б) |
| `ROUT` | C | слоты / шаги | `HighWater`, `FreeList`, `Generation`, `WinCount`, `PlanFirst/Count/Cap`, `PlanCost`, `PlanTick`, `WindowSteps` (проверка), `WinLane/WinNode` (окна живых слотов подряд), `PlanArena[PlanUsed]`, `Compactions` |
| `RGRP` | C | `DirSegCount` | `SegCost`, `WeightsVersion` |
| `OVLY` | C | — | есть ⇔ `Overlay != null`; §1 |
| `LODS` | C | — | (ADR-0013) есть ⇔ `Lod.Mode != Off`; §1 |

Блоки хоста (флаг `host`, id со строчной первой буквой, ядро не разбирает): `meta` (имя города, время создания/изменения
по часам хоста, сыгранные тики, имя сценария, версия игры), `thmb` (превью PNG), `camr` (камера), `mod?` (состояние модов,
ADR-0015). Хост сам решает `critical` для своих блоков.

Статика сети (`N*`, `CONN`, раскладка `OVLY`) отделена от динамики (`*DYN`, `VEHC`, `ROUT`…) намеренно: будущий
«компактный сейв» опускает статику и пересобирает её (§11 п.3) без смены остальных блоков.

## 4. Версии и миграции

- `container_version` — раскладка заголовка/оглавления/каталога столбцов. Меняется почти никогда.
- **`world_version`** — «версия мира», монотонное целое в ядре (`SaveSchema.WorldVersion`). `++` при **любом** изменении:
  новый/удалённый блок или столбец, смена типа, единиц, смысла, инварианта. Таблица изменений — §12 (журнал).
- `schema_version` блока — растёт, когда меняется трактовка блока целиком (индексация строк, разбиение); новые столбцы её не меняют.
- `hash_version` — порядок `core-api.md` §9; сравнение хэша после `Load` только при равных `hash_version` и без миграций.

Правила изменения (вписаны в DoD задач, меняющих состояние ядра):

| Изменение | Что делать |
|---|---|
| Новое поле состояния | новый столбец, дефолт в приложении A (старый сейв → дефолт, кода миграции нет), `world_version++` |
| Поле стало производным / удалено | столбец больше не пишется, id выбывает; читатель старых версий его пропускает по таблице миграции |
| Смена типа/единиц/смысла | новый id столбца + шаг миграции, вычисляющий его из старого |
| Перестройка блока (AoS↔SoA не считается — формат уже SoA) | `schema_version++` блока + шаг миграции |
| Смена модели (класс Economy 2.0 у CS2) | смысловой шаг миграции (пересчёт состояния под новую модель) + тест «сейв vN → N игровых дней → инварианты» |

**Цепочка**: `Load` декодирует блоки в `SaveTables` (массив блоков → столбцы как `byte[]`; аллокации допустимы), затем
применяет `Migrations[v]` для `v = file.world_version .. current−1`. Шаг — `static MigrationStatus Step(SaveTables t)`:
чистая функция над таблицами (без `World`, без PRNG, без времени), переносится на Rust 1:1. Шаг вправе добавлять/удалять
столбцы и блоки, но не вызывать код симуляции. После цепочки — те же проверки, что для текущей версии. `world_version`
файла > текущей → `TooNew` (вниз не мигрируем). Каждый шаг сопровождается golden-сейвом исходной версии в корпусе (§7).

**Неизвестное.** Неизвестный столбец в известном блоке → `UnknownColumn` (как неизвестный ключ JSON: неизвестное в
принятой версии = порча). Неизвестный блок: `critical` → `UnknownCriticalBlock`; иначе блок хранится как есть
(`World.KeptBlocks`, байты + id + flags) и **переписывается в следующий сейв** с флагом `kept`; в `LoadReport` — счётчик.
Отсутствует обязательный блок → `MissingBlock` (кроме введённых позже версии файла — их создаёт миграция).

## 5. Манифест ассетов и заглушки

`MANI`: `Kind u8[]` (1 тип ТС, 2 здание — резерв, 3 проп — резерв, 4 пакет/мод), `SavedIndex i32[]`, `IdOffset u32[]`,
`IdLength u32[]`, `Version u32[]`, `ContentHash u64[]` (xxh64 параметров/файла ассета — считает хост), `IdBlob u8[]`
(UTF-8, например `vehicle:car_sedan`, `mod:transit_plus/bus_long`). Ядро строки не разбирает: хост задаёт манифест после
`Create` (`SetAssetManifest`), ядро хранит его, пишет в сейв и отдаёт после `Load`.

**Принцип: сейв несёт симуляционные параметры каждого ассета, на который ссылается состояние** (`VTYP` — типы ТС целиком;
будущие здания — их данные: габарит, вместимость, правила по ADR-0011/0015). Пакет ассетов даёт только визуал и значения
для новых городов. Поэтому:
- **Ассет отсутствует** (снят мод/DLC): симуляция грузится и продолжается побитово. Клиент рисует **явную заглушку** — бокс по
  габаритам из сейва (`Length × Width`; для зданий — footprint), контрастный «нет ассета» материал с меткой «?», — и
  показывает список недостающих (`AssetReport` хоста: вид, id, число сущностей). Запись манифеста при пересохранении
  **сохраняется** — вернули мод → вернулся визуал. Тихой подмены нет.
- **Ассет обновился** (`ContentHash` другой): по умолчанию действуют параметры из сейва (продолжение побитовое); опция
  `LoadOptions.AssetParams = UseCurrentForNewSpawns` — хост передаёт новые `VehicleTypeDesc`, ядро применяет их только к
  новым спавнам, отчёт отмечает расхождение.
- **Состояние мода** — блок хоста: мод-данные (`critical = 0`) теряют функцию, но сохраняются; мод-механика с состоянием, без
  которого мир неверен (`critical = 1`), — сейв не грузится без мода с понятной ошибкой. Выбор — ADR-0015.

## 6. API ядра

```csharp
namespace Sim.Core.Api;

public abstract class SaveSink   { public abstract void Write(ReadOnlySpan<byte> data); }   // поток хоста; FFI — колбэк
public abstract class SaveSource { public abstract int Read(Span<byte> buffer); }           // 0 = конец

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HostBlock { public uint Id; public ushort Flags; public ushort Reserved; public int Offset, Length; } // срез hostData

public enum SaveStatus : int { Ok = 0, BufferTooSmall = 1, Disposed = 2, InvalidHostBlock = 3, InvalidTables = 4 /* описание блоков/столбцов источника противоречиво */ }
public enum LoadStatus : int
{
    Ok = 0, NotASave = 1, Truncated = 2, ChecksumMismatch = 3, UnsupportedContainer = 4, TooNew = 5,
    UnknownCriticalBlock = 6, MissingBlock = 7, UnknownColumn = 8, BadColumn = 9 /* тип/длина */, InvalidReference = 10,
    InvalidValue = 11 /* NaN, вне диапазона */, CapacityExceeded = 12, MigrationFailed = 13, HashMismatch = 14, NotSupported = 15,
}

public struct LoadOptions
{
    public SimCapacities CapacityOverride; // 0 в поле = как в сейве; иначе ≥ занятого. Продолжение уже не обязано совпасть с прогоном без сейва
    public byte StrictHash;                // 1 = HashMismatch — ошибка (тесты, CI); 0 = флаг в отчёте
    public byte AssetParams;               // 0 KeepSaved, 1 UseCurrentForNewSpawns (+ CurrentTypes)
    public VehicleTypeDesc[]? CurrentTypes;
}

public struct LoadReport                   // без строк: хост переводит в текст
{
    public LoadStatus Status;
    public int FileWorldVersion, MigrationsApplied, KeptBlocks;
    public uint FailedBlock, FailedColumn; public long FailedRow;   // где сломалось (0 — неприменимо)
    public byte HashChecked, HashMatched;
}

public struct SaveHeaderInfo { public int ContainerVersion, WorldVersion, HashVersion, BlockCount, Writer, WriterVersion; public long Tick, TotalLength; public ulong StateHash; }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct AssetRef { public byte Kind; public byte R0, R1, R2; public int SavedIndex; public uint IdOffset, IdLength, Version, R3; public ulong ContentHash; }

public sealed partial class Simulation
{
    public long MeasureSave(ReadOnlySpan<HostBlock> host = default);                    // точная длина контейнера
    public SaveStatus Save(Span<byte> dest, out long written, ReadOnlySpan<HostBlock> host = default, ReadOnlySpan<byte> hostData = default);
    public SaveStatus Save(SaveSink sink, ReadOnlySpan<HostBlock> host = default, ReadOnlySpan<byte> hostData = default);
    public static LoadStatus Load(ReadOnlySpan<byte> src, in LoadOptions o, in SimRuntimeOptions rt, out Simulation? sim, out LoadReport report);
    public static LoadStatus Load(SaveSource src, in LoadOptions o, in SimRuntimeOptions rt, out Simulation? sim, out LoadReport report);
    public static LoadStatus InspectSave(ReadOnlySpan<byte> head, out SaveHeaderInfo info); // первые 64 Б; для списка сейвов

    public void SetAssetManifest(ReadOnlySpan<AssetRef> refs, ReadOnlySpan<byte> ids);
    public ReadOnlySpan<AssetRef> AssetManifest { get; }
    public ReadOnlySpan<byte> AssetIds { get; }
    public ReadOnlySpan<HostBlock> LoadedHostBlocks { get; }    // блоки хоста из загруженного сейва (камера, meta)
    public ReadOnlySpan<byte> LoadedHostData { get; }
}
```

- `Save` — только между `Step` (тот же поток, воркеры спят). Не меняет состояние (хэш до/после равен). В `Span` — 0 Б аллокаций:
  столбцы пишутся из массивов `World` напрямую (живые — копией, мёртвые слоты — каноном), xxh64 потоковый.
  `Span` ограничен 2 ГБ — для s1m+ с жителями нужен `SaveSink`. `SaveSink` вызывается кусками ≤ 1 МБ.
- `Load` = `Create` без генератора: аллокации по ёмкостям из `PARM` (или `CapacityOverride`), заполнение, проверки (§6.1),
  построение производных, фаза 10 для снапшота, запуск воркеров по `rt`. Загрузчик не бросает исключений: все индексы и
  длины проверены до использования; `try/catch` как способ валидации запрещён.
- `Overlay`, `RouteGraph`: нужны конструкторы «из сохранённого» — без начальной кастомизации (s1m — 3 с в `Create`);
  `RouteGraph.Rebuild()` сбрасывает `SegCost` к свободному потоку — после него вернуть сохранённые `SegCost`/`WeightsVersion`.
- FFI (`core-api.md` §12, добавляет S2): `tc_sim_measure_save`, `tc_sim_save(sim, dst, cap, *written)`,
  `tc_sim_save_stream(sim, int32_t (*write)(void*, const uint8_t*, int64_t), void*)`,
  `tc_sim_load(src, len, *load_options, *runtime_options, **out, *report)`, `tc_sim_inspect_save`.

### 6.1 Проверки при загрузке (после миграций)
- Ёмкости в пределах `Create` (`[1, 2^24]`…); каждый `*Count`/`HighWater`/`PlanUsed`/длина очереди ≤ ёмкости; длины столбцов
  соответствуют своим счётчикам.
- Каждая ссылка в диапазоне или равна своему «нет» (−1): узлы, сегменты, полосы, connector'ы, конфликты (`FirstConflict +
  ConflictCount ≤ ConflictsUsed`), диапазоны точек/рукавов/`LaneNext`, зоны, остановки, маршруты, слоты машин/маршрутов
  (+ поколение живого слота), `PlanFirst + PlanCap ≤ PlanUsed`, `WinCount ≤ WindowSteps`, `Selected`.
- Пулы: free-list без повторов, только мёртвые слоты `< HighWater`, поколение живого нечётно, мёртвого — чётно.
- Списки полос: обход от `LaneHead` по `Behind` заканчивается за `LaneCount` шагов на `LaneTail`, `Ahead/Behind`
  взаимны, `Lane` машины совпадает; ghost-списки — то же через `Ghost*`. Машина в ровно одном основном списке.
- Числа: все `float` состояния конечны; `0 ≤ S ≤ длина полосы + допуск`, `Speed ≥ 0`, длины полос > 0, точки полилиний
  по `s` неубывают. Перечисления — в известных значениях.
- Нарушение → код + `FailedBlock/Column/Row`; мир не создаётся (частично созданный освобождается).

## 7. Хост: файл, сжатие, автосейв, корпус

**Конверт файла `.tcsave`** (пишет `Sim.Scenarios/SaveFile.cs`, net8, IO разрешён; Rust-хост — так же):
```
0  magic "TCSZ\r\n\x1a\n" (8)
8  u16 envelope_version = 1, u16 codec (0 нет, 1 deflate RFC 1951, 2 zstd — резерв), u32 reserved
16 u64 raw_length, 24 u64 xxh64(сырого контейнера)
32 копия 64-байтного заголовка контейнера (список сейвов читается без распаковки)
96 сжатый поток сырого контейнера
```
Ядро сжатие не делает: `System.IO.Compression` в ядре запрещён, и сжатые байты C#/Rust не совпадают; канонический — сырой
контейнер. Кодек v1 — deflate (`CompressionLevel.Fastest`, без зависимостей); zstd — через `codec` позже (NuGet в хосте).

**Автосейв** (клиент, отдельная задача): `Save` в предвыделенный буфер (`MeasureSave` + запас) на потоке симуляции между
тиками → сжатие и запись в фоне → запись во временный файл + атомарное переименование; кольцо из N файлов; битый файл
не затирает последний хороший. Пауза кадра = только копия в буфер.

**Golden-корпус** `tests/SaveCorpus/` (бинарные, `.gitattributes: binary`, бюджет ≤ 5 МБ):
- `w{world_version}-{имя}-t{tick}.tcsave` + `.json`: `world_version`, `tick`, `state_hash` (заголовок), `hash_after_load`,
  `continue_ticks` (200), `hash_after_continue`.
- Состав на каждую `world_version`: `small` (явная сеть фикстур, ~100 машин, единицы КБ) и `s1k-t2400` (≈ 0.5 МБ сжатый).
  s10k в корпус не кладём (≈ 2.5 МБ на версию) — его round-trip строится в тесте.
- Файлы прошлых версий **не меняются и не удаляются**. Для текущей версии `hash_after_*` проверяются строго и
  обновляются вместе с golden-хэшами при смене поведения (как `HotPathGoldenHashTests`). Для прошлых — загрузка + миграция +
  проверки §6.1 + `continue_ticks` без исключений + повторный сейв → загрузка → побайтно равен.
- Пополнение: `Sim.Headless save --scenario … --ticks … --out tests/SaveCorpus/…` + `load --in … --ticks 200` печатает хэши
  для `.json`. Новая `world_version` без файлов корпуса → тест `Corpus_HasCurrentVersion` красный.

## 8. Размер сейва (замер прототипа + оценка)

Прототип (консольный проект в scratchpad, не в репо): сырые байты массивов §1 подряд без заголовков и каталогов (их
вклад < 0.1%), матрица оверлея — вся ёмкость; сжатие — `System.IO.Compression` .NET 10, один прогон, контейнер load ≈ 1.5–2.2
(времена — ориентир, шумят). s1k/s10k — после рампы (`Step` до 98% цели), s100k/s1m — сразу после `Create` (0 машин).
МБ = 10^6 Б.

| | s1k, t 2400, 998 машин | s10k, t 5900, 9 818 | s100k, `Create` | s1m, `Create` |
|---|---|---|---|---|
| сеть (`N*`) | 0.99 | 3.89 | 24.15 | 245.7 |
| connector'ы + конфликты | 0.30 | 1.20 | 7.54 | 77.1 |
| оверлей | 0.29 | 0.44 | 2.26 | 30.3 |
| полосы/сегменты динамика + `SegCost` | 0.09 | 0.35 | 2.19 | 22.3 |
| машины (124 Б/слот) | 0.12 | 1.22 | — | — |
| маршруты (окна + планы + слоты) | 0.20 | 2.33 (237 Б/машину) | — | — |
| **итого сырых** | **2.00** | **9.42** | **36.1** | **375.3** |
| deflate fastest | 0.48 (17 мс) | 2.68 (98 мс) | 9.44 (426 мс) | 97.8 (3.6 с) |
| deflate optimal | 0.43 (62 мс) | 2.43 (363 мс) | 8.81 (1.2 с) | 91.5 (13.8 с) |
| brotli fastest | 0.48 (12 мс) | 2.45 (58 мс) | 9.68 (161 мс) | 104.3 (1.9 с) |

Счётчики: s10k — 1 666 узлов, 24 542 полосы, 151 480 точек, 17 066 connector'ов, 72 680 конфликтов, 143 417 шагов окон,
233 528 элементов арены планов; s1m — 102 098 узлов, 1 550 356 полос, 9 599 972 точки, 1 083 210 connector'ов, 4 760 016 конфликтов.

**Оценка при полной загрузке** (машины 124 Б + маршруты 237 Б на s10k с 1 уровнем; при 3 уровнях план длиннее на ≈ 64 Б):
s100k ≈ 36 + 100 000 × 0.36 КБ ≈ **72 МБ сырых, ≈ 19–21 МБ deflate**; s1m ≈ 375 + 1 000 000 × (0.36…0.42 КБ) ≈
**0.74–0.8 ГБ сырых, ≈ 0.2 ГБ deflate**. Сеть — 54% сейва s10k и ≈ 50% s1m: первый кандидат на «компактный сейв» (§11 п.3).
Время `Save`/`Load` не замерено — замеряет S2 (`save`/`load` печатают `save_ms`, `load_ms`); ориентир: копия ≈ размер / пропускную
способность памяти (s10k — единицы мс, s1m — сотни мс) + xxh64.

## 9. Тесты (DoD шага)

| # | Тест | Где |
|---|---|---|
| T1 | Round-trip: s1k после 2 400 тиков, s10k после рампы (`warmup-until-full`): `hash(Load(Save(w))) == hash(w)` и `Save(Load(Save(w))) == Save(w)` побайтно | Core.Tests (s10k — `Trait("Category","Slow")`) |
| T2 | Продолжение: A — `Step(2000)`; B — `Save` в момент T, `Load`, `Step(2000)`; хэши равны каждые 200 тиков, финальные сейвы побайтно равны. s1k; s10k — 400 тиков | Core.Tests |
| T3 | Потоки: сейв при 1 потоке → загрузка при 2 (и наоборот), продолжение = T2. LOD auto — добавить в DoD задачи LOD (ADR-0013) | Core.Tests (коллекция без параллелизации) |
| T4 | «Неудобные моменты»: малый сценарий (явная сеть, ~100 машин), сейв на **каждом** тике 0…1 200 → `Load` → 20 тиков = эталон. Отдельно: посреди волны кастомизации (`WaveActive`), при `RecalcPending`, посреди смены полосы, с непустыми `Commands`/`Rerouting`/`RefillQueue`, после `BuildRoad`, с `Selected` | Core.Tests |
| T5 | Контейнер: XXH64 против `System.IO.Hashing.XxHash64` (NuGet только в тестах) на случайных длинах 0…4 097; канон (одни таблицы → одни байты), выравнивание, порядок | Core.Tests |
| T6 | Корпус: каждый файл грузится и мигрирует; текущая версия — строгие хэши; прошлые — §7 | Core.Tests + CI (тот же `dotnet test`) |
| T7 | Fuzz (seed фиксирован, ≤ 20 с): обрезка на каждом байте заголовка/оглавления и на 1 000 точках дальше → не `Ok`, без исключений; 5 000 случайных бит-флипов → `ChecksumMismatch`; 2 000 флипов **с пересчётом xxh64** (бьют по проверкам §6.1) → код ошибки либо `Ok` + `Step(200)` без исключений | Core.Tests |
| T8 | `Save` в `Span` — 0 Б (`GetAllocatedBytesForCurrentThread`, через `AllocationProbe`); после `Load` — 0 Б/тик в стационаре | Core.Tests |
| T9 | Блоки хоста и чужие ancillary: переписываются побайтно при `Load → Save`; неизвестный critical → `UnknownCriticalBlock`; неизвестный столбец → `UnknownColumn` | Core.Tests |
| T10 | Миграции: тестовая цепочка (фиктивные шаги v1→v2→v3 в тесте, реестр подменяемый) — порядок, `TooNew`, `MigrationFailed`, дефолт нового столбца | Core.Tests |
| T11 | Манифест: round-trip; хост без одного типа ТС → `AssetReport` перечисляет, продолжение побитовое | Headless.Tests |
| T12 | CLI: `save` → `load --ticks N` печатает те же хэши, что `hash` без сейва | Headless.Tests |

Почему не только хэш: §9 `core-api.md` не включает 14 полей машины и ряд массивов, нужных для продолжения (§1: `Ahead/Behind`, `Ghost*`,
`LaneChangeTicks`, `NextMobilTick`, поля автобуса, `DesiredSpeed`, окна/планы, матрицы и `SegCost`, `SegmentLoads`,
`Commands`, планы светофоров). Побайтное равенство повторного сейва (T1) и хэши продолжения (T2–T4) их покрывают;
расширять §9 ради сейва не нужно.

## 10. Реплей (дополнение, реализация — этап 3)

Реплей = сейв (или `ScenarioDesc` + seed) + лог команд. Лог ведёт **хост**: команды применяются в фазе 1 первого тика
следующего `Step`, значит запись `(Tick на момент Enqueue, Command 64 Б)` воспроизводит их точно. Ядру ничего не нужно,
кроме детерминизма и стабильной раскладки `Command` (§4 `core-api.md`; новые `CommandKind` — только добавлением).
Файл — тот же контейнер: `RPLH` (базовый `state_hash`, тик начала, `world_version`, версия ядра, контрольные хэши каждые
1 200 тиков) + `CMDL` (`Tick i64[]`, `Cmd raw[64]`). Действителен только для той же версии **поведения** ядра (любая
правка правил расходит хэши) — поэтому не замена сейву. Назначение: баг-репорт = последний автосейв + лог с него;
регрессии (golden-реплей); поиск первого расхождения по контрольным хэшам.

## 11. План реализации (sonnet, последовательно: все три трогают API ядра)

| ID | Задача | Файлы | DoD / тесты |
|---|---|---|---|
| S1 | Контейнер: `Util/XxHash64` (потоковый), `Save/SaveContainer` (писатель в `Span`/`SaveSink`, читатель из `Span`/`SaveSource` в `SaveTables`), каталог столбцов, канон, проверки §2, `SaveStatus`/`LoadStatus`, `InspectSave`. Без `World` | `src/Sim.Core/Save/*`, `Util/XxHash64.cs`, тесты | T5; T7 на уровне контейнера; 0 Б при записи в `Span` |
| S2 | Состояние мира: `Save/SaveSchema.cs` (реестр блоков и столбцов = приложение A), `WorldSaver`, `WorldLoader` (= `Create` без генератора), `SaveValidator` (§6.1), построение производных (`World.RebuildDerived`, конструкторы `Overlay`/`RouteGraph` «из сохранённого»), канон мёртвых слотов, `Simulation.Save/Load/MeasureSave`, хост-блоки и `kept`; `Sim.Headless save`/`load`/`hash --load`; правки `core-api.md` §3/§12, приложение A здесь | `src/Sim.Core/Save/*`, `World/*`, `Routing/Overlay*.cs`, `RoutingGraph.cs`, `Api/Simulation.cs`, `src/Sim.Headless/*`, тесты | T1–T4, T8, T9, T12, T7 на уровне мира; замер `save_ms/load_ms` s1k/s10k в отчёте |
| S3 | Версии и хост: реестр миграций (`Save/Migrations.cs`, пока пустой, + T10 на фиктивной цепочке), манифест (`SetAssetManifest`, `Sim.Scenarios` пишет `vehicle:<имя>` и `ContentHash`), конверт `.tcsave` (`Sim.Scenarios/SaveFile.cs`, deflate), `AssetReport` в `load`, корпус `tests/SaveCorpus/` (w1-small, w1-s1k-t2400) + T6 + `.gitattributes` | `src/Sim.Core/Save/Migrations.cs`, `Api/*`, `src/Sim.Scenarios/*`, `src/Sim.Headless/*`, `tests/SaveCorpus/`, тесты | T6, T10, T11 |

После S3 — верификатор (haiku): полный `dotnet test`, bench-smoke s1k + `compare` (сейв не должен тронуть тик), `save/load`
s10k с цифрами размера и времени. Клиентские кнопки «Сохранить/Загрузить», автосейв и заглушка ассета — отдельная задача
клиента (sonnet) после S3; «компактный сейв» и реплей — этап 3.

Попутно: при каждой будущей задаче, добавляющей поле состояния (LOD, жители), в DoD — столбец в приложении A,
`world_version++`, файл корпуса новой версии.

## 12. Журнал `world_version`

| Версия | Изменение | Миграция |
|---|---|---|
| 1 | первый формат (этот документ) | — |

## 13. Открытые вопросы
1. (владелец) Совместимость сейвов до релиза: держать миграции с первой `world_version` или до первой публичной сборки
   разрешить «несовместимо → понятная ошибка» (корпус — только текущая версия)? Рекомендация: строгий режим с этапа 3.
2. (владелец) Автосейв: частота, число слотов, допустимая пауза кадра; нужна ли для s1m копия частями между тиками.
3. (владелец) Облако: лимит размера (s1m ≈ 0.2 ГБ сжатый) — нужен ли «компактный сейв» (без статики сети, −50%) раньше этапа 3.
4. (владелец) Обновлённый ассет: по умолчанию сохранённые параметры (побитово) или новые для новых спавнов?
5. «Компактный сейв»: канонический порядок раскладки сети (connector'ы/конфликты/входы оверлея по узлам в порядке индекса,
   а не истории `BuildRoad`) — менять ли построитель сейчас, пока это дёшево (влияет на golden-хэши после `BuildRoad`).
6. Снос дорог (этап 2+): пул узлов/сегментов с free-list → `IncHead/Cells` перестают быть производными «в порядке индекса» —
   при появлении сноса перенести их в сохраняемые или доказать независимость.

## Приложение A. Реестр столбцов `world_version = 1`
Заполняет S2 вместе с `Save/SaveSchema.cs` (блок, id, имя, тип, длина, дефолт для старых версий).
