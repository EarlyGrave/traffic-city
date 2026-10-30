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
| `SignalStore` + выходы фазы 4 | все массивы до `Count` (включая `PhaseMask`, `TicksLeft`), `SignalStates`, `SignalCount`, `LaneSignals` (1 Б/полосу — дешевле, чем доказывать чистоту). 2.5 S2 (ADR-0018): параметры плана (`MinGreenTicks`, `GapTicks`, `ArrowTrigger/Min/Max/GapTicks`, `CycleTicks`, `Flags`) и состояние автомата с подфазами - `Machine`, `Sub`, `GreenMask` (открытая маска), `NextMask/NextAxis/NextSub/NextArrowArms` (переход через жёлтый и all-red читает следующий тик), `ArrowArms`, `GreenElapsed`, `GapLeft` (таймеры gap-out/max-out/ArrowGap), `SpillTicks` (2.5 S3: счёт анти-спилловера светофора, intersections.md 6.8; читается раньше записи; координация - окна осей считаются от `World.Tick` и `OffsetTicks/CycleTicks`, отдельного состояния нет), `ArmLeftYield[Count·16]` и `ArmLeftHead[Count·16]` (счёт уступания левого на зелёном: читается раньше записи, индекс машины-головы; без них стрелка в момент включения сработала бы позже/раньше) | Статика слота, её строит `SignalStore.RebuildStatics` тем же кодом, что `TryAdd`: `ArmMask/LeftMask/RightMask`, `ArmAxis`, `ArmOpp`, `HasArrow`, `ArmCountOf`, список входных полос детекторов `SigLane/SigLaneArm/SigLaneAxis/SigLaneLeft/SigLaneCount` (функция сети, `PhaseMask` и манёвров connector'ов). Детекторов-накопителей нет: спрос читается из `LaneOccupancy`/машин на лету. `ArrowEntries` - дельта-счётчик внутри тика (в `SimCounters.ArrowPhases`), не состояние |
| `VehicleStore` | `Pool` (поколения, free-list **в LIFO-порядке**, `HighWater`), `Lane, S, Speed, Accel, Length, Type, DesiredSpeed, Ahead, Behind, GhostLane, GhostAhead, GhostBehind, LaneChangeDir, LaneChangeTicks, NextMobilTick, Route, RouteGen, RouteCursor, DestZone, DestLane, DestS, NextConn, GateState, GrantConnector, Ticket, WaitTicks, PointHint, Lateral, SpawnTick, StillTicks, Flags, BusRoute, BusNextStop, DwellTicks, Passengers` (124 Б/слот) и `Gate.DenyReason` (1 Б/слот, 2.5 S2: детекторы Adaptive читают код отказа конца прошлой фазы 4 раньше, чем фаза 4 его перепишет; столбец VEHC 40) | `ClampLeadGap/ClampFlags/ClampGateGap` (scratch 5→6), `RouteDirty` (после `Load` = 1 у живых: пересчёт входа `AssignConnectors` тот же) |
| `LaneOccupancy` | `LaneHead`, `LaneTail`, `LaneCount` | — |
| Очереди | `PendingTrips` (FIFO + `_unrouted`), `Rerouting`, `RefillQueue`, `Commands` (не применённые команды — 64 Б сырые, раскладка `core-api.md` §4 и так C ABI) — в **логическом** порядке, `head` при загрузке = 0 | — |
| `RouteStore` | `Pool`, `WinCount, PlanFirst, PlanCount, PlanCap, PlanCost, PlanTick`, окна живых слотов (`WinCount` шагов), `PlanArena[0..PlanUsed)`, `Compactions`. Окна и планы **не** производные: распакованы при старых весах | `_compactKeys/_compactItems` |
| `RouteGraph` | `SegCost` (веса по загрузке на момент пересчёта), `ArcCost` (штраф узла берётся из `Control` на момент последней перестройки: после `SetNodeControl` до следующей перестройки графа он историчен), `WeightsVersion`, `BuiltVersion` (граф может отставать от `Net.Version`) | топология (`ArcFirst/To/Maneuver`, `StartNode/EndNode/EndPos`, `VMax`): `Rebuild()`, затем вернуть веса и версии |
| `Overlay` | параметры (проверка), раскладка (`LevelNx/Nz`, `CellBase`, `Entry*/Exit*First/Count/Cap`, `MatrixFirst`, `Entries`, `Exits`, построенные узлы/DirSeg, `_failed`), `Matrix` (до занятой длины), `Dirty`, `CustomizedSegCost`, `WeightsVersion`, `WaveActive/Cell/Row`, `Disabled`, `Rebuilds`. Матрицы посреди волны — смесь старых и новых весов, пересчитать нельзя | `NodeCellX/Z`, `CutLevel`, `LevelDiv`, `EntryIdx/ExitIdx` |
| Метрики фазы 9 | `SegmentLoads` (читает фаза 3 следующего тика) и `SegmentLoadsTick`, `NodeThroughput`, `BusRouteStats`; `SignalMetrics` (S1 светофоров), `ConnWaitSum/ConnGrantCount` (накопители допусков за окно пересчёта весов) и `ConnDelay` (EWMA измеренной задержки connector'а, 2.5 S2; лежат в `GateScratch`, но это состояние: читаются `SignalDelays.Update` раньше записи и входят в хэш) - блок `SMET` | — |
| Спавнер, ворота, воркеры | — | `SpawnerData` (лениво из зон и типов на первом тике), `GateScratch` целиком, кроме `ConnWaitSum/ConnGrantCount/ConnDelay` и `DenyReason` (см. выше) (в т.ч. `PrevList/NextList`, `CandStamp`, `NodeBusy`, маски `GatedMask/ConnMask/SignalMask`: после загрузки `FullScan = true` - первая фаза 4 делает полный проход, он по построению даёт то же, что активный; `OutTouched` строится по ненулевым `OutReserved`), `Behaviour/Integrate/SnapshotJob`, `Pool` |
| Маски активных множеств | — | `World.Masks` (`TickMasks`: перестраивается фазой 6 каждого тика), `LaneOccupancy.OccMask` (из `LaneCount > 0`), `LaneOccupancy.Linked` (пуст: первая фаза 4 - полный проход), `VehicleStore.RouteDirtyBits` (после загрузки все 1: пересчёт `AssignConnectors` даёт сохранённый `NextConn`), `ClampLeadGap/ClampFlags/ClampGateGap` (scratch фаз 5-6) |
| Снапшоты, события, вид | — | `Render[2]`: после `Load` фаза 10 заполняет `Render[Tick & 1]` (`SnapshotTick = Tick`), `PreviousVehicles` = копия; `Events` пуст; `RenderViewState` (`SetRenderView`) - презентация, не сохраняется (вид «всё») |
| LOD (ADR-0013), блок `LODS` | параметры сетки (`OriginX/Z`, `CellsX/Z` - заданы размерами сети в `Create` и после `BuildRoad` не меняются), по машине `Lod` (биты meso/fresh/queue/head/parked), `MesoAllow`, `MesoNext`; по ячейке `CellLevel`, `CellHoldUntil`, `CellCongStreak`; `Focus[0..7]`, `LodEvalRequested`, `LastLodEvalTick` | привязка узлов/полос к ячейкам (`NodeCell`, `LaneCell`, `LaneColor`, CSR полос) - `OnNetworkGrown` |
| Q-meso (ADR-0019), блок `QMSO` | по машине `QEnter`, `QReady`, тик колеса таймеров (`_wTick`), по полосе `LaneLastExit`; агрегаты `PendingDistMm`, `Events`, `Wakes` | `QBits`, `LaneQCount`, `LaneSpanCm`, `KinMask/_kinCount`, `HeadMask/_headCount`, `Total/Parked/StuckCount`, колесо (корзины - из тиков машин; порядок внутри корзины не важен: события обходятся по маске), `ViewS/ViewSpeed` (синтез фазы 10) |

**Мёртвые слоты** (машины, маршруты, блоки арены вне живых планов, хвосты `PlanCap > PlanCount`) пишутся **каноническими
значениями** — теми, что получает свежий слот (ссылки −1, остальное 0), кроме поколения. Так байты сейва не зависят от мусора.
Если `Spawn` не переинициализирует какое-то поле свежего слота — это баг, который всплывёт в T2: тогда поле чинится в ядре.
Арена планов маршрутов пишется так же: блоки живых слотов `[PlanFirst, PlanFirst + PlanCount)` как есть, остальное - нули.

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

## 3. Блоки `world_version = 1` (и 2: добавлены блоки пешеходов, и 3: блоки города, и 4: сеть с дырами, см. ниже)

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
| `SIGS` | C | `Count` | `SignalStore` (все массивы до `Count`, `GreenTicks`/`PhaseMask` — `Count·8`, `ArmLeftYield/ArmLeftHead` — `Count·16`), `SignalStates`, `SignalCount`; столбцы 18-37 - Adaptive/стрелка (2.5 S2), 38 - `SpillTicks` (2.5 S3) |
| `NDYN` | C | `NodeCount` | `NodeTicket`, `NodeThroughput` |
| `LDYN` | C | `LaneCount` | `LaneHead`, `LaneTail`, `LaneCount`, `OutReserved`, `LaneSignals` |
| `SDYN` | C | `SegmentCount` | `SegmentLoads` по полям |
| `BDYN` | C | остановки / маршруты | `Waiting`, `LastArrivalTick`, `LastDepartTick`; `ActiveBuses`, `LastDispatchTick`; `BusRouteStats` по полям |
| `VEHC` | C | `HighWater` | `HighWater`, `FreeList` (снизу вверх), `Generation`, поля §1 |
| `TRIP` | C | очередь | `PendingTrip` по полям в порядке FIFO (Z2: `ResidentRef` - индекс жителя города + 1 или 0), `Unrouted` |
| `QUES` | C | очереди | `Rerouting (Index, Generation, ReadyTick, Kind)`, `Refill (Index, Generation)`, `Commands` (`raw`, 64 Б) |
| `ROUT` | C | слоты / шаги | `HighWater`, `FreeList`, `Generation`, `WinCount`, `PlanFirst/Count/Cap`, `PlanCost`, `PlanTick`, `WindowSteps` (проверка), `WinLane/WinNode` (окна живых слотов подряд), `PlanArena[PlanUsed]`, `Compactions` |
| `RGRP` | C | `DirSegCount` | `SegCost`, `WeightsVersion` |
| `OVLY` | C | — | есть ⇔ `Overlay != null`; §1 |
| `LODS` | C | — | (ADR-0013) есть ⇔ `Lod.Mode != Off`; §1 |
| `PEDS` | C | `HighWater` пешеходов | (v2, ADR-0020) есть ⇔ `peds.enabled`: пул (`HighWater`, `FreeList`, `Generation`), поля `PedStore` (`State`, `Leg`, `Resident`, `Edge`, `T0`, `S0Mm`, `VMmT`, `EvTick`, `PathCursor/Count`, `GoalEdge/SMm`, `Next`, `Bus`, `AlightStop`, `TripTick`); мёртвые слоты - канон (ссылки −1, остальное 0) |
| `PPTH` | C | `HighWater · max_ped_path_steps` | (v2) арена путей; мёртвые слоты - нули; `Steps` - проверка ёмкости |
| `RESI` | C | `Residents` | (v2) жители: дом, работа, способ, маршруты и остановки (P4), `Where`, `ReadyTick`, `Ped`, `ReadyNext`; голова/хвост очереди готовых |
| `XING` | C | переходы / остановки | (v2) `XState`, `QHead/QTail/QCount`, `OnCross`, `BusyUntil`, `BusyUntilIn`, `BusyUntilOut` (половины перехода, pedestrians.md §5.9), `ClosedSince`, `CarWindowUntil`, `CarsWaiting`, `WaitSum`, `Releases`, `MeasuredWait` по индексу перехода (`NodeArmCount` + по одному на остановку при `midblock_at_stops`); `StopQHead/Tail/Count` по остановкам; `PaxHead` по машинам; гистограммы ожидания (256 корзин по 1 с) |
| `PQUE` | C | очередь | (v2) `PedPathQueue` (индекс, поколение) в логическом порядке |
| `BTYP` | C | типы зданий | (v3, ADR-0011, PreWorld как `VTYP`) есть ⇔ `demand.model = residents`: таблица типов зданий по полям (`Kind`, `Density`, `Capacity`, `ShopCapacity`, `ShiftStartSlot`, `ShiftSlots`, `ConstructDays`, `AttractQ8`, `VisualRow`); строки только дописываются |
| `LOTS` | C | `Count` участков | (v3) участки: `Seg`, `SegGen`, `Side`, `S0Mm`, `S1Mm`, `Zone`, `Density`, `Building`, `Flags`; геометрия, ячейка, точки доступа машины и пешехода, компоненты - производные |
| `BLDG` | C | `HighWater` зданий | (v3) пул зданий (`HighWater`, `FreeList`, `Generation`) и поля (`Lot`, `Type`, `State`, `StateTick`, `Occupants`, `Jobs`, `HeadRes`, `HeadWork`, `Account`, `RevenueEwma`, `ProfitEwma`, пары «сегодня/вчера» `VisitsDay`/`RevenueDay`/`CostDay`, `LossDays`, `VacantDays`); мёртвые слоты - канон |
| `RESD` | C | `HighWater` жителей | (v3) пул жителей, поля (`Home`, `Work`, `NextHome/NextWork`, `Flags`, `State`, `StateTick`, `EvTick`, `EvKind`), план дня (`PlanCount/Cursor` и по 3 тура `PlanKind/Mode/Dest/Slot`), нога (`LegMode/O/D/Req/Walk/Wait/Ride`, Z2: `LegDest`, `LegStopA/B`), `Agent/AgentGen`, `Account`, `IncomeEwma`, `SeekDays`, `EvictDays`, `Seq`, `NextSeq`, очередь ищущих работу (индекс, поколение) |
| `SKIM` | C | `NC` ячеек | (v3) `Skim` (`NC * NC * 3 * P`), `K` (`NC * NC * P`), `LsRef`/`LsRefSet`, `LsC` (`NC * 3`), `LsJobs` (`NC`; поле доступности Z3), `CurPeriod`, `UpdateCursor`, накопитель периода (отсортированные `ObsKey/ObsSum/ObsCount`), слепок прошлого периода (`FrozenKey/Sum/Count`) |
| `LEDG` | C | 1 | (v3) `External`, `City`, потоки по 13 видам (`FlowDay/Yesterday/Total`; с v9 - по 17, виды 13-16 - казна, city-economy.md §2.1), курсоры и стадия дневных проходов (`DayStage`), сглаженный спрос R/C/I, суточные накопители туров, ног, визитов и визитов впустую, `CellLoads` |
| `CBGT` | C | 1 | (v9, ADR-0025, city-economy.md §12) есть ⇔ `demand.model = residents` и `economy.city_budget` (`PARM.CityBudget = 1`): действующая и ожидающая политики по полям (`TaxRBp`, `TaxCBp`, `TaxIBp`, `LandTaxBpDay`, `Fare`, `Flags`, `R0`, `R1`; u16), `HasPending`, `LandValue` (`NC`), `IncomeEwma`, `ExpenseEwma`, `Carry` (17 видов потоков), `DebtDays`, `OverLimitDays`, `Insolvent`, кольцо `UndoCredits` (64 записи по полям), суточные пары `FareEvadedDay`, `DebtEvictionsDay`. Производные `MaintRoadDay`, `MaintBusDay`, `VotDsQ8` не сохраняются (`CityWorld.RestoreDerived`) |
| `PARK` | C | `HighWater` объектов | (v10, ADR-0026, parking.md §2.1, §13) есть ⇔ `demand.model = residents` и `parking.model = resource` (`PARM.ParkingEnabled = 1`): пул объектов стоянки (`HighWater`, `FreeList`, `Generation`; слот 0 - `Overflow`, всегда жив) и поля (`Kind`, `Flags`, `Lot`, `Building`, `BuildingGen`, `Capacity`, `Assigned`, `Occupied`, `Reserved`, `Fee`); мёртвые слоты - канон (`Lot`, `Building` = -1, остальное 0). Точка въезда (`AccessLane/AccessS`), точка пешехода, положение, компоненты, `WalkMm`, `HomeAway`, окрестности участков и обратные ссылки участок -> обочина, здание -> стоянка не сохраняются (`ParkingBuild.RefreshAll` в `LotBuilder.RestoreDerived`). `ParkSearchQueue` добавит K3 |
| `LUSE` | C | `Count` полигонов | (v10, ADR-0027) есть ⇔ `PARM.LandUseCount > 0`: полигоны парков и воды генератора - четыре вершины (`P0X`..`P3Z`, f32), `Kind`, `Flags` (`LandUseMarks`); `Load` кладёт их в `World.LandUse` (`Simulation.LandUse`, `LandUseVersion = 1`), поэтому участки, нарезаемые после `Load` (`BuildRoad`), обходят парки и воду так же, как в `Create`. Сейвы до v10 полигонов не несут (в файле нет seed-параметров `city.landuse`): после миграции `LandUse` пусто, как было до этой версии |
| `TERR` | C | `SamplesX · SamplesZ` отсчётов | (v12, ADR-0036, terrain.md §2.1, §8) есть ⇔ `PARM.TerrainEnabled = 1`: сетка рельефа - `CellMm` (4000/8000/16000), `OriginX/OriginZ` (клетка мировой сетки от `WorldGridOrigin`, валидатор сверяет побитово с расчётом `Create`), `SamplesX/SamplesZ`, `BaseCm`, `HeightUnitMm` (= 10); массивы `Ground`, `Water` (u16, см над базой), `WaterKind` (u8: 0 суша, 1 стоячая вода, 2 источник; 3 - резерв течения, отклоняется; `WaterKind = 0` ⇔ `Water = 0`); `Version`, `OpSeq`. `Lock` и кольцо грязи - производные: после `Load` замков нет, кольцо пусто с `DroppedVersion = Version`. Резерв: тег `WSRC` и столбцы `TERR` 13+ (источники и течение воды, terrain.md §3.4) |
| `WSRC` | - | - | **резерв, не пишется и не читается** (ADR-0036, terrain.md §3.4, источники воды и течение): SoA `X, Z, Kind, FlowMm3s, TargetCm, RadiusDm, Generation` и столбцы `TERR` 13+ (поток по рёбрам `FluxE/FluxS`). Включатся новой `world_version` (13+) с миграцией «источников нет»; до тех пор ядро не сохраняет и не хэширует `water.sources` сценария (загрузчик сценария их только проверяет) |

Блоки хоста (флаг `host`, id со строчной первой буквой, ядро не разбирает): `meta` (имя города, время создания/изменения
по часам хоста, сыгранные тики, имя сценария, версия игры), `thmb` (превью PNG), `camr` (камера), `mod?` (состояние модов,
ADR-0015). Хост сам решает `critical` для своих блоков.

Статика сети (`N*`, `CONN`, раскладка `OVLY`) отделена от динамики (`*DYN`, `VEHC`, `ROUT`…) намеренно: будущий
«компактный сейв» опускает статику и пересобирает её (§11 п.3) без смены остальных блоков.

Блоки пешеходов (v2) и столбцы в `PARM` (86-112: секция `peds`, `PassengerModel`, ёмкости `MaxPeds/MaxResidents/MaxPedPathSteps`), `WRLD` (11-13: поток `Peds`, `TargetPeds`), `CNTR` (96-117: счётчики пешеходов), `SIGS` (39-41: `XWalkLeft/XClearLeft/XServed` пешеходной фазы) объявлены задачей P2 целиком, поэтому P3 и P4 схему не меняют. Индекс перехода `x` = слот рукава в `NodeArms`, переход на перегоне `m` = `NodeArmCount + m`. Сеть пешеходов, списки пешеходов по рёбрам, колесо таймеров, `NodeXIn/NodeXOut`, `XActive`, `RenderHint` - производные, не сохраняются; после `Load` их строит `PedWorld.RestoreDerived` (после `PedNetwork.Rebuild` в `WorldLoader.Finish`). Проверки состояния - `SaveValidator.ValidatePeds` (пул, жители, очередь готовых, события, очереди переходов и остановок, списки пассажиров, очередь путей).

Блоки и столбцы города (v3, ADR-0011, zoning-demand.md §15) объявлены задачей Z1 целиком, поэтому Z2-Z6 схему не меняют: блоки `BTYP`, `LOTS`, `BLDG`, `RESD`, `SKIM`, `LEDG` (только при `demand.model = residents`), столбцы `PARM` 113-184 (`PedSource`, секции `calendar/zoning/residents/economy/choice` по полям, ёмкости `MaxLots/MaxBuildings/MaxCityResidents/SkimObsCapacity`, сетка ячеек города: начало и число ячеек), `WRLD` 14-18 (поток `Demand`, `CityDay`, `CityExec`, `CityExecPending`), `CNTR` 118-149 (счётчики города), `VEHC` 41 (`Resident`, -1 у машин без жителя). С v9 добавлены казна города (блок `CBGT`, `PARM` 191-218, `BLDG` 21, `CNTR` 154-156, потоки 13-16 в `LEDG`; `SaveValidator.ValidateBudget`: политики в пределах `max_*`, `LV` в `[lv_min, lv_max]`, переносы в `[0, 10 000)`, записи `UndoCredits` нули либо живые с `ExpireTick >= Tick`, `Insolvent` только при `DebtDays >= 1`). Производные не сохраняются и строятся `CityWorld.RestoreDerived` (после `PedNetwork.Rebuild` в `WorldLoader.Finish`): центр, курс и ячейка участка, `CarLane/CarS`, `PedEdge/PedSMm`, `RoadComp/PedComp`, сетка занятости нарезки; колесо жителей по `EvTick/EvKind` добавляет Z2 в тот же метод. Проверки состояния - `SaveValidator.ValidateCity` (пулы, ссылки участок-здание-житель, списки дома и работы, `Occupants` = длина списка, `Σ счетов = 0`, агенты жителей в пути живы и ссылаются назад (нога агрегата, Z5: агента нет, `EvKind = Arrive` и `EvTick >= Tick`), накопители скимов). Пока `world_version = 3` не закрыт (Z9), Z2-Z6 вправе дописывать столбцы в этих блоках, добавляя дефолт и в `MigrationV3`, и перегенерируя `w3-*` корпуса.

**Сеть с дырами (v4, ADR-0023, player-tools.md §3, §10; задача T2).** Столбцы `Count`-счётчиков сети (`NodeCount`, `SegmentCount`, `BusStopCount`, `BusRouteCount`) теперь `HighWater` пула, а `LaneCount`, `LanePointCount`, `SegmentPointCount`, `NodeArmCount`, `LaneNextCount`, `ZoneLaneCount`, `BusRouteStopCount`, `CONN.Count`, `CONN.ConflictsUsed` - хвосты арен; в хвосте лежат живые блоки и дыры. Поколение слота (`Generation` в `NNOD/NSEG/NBUS`; нечётное = занят) - единственный признак живости; мёртвый слот пишется каноническими значениями (ссылки -1, остальное 0; полоса - `Generation = 0`, `Segment = Node = FromLane = ToLane = Left = Right = ConflictGroup = -1`, запас блока выглядит так же; элементы остальных массивов в дырах - нули), валидатор требует именно их. Free-list пула - массив слотов в LIFO-порядке снизу вверх. Дыры арены - упакованные `класс << 32 | первый элемент`, класс по возрастанию, внутри класса сверху вниз (порядок выдачи); битовая карта (по `Tail`) помечает начало блоков, выделенных по лестнице ёмкостей (`BlockArena.CapFor`) - их физическая ёмкость `CapFor(n)`, у блока без запаса (генератор, `Create`, сеть без правок) она равна `n`. Для сети без правок (`ToolsTouched = 0`) всё это нулевое, а валидатор отклоняет дыры, потери и биты лестницы. `ToolsTouched` ставит первая структурная правка; он управляет разделом хэша (core-api §9 п. 13) и способом выделения блоков (без запаса / по лестнице). Арены строк и конфликтов connector'ов (`CONN` 18-25) до T3 пусты (валидатор требует). Проверки: пулы (§6.1), дыры арен внутри хвоста и без пересечений, канонические мёртвые слоты, живые полосы - только у живых владельцев, `RefKind` (`SaveValidator.ValidateRefs`: каждая ссылка тегированного столбца в диапазоне своего вида, `InvalidReference`).

**Операции дренажа (v5, ADR-0023 T4).** `NETW` столбцы 50-60: `ToolOpCount`, `ToolOpNextId` (по умолчанию 1), массивы по числу операций - `Id`, `Kind`, `TargetIndex`, `TargetGen`, `Flags`, `StartTick`, `LastProgressTick`, `LastStallTick`, `BestRemaining`.
`Remaining` - производное (пересчитывается при загрузке). Миграция `MigrationV5` ставит «операций нет». Валидатор: без правок операций нет; id строго растут и меньше `NextId`; цель - живой закрывающийся сегмент того же поколения, по одной операции на сегмент; каждый закрывающийся сегмент имеет операцию.
`DenyReason` ворот допускает значение 8 (`Closed`). Корпус `w5-*`: `small-drain-t600` - сохранение посреди дренажа; бюджет корпуса поднят до 8 МБ (файлы старых версий не удаляются).

**Строки и конфликты connector'ов (T3, версия прежняя - 4).** Строки `CONN` (`Lane, Node, InLane, OutLane, ...`) и записи конфликтов - блоки арен `RowArena/ConflictArena` (блок узла: строки подряд, `LocalBit` = номер в блоке; блок записей конфликтов узла - одним куском). Мёртвая строка (дыра арены или запас блока по лестнице) пишется каноном: `Lane = Node = InLane = OutLane = -1`, остальное 0; поэтому `CONN.Lane/Node/InLane/OutLane` в реестре ссылок допускают -1. Валидатор проверяет арены строк и конфликтов так же, как арены сети (дыры внутри хвоста, лестница, канон содержимого дыр); у сети без правок они подряд, как раньше.

С v17 (блок BL, `building-lifecycle.md` §5; без миграции, режим прототипа) режим `city.lifecycle` добавляет столбцы `PARM` 280-299, `BLDG` 22-27, `BTYP` 13-19, `CNTR` 176-184, блок `LIFE` (⇔ режим: поля квартала, очередь заявок, бригады, фонд),
счёт `Utility` и потоки 17-21 в `LEDG`; без режима столбцы нулевые, хэши прежние. Окно v17 закрыто в BL4; калибровка BL5 (`reports/L106.md`) схему не меняла - сменились только дефолты параметров и базовых типов зданий (значения лежат в сейве, старые сейвы v17 грузятся со своими числами).

С v18 (службы, `services.md` §5, задача SR1; без миграции, режим прототипа, окно открыто до SR5): очередь заявок и бригады ЖКХ переехали из `LIFE` (id 4-17 выбыли) в новый блок `SERV` (⇔ `city.lifecycle`: станции, кольца вызовов по записям, бригады со ссылкой на станцию); секция `city.services` - столбцы `PARM` 300-313 и блок `SVCR` (PreWorld, ⇔ `PARM.ServicesEnabled = 1`: таблицы записей служб). Хэш без `city.services` - побитово прежний (ЖКХ пишется как в BL). Окно w18 (SR1-SR5) закрыто SR5: версия остаётся 18, поздние колонки (SERV 29-48, PARM 314-315, CNTR 185-199, RESD 50, BTYP 20) входят в неё; SR5 добавил `SERV 48` (пристройки станций, `LotFlags.Annex` в `LOTS` Flags, значения 0..7). Корпус w18 не менялся: его миры без города.

Состав блоков `world_version = 1` по факту (S2): список выше плюс `LODS` (`lod.mode != off`), `QMSO` (`lod.meso_model = queue`) и `SMET` (всегда). `MANI` добавляет S3 (манифест ассетов) в ту же версию, пока не выпущены сейвы. Отличия от наброска: `PARM` несёт и `NetLaneWidth/NetConnectorSamples` (геометрия построителя для `BuildRoad`; `CityEnabled` не нужен - построитель создаётся всегда), `DEMD` - ещё `TypeMix`; `RGRP` - `ArcCost` и `BuiltVersion`; `OVLY` - явные запасы (`EntryCap/ExitCap`), общие массивы раскладки и размеры массивов (запас растёт вместе с историей, перестройка при загрузке не нужна); раскладка входов/выходов оверлея и смещения матриц - канонические (§13 п. 5), `EntryFirst/ExitFirst/MatrixFirst` - префиксные суммы запасов (производные). Проверки загрузки, не покрытые контейнером, - `SaveValidator`; `FailedColumn/FailedRow` при их срабатывании - номер проверки и строка (не id столбца).

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

**Реализация S3 (минимум).** `MANI` - всегда присутствующий блок (пустой, если хост манифест не задал); `Simulation.SetAssetManifest(refs, ids)` проверяет записи (вид 1..4, срез id в буфере,
индекс типа ТС в `VTYP`, нулевые резервные поля) и возвращает false без изменений при противоречии. Каталог «что есть сейчас» хост передаёт в `LoadOptions.AvailableAssets/AvailableIds` (тот же формат,
`Sim.Scenarios/AssetManifestBuilder`: `vehicle:<имя>`, `ContentHash` = xxh64 параметров типа); ядро сверяет вид + id побайтно и отдаёт `LoadReport.AssetsInManifest/AssetsMissing/AssetsChanged` и
`Simulation.AssetStates` (`AssetState` на запись, в сейв не пишется) вместе с `Simulation.VehicleTypes` (габариты заглушки). Каталога нет - ничего не сверяется. Параметры отсутствующего и обновлённого ассета
всегда из сейва (продолжение побитовое). Опция `AssetParams = UseCurrentForNewSpawns` (новые параметры только новым спавнам - решение владельца) **не реализована**: нужны версионные параметры типа в состоянии; поле `LoadOptions.AssetParams` зарезервировано. План: дописываемые строки типов, `modding.md` §6.3 (M5).

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
- Пулы (в том числе узлов, сегментов, остановок, маршрутов; v4): free-list без повторов, только мёртвые слоты `< HighWater`, поколение живого нечётно, мёртвого — чётно.
- Теги `RefKind` (v4): значение каждого столбца-ссылки (`SaveSchema.Refs`) в `[0, граница вида)` либо -1 там, где «нет» допустимо; нарушение - `InvalidReference`, `FailedColumn` - id столбца.
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

**Реализация S3:** конверт - `Sim.Headless/SaveFile.cs` (`Pack/TryUnpack/ReadContainer`; `Sim.Headless save` пишет конверт, `--raw` - голый контейнер, `load` определяет формат по магии).
Корпус проверяет `Sim.Core.Tests/Save/SaveCorpusTests` (входит в обычный `dotnet test`; файлы копируются в вывод тестов как `SaveCorpus/*`). Файлы: `w1-small-t600`, `w1-s1k-t2400`
(`.tcsave` + `.json` с `world_version`, `tick`, `state_hash`, `hash_after_load`, `continue_ticks` = 200, `hash_after_continue`, хэши - hex). Перегенерация (с этапа 3.1 перегенерируются **только файлы текущей версии**, `w1-*` остаются навсегда):
`TC_REGEN_CORPUS=1 dotnet test tests/Sim.Core.Tests --filter FullyQualifiedName~SaveCorpusTests`, результат коммитится отдельным коммитом. Изменение схемы или поведения без перегенерации -
тест падает сообщением «перегенерируй корпус: …». Миграции с этапа 3.1: файлы прошлых версий остаются, шаг добавляется в `Save/Migrations.cs`. `w2` «открыта» до конца волны 3.1: P3 и P4 меняют
поведение пешеходов (golden-хэши `w2-s1k-peds-t2400` меняются), поэтому корпус w2 перегенерирует последняя слитая задача волны; столбцы w2 заморожены P2.

**Golden-корпус** `tests/SaveCorpus/` (бинарные, `.gitattributes: binary`, бюджет ≤ 10 МБ (5 МБ до v4, 8 МБ до v6, растёт ~1,4 МБ на версию)):
- `w{world_version}-{имя}-t{tick}.tcsave` + `.json`: `world_version`, `tick`, `state_hash` (заголовок), `hash_after_load`,
  `continue_ticks` (200), `hash_after_continue`.
- Состав на каждую `world_version`: `small` (явная сеть фикстур, ~100 машин, единицы КБ) и `s1k-t2400` (≈ 0.5 МБ сжатый); с v2 ещё `s1k-peds-t2400` (≤ 1 МБ сжатый); с v3 `s1k-res-t24000`; с v5 `small-drain-t600` (сохранение посреди дренажа: операция сноса, закрытые полосы); с v4 `small-edited-t600` (малый мир без оверлея, дороги построены и освобождены: дыры в пулах и аренах, `ToolsTouched = 1`).
  Файлы w2 без пешеходов (`w2-small-t600`, `w2-s1k-t2400`) несут те же `state_hash` и `hash_after_continue`, что `w1-*` (миграция 1 → 2 поведение не меняет).
  С v12 (TR1, рельеф) бюджет 32 МБ не растёт: в текущей версии пересоздаётся только подмножество (`small-t600`, `s1k-t2400`, новый `terrain-demo-t600` - малый город на рельефе с морем, озером и рекой); остальные случаи покрыты файлами w11, они мигрируют 11 → 12 без смены поведения (флаг `Current` у случая в `SaveCorpusTests`, тест `Corpus_W11Files_AreKeptAndMigrate_ToNoTerrain`). Тесты, которым нужен конкретный «старый» случай (`ResCorpusTests` - `w11-s1k-res-d3`), читают файл w11.
  s10k в корпус не кладём (≈ 2.5 МБ на версию) — его round-trip строится в тесте.
- Файлы прошлых версий **не меняются и не удаляются**. Для текущей версии `hash_after_*` проверяются строго и
  обновляются вместе с golden-хэшами при смене поведения (как `HotPathGoldenHashTests`). Для прошлых — загрузка + миграция +
  проверки §6.1 + `continue_ticks` и **тот же `hash_after_continue`** (для миграции 1 → 2: пешеходов в старом мире нет, поведение прежнее) + повторный сейв → загрузка → побайтно равен.
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
| S3 (сделано, см. §5 и §7) | Версии и хост: реестр миграций (`Save/Migrations.cs`, пока пустой, + T10 на фиктивной цепочке), манифест (`SetAssetManifest`, `Sim.Scenarios` пишет `vehicle:<имя>` и `ContentHash`), конверт `.tcsave` (`Sim.Scenarios/SaveFile.cs`, deflate), `AssetReport` в `load`, корпус `tests/SaveCorpus/` (w1-small, w1-s1k-t2400) + T6 + `.gitattributes` | `src/Sim.Core/Save/Migrations.cs`, `Api/*`, `src/Sim.Scenarios/*`, `src/Sim.Headless/*`, `tests/SaveCorpus/`, тесты | T6, T10, T11 |

После S3 — верификатор (haiku): полный `dotnet test`, bench-smoke s1k + `compare` (сейв не должен тронуть тик), `save/load`
s10k с цифрами размера и времени. Клиентские кнопки «Сохранить/Загрузить», автосейв и заглушка ассета — отдельная задача
клиента (sonnet) после S3; «компактный сейв» и реплей — этап 3.

Попутно: при каждой будущей задаче, добавляющей поле состояния (LOD, жители), в DoD — столбец в приложении A,
`world_version++`, файл корпуса новой версии.

## 12. Журнал `world_version`

| Версия | Изменение | Миграция |
|---|---|---|
| 1 | первый формат (этот документ); реализован S2 (`Save/SaveSchema*.cs`). Дополнен в рамках 2.5 S2 (адаптивные светофоры, ADR-0018) без `world_version++`: `PARM` 76-82 (дефолты `RulesDesc.Signal*`), `SIGS` 18-37, `SMET` 6 (`ConnDelay`), `VEHC` 40 (`DenyReason`) | — |

| 2 | 3.1, ADR-0020, задача P2: пешеходы. Блоки `PEDS`, `PPTH`, `RESI`, `XING`, `PQUE` (только при `peds.enabled`); столбцы `PARM` 86-112, `WRLD` 11-13, `CNTR` 96-117, `SIGS` 39-41; `hash_version` 2 (секции пешеходов в хэше только при `peds`) | `MigrationV2` (`Save/MigrationV2.cs`): добавляет столбцы с дефолтами «пешеходов нет, пассажиры - счётчик», блоков не создаёт; w1 продолжается тем же хэшем |
| 3 | 3.2, ADR-0011/0021, задача Z1: жители, зоны, экономика, скимы. Блоки `BTYP`, `LOTS`, `BLDG`, `RESD`, `SKIM`, `LEDG` (только при `demand.model = residents`); столбцы `PARM` 113-184, `WRLD` 14-18, `CNTR` 118-149, `VEHC` 41; `hash_version` 3 (секции города в хэше только при `residents`). Номера занял Z1 (решение оркестратора 2026-10-26): запланированные T2 и M4 сдвигаются на следующие свободные (`w4`, `w5`). Дополнено задачей Z4: `LEDG` 16-18 (`DayStage`, `VisitsDayTotal`, `UnservedDayTotal`); флаги жителя `RESD.Flags` используют бит 4 (`InSeekQueue`), предел значения - 31. Дополнено задачей Z2 (до закрытия w3): `RESD` 38-40 (`LegDest`, `LegStopA`, `LegStopB`), `TRIP` 11 (`ResidentRef`, 0 = поездка не жителя; в хэш только при `residents`) | `MigrationV3` (`Save/MigrationV3.cs`): добавляет столбцы с дефолтами «города нет», `VEHC.Resident = -1`, `TRIP.ResidentRef = 0`, блоков не создаёт; w1 и w2 продолжаются тем же хэшем |
| 4 | 4.3, ADR-0023, задача T2: сеть с дырами (пулы узлов, сегментов, остановок, маршрутов; арены блоков). Столбцы `NETW` 13-49 (`ToolsTouched`, длины и списки free-list'ов четырёх пулов, у каждой из семи арен `Lane/LanePoint/SegmentPoint/Arm/LaneNext/ZoneLane/BusRouteStop` четыре столбца: число дыр, потерянные элементы, дыры `класс<<32 \| первый`, битовая карта лестницы), `CONN` 18-25 (арены строк и конфликтов connector'ов), `NNOD` 13-14 (`Flags`, `TurnBan`), `NSEG` 13-15 (`Flags`, `Cx`, `Cz`), `NBUS` 11 (поколение остановки); `hash_version` 3 (раздел хэша «состояние инструментов» только при `ToolsTouched = 1`). Теги `RefKind` на столбцах-ссылках (`SaveSchema.Refs`) - не столбцы, а признак схемы | `MigrationV4` (`Save/MigrationV4.cs`): все столбцы с дефолтами «правок не было, всё живо» (`ToolsTouched = 0`, free-list'ы пусты, поколение остановок 1), блоков не создаёт; w1-w3 продолжаются тем же хэшем |
| 5 | 4.3, ADR-0023, задача T4: операции дренажа инструментов. Столбцы `NETW` 50-60 (`ToolOpCount`, `ToolOpNextId`, девять массивов полей операций); раздел хэша «операции» - только если операции заводились (`NextId != 1`); `DenyReason` допускает 8 | `MigrationV5` (`Save/MigrationV5.cs`): «операций нет» (`Count = 0`, `NextId = 1`), блоков не создаёт; w1-w4 продолжаются тем же хэшем |
| 6 | ADR-0028, отступ рукава узла по ширине поперечной дороги + `corner_radius`. Столбец `PARM` 185 `NetCornerRadius` (f32, опция построителя: 0 - фиксированный отступ 7 м); геометрия сети по-прежнему сохраняется целиком (`NNOD.Radius`, точки полос), поэтому загрузка от радиуса не зависит - он нужен только `BuildRoad` после загрузки | `MigrationV6` (`Save/MigrationV6.cs`): `NetCornerRadius = 0` - дороги, построенные в старом сейве после загрузки, получают геометрию до ADR-0028; блоков не создаёт; w1-w5 продолжаются тем же хэшем |
| 7 | ADR-0029, карман автобусных остановок (`docs/design/bus-bays.md`). Столбцы `PARM` 186-187 (`TransitBayModel`: 0 curb, 1 virtual, 2 lane - резерв под настоящую полосу-карман; `TransitBayYieldTicks`), `BDYN` 10-12 (`BayUsed`, `BayFlags`, `BayExitFrom` по остановкам), `CNTR` 150-153 (`BayEntries`, `BayFallbacks`, `BayYields`, `BayExitWaitTicksTotal`); флаг машины `InBay = 64` (`VEHC.Flags` до 127); раздел хэша «карманы» - только при `bay_model = virtual` и наличии остановок `Bay` | `MigrationV7` (`Save/MigrationV7.cs`): `TransitBayModel = 0` (curb), `TransitBayYieldTicks = 60`, поля кармана нулевые (`BayExitFrom = -1`), счётчики 0 - старые сейвы ведут себя как раньше; блоков не создаёт; w1-w6 продолжаются тем же хэшем |
| 8 | ADR-0030, профили дорог и участки на мировой сетке (`docs/design/road-profiles.md` §3.4, §4.4). Блок `RPRF` (PreWorld, только при `PARM.RoadProfileMode = 1`): каталог целиком - модуль, по профилю вид/ширина/число элементов/длина id, по элементу тип/направление/ширина, байты id (UTF-8); столбцы `PARM` 188-190 (`RoadProfileMode`: 0 legacy, 1 профили; `WorldGridOriginX/Z`), `NSEG` 16 `Profile` (-1 - без профиля; бит `Mirrored` - в `NSEG.Flags`), `NLAN` 19 `Width`, `LOTS` 11-15 (`CellX`, `CellZ`, `CellsF`, `CellsD`, `Orient`: 255 - legacy; объявлены сразу, заполняет RP5); раздел хэша «профили» - только в режиме профилей (RP1); хэш участков столбцы сетки пока не читает | `MigrationV8` (`Save/MigrationV8.cs`): режим legacy, `Profile = -1`, `Width = NetLaneWidth` у живых обычных полос (connector'ы и мёртвые слоты 0), начало сетки 0, у участков клеток нет и `Orient = 255`; блоков не создаёт; w1-w7 продолжаются тем же хэшем |
| 9 | ADR-0025, казна города (`docs/design/city-economy.md` §12, задача E1). Блок `CBGT` (только при `PARM.CityBudget = 1`); столбцы `PARM` 191-218 (секция `economy`: `CityBudget`, ставки, плата за проезд, вклад в казну, процент, кредит, содержание, цена земли, ценность времени, буферы, пределы политики), `BLDG` 21 `PayrollEwma`, `CNTR` 154-156 (`FareEvaded`, `DebtEvictions`, `InsufficientFunds`), `LEDG` 3-5 длиной 17 (виды потоков 13-16: `Interest`, `Dividend`, `Leakage`, `Refund`); `hash_version` 4: раздел хэша казны (политики, цена земли, переносы, долг, кредиты undo, потоки 13-16, фонд оплаты, счётчики) - только при бюджете, виды 13-16 вне бюджета в хэш не входят. Окно версии открыто до E8: E2-E5 вправе дописывать столбцы с дефолтом в `MigrationV9` и перегенерировать корпус w9 | `MigrationV9` (`Save/MigrationV9.cs`): бюджет выключен - `PARM` 191-218 = дефолты `economy` (нули у мира без жителей), `CityBudget = 0`; потоки 13-16 = 0 (столбцы `LEDG` 3-5 дополняются нулями), `PayrollEwma = 0`, счётчики 0; блоков не создаёт; w1-w8 продолжаются тем же хэшем |
| 10 | ADR-0026, парковка как ресурс (`docs/design/parking.md` §13, задача K1) и землепользование в сейве (ADR-0027, долг VS8). Блоки `PARK` (только при `PARM.ParkingEnabled = 1`) и `LUSE` (только при `PARM.LandUseCount > 0`); столбцы `PARM` 219 (`LandUseCount`), 220-238 (секция `parking`: режим, ходьба, кандидаты, повторы, обочина, гараж) и 239 (`MaxParking`), `BTYP` 10-12 (`ParkingCapacity`, `ParkingAccess`, `ParkingFee`), `RESD` 41-49 (`HomeFac`, `HomeFacGen`, `CarFac`, `CarFacGen`, `CarLot`, `CarWalkMm`, `LegStage`, `LegWaitFrom`, `LegParkWait`), `VEHC` 42-44 (`ParkFac`, `ParkFacGen`, `ParkSearchTick`), `TRIP` 12-13 (`ParkRef`, `ParkGen`), `CNTR` 157-172 (счётчики parking.md §11); `hash_version` 4 без изменений: раздел хэша парковки (`ParkingHash`) и поля заявок пишутся только в режиме. `AccessLane/AccessS` и точки пешехода объектов - производные от участка (отличие от parking.md §2.1: сохранять индексы полос значило бы заводить `RefKind` и перенос при правках сети). Окно версии открыто до K8: K2-K7 вправе дописывать столбцы с дефолтом в `MigrationV10` и перегенерировать корпус w10 | `MigrationV10` (`Save/MigrationV10.cs`): `PARM` 219-239 = нули (режима нет), `BTYP` 10-12 = нули, `RESD` 41-49 (`HomeFac`, `CarFac`, `CarLot` = -1, остальное 0), `VEHC` 42-44 (`ParkFac`, `ParkSearchTick` = -1), `TRIP` 12-13 = 0, `CNTR` 157-172 = 0; блоков `PARK` и `LUSE` не создаёт; w1-w9 продолжаются тем же хэшем |
| 11 | ADR-0034, прогрессия и показатели города (`docs/design/progression.md` §2.4, §4.4, задачи PG1-PG2). Блоки `CKPI` (всегда при `demand.model = residents`: кольцо 24 игровых часов `KpiHour` в физическом порядке с головой и заполненностью, текущий час, отметки `VehicleTicksTotal/MovingVehicleTicksTotal` начала часа, суточное кольцо баланса 32) и `PROG` (только при `PARM.ProgressionEnabled = 1`: вехи - порог, грант, кредитные дни, область, флаги, ворота G0/G1, маска признаков, диапазон профилей, тик взятия; индексы профилей, области, `MinLegs`, закрытые на старте признаки, размер каталога профилей; состояние - взято, открытые признаки и битсет профилей, бонус кредита, стадия области, `GatedNotified`); столбцы `PARM` 240 (`ProgressionEnabled`), `CNTR` 173 (`LockedRejects`), `CBGT` 31 (`WarnLevel`, уровень предупреждения PG5). `hash_version` 4 без изменений: раздел хэша прогрессии (состояние, `CKPI`, `LockedRejects`) пишется только при прогрессии, без неё кольцо показателей - наблюдатель вне хэша; `WarnLevel` в хэш войдёт с PG5 | `MigrationV11` (`Save/MigrationV11.cs`): `PARM` 240 = 0 (прогрессии нет, `PROG` не создаётся), `CNTR` 173 = 0, `CBGT` 31 = 0; у мира с жителями создаётся блок `CKPI` с пустыми кольцами («собираем данные») и отметками начала часа = текущим `CNTR` 7-8 (первый час не берёт всю историю); w1-w10 продолжаются тем же хэшем |
| 12 | ADR-0036, рельеф и вода (`docs/design/terrain.md` §2, §3.2, §8, задача TR1). Блок `TERR` (только при `PARM.TerrainEnabled = 1`: сетка, `Ground`, `Water`, `WaterKind`, `Version`, `OpSeq`); столбцы `PARM` 241 (`TerrainEnabled`), 242-255 (уклоны улиц и магистралей, срезка/подсыпка, откос берега, просвет и длина моста, `TerrainAutoBridge`, кисть и мазок, лимиты отсчётов за тик и заливки, `LotsMaxSlopePct`, `GradeMaxBoostPm`; без рельефа - строго дефолты). `hash_version` 4 без изменений: раздел хэша 14 (геометрия сетки, база, версии, xxh64 трёх массивов, `Elevation` живых узлов) пишется только при рельефе | `MigrationV12` (`Save/MigrationV12.cs`): `PARM` 241 = 0 (рельефа нет, `TERR` не создаётся), 242-255 - дефолты; w1-w11 продолжаются тем же хэшем |
| 13 | `docs/design/new-game-scratch.md` §2.2, задача Y4: параметры города для старта с нуля. Столбцы `PARM` 256-258 (`CityMinImmigrantsPerDay` - пол суточного въезда, `CityGrowthHourly` - рост по часам `zoning.growth_step = "hour"`, `CityInitialCityBalance` - абсолютный вклад в казну); блоков нет, `hash_version` 4 без изменений. `calendar.start_hour` (сдвиг `Tick` при `Create`) и `zoning.grid_from_bounds` (сетка города при `Create`; сама сетка уже лежит в `PARM`) в сейв не идут. Окно версии открыто до Y3: Y3 (секция `outside`, состояние `Arriving`, `OUTS`) вправе дописывать столбцы с дефолтом в `MigrationV13` | `MigrationV13` (`Save/MigrationV13.cs`): `PARM` 256-258 = 0 (прежнее суточное поведение, пола и абсолютного вклада нет); блоков не создаёт; w1-w12 продолжаются тем же хэшем |
| 14 | `docs/design/new-game-scratch.md` §2.1-§2.5, ADR-0037, задача Y3: внешние подключения. Столбцы `PARM` 259-261 (`CityOutsideCount`, `CityOutsideJobsDecayQ16`, `CityOutsideArrivalByCar`), 262-277 (по подключению 0..3: `Node`, `Jobs`, `JobCostTicks`, `ShareQ16`); блок `OUTS` (только при секции `outside`: внешние места, занято, въезд за сутки и час, всего доехало, не дождались выезда); бит `NodeFlags.Outside` (2) в флагах узла; бит жителя `Arriving` (32) в `RESD.Flags`; коды мест `-2 - k` в `Work`, `PlanDest`, `LegDest`. Раздел хэша - только при секции `outside`, `hash_version` 4 без изменений | `MigrationV14` (`Save/MigrationV14.cs`): `PARM` 259-277 = 0 (секции нет); блоков не создаёт; w1-w13 продолжаются тем же хэшем |
| 15 | `docs/design/scenarios-mode.md` §2.2-§2.3, ADR-0038, задача YS1: цели сценария. Блок `GOAL` (только при секции `goals`; признак секции - само наличие блока при жителях, столбца `PARM` нет): 16 слотов условий (0..7 победа, 8..15 поражение: вид, показатель, значение, часов подряд нужно), грейс в часах, `after_win`, часы подряд по слотам, часов от старта, исход, тик исхода, индекс условия поражения. Раздел хэша - только при секции `goals` (в конце раздела города), `hash_version` 4 без изменений. Сейвы без целей отличаются от w14 только номером версии | `MigrationV15` (`Save/MigrationV15.cs`): ничего не меняет (секции нет - блока нет); w1-w14 продолжаются тем же хэшем |

Решение по 2.5 S2 (и S3: столбец `SpillTicks`, дефолт 0): `world_version` остаётся 1. Правило ADR-0012 п.6 («новое поле = новый столбец с дефолтом, `world_version++`») защищает **выпущенные** сейвы; корпуса и сейвов игроков ещё нет (тот же довод, что у `MANI`/`QMSO`), поэтому миграция `1 → 2` была бы кодом без входных данных. Дефолты столбцов (приложение A) приведены на случай, когда версия всё же будет поднята после выпуска первых сейвов: столбец Adaptive отсутствует → слот грузится как `Fixed` без автомата (`Machine = 0`), `ArmLeftHead = -1`, дефолты `RulesDesc.Signal*` = значения `WorldBuilder.WithDefaults`. Сейвы, записанные до 2.5 S2 (только отладочные), не поддерживаются - допустимо до первого релиза. Первый выпущенный сейв закрывает «окно»: любое следующее изменение схемы - `world_version++` и миграция.

## 13. Открытые вопросы
1. (владелец) Совместимость сейвов до релиза: держать миграции с первой `world_version` или до первой публичной сборки
   разрешить «несовместимо → понятная ошибка» (корпус — только текущая версия)? Рекомендация: строгий режим с этапа 3.
2. (владелец) Автосейв: частота, число слотов, допустимая пауза кадра; нужна ли для s1m копия частями между тиками.
3. (владелец) Облако: лимит размера (s1m ≈ 0.2 ГБ сжатый) — нужен ли «компактный сейв» (без статики сети, −50%) раньше этапа 3.
4. (владелец) Обновлённый ассет: по умолчанию сохранённые параметры (побитово) или новые для новых спавнов?
5. «Компактный сейв»: канонический порядок раскладки сети - **решено владельцем (2026-10-25) и сделано**: connector'ы и конфликты (`ConnectorStore`) идут по возрастанию индекса узла
   (`BuildRoad` строит концы сегмента по возрастанию индекса; ранее созданный флагом 1, но не построенный узел с индексом ниже построенных - `NotSupported`), входы/выходы оверлея - по возрастанию
   DirSeg с запасом `Overlay.CapFor(count)` (лестница, функция числа входов; нехватка запаса после дописывания - полная перестройка). Раскладка живого мира равна построенной заново по
   узлам и сегментам (тест `CanonicalLayoutTests`). Индексы полос остаются историческими (сегменты `BuildRoad` лежат после уже построенных connector'ов): у «компактного сейва» они
   придут из `FirstLane*/FirstConnector` узлов и сегментов. Golden-хэши s1k/s10k (без `BuildRoad`) не изменились; тестов с хэшем после `BuildRoad` не было.
6. Снос дорог (этап 2+): пул узлов/сегментов с free-list → `IncHead/Cells` перестают быть производными «в порядке индекса» —
   при появлении сноса перенести их в сохраняемые или доказать независимость. **Закрыто в T2 (v4):** цепочки `IncHead/IncNext` и `CellNext/Cells` идут по убыванию индекса живых слотов и
   поддерживаются вставкой/удалением в этом порядке (равны полной перестройке по возрастанию индекса живых; `World.VerifyDerived()` сверяет), поэтому остаются производными и не сохраняются.
   П. 5 («раскладка = построенной заново») после первой правки не действует: раскладка (дыры, free-list'ы, `Alive`) - состояние, v4 её сохраняет; для сетей без правок п. 5 остаётся (`CanonicalLayoutTests`).

## Приложение A. Реестр столбцов `world_version = 4`

Стрится из `Save/SaveSchema*.cs` (`SaveSchema.AppendixMarkdown()`); тест `SaveSchemaTests.EveryDoc_Appendix_MatchesRegistry` сверяет таблицу с реестром
(обновить: `TC_DUMP_SCHEMA=<файл> dotnet test --filter SaveSchemaTests`). Дефолт - значение для сейвов без столбца (новые столбцы будущих версий) и канон мёртвого слота.
Длина указана словами: счётчики структуры - из блока `NETW`/своего блока, `HighWater` - число слотов пула.

<!-- schema:begin -->
| Блок | id | Имя | Тип | Длина | Дефолт |
|---|---|---|---|---|---|
| `PARM` | 1 | Seed | u64 | 1 | 0 |
| `PARM` | 2 | MaxVehicles | i32 | 1 | 0 |
| `PARM` | 3 | MaxNodes | i32 | 1 | 0 |
| `PARM` | 4 | MaxSegments | i32 | 1 | 0 |
| `PARM` | 5 | MaxLanes | i32 | 1 | 0 |
| `PARM` | 6 | MaxLanePoints | i32 | 1 | 0 |
| `PARM` | 7 | MaxZones | i32 | 1 | 0 |
| `PARM` | 8 | MaxBusRoutes | i32 | 1 | 0 |
| `PARM` | 9 | MaxBusStops | i32 | 1 | 0 |
| `PARM` | 10 | MaxBusStopsPerRoute | i32 | 1 | 0 |
| `PARM` | 11 | MaxRoutes | i32 | 1 | 0 |
| `PARM` | 12 | MaxPlanSteps | i32 | 1 | 0 |
| `PARM` | 13 | MaxPendingTrips | i32 | 1 | 0 |
| `PARM` | 14 | CommandQueueCapacity | i32 | 1 | 0 |
| `PARM` | 15 | EventCapacity | i32 | 1 | 0 |
| `PARM` | 16 | MaxSignalizedNodes | i32 | 1 | 0 |
| `PARM` | 17 | MaxConnectors | i32 | 1 | 0 |
| `PARM` | 18 | MaxConflicts | i32 | 1 | 0 |
| `PARM` | 19 | RulesMinPhysicalGap | f32 | 1 | 0 |
| `PARM` | 20 | RulesStuckThresholdTicks | i32 | 1 | 0 |
| `PARM` | 21 | RulesStuckSpeedThreshold | f32 | 1 | 0 |
| `PARM` | 22 | RulesGridlockStuckShare | f32 | 1 | 0 |
| `PARM` | 23 | RulesGridlockMinStuck | i32 | 1 | 0 |
| `PARM` | 24 | RulesGridlockHoldTicks | i32 | 1 | 0 |
| `PARM` | 25 | RulesMobilEveryTicks | i32 | 1 | 0 |
| `PARM` | 26 | RulesDefaultYellowTicks | i32 | 1 | 0 |
| `PARM` | 27 | RulesDefaultAllRedTicks | i32 | 1 | 0 |
| `PARM` | 28 | RulesYieldGapTime | f32 | 1 | 0 |
| `PARM` | 29 | RulesMaxYieldWaitTicks | i32 | 1 | 0 |
| `PARM` | 30 | RoutingLevels | i32 | 1 | 0 |
| `PARM` | 31 | RoutingFanout | i32 | 1 | 0 |
| `PARM` | 32 | RoutingCellSize | f32 | 1 | 0 |
| `PARM` | 33 | RoutingWindowSteps | i32 | 1 | 0 |
| `PARM` | 34 | RoutingPathfindBudgetPerTick | i32 | 1 | 0 |
| `PARM` | 35 | RoutingWorkBudgetPerTick | i32 | 1 | 0 |
| `PARM` | 36 | RoutingQueryWorkCap | i32 | 1 | 0 |
| `PARM` | 37 | RoutingCustomizeBudgetPerTick | i32 | 1 | 0 |
| `PARM` | 38 | RoutingCustomizeEps | f32 | 1 | 0 |
| `PARM` | 39 | RoutingCostRecalcTicks | i32 | 1 | 0 |
| `PARM` | 40 | RoutingTimedDrift | f32 | 1 | 0 |
| `PARM` | 41 | RoutingTimedDriftAbsTicks | i32 | 1 | 0 |
| `PARM` | 42 | LodMode | i32 | 1 | 0 |
| `PARM` | 43 | LodCellM | f32 | 1 | 0 |
| `PARM` | 44 | LodMicroBudget | i32 | 1 | 0 |
| `PARM` | 45 | LodEvalTicks | i32 | 1 | 0 |
| `PARM` | 46 | LodHoldTicks | i32 | 1 | 0 |
| `PARM` | 47 | LodFocusMarginCells | i32 | 1 | 0 |
| `PARM` | 48 | LodFollowSelected | u8 | 1 | 0 |
| `PARM` | 49 | LodFocusCommands | u8 | 1 | 0 |
| `PARM` | 50 | LodCongestionShare | f32 | 1 | 0 |
| `PARM` | 51 | LodCongestionMin | i32 | 1 | 0 |
| `PARM` | 52 | LodMesoHeadwayScale | f32 | 1 | 0 |
| `PARM` | 53 | LodMesoAccelScale | f32 | 1 | 0 |
| `PARM` | 54 | LodMesoModel | i32 | 1 | 0 |
| `PARM` | 55 | LodQueueHeadwayScale | f32 | 1 | 0 |
| `PARM` | 56 | DemandTargetVehicles | i32 | 1 | 0 |
| `PARM` | 57 | DemandSpawnRampTicks | i32 | 1 | 0 |
| `PARM` | 58 | DemandZoneSize | f32 | 1 | 0 |
| `PARM` | 59 | DemandModel | i32 | 1 | 0 |
| `PARM` | 60 | DemandGravityExponent | f32 | 1 | 0 |
| `PARM` | 61 | DemandWeightJitter | f32 | 1 | 0 |
| `PARM` | 62 | DemandExternalShare | f32 | 1 | 0 |
| `PARM` | 63 | DemandMinTripLength | f32 | 1 | 0 |
| `PARM` | 64 | TransitRoutes | i32 | 1 | 0 |
| `PARM` | 65 | TransitHeadwayTicks | i32 | 1 | 0 |
| `PARM` | 66 | TransitStopsPerRouteMin | i32 | 1 | 0 |
| `PARM` | 67 | TransitStopsPerRouteMax | i32 | 1 | 0 |
| `PARM` | 68 | TransitStopSpacing | f32 | 1 | 0 |
| `PARM` | 69 | TransitStopKind | i32 | 1 | 0 |
| `PARM` | 70 | TransitBusType | i32 | 1 | 0 |
| `PARM` | 71 | TransitDwellBaseTicks | i32 | 1 | 0 |
| `PARM` | 72 | TransitDwellPerPassengerTicks | f32 | 1 | 0 |
| `PARM` | 73 | TransitPassengerRatePerMin | f32 | 1 | 0 |
| `PARM` | 74 | NetLaneWidth | f32 | 1 | 0 |
| `PARM` | 75 | NetConnectorSamples | i32 | 1 | 0 |
| `PARM` | 76 | RulesSignalMinGreenTicks | i32 | 1 | 140 |
| `PARM` | 77 | RulesSignalMaxGreenTicks | i32 | 1 | 480 |
| `PARM` | 78 | RulesSignalGapTicks | i32 | 1 | 50 |
| `PARM` | 79 | RulesSignalArrowTriggerTicks | i32 | 1 | 80 |
| `PARM` | 80 | RulesSignalArrowMinTicks | i32 | 1 | 60 |
| `PARM` | 81 | RulesSignalArrowMaxTicks | i32 | 1 | 120 |
| `PARM` | 82 | RulesSignalArrowGapTicks | i32 | 1 | 40 |
| `PARM` | 83 | TransitHoldAlpha | f32 | 1 | 0 |
| `PARM` | 84 | TransitHoldMaxTicks | i32 | 1 | 0 |
| `PARM` | 85 | TransitHoldEveryN | i32 | 1 | 0 |
| `PARM` | 86 | PedsEnabled | u8 | 1 | 0 |
| `PARM` | 87 | TransitPassengerModel | i32 | 1 | 0 |
| `PARM` | 88 | MaxPeds | i32 | 1 | 0 |
| `PARM` | 89 | MaxResidents | i32 | 1 | 0 |
| `PARM` | 90 | MaxPedPathSteps | i32 | 1 | 0 |
| `PARM` | 91 | PedCount | i32 | 1 | 0 |
| `PARM` | 92 | PedResidents | i32 | 1 | 0 |
| `PARM` | 93 | PedSpawnRampTicks | i32 | 1 | 0 |
| `PARM` | 94 | PedSpeedMinMmPerTick | i32 | 1 | 0 |
| `PARM` | 95 | PedSpeedMaxMmPerTick | i32 | 1 | 0 |
| `PARM` | 96 | PedActivityMinTicks | i32 | 1 | 0 |
| `PARM` | 97 | PedActivityMaxTicks | i32 | 1 | 0 |
| `PARM` | 98 | PedWalkOnlyMm | i32 | 1 | 0 |
| `PARM` | 99 | PedMaxWalkToStopMm | i32 | 1 | 0 |
| `PARM` | 100 | PedMaxWalkMm | i32 | 1 | 0 |
| `PARM` | 101 | PedSidewalkWidthMm | i32 | 1 | 0 |
| `PARM` | 102 | PedCrosswalkWidthMm | i32 | 1 | 0 |
| `PARM` | 103 | PedArterialCrossings | i32 | 1 | 0 |
| `PARM` | 104 | PedMidblockAtStops | u8 | 1 | 0 |
| `PARM` | 105 | PedWalkTicks | i32 | 1 | 0 |
| `PARM` | 106 | PedClearSpeedMmPerTick | i32 | 1 | 0 |
| `PARM` | 107 | PedMaxCloseTicks | i32 | 1 | 0 |
| `PARM` | 108 | PedCarWindowTicks | i32 | 1 | 0 |
| `PARM` | 109 | PedPathfindBudgetPerTick | i32 | 1 | 0 |
| `PARM` | 110 | PedWorkBudgetPerTick | i32 | 1 | 0 |
| `PARM` | 111 | PedStuckCrossTicks | i32 | 1 | 0 |
| `PARM` | 112 | PedStuckStopTicks | i32 | 1 | 0 |
| `PARM` | 113 | PedSource | i32 | 1 | 0 |
| `PARM` | 114 | CityDayTicks | i32 | 1 | 0 |
| `PARM` | 115 | CityLotFrontageMm | i32 | 1 | 0 |
| `PARM` | 116 | CityLotDepthMm | i32 | 1 | 0 |
| `PARM` | 117 | CityNodeClearMm | i32 | 1 | 0 |
| `PARM` | 118 | CityGrowth | u8 | 1 | 0 |
| `PARM` | 119 | CityMaxNewBuildingsPerDay | i32 | 1 | 0 |
| `PARM` | 120 | CityZoningAuto | u8 | 1 | 0 |
| `PARM` | 121 | CityCCenterShareQ16 | i32 | 1 | 0 |
| `PARM` | 122 | CityIEdgeShareQ16 | i32 | 1 | 0 |
| `PARM` | 123 | CityUnzonedShareQ16 | i32 | 1 | 0 |
| `PARM` | 124 | CityMaxPaintM2 | i32 | 1 | 0 |
| `PARM` | 125 | CityInitialFillQ16 | i32 | 1 | 0 |
| `PARM` | 126 | CityCarOwnershipQ16 | i32 | 1 | 0 |
| `PARM` | 127 | CityLabourShareQ16 | i32 | 1 | 0 |
| `PARM` | 128 | CityTripExecution | i32 | 1 | 0 |
| `PARM` | 129 | CitySeekMaxDays | i32 | 1 | 0 |
| `PARM` | 130 | CityEvictMaxDays | i32 | 1 | 0 |
| `PARM` | 131 | CityWaitMaxTicks | i32 | 1 | 0 |
| `PARM` | 132 | CityMaxRelocatePerDayQ16 | i32 | 1 | 0 |
| `PARM` | 133 | CityMaxJobSwitchPerDayQ16 | i32 | 1 | 0 |
| `PARM` | 134 | CityMaxImmigrantsPerDayQ16 | i32 | 1 | 0 |
| `PARM` | 135 | CityPlanSlot | i32 | 1 | 0 |
| `PARM` | 136 | CityShopSlots | i32 | 1 | 0 |
| `PARM` | 137 | CityHomeMinSlots | i32 | 1 | 0 |
| `PARM` | 138 | CityMatchPerTick | i32 | 1 | 0 |
| `PARM` | 139 | CityVacateDays | i32 | 1 | 0 |
| `PARM` | 140 | CitySwitchRatioQ8 | i32 | 1 | 0 |
| `PARM` | 141 | CityRBias | i32 | 1 | 0 |
| `PARM` | 142 | CityWagePerShift | i32 | 1 | 0 |
| `PARM` | 143 | CityBasketPrice | i32 | 1 | 0 |
| `PARM` | 144 | CityGoodsPct | i32 | 1 | 0 |
| `PARM` | 145 | CityImportPct | i32 | 1 | 0 |
| `PARM` | 146 | CityExportPerShift | i32 | 1 | 0 |
| `PARM` | 147 | CityInitialResidentBalance | i32 | 1 | 0 |
| `PARM` | 148 | CityInitialFirmBalance | i32 | 1 | 0 |
| `PARM` | 149 | CityConstructCost | i32 | 1 | 0 |
| `PARM` | 150 | CityHireMarginPct | i32 | 1 | 0 |
| `PARM` | 151 | CityLossDaysFire | i32 | 1 | 0 |
| `PARM` | 152 | CityLossDaysClose | i32 | 1 | 0 |
| `PARM` | 153 | CityEwmaShift | i32 | 1 | 0 |
| `PARM` | 154 | CityCTargetPct | i32 | 1 | 0 |
| `PARM` | 155 | CityThetaMode64 | i32 | 1 | 0 |
| `PARM` | 156 | CityThetaDest64 | i32 | 1 | 0 |
| `PARM` | 157 | CityThetaTime64 | i32 | 1 | 0 |
| `PARM` | 158 | CityThetaFreq64 | i32 | 1 | 0 |
| `PARM` | 159 | CityBetaFreqQ8 | i32 | 1 | 0 |
| `PARM` | 160 | CityAscFreqDs | i32 | 1 | 0 |
| `PARM` | 161 | CityGammaFreqDs | i32 | 1 | 0 |
| `PARM` | 162 | CityAscNonWorkerDs | i32 | 1 | 0 |
| `PARM` | 163 | CityFreqMax | i32 | 1 | 0 |
| `PARM` | 164 | CityWWaitQ8 | i32 | 1 | 0 |
| `PARM` | 165 | CityWWalkQ8 | i32 | 1 | 0 |
| `PARM` | 166 | CityAscCarDs | i32 | 1 | 0 |
| `PARM` | 167 | CityAscBusDs | i32 | 1 | 0 |
| `PARM` | 168 | CityAscWalkDs | i32 | 1 | 0 |
| `PARM` | 169 | CitySdeQ8 | i32 | 1 | 0 |
| `PARM` | 170 | CitySdlQ8 | i32 | 1 | 0 |
| `PARM` | 171 | CityTimeStepSlots | i32 | 1 | 0 |
| `PARM` | 172 | CityDestCandidates | i32 | 1 | 0 |
| `PARM` | 173 | CityMaxWalkMm | i32 | 1 | 0 |
| `PARM` | 174 | CitySkimPeriods | i32 | 1 | 0 |
| `PARM` | 175 | CitySkimShift | i32 | 1 | 0 |
| `PARM` | 176 | CitySkimUpdatePerTick | i32 | 1 | 0 |
| `PARM` | 177 | MaxLots | i32 | 1 | 0 |
| `PARM` | 178 | MaxBuildings | i32 | 1 | 0 |
| `PARM` | 179 | MaxCityResidents | i32 | 1 | 0 |
| `PARM` | 180 | SkimObsCapacity | i32 | 1 | 0 |
| `PARM` | 181 | CityGridOriginX | f32 | 1 | 0 |
| `PARM` | 182 | CityGridOriginZ | f32 | 1 | 0 |
| `PARM` | 183 | CityGridCellsX | i32 | 1 | 0 |
| `PARM` | 184 | CityGridCellsZ | i32 | 1 | 0 |
| `PARM` | 185 | NetCornerRadius | f32 | 1 | 0 |
| `PARM` | 186 | TransitBayModel | i32 | 1 | 0 |
| `PARM` | 187 | TransitBayYieldTicks | i32 | 1 | 60 |
| `PARM` | 188 | RoadProfileMode | i32 | 1 | 0 |
| `PARM` | 189 | WorldGridOriginX | f32 | 1 | 0 |
| `PARM` | 190 | WorldGridOriginZ | f32 | 1 | 0 |
| `PARM` | 191 | CityBudget | u8 | 1 | 0 |
| `PARM` | 192 | CityTaxRBp | i32 | 1 | 600 |
| `PARM` | 193 | CityTaxCProfitBp | i32 | 1 | 2000 |
| `PARM` | 194 | CityTaxIProfitBp | i32 | 1 | 2000 |
| `PARM` | 195 | CityLandTaxBpDay | i32 | 1 | 15 |
| `PARM` | 196 | CityBusFare | i32 | 1 | 2 |
| `PARM` | 197 | CityInitialCityBalancePerResident | i32 | 1 | 60 |
| `PARM` | 198 | CityInterestBpDay | i32 | 1 | 3 |
| `PARM` | 199 | CityCreditDays | i32 | 1 | 60 |
| `PARM` | 200 | CityMaintRoadStreetPerLaneKmDay | i32 | 1 | 0 |
| `PARM` | 201 | CityMaintRoadArterialPerLaneKmDay | i32 | 1 | 0 |
| `PARM` | 202 | CityMaintBusDay | i32 | 1 | 0 |
| `PARM` | 203 | CityMaintStopDay | i32 | 1 | 0 |
| `PARM` | 204 | CityLvRef | i32 | 1 | 200 |
| `PARM` | 205 | CityLvSlopePerMin | i32 | 1 | 20 |
| `PARM` | 206 | CityLvMin | i32 | 1 | 50 |
| `PARM` | 207 | CityLvMax | i32 | 1 | 1000 |
| `PARM` | 208 | CityLvShift | i32 | 1 | 3 |
| `PARM` | 209 | CityVotPct | i32 | 1 | 50 |
| `PARM` | 210 | CityResBufferDays | i32 | 1 | 5 |
| `PARM` | 211 | CityFirmBufferDays | i32 | 1 | 10 |
| `PARM` | 212 | CityPayoutShift | i32 | 1 | 3 |
| `PARM` | 213 | CityEvictDebtDays | i32 | 1 | 3 |
| `PARM` | 214 | CityBudgetEwmaShift | i32 | 1 | 3 |
| `PARM` | 215 | CityUndoRefundS | i32 | 1 | 10 |
| `PARM` | 216 | CityMaxTaxBp | i32 | 1 | 5000 |
| `PARM` | 217 | CityMaxLandBp | i32 | 1 | 100 |
| `PARM` | 218 | CityMaxFare | i32 | 1 | 1000 |
| `PARM` | 219 | LandUseCount | i32 | 1 | 0 |
| `PARM` | 220 | ParkingEnabled | u8 | 1 | 0 |
| `PARM` | 221 | ParkingAccessWalkAgents | u8 | 1 | 0 |
| `PARM` | 222 | ParkingWalkMaxMm | i32 | 1 | 0 |
| `PARM` | 223 | ParkingHomeWalkMaxMm | i32 | 1 | 0 |
| `PARM` | 224 | ParkingMaxCandidates | i32 | 1 | 0 |
| `PARM` | 225 | ParkingWalkSpeedMmS | i32 | 1 | 0 |
| `PARM` | 226 | ParkingFeeDsPerMoney | i32 | 1 | 0 |
| `PARM` | 227 | ParkingRetryTicks | i32 | 1 | 0 |
| `PARM` | 228 | ParkingSearchRetryTicks | i32 | 1 | 0 |
| `PARM` | 229 | ParkingSearchMaxTicks | i32 | 1 | 0 |
| `PARM` | 230 | ParkingHomeRetryPerDayQ16 | i32 | 1 | 0 |
| `PARM` | 231 | ParkingCurbSlotMm | i32 | 1 | 0 |
| `PARM` | 232 | ParkingCurbStopClearMm | i32 | 1 | 0 |
| `PARM` | 233 | ParkingCurbStreet | u8 | 1 | 0 |
| `PARM` | 234 | ParkingCurbArterial | u8 | 1 | 0 |
| `PARM` | 235 | ParkingCurbFee | i32 | 1 | 0 |
| `PARM` | 236 | ParkingCurbPermits | u8 | 1 | 0 |
| `PARM` | 237 | ParkingGarageFee | i32 | 1 | 0 |
| `PARM` | 238 | ParkingGaragePermits | u8 | 1 | 0 |
| `PARM` | 239 | MaxParking | i32 | 1 | 0 |
| `PARM` | 240 | ProgressionEnabled | u8 | 1 | 0 |
| `PARM` | 241 | TerrainEnabled | u8 | 1 | 0 |
| `PARM` | 242 | TerrainMaxGradeStreetPm | i32 | 1 | 80 |
| `PARM` | 243 | TerrainMaxGradeArterialPm | i32 | 1 | 60 |
| `PARM` | 244 | TerrainMaxCutCm | i32 | 1 | 1000 |
| `PARM` | 245 | TerrainMaxFillCm | i32 | 1 | 600 |
| `PARM` | 246 | TerrainBankSlopePm | i32 | 1 | 500 |
| `PARM` | 247 | TerrainBridgeClearanceCm | i32 | 1 | 200 |
| `PARM` | 248 | TerrainMaxBridgeM | i32 | 1 | 300 |
| `PARM` | 249 | TerrainAutoBridge | u8 | 1 | 1 |
| `PARM` | 250 | TerrainMaxBrushM | i32 | 1 | 96 |
| `PARM` | 251 | TerrainMaxDabCm | i32 | 1 | 200 |
| `PARM` | 252 | TerrainMaxSamplesPerTick | i32 | 1 | 32768 |
| `PARM` | 253 | TerrainMaxFloodSamples | i32 | 1 | 65536 |
| `PARM` | 254 | LotsMaxSlopePct | i32 | 1 | 15 |
| `PARM` | 255 | GradeMaxBoostPm | i32 | 1 | 1100 |
| `PARM` | 256 | CityMinImmigrantsPerDay | i32 | 1 | 0 |
| `PARM` | 257 | CityGrowthHourly | u8 | 1 | 0 |
| `PARM` | 258 | CityInitialCityBalance | i32 | 1 | 0 |
| `PARM` | 259 | CityOutsideCount | i32 | 1 | 0 |
| `PARM` | 260 | CityOutsideJobsDecayQ16 | i32 | 1 | 0 |
| `PARM` | 261 | CityOutsideArrivalByCar | u8 | 1 | 0 |
| `PARM` | 262 | CityOutside0Node | i32 | 1 | 0 |
| `PARM` | 263 | CityOutside0Jobs | i32 | 1 | 0 |
| `PARM` | 264 | CityOutside0JobCostTicks | i32 | 1 | 0 |
| `PARM` | 265 | CityOutside0ShareQ16 | i32 | 1 | 0 |
| `PARM` | 266 | CityOutside1Node | i32 | 1 | 0 |
| `PARM` | 267 | CityOutside1Jobs | i32 | 1 | 0 |
| `PARM` | 268 | CityOutside1JobCostTicks | i32 | 1 | 0 |
| `PARM` | 269 | CityOutside1ShareQ16 | i32 | 1 | 0 |
| `PARM` | 270 | CityOutside2Node | i32 | 1 | 0 |
| `PARM` | 271 | CityOutside2Jobs | i32 | 1 | 0 |
| `PARM` | 272 | CityOutside2JobCostTicks | i32 | 1 | 0 |
| `PARM` | 273 | CityOutside2ShareQ16 | i32 | 1 | 0 |
| `PARM` | 274 | CityOutside3Node | i32 | 1 | 0 |
| `PARM` | 275 | CityOutside3Jobs | i32 | 1 | 0 |
| `PARM` | 276 | CityOutside3JobCostTicks | i32 | 1 | 0 |
| `PARM` | 277 | CityOutside3ShareQ16 | i32 | 1 | 0 |
| `PARM` | 278 | RulesEmergencyRedSpeed | f32 | 1 | 4.17 |
| `PARM` | 279 | DemandSpecialOnCall | u8 | 1 | 0 |
| `PARM` | 280 | LifecycleEnabled | u8 | 1 | 0 |
| `PARM` | 281 | LifecycleAbandonedDecayPct | i32 | 1 | 0 |
| `PARM` | 282 | LifecycleLowlandCm | i32 | 1 | 0 |
| `PARM` | 283 | LifecycleWornRelocateQ16 | i32 | 1 | 0 |
| `PARM` | 284 | LifecycleRubbleCostPct | i32 | 1 | 0 |
| `PARM` | 285 | LifecycleCellM | i32 | 1 | 0 |
| `PARM` | 286 | LifecycleSigShift | i32 | 1 | 0 |
| `PARM` | 287 | LifecycleHeritageLossRadiusM | i32 | 1 | 0 |
| `PARM` | 288 | LifecycleLvBlightPermille | i32 | 1 | 0 |
| `PARM` | 289 | LifecycleRepairRequestBp | i32 | 1 | 0 |
| `PARM` | 290 | LifecycleRepairPerUnit | i32 | 1 | 0 |
| `PARM` | 291 | LifecycleRepairMinutes | i32 | 1 | 0 |
| `PARM` | 292 | LifecycleCrewDayCost | i32 | 1 | 0 |
| `PARM` | 293 | LifecycleMaxServiceVehicles | i32 | 1 | 0 |
| `PARM` | 294 | LifecycleMaxDispatchPerTick | i32 | 1 | 0 |
| `PARM` | 295 | LifecycleTariffBurdenPct | i32 | 1 | 0 |
| `PARM` | 296 | LifecycleTariffLeaveQ16 | i32 | 1 | 0 |
| `PARM` | 297 | LifecycleMaxUtilityTariff | i32 | 1 | 0 |
| `PARM` | 298 | LifecycleUtilityTariff | i32 | 1 | 0 |
| `PARM` | 299 | LifecycleRestorePerUnit | i32 | 1 | 0 |
| `PARM` | 300 | ServicesEnabled | u8 | 1 | 0 |
| `PARM` | 301 | ServicesMaxServiceSites | i32 | 1 | 0 |
| `PARM` | 302 | ServicesMaxCalls | i32 | 1 | 0 |
| `PARM` | 303 | ServicesMaxFires | i32 | 1 | 0 |
| `PARM` | 304 | ServicesDisorderIllPct | i32 | 1 | 0 |
| `PARM` | 305 | ServicesFireRiskPct | i32 | 1 | 0 |
| `PARM` | 306 | ServicesSmolderS | i32 | 1 | 0 |
| `PARM` | 307 | ServicesAnnexBuyPct | i32 | 1 | 0 |
| `PARM` | 308 | ServicesIllLightPpm | i32 | 1 | 0 |
| `PARM` | 309 | ServicesIllInpatientPpm | i32 | 1 | 0 |
| `PARM` | 310 | ServicesIllCriticalPpm | i32 | 1 | 0 |
| `PARM` | 311 | ServicesIllDaysLight | i32 | 1 | 0 |
| `PARM` | 312 | ServicesIllDaysInpatient | i32 | 1 | 0 |
| `PARM` | 313 | ServicesIllDaysCritical | i32 | 1 | 0 |
| `PARM` | 314 | ServicesFireCap | i32 | 1 | 0 |
| `PARM` | 315 | ServicesFireSpreadPpm | i32 | 1 | 0 |
| `DEMD` | 1 | TypeMix | f32 | TypeMix.Length | 0 |
| `DEMD` | 2 | ZoneCenterX | f32 | Zones.Length | 0 |
| `DEMD` | 3 | ZoneCenterZ | f32 | Zones.Length | 0 |
| `DEMD` | 4 | ZoneHalfSize | f32 | Zones.Length | 0 |
| `DEMD` | 5 | ZoneWeight | f32 | Zones.Length | 0 |
| `DEMD` | 6 | ZoneIsExternal | u8 | Zones.Length | 0 |
| `DEMD` | 7 | OdFromZone | i32 | OdPairs.Length | 0 |
| `DEMD` | 8 | OdToZone | i32 | OdPairs.Length | 0 |
| `DEMD` | 9 | OdWeight | f32 | OdPairs.Length | 0 |
| `VTYP` | 1 | Category | i32 | Types.Length | 0 |
| `VTYP` | 2 | Length | f32 | Types.Length | 0 |
| `VTYP` | 3 | Width | f32 | Types.Length | 0 |
| `VTYP` | 4 | MaxSpeed | f32 | Types.Length | 0 |
| `VTYP` | 5 | SpeedFactor | f32 | Types.Length | 0 |
| `VTYP` | 6 | Accel | f32 | Types.Length | 0 |
| `VTYP` | 7 | ComfortDecel | f32 | Types.Length | 0 |
| `VTYP` | 8 | HeadwayTime | f32 | Types.Length | 0 |
| `VTYP` | 9 | MinGap | f32 | Types.Length | 0 |
| `VTYP` | 10 | Delta | f32 | Types.Length | 0 |
| `VTYP` | 11 | Politeness | f32 | Types.Length | 0 |
| `VTYP` | 12 | LaneChangeThreshold | f32 | Types.Length | 0 |
| `VTYP` | 13 | SafeDecel | f32 | Types.Length | 0 |
| `VTYP` | 14 | RightBias | f32 | Types.Length | 0 |
| `VTYP` | 15 | LaneChangeDurationTicks | i32 | Types.Length | 0 |
| `RPRF` (PARM.RoadProfileMode = 1) | 1 | ModuleMm | i32 | 1 | 0 |
| `RPRF` (PARM.RoadProfileMode = 1) | 2 | Kind | u8 | ProfileCount | 0 |
| `RPRF` (PARM.RoadProfileMode = 1) | 3 | WidthMm | i32 | ProfileCount | 0 |
| `RPRF` (PARM.RoadProfileMode = 1) | 4 | ElemCount | u8 | ProfileCount | 0 |
| `RPRF` (PARM.RoadProfileMode = 1) | 5 | IdLength | u16 | ProfileCount | 0 |
| `RPRF` (PARM.RoadProfileMode = 1) | 6 | ElemKind | u8 | sum(ElemCount) | 0 |
| `RPRF` (PARM.RoadProfileMode = 1) | 7 | ElemDir | u8 | sum(ElemCount) | 0 |
| `RPRF` (PARM.RoadProfileMode = 1) | 8 | ElemWidthMm | i32 | sum(ElemCount) | 0 |
| `RPRF` (PARM.RoadProfileMode = 1) | 9 | IdBlob | u8 | sum(IdLength) | 0 |
| `BTYP` (demand.model = residents) | 1 | Kind | u8 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 2 | Density | u8 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 3 | Capacity | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 4 | ShopCapacity | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 5 | ShiftStartSlot | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 6 | ShiftSlots | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 7 | ConstructDays | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 8 | AttractQ8 | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 9 | VisualRow | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 10 | ParkingCapacity | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 11 | ParkingAccess | u8 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 12 | ParkingFee | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 13 | DecayBpDay | u16 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 14 | CeilDropBpDay | u16 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 15 | DemolishCost | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 16 | BuildCost | i32 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 17 | ServiceKind | u8 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 18 | Vehicles | u8 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 19 | VehicleType | u8 | Rows.Length | 0 |
| `BTYP` (demand.model = residents) | 20 | FirePpm | i32 | Rows.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 1 | RecFund | u8 | Records.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 2 | RecRev | u16 | Records.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 3 | RecFirstLevel | i32 | Records.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 4 | RecLevelCount | i32 | Records.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 5 | RecFirstCall | i32 | Records.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 6 | RecCallCount | i32 | Records.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 7 | RecFirstVisit | i32 | Records.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 8 | RecVisitCount | i32 | Records.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 9 | LevelType | i32 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 10 | LevelRoles | u8 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 11 | LevelLotMinF | u8 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 12 | LevelLotMinD | u8 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 13 | LevelMilestone | u8 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 14 | LevelBuildCost | i32 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 15 | LevelUpkeepDay | i32 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 16 | LevelCapacityDay | i32 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 17 | LevelBeds | i32 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 18 | LevelIcu | i32 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 19 | LevelOpenSlot | i16 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 20 | LevelCloseSlot | i16 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 21 | LevelStaySlots | i16 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 22 | LevelDispatchVehicle | i16 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 23 | LevelUnits | i32 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 24 | LevelPerUnitDay | i32 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 25 | LevelFirstUpgrade | i32 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 26 | LevelUpgradeCount | i32 | Levels.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 27 | UpgradeCost | i32 | Upgrades.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 28 | UpgradeUpkeepDay | i32 | Upgrades.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 29 | UpgradeMilestone | u8 | Upgrades.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 30 | UpgradeEffect | u8 | Upgrades.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 31 | UpgradeValue | i32 | Upgrades.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 32 | CallKind | u8 | Calls.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 33 | CallFlags | u8 | Calls.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 34 | CallOnsiteS | i32 | Calls.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 35 | CallDeadlineS | i32 | Calls.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 36 | CallPerCall | i32 | Calls.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 37 | VisitReason | u8 | Visits.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 38 | VisitMinRole | u8 | Visits.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 39 | VisitPerVisit | i32 | Visits.Length | 0 |
| `SVCR` (PARM.ServicesEnabled = 1) | 40 | VisitMaxCostS | i32 | Visits.Length | 0 |
| `WRLD` | 1 | Tick | i64 | 1 | 0 |
| `WRLD` | 2 | NetVersion | i32 | 1 | 0 |
| `WRLD` | 3 | TargetVehicles | i32 | 1 | 0 |
| `WRLD` | 4 | SelectedIndex | i32 | 1 | 0 |
| `WRLD` | 5 | SelectedGeneration | i32 | 1 | 0 |
| `WRLD` | 6 | GridlockStreak | i32 | 1 | 0 |
| `WRLD` | 7 | RecalcPending | u8 | 1 | 0 |
| `WRLD` | 8 | SegmentLoadsTick | i64 | 1 | -1 |
| `WRLD` | 9 | RngState | u64 | 6 | 0 |
| `WRLD` | 10 | RngInc | u64 | 6 | 0 |
| `WRLD` | 11 | PedRngState | u64 | 1 | 0 |
| `WRLD` | 12 | PedRngInc | u64 | 1 | 0 |
| `WRLD` | 13 | TargetPeds | i32 | 1 | 0 |
| `WRLD` | 14 | DemandRngState | u64 | 1 | 0 |
| `WRLD` | 15 | DemandRngInc | u64 | 1 | 0 |
| `WRLD` | 16 | CityDay | i32 | 1 | 0 |
| `WRLD` | 17 | CityExec | i32 | 1 | 0 |
| `WRLD` | 18 | CityExecPending | i32 | 1 | 0 |
| `CNTR` | 1 | Ticks | i64 | 1 | 0 |
| `CNTR` | 2 | VehiclesSpawned | i64 | 1 | 0 |
| `CNTR` | 3 | TripsCompleted | i64 | 1 | 0 |
| `CNTR` | 4 | TripsDropped | i64 | 1 | 0 |
| `CNTR` | 5 | TripTimeTicksTotal | i64 | 1 | 0 |
| `CNTR` | 6 | DistanceTotalMm | i64 | 1 | 0 |
| `CNTR` | 7 | VehicleTicksTotal | i64 | 1 | 0 |
| `CNTR` | 8 | MovingVehicleTicksTotal | i64 | 1 | 0 |
| `CNTR` | 9 | ActiveVehicles | i32 | 1 | 0 |
| `CNTR` | 10 | StuckVehicles | i32 | 1 | 0 |
| `CNTR` | 11 | StuckEventsTotal | i64 | 1 | 0 |
| `CNTR` | 12 | StuckTicksTotal | i64 | 1 | 0 |
| `CNTR` | 13 | GridlockFlag | i32 | 1 | 0 |
| `CNTR` | 14 | GridlockTicksTotal | i64 | 1 | 0 |
| `CNTR` | 15 | LaneChanges | i64 | 1 | 0 |
| `CNTR` | 16 | LaneChangesAborted | i64 | 1 | 0 |
| `CNTR` | 17 | HardClamps | i64 | 1 | 0 |
| `CNTR` | 18 | PathfindQueries | i64 | 1 | 0 |
| `CNTR` | 19 | RouteDeferred | i64 | 1 | 0 |
| `CNTR` | 20 | PathfindExpandedNodes | i64 | 1 | 0 |
| `CNTR` | 21 | PathfindWork | i64 | 1 | 0 |
| `CNTR` | 22 | RouteFailed | i64 | 1 | 0 |
| `CNTR` | 23 | Reroutes | i64 | 1 | 0 |
| `CNTR` | 24 | PlanCompactions | i64 | 1 | 0 |
| `CNTR` | 25 | PlanUnpacks | i64 | 1 | 0 |
| `CNTR` | 26 | UnpackWork | i64 | 1 | 0 |
| `CNTR` | 27 | WindowRefills | i64 | 1 | 0 |
| `CNTR` | 28 | RefillLate | i64 | 1 | 0 |
| `CNTR` | 29 | TimedChecks | i64 | 1 | 0 |
| `CNTR` | 30 | TimedTriggered | i64 | 1 | 0 |
| `CNTR` | 31 | CostRecalcs | i64 | 1 | 0 |
| `CNTR` | 32 | TimedReroutes | i64 | 1 | 0 |
| `CNTR` | 33 | CustomizeRows | i64 | 1 | 0 |
| `CNTR` | 34 | CustomizeWork | i64 | 1 | 0 |
| `CNTR` | 35 | CustomizeLagTicks | i64 | 1 | 0 |
| `CNTR` | 36 | OverlayRebuilds | i64 | 1 | 0 |
| `CNTR` | 37 | OverlayDisabled | i32 | 1 | 0 |
| `CNTR` | 38 | ConnectorEntries | i64 | 1 | 0 |
| `CNTR` | 39 | CommandsApplied | i64 | 1 | 0 |
| `CNTR` | 40 | CommandsRejected | i64 | 1 | 0 |
| `CNTR` | 41 | CommandsDropped | i64 | 1 | 0 |
| `CNTR` | 42 | EventsDropped | i64 | 1 | 0 |
| `CNTR` | 43 | SpawnDeferredTicks | i64 | 1 | 0 |
| `CNTR` | 44 | InvalidHandleOps | i64 | 1 | 0 |
| `CNTR` | 45 | SignalizedNodes | i32 | 1 | 0 |
| `CNTR` | 46 | NodeCount | i32 | 1 | 0 |
| `CNTR` | 47 | SegmentCount | i32 | 1 | 0 |
| `CNTR` | 48 | LaneCount | i32 | 1 | 0 |
| `CNTR` | 49 | ZoneCount | i32 | 1 | 0 |
| `CNTR` | 50 | BusDispatched | i64 | 1 | 0 |
| `CNTR` | 51 | BusStopArrivals | i64 | 1 | 0 |
| `CNTR` | 52 | BusHeadwayDeviationTicksTotal | i64 | 1 | 0 |
| `CNTR` | 53 | BusHeadwaySamples | i64 | 1 | 0 |
| `CNTR` | 54 | BusPassengersBoarded | i64 | 1 | 0 |
| `CNTR` | 55 | Grants | i64 | 1 | 0 |
| `CNTR` | 56 | GateDeniedExit | i64 | 1 | 0 |
| `CNTR` | 57 | PriorityInversions | i64 | 1 | 0 |
| `CNTR` | 58 | BoxWaitTicks | i64 | 1 | 0 |
| `CNTR` | 59 | GateWaitTicksTotal | i64 | 1 | 0 |
| `CNTR` | 60 | SignalLeftWaitMax | i64 | 1 | 0 |
| `CNTR` | 61 | ArterialSignalGrants | i64 | 1 | 0 |
| `CNTR` | 62 | ArterialSignalStops | i64 | 1 | 0 |
| `CNTR` | 63 | StarveDenials | i64 | 1 | 0 |
| `CNTR` | 64 | ArrowPhases | i64 | 1 | 0 |
| `CNTR` | 65 | SpillOuts | i64 | 1 | 0 |
| `CNTR` | 66 | ExitClaims | i64 | 1 | 0 |
| `CNTR` | 67 | ExitClaimDenials | i64 | 1 | 0 |
| `CNTR` | 68 | GrantsRevoked | i64 | 1 | 0 |
| `CNTR` | 69 | MicroVehicles | i32 | 1 | 0 |
| `CNTR` | 70 | MesoVehicles | i32 | 1 | 0 |
| `CNTR` | 71 | MicroVehicleTicks | i64 | 1 | 0 |
| `CNTR` | 72 | MesoVehicleTicks | i64 | 1 | 0 |
| `CNTR` | 73 | MicroDistanceMm | i64 | 1 | 0 |
| `CNTR` | 74 | MesoDistanceMm | i64 | 1 | 0 |
| `CNTR` | 75 | LodEvals | i64 | 1 | 0 |
| `CNTR` | 76 | LodCellFlips | i64 | 1 | 0 |
| `CNTR` | 77 | LodBudgetSkips | i64 | 1 | 0 |
| `CNTR` | 78 | MesoClamps | i64 | 1 | 0 |
| `CNTR` | 79 | MesoLaneChanges | i64 | 1 | 0 |
| `CNTR` | 80 | StillMaxTicks | i32 | 1 | 0 |
| `CNTR` | 81 | StillConcurrent60Max | i32 | 1 | 0 |
| `CNTR` | 82 | StillEpisodes60 | i64 | 1 | 0 |
| `CNTR` | 83 | StillEpisodes120 | i64 | 1 | 0 |
| `CNTR` | 84 | LaneShareSamples | i64 | 1 | 0 |
| `CNTR` | 85 | LaneShareMaxPermilleSum | i64 | 1 | 0 |
| `CNTR` | 86 | LaneShareH0 | i64 | 1 | 0 |
| `CNTR` | 87 | LaneShareH1 | i64 | 1 | 0 |
| `CNTR` | 88 | LaneShareH2 | i64 | 1 | 0 |
| `CNTR` | 89 | LaneShareH3 | i64 | 1 | 0 |
| `CNTR` | 90 | LaneShareH4 | i64 | 1 | 0 |
| `CNTR` | 91 | LaneShareH5 | i64 | 1 | 0 |
| `CNTR` | 92 | LaneShareH6 | i64 | 1 | 0 |
| `CNTR` | 93 | LaneShareH7 | i64 | 1 | 0 |
| `CNTR` | 94 | LaneShareH8 | i64 | 1 | 0 |
| `CNTR` | 95 | LaneShareH9 | i64 | 1 | 0 |
| `CNTR` | 96 | PedActive | i32 | 1 | 0 |
| `CNTR` | 97 | PedActiveTicksTotal | i64 | 1 | 0 |
| `CNTR` | 98 | PedSpawned | i64 | 1 | 0 |
| `CNTR` | 99 | PedTripsCompleted | i64 | 1 | 0 |
| `CNTR` | 100 | PedTripTicksTotal | i64 | 1 | 0 |
| `CNTR` | 101 | PedCrossWaitTicksTotal | i64 | 1 | 0 |
| `CNTR` | 102 | PedCrossReleases | i64 | 1 | 0 |
| `CNTR` | 103 | PedStopWaitTicksTotal | i64 | 1 | 0 |
| `CNTR` | 104 | PedStopBoardings | i64 | 1 | 0 |
| `CNTR` | 105 | PedStuckEvents | i64 | 1 | 0 |
| `CNTR` | 106 | CarCrossDelayTicks | i64 | 1 | 0 |
| `CNTR` | 107 | CrossingClosedTicks | i64 | 1 | 0 |
| `CNTR` | 108 | CrossingObservedTicks | i64 | 1 | 0 |
| `CNTR` | 109 | BusPassengersAlighted | i64 | 1 | 0 |
| `CNTR` | 110 | BusPaxOnBoardTicks | i64 | 1 | 0 |
| `CNTR` | 111 | PedPathQueries | i64 | 1 | 0 |
| `CNTR` | 112 | PedPathWork | i64 | 1 | 0 |
| `CNTR` | 113 | PedNoPath | i64 | 1 | 0 |
| `CNTR` | 114 | PedModeFallback | i64 | 1 | 0 |
| `CNTR` | 115 | GrantsRevokedPed | i64 | 1 | 0 |
| `CNTR` | 116 | PedAllocFailed | i64 | 1 | 0 |
| `CNTR` | 117 | PedNetResets | i64 | 1 | 0 |
| `CNTR` | 118 | CityEvents | i64 | 1 | 0 |
| `CNTR` | 119 | ResToursPlanned | i64 | 1 | 0 |
| `CNTR` | 120 | ResToursDropped | i64 | 1 | 0 |
| `CNTR` | 121 | ResToursUnreachable | i64 | 1 | 0 |
| `CNTR` | 122 | ResToursCancelled | i64 | 1 | 0 |
| `CNTR` | 123 | ResLegsCar | i64 | 1 | 0 |
| `CNTR` | 124 | ResLegsBus | i64 | 1 | 0 |
| `CNTR` | 125 | ResLegsWalk | i64 | 1 | 0 |
| `CNTR` | 126 | ResLegsDone | i64 | 1 | 0 |
| `CNTR` | 127 | ResLegCostDsTotal | i64 | 1 | 0 |
| `CNTR` | 128 | ResVisits | i64 | 1 | 0 |
| `CNTR` | 129 | ResVisitsUnserved | i64 | 1 | 0 |
| `CNTR` | 130 | ResLegOverdue | i64 | 1 | 0 |
| `CNTR` | 131 | ResStuck | i64 | 1 | 0 |
| `CNTR` | 132 | ResWaitDepartTicks | i64 | 1 | 0 |
| `CNTR` | 133 | SkimObserved | i64 | 1 | 0 |
| `CNTR` | 134 | SkimObsDropped | i64 | 1 | 0 |
| `CNTR` | 135 | SkimUpdates | i64 | 1 | 0 |
| `CNTR` | 136 | Immigrants | i64 | 1 | 0 |
| `CNTR` | 137 | Emigrants | i64 | 1 | 0 |
| `CNTR` | 138 | ResRelocations | i64 | 1 | 0 |
| `CNTR` | 139 | ResHired | i64 | 1 | 0 |
| `CNTR` | 140 | ResFired | i64 | 1 | 0 |
| `CNTR` | 141 | ResJobSwitches | i64 | 1 | 0 |
| `CNTR` | 142 | BuildingsBuilt | i64 | 1 | 0 |
| `CNTR` | 143 | BuildingsAbandoned | i64 | 1 | 0 |
| `CNTR` | 144 | BuildingsRemoved | i64 | 1 | 0 |
| `CNTR` | 145 | FirmClosures | i64 | 1 | 0 |
| `CNTR` | 146 | PaintCommands | i64 | 1 | 0 |
| `CNTR` | 147 | ExecSwitches | i64 | 1 | 0 |
| `CNTR` | 148 | CityDays | i64 | 1 | 0 |
| `CNTR` | 149 | AggregateLegs | i64 | 1 | 0 |
| `CNTR` | 150 | BayEntries | i64 | 1 | 0 |
| `CNTR` | 151 | BayFallbacks | i64 | 1 | 0 |
| `CNTR` | 152 | BayYields | i64 | 1 | 0 |
| `CNTR` | 153 | BayExitWaitTicksTotal | i64 | 1 | 0 |
| `CNTR` | 154 | FareEvaded | i64 | 1 | 0 |
| `CNTR` | 155 | DebtEvictions | i64 | 1 | 0 |
| `CNTR` | 156 | InsufficientFunds | i64 | 1 | 0 |
| `CNTR` | 157 | ParkSearchTimeSum | i64 | 1 | 0 |
| `CNTR` | 158 | ParkSearchTimeCount | i64 | 1 | 0 |
| `CNTR` | 159 | ParkSearchHist0 | i64 | 1 | 0 |
| `CNTR` | 160 | ParkSearchHist1 | i64 | 1 | 0 |
| `CNTR` | 161 | ParkSearchHist2 | i64 | 1 | 0 |
| `CNTR` | 162 | ParkSearchHist3 | i64 | 1 | 0 |
| `CNTR` | 163 | ParkSearchHist4 | i64 | 1 | 0 |
| `CNTR` | 164 | ParkSearchHist5 | i64 | 1 | 0 |
| `CNTR` | 165 | ParkNoSpotWaits | i64 | 1 | 0 |
| `CNTR` | 166 | ParkModeSwitch | i64 | 1 | 0 |
| `CNTR` | 167 | ParkToursDropped | i64 | 1 | 0 |
| `CNTR` | 168 | ParkOverflow | i64 | 1 | 0 |
| `CNTR` | 169 | ParkTowed | i64 | 1 | 0 |
| `CNTR` | 170 | ParkCarShed | i64 | 1 | 0 |
| `CNTR` | 171 | ParkSearching | i64 | 1 | 0 |
| `CNTR` | 172 | ParkWalkTicks | i64 | 1 | 0 |
| `CNTR` | 173 | LockedRejects | i64 | 1 | 0 |
| `CNTR` | 174 | OnCallRedGrants | i64 | 1 | 0 |
| `CNTR` | 175 | DenyDescends | i64 | 1 | 0 |
| `CNTR` | 176 | BuildingsCrumbled | i64 | 1 | 0 |
| `CNTR` | 177 | BuildingsDemolished | i64 | 1 | 0 |
| `CNTR` | 178 | BuildingsResettled | i64 | 1 | 0 |
| `CNTR` | 179 | RepairsDone | i64 | 1 | 0 |
| `CNTR` | 180 | RepairDelayTicksTotal | i64 | 1 | 0 |
| `CNTR` | 181 | RepairNoDepot | i64 | 1 | 0 |
| `CNTR` | 182 | CrewTimeouts | i64 | 1 | 0 |
| `CNTR` | 183 | TariffLeaves | i64 | 1 | 0 |
| `CNTR` | 184 | Restorations | i64 | 1 | 0 |
| `CNTR` | 185 | CallsQueued | i64 | 1 | 0 |
| `CNTR` | 186 | CallsDone | i64 | 1 | 0 |
| `CNTR` | 187 | RespTicksTotal | i64 | 1 | 0 |
| `CNTR` | 188 | CallsLate | i64 | 1 | 0 |
| `CNTR` | 189 | IllStarted | i64 | 1 | 0 |
| `CNTR` | 190 | CareVisits | i64 | 1 | 0 |
| `CNTR` | 191 | CareRefused | i64 | 1 | 0 |
| `CNTR` | 192 | IcuAdmitted | i64 | 1 | 0 |
| `CNTR` | 193 | IcuOverflow | i64 | 1 | 0 |
| `CNTR` | 194 | ResidentsLostNoAmbulance | i64 | 1 | 0 |
| `CNTR` | 195 | FiresStarted | i64 | 1 | 0 |
| `CNTR` | 196 | FiresOut | i64 | 1 | 0 |
| `CNTR` | 197 | BuildingsBurned | i64 | 1 | 0 |
| `CNTR` | 198 | FiresSpread | i64 | 1 | 0 |
| `CNTR` | 199 | FiresCapped | i64 | 1 | 0 |
| `NETW` | 1 | NodeCount | i32 | 1 | 0 |
| `NETW` | 2 | SegmentCount | i32 | 1 | 0 |
| `NETW` | 3 | LaneCount | i32 | 1 | 0 |
| `NETW` | 4 | LanePointCount | i32 | 1 | 0 |
| `NETW` | 5 | SegmentPointCount | i32 | 1 | 0 |
| `NETW` | 6 | NodeArmCount | i32 | 1 | 0 |
| `NETW` | 7 | LaneNextCount | i32 | 1 | 0 |
| `NETW` | 8 | ZoneCount | i32 | 1 | 0 |
| `NETW` | 9 | ZoneLaneCount | i32 | 1 | 0 |
| `NETW` | 10 | BusStopCount | i32 | 1 | 0 |
| `NETW` | 11 | BusRouteCount | i32 | 1 | 0 |
| `NETW` | 12 | BusRouteStopCount | i32 | 1 | 0 |
| `NETW` | 13 | ToolsTouched | u8 | 1 | 0 |
| `NETW` | 14 | NodeFreeCount | i32 | 1 | 0 |
| `NETW` | 15 | SegmentFreeCount | i32 | 1 | 0 |
| `NETW` | 16 | BusStopFreeCount | i32 | 1 | 0 |
| `NETW` | 17 | BusRouteFreeCount | i32 | 1 | 0 |
| `NETW` | 18 | NodeFree | i32 | NodeFreeCount | 0 |
| `NETW` | 19 | SegmentFree | i32 | SegmentFreeCount | 0 |
| `NETW` | 20 | BusStopFree | i32 | BusStopFreeCount | 0 |
| `NETW` | 21 | BusRouteFree | i32 | BusRouteFreeCount | 0 |
| `NETW` | 22 | LaneFreeCount | i32 | 1 | 0 |
| `NETW` | 23 | LaneLeaked | i64 | 1 | 0 |
| `NETW` | 24 | LaneFree | u64 | LaneFreeCount | 0 |
| `NETW` | 25 | LaneLadder | u64 | ceil(Tail / 64) | 0 |
| `NETW` | 26 | LanePointFreeCount | i32 | 1 | 0 |
| `NETW` | 27 | LanePointLeaked | i64 | 1 | 0 |
| `NETW` | 28 | LanePointFree | u64 | LanePointFreeCount | 0 |
| `NETW` | 29 | LanePointLadder | u64 | ceil(Tail / 64) | 0 |
| `NETW` | 30 | SegmentPointFreeCount | i32 | 1 | 0 |
| `NETW` | 31 | SegmentPointLeaked | i64 | 1 | 0 |
| `NETW` | 32 | SegmentPointFree | u64 | SegmentPointFreeCount | 0 |
| `NETW` | 33 | SegmentPointLadder | u64 | ceil(Tail / 64) | 0 |
| `NETW` | 34 | ArmFreeCount | i32 | 1 | 0 |
| `NETW` | 35 | ArmLeaked | i64 | 1 | 0 |
| `NETW` | 36 | ArmFree | u64 | ArmFreeCount | 0 |
| `NETW` | 37 | ArmLadder | u64 | ceil(Tail / 64) | 0 |
| `NETW` | 38 | LaneNextFreeCount | i32 | 1 | 0 |
| `NETW` | 39 | LaneNextLeaked | i64 | 1 | 0 |
| `NETW` | 40 | LaneNextFree | u64 | LaneNextFreeCount | 0 |
| `NETW` | 41 | LaneNextLadder | u64 | ceil(Tail / 64) | 0 |
| `NETW` | 42 | ZoneLaneFreeCount | i32 | 1 | 0 |
| `NETW` | 43 | ZoneLaneLeaked | i64 | 1 | 0 |
| `NETW` | 44 | ZoneLaneFree | u64 | ZoneLaneFreeCount | 0 |
| `NETW` | 45 | ZoneLaneLadder | u64 | ceil(Tail / 64) | 0 |
| `NETW` | 46 | BusRouteStopFreeCount | i32 | 1 | 0 |
| `NETW` | 47 | BusRouteStopLeaked | i64 | 1 | 0 |
| `NETW` | 48 | BusRouteStopFree | u64 | BusRouteStopFreeCount | 0 |
| `NETW` | 49 | BusRouteStopLadder | u64 | ceil(Tail / 64) | 0 |
| `NETW` | 50 | ToolOpCount | i32 | 1 | 0 |
| `NETW` | 51 | ToolOpNextId | i32 | 1 | 1 |
| `NETW` | 52 | ToolOpId | i32 | ToolOpCount | 0 |
| `NETW` | 53 | ToolOpKind | u8 | ToolOpCount | 0 |
| `NETW` | 54 | ToolOpTargetIndex | i32 | ToolOpCount | 0 |
| `NETW` | 55 | ToolOpTargetGen | i32 | ToolOpCount | 0 |
| `NETW` | 56 | ToolOpFlags | u8 | ToolOpCount | 0 |
| `NETW` | 57 | ToolOpStartTick | i64 | ToolOpCount | 0 |
| `NETW` | 58 | ToolOpLastProgressTick | i64 | ToolOpCount | 0 |
| `NETW` | 59 | ToolOpLastStallTick | i64 | ToolOpCount | 0 |
| `NETW` | 60 | ToolOpBestRemaining | i32 | ToolOpCount | 0 |
| `NNOD` | 1 | PositionX | f32 | NodeCount | 0 |
| `NNOD` | 2 | PositionZ | f32 | NodeCount | 0 |
| `NNOD` | 3 | Elevation | f32 | NodeCount | 0 |
| `NNOD` | 4 | Radius | f32 | NodeCount | 0 |
| `NNOD` | 5 | Control | i32 | NodeCount | 0 |
| `NNOD` | 6 | FirstArm | i32 | NodeCount | -1 |
| `NNOD` | 7 | ArmCount | i32 | NodeCount | 0 |
| `NNOD` | 8 | FirstConnector | i32 | NodeCount | 0 |
| `NNOD` | 9 | ConnectorCount | i32 | NodeCount | 0 |
| `NNOD` | 10 | Signal | i32 | NodeCount | -1 |
| `NNOD` | 11 | Zone | i32 | NodeCount | -1 |
| `NNOD` | 12 | Generation | i32 | NodeCount | 0 |
| `NNOD` | 13 | Flags | u32 | NodeCount | 0 |
| `NNOD` | 14 | TurnBan | u64 | NodeCount | 0 |
| `NSEG` | 1 | NodeA | i32 | SegmentCount | 0 |
| `NSEG` | 2 | NodeB | i32 | SegmentCount | 0 |
| `NSEG` | 3 | Length | f32 | SegmentCount | 0 |
| `NSEG` | 4 | SpeedLimit | f32 | SegmentCount | 0 |
| `NSEG` | 5 | Kind | u8 | SegmentCount | 0 |
| `NSEG` | 6 | LanesAB | u8 | SegmentCount | 0 |
| `NSEG` | 7 | LanesBA | u8 | SegmentCount | 0 |
| `NSEG` | 8 | FirstLaneAB | i32 | SegmentCount | -1 |
| `NSEG` | 9 | FirstLaneBA | i32 | SegmentCount | -1 |
| `NSEG` | 10 | FirstPoint | i32 | SegmentCount | 0 |
| `NSEG` | 11 | PointCount | i32 | SegmentCount | 0 |
| `NSEG` | 12 | Generation | i32 | SegmentCount | 0 |
| `NSEG` | 13 | Flags | u8 | SegmentCount | 0 |
| `NSEG` | 14 | Cx | f32 | SegmentCount | 0 |
| `NSEG` | 15 | Cz | f32 | SegmentCount | 0 |
| `NSEG` | 16 | Profile | i32 | SegmentCount | -1 |
| `NSEG` | 17 | Deny | u8 | SegmentCount | 0 |
| `NLAN` | 1 | Kind | u8 | LaneCount | 0 |
| `NLAN` | 2 | LaneNo | u8 | LaneCount | 0 |
| `NLAN` | 3 | Direction | u8 | LaneCount | 0 |
| `NLAN` | 4 | Flags | u8 | LaneCount | 0 |
| `NLAN` | 5 | Segment | i32 | LaneCount | -1 |
| `NLAN` | 6 | Node | i32 | LaneCount | -1 |
| `NLAN` | 7 | FromLane | i32 | LaneCount | -1 |
| `NLAN` | 8 | ToLane | i32 | LaneCount | -1 |
| `NLAN` | 9 | Length | f32 | LaneCount | 0 |
| `NLAN` | 10 | SpeedLimit | f32 | LaneCount | 0 |
| `NLAN` | 11 | FirstPoint | i32 | LaneCount | 0 |
| `NLAN` | 12 | PointCount | i32 | LaneCount | 0 |
| `NLAN` | 13 | Left | i32 | LaneCount | -1 |
| `NLAN` | 14 | Right | i32 | LaneCount | -1 |
| `NLAN` | 15 | FirstNext | i32 | LaneCount | 0 |
| `NLAN` | 16 | NextCount | i32 | LaneCount | 0 |
| `NLAN` | 17 | ConflictGroup | i32 | LaneCount | -1 |
| `NLAN` | 18 | Generation | i32 | LaneCount | 0 |
| `NLAN` | 19 | Width | f32 | LaneCount | 0 |
| `NLAN` | 20 | Deny | u8 | LaneCount | 0 |
| `NARM` | 1 | NodeArms | i32 | NodeArmCount | 0 |
| `NARM` | 2 | ArmFlags | u8 | NodeArmCount | 0 |
| `NARM` | 3 | ArmDirX | f32 | NodeArmCount | 0 |
| `NARM` | 4 | ArmDirZ | f32 | NodeArmCount | 0 |
| `NLNX` | 1 | LaneNext | i32 | LaneNextCount | 0 |
| `NPTS` | 1 | SegmentPointsX | f32 | SegmentPointCount | 0 |
| `NPTS` | 2 | SegmentPointsZ | f32 | SegmentPointCount | 0 |
| `NPTS` | 3 | LanePointsX | f32 | LanePointCount | 0 |
| `NPTS` | 4 | LanePointsZ | f32 | LanePointCount | 0 |
| `NPTS` | 5 | LanePointS | f32 | LanePointCount | 0 |
| `NZON` | 1 | CenterX | f32 | ZoneCount | 0 |
| `NZON` | 2 | CenterZ | f32 | ZoneCount | 0 |
| `NZON` | 3 | HalfSize | f32 | ZoneCount | 0 |
| `NZON` | 4 | Weight | f32 | ZoneCount | 0 |
| `NZON` | 5 | FirstSpawnLane | i32 | ZoneCount | 0 |
| `NZON` | 6 | SpawnLaneCount | i32 | ZoneCount | 0 |
| `NZON` | 7 | IsExternal | u8 | ZoneCount | 0 |
| `NZON` | 8 | ZoneLanes | i32 | ZoneLaneCount | 0 |
| `NBUS` | 1 | StopLane | i32 | BusStopCount | 0 |
| `NBUS` | 2 | StopS | f32 | BusStopCount | 0 |
| `NBUS` | 3 | StopKind | i32 | BusStopCount | 0 |
| `NBUS` | 4 | StopRoute | i32 | BusStopCount | -1 |
| `NBUS` | 5 | RouteFirstStop | i32 | BusRouteCount | 0 |
| `NBUS` | 6 | RouteStopCount | i32 | BusRouteCount | 0 |
| `NBUS` | 7 | RouteHeadwayTicks | i32 | BusRouteCount | 0 |
| `NBUS` | 8 | RouteVehicleType | i32 | BusRouteCount | 0 |
| `NBUS` | 9 | RouteGeneration | i32 | BusRouteCount | 0 |
| `NBUS` | 10 | BusRouteStops | i32 | BusRouteStopCount | 0 |
| `NBUS` | 11 | StopGeneration | i32 | BusStopCount | 0 |
| `CONN` | 1 | Count | i32 | 1 | 0 |
| `CONN` | 2 | ConflictsUsed | i32 | 1 | 0 |
| `CONN` | 3 | Lane | i32 | Count | 0 |
| `CONN` | 4 | Node | i32 | Count | 0 |
| `CONN` | 5 | InLane | i32 | Count | 0 |
| `CONN` | 6 | OutLane | i32 | Count | 0 |
| `CONN` | 7 | LocalBit | u8 | Count | 0 |
| `CONN` | 8 | Maneuver | u8 | Count | 0 |
| `CONN` | 9 | FromArm | u8 | Count | 0 |
| `CONN` | 10 | Flags | u8 | Count | 0 |
| `CONN` | 11 | FirstConflict | i32 | Count | 0 |
| `CONN` | 12 | ConflictCount | i32 | Count | 0 |
| `CONN` | 13 | ConflictMask | u64 | Count | 0 |
| `CONN` | 14 | OtherLocal | u8 | ConflictsUsed | 0 |
| `CONN` | 15 | ConflictFlags | u8 | ConflictsUsed | 0 |
| `CONN` | 16 | SMineCm | u16 | ConflictsUsed | 0 |
| `CONN` | 17 | SOtherCm | u16 | ConflictsUsed | 0 |
| `CONN` | 18 | RowFreeCount | i32 | 1 | 0 |
| `CONN` | 19 | RowLeaked | i64 | 1 | 0 |
| `CONN` | 20 | RowFree | u64 | RowFreeCount | 0 |
| `CONN` | 21 | RowLadder | u64 | ceil(Tail / 64) | 0 |
| `CONN` | 22 | ConflictFreeCount | i32 | 1 | 0 |
| `CONN` | 23 | ConflictLeaked | i64 | 1 | 0 |
| `CONN` | 24 | ConflictFree | u64 | ConflictFreeCount | 0 |
| `CONN` | 25 | ConflictLadder | u64 | ceil(Tail / 64) | 0 |
| `SIGS` | 1 | Count | i32 | 1 | 0 |
| `SIGS` | 2 | SignalCount | i32 | 1 | 0 |
| `SIGS` | 3 | Node | i32 | Count | -1 |
| `SIGS` | 4 | Mode | i32 | Count | 0 |
| `SIGS` | 5 | PhaseCount | u8 | Count | 0 |
| `SIGS` | 6 | YellowTicks | u16 | Count | 0 |
| `SIGS` | 7 | AllRedTicks | u16 | Count | 0 |
| `SIGS` | 8 | OffsetTicks | u16 | Count | 0 |
| `SIGS` | 9 | Phase | u8 | Count | 0 |
| `SIGS` | 10 | Interval | u8 | Count | 0 |
| `SIGS` | 11 | TicksLeft | u16 | Count | 0 |
| `SIGS` | 12 | GreenTicks | u16 | Count * 8 | 0 |
| `SIGS` | 13 | PhaseMask | u64 | Count * 8 | 0 |
| `SIGS` | 14 | StateNode | i32 | SignalCount | 0 |
| `SIGS` | 15 | StatePhase | u8 | SignalCount | 0 |
| `SIGS` | 16 | StateInterval | u8 | SignalCount | 0 |
| `SIGS` | 17 | StateTicksToChange | u16 | SignalCount | 0 |
| `SIGS` | 18 | MinGreenTicks | u16 | Count | 0 |
| `SIGS` | 19 | GapTicks | u16 | Count | 0 |
| `SIGS` | 20 | ArrowTriggerTicks | u16 | Count | 0 |
| `SIGS` | 21 | ArrowMinTicks | u16 | Count | 0 |
| `SIGS` | 22 | ArrowMaxTicks | u16 | Count | 0 |
| `SIGS` | 23 | ArrowGapTicks | u16 | Count | 0 |
| `SIGS` | 24 | CycleTicks | u16 | Count | 0 |
| `SIGS` | 25 | PlanFlags | u8 | Count | 0 |
| `SIGS` | 26 | Machine | u8 | Count | 0 |
| `SIGS` | 27 | Sub | u8 | Count | 0 |
| `SIGS` | 28 | GreenMask | u64 | Count | 0 |
| `SIGS` | 29 | NextMask | u64 | Count | 0 |
| `SIGS` | 30 | NextAxis | u8 | Count | 0 |
| `SIGS` | 31 | NextSub | u8 | Count | 0 |
| `SIGS` | 32 | NextArrowArms | u16 | Count | 0 |
| `SIGS` | 33 | ArrowArms | u16 | Count | 0 |
| `SIGS` | 34 | GreenElapsed | u16 | Count | 0 |
| `SIGS` | 35 | GapLeft | u16 | Count | 0 |
| `SIGS` | 36 | ArmLeftYield | u16 | Count * 16 | 0 |
| `SIGS` | 37 | ArmLeftHead | i32 | Count * 16 | -1 |
| `SIGS` | 38 | SpillTicks | u16 | Count | 0 |
| `SIGS` | 39 | XWalkLeft | u16 | Count * 8 | 0 |
| `SIGS` | 40 | XClearLeft | u16 | Count * 8 | 0 |
| `SIGS` | 41 | XServed | u8 | Count | 0 |
| `NDYN` | 1 | NodeTicket | u32 | NodeCount | 0 |
| `NDYN` | 2 | NodeThroughput | u32 | NodeCount | 0 |
| `LDYN` | 1 | LaneHead | i32 | LaneCount | -1 |
| `LDYN` | 2 | LaneTail | i32 | LaneCount | -1 |
| `LDYN` | 3 | LaneCount | i32 | LaneCount | 0 |
| `LDYN` | 4 | OutReserved | f32 | LaneCount | 0 |
| `LDYN` | 5 | LaneSignals | u8 | LaneCount | 0 |
| `SDYN` | 1 | Density | f32 | SegmentCount | 0 |
| `SDYN` | 2 | SpeedRatio | f32 | SegmentCount | 1 |
| `SDYN` | 3 | LoadCount | u16 | SegmentCount | 0 |
| `BDYN` | 1 | Waiting | u16 | BusStopCount | 0 |
| `BDYN` | 2 | LastArrivalTick | i64 | BusStopCount | -1 |
| `BDYN` | 3 | ActiveBuses | i32 | BusRouteCount | 0 |
| `BDYN` | 4 | LastDispatchTick | i64 | BusRouteCount | -1 |
| `BDYN` | 5 | StatsActiveBuses | i32 | BusRouteCount | 0 |
| `BDYN` | 6 | StatsArrivals | i64 | BusRouteCount | 0 |
| `BDYN` | 7 | StatsHeadwayDeviationTicksTotal | i64 | BusRouteCount | 0 |
| `BDYN` | 8 | StatsLastDispatchTick | i64 | BusRouteCount | -1 |
| `BDYN` | 9 | LastDepartTick | i64 | BusStopCount | -1 |
| `BDYN` | 10 | BayUsed | u8 | BusStopCount | 0 |
| `BDYN` | 11 | BayFlags | u8 | BusStopCount | 0 |
| `BDYN` | 12 | BayExitFrom | i64 | BusStopCount | -1 |
| `VEHC` | 1 | HighWater | i32 | 1 | 0 |
| `VEHC` | 2 | FreeCount | i32 | 1 | 0 |
| `VEHC` | 3 | FreeList | i32 | FreeCount | 0 |
| `VEHC` | 4 | Generation | i32 | HighWater | 0 |
| `VEHC` | 5 | Lane | i32 | HighWater | -1 |
| `VEHC` | 6 | S | f32 | HighWater | 0 |
| `VEHC` | 7 | Speed | f32 | HighWater | 0 |
| `VEHC` | 8 | Accel | f32 | HighWater | 0 |
| `VEHC` | 9 | Length | f32 | HighWater | 0 |
| `VEHC` | 10 | Type | u8 | HighWater | 0 |
| `VEHC` | 11 | DesiredSpeed | f32 | HighWater | 0 |
| `VEHC` | 12 | Ahead | i32 | HighWater | -1 |
| `VEHC` | 13 | Behind | i32 | HighWater | -1 |
| `VEHC` | 14 | GhostLane | i32 | HighWater | -1 |
| `VEHC` | 15 | GhostAhead | i32 | HighWater | -1 |
| `VEHC` | 16 | GhostBehind | i32 | HighWater | -1 |
| `VEHC` | 17 | LaneChangeDir | i8 | HighWater | 0 |
| `VEHC` | 18 | LaneChangeTicks | u16 | HighWater | 0 |
| `VEHC` | 19 | NextMobilTick | u16 | HighWater | 0 |
| `VEHC` | 20 | Route | i32 | HighWater | -1 |
| `VEHC` | 21 | RouteGen | i32 | HighWater | 0 |
| `VEHC` | 22 | RouteCursor | i32 | HighWater | 0 |
| `VEHC` | 23 | DestZone | i32 | HighWater | -1 |
| `VEHC` | 24 | DestLane | i32 | HighWater | -1 |
| `VEHC` | 25 | DestS | f32 | HighWater | 0 |
| `VEHC` | 26 | NextConn | i32 | HighWater | -1 |
| `VEHC` | 27 | GateState | u8 | HighWater | 0 |
| `VEHC` | 28 | GrantConnector | i32 | HighWater | -1 |
| `VEHC` | 29 | Ticket | u32 | HighWater | 0 |
| `VEHC` | 30 | WaitTicks | u16 | HighWater | 0 |
| `VEHC` | 31 | PointHint | i32 | HighWater | 0 |
| `VEHC` | 32 | Lateral | f32 | HighWater | 0 |
| `VEHC` | 33 | SpawnTick | i64 | HighWater | 0 |
| `VEHC` | 34 | StillTicks | u16 | HighWater | 0 |
| `VEHC` | 35 | Flags | u8 | HighWater | 0 |
| `VEHC` | 36 | BusRoute | i32 | HighWater | -1 |
| `VEHC` | 37 | BusNextStop | i32 | HighWater | 0 |
| `VEHC` | 38 | DwellTicks | u16 | HighWater | 0 |
| `VEHC` | 39 | Passengers | u16 | HighWater | 0 |
| `VEHC` | 40 | DenyReason | u8 | HighWater | 0 |
| `VEHC` | 41 | Resident | i32 | HighWater | -1 |
| `VEHC` | 42 | ParkFac | i32 | HighWater | -1 |
| `VEHC` | 43 | ParkFacGen | i32 | HighWater | 0 |
| `VEHC` | 44 | ParkSearchTick | i64 | HighWater | -1 |
| `TRIP` | 1 | Count | i32 | 1 | 0 |
| `TRIP` | 2 | FromZone | i32 | Count | 0 |
| `TRIP` | 3 | ToZone | i32 | Count | 0 |
| `TRIP` | 4 | FromLane | i32 | Count | 0 |
| `TRIP` | 5 | FromS | f32 | Count | 0 |
| `TRIP` | 6 | Type | u8 | Count | 0 |
| `TRIP` | 7 | DestLane | i32 | Count | 0 |
| `TRIP` | 8 | DestS | f32 | Count | 0 |
| `TRIP` | 9 | Route | i32 | Count | 0 |
| `TRIP` | 10 | RouteGen | i32 | Count | 0 |
| `TRIP` | 11 | ResidentRef | i32 | Count | 0 |
| `TRIP` | 12 | ParkRef | i32 | Count | 0 |
| `TRIP` | 13 | ParkGen | i32 | Count | 0 |
| `QUES` | 1 | ReroutingCount | i32 | 1 | 0 |
| `QUES` | 2 | RefillCount | i32 | 1 | 0 |
| `QUES` | 3 | CommandCount | i32 | 1 | 0 |
| `QUES` | 4 | ReroutingIndex | i32 | ReroutingCount | 0 |
| `QUES` | 5 | ReroutingGeneration | i32 | ReroutingCount | 0 |
| `QUES` | 6 | ReroutingReadyTick | i64 | ReroutingCount | 0 |
| `QUES` | 7 | ReroutingKind | i32 | ReroutingCount | 0 |
| `QUES` | 8 | RefillIndex | i32 | RefillCount | 0 |
| `QUES` | 9 | RefillGeneration | i32 | RefillCount | 0 |
| `QUES` | 10 | Commands | raw[64] | CommandCount | 0 |
| `ROUT` | 1 | HighWater | i32 | 1 | 0 |
| `ROUT` | 2 | FreeCount | i32 | 1 | 0 |
| `ROUT` | 3 | FreeList | i32 | FreeCount | 0 |
| `ROUT` | 4 | Generation | i32 | HighWater | 0 |
| `ROUT` | 5 | PlanUsed | i32 | 1 | 0 |
| `ROUT` | 6 | Compactions | i32 | 1 | 0 |
| `ROUT` | 7 | WindowSteps | i32 | 1 | 0 |
| `ROUT` | 8 | WinCount | u8 | HighWater | 0 |
| `ROUT` | 9 | PlanFirst | i32 | HighWater | 0 |
| `ROUT` | 10 | PlanCount | i32 | HighWater | 0 |
| `ROUT` | 11 | PlanCap | i32 | HighWater | 0 |
| `ROUT` | 12 | PlanCost | f32 | HighWater | 0 |
| `ROUT` | 13 | PlanTick | i64 | HighWater | 0 |
| `ROUT` | 14 | WinLane | i32 | Σ WinCount живых | 0 |
| `ROUT` | 15 | WinNode | i32 | Σ WinCount живых | -1 |
| `ROUT` | 16 | PlanArena | i32 | PlanUsed | 0 |
| `RGRP` | 1 | BuiltVersion | i32 | 1 | -1 |
| `RGRP` | 2 | WeightsVersion | i32 | 1 | 0 |
| `RGRP` | 3 | SegCost | f32 | DirSegCount | 0 |
| `RGRP` | 4 | ArcCost | f32 | ArcCount | 0 |
| `OVLY` (routing.levels > 0) | 1 | Levels | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 2 | OriginX | f32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 3 | OriginZ | f32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 4 | Nx1 | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 5 | Nz1 | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 6 | EntriesLen | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 7 | ExitsLen | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 8 | MatrixLen | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 9 | BuiltNodes | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 10 | BuiltDirSegs | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 11 | WeightsVersion | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 12 | Disabled | u8 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 13 | Rebuilds | i64 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 14 | WaveActive | u8 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 15 | WaveCell | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 16 | WaveRow | i32 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 17 | CustomizeRows | i64 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 18 | CustomizeWork | i64 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 19 | FullRows | i64 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 20 | FullWork | i64 | 1 | 0 |
| `OVLY` (routing.levels > 0) | 21 | EntryCount | i32 | CellCap | 0 |
| `OVLY` (routing.levels > 0) | 22 | EntryCap | i32 | CellCap | 0 |
| `OVLY` (routing.levels > 0) | 23 | ExitCount | i32 | CellCap | 0 |
| `OVLY` (routing.levels > 0) | 24 | ExitCap | i32 | CellCap | 0 |
| `OVLY` (routing.levels > 0) | 25 | Dirty | u8 | CellCap | 0 |
| `OVLY` (routing.levels > 0) | 26 | Entries | i32 | Σ EntryCap | 0 |
| `OVLY` (routing.levels > 0) | 27 | Exits | i32 | Σ ExitCap | 0 |
| `OVLY` (routing.levels > 0) | 28 | Matrix | f32 | Σ EntryCap · ExitCap | +inf |
| `OVLY` (routing.levels > 0) | 29 | CustomizedSegCost | f32 | BuiltDirSegs | 0 |
| `LODS` (lod.mode != off) | 1 | OriginX | f32 | 1 | 0 |
| `LODS` (lod.mode != off) | 2 | OriginZ | f32 | 1 | 0 |
| `LODS` (lod.mode != off) | 3 | CellsX | i32 | 1 | 0 |
| `LODS` (lod.mode != off) | 4 | CellsZ | i32 | 1 | 0 |
| `LODS` (lod.mode != off) | 5 | LodEvalRequested | u8 | 1 | 0 |
| `LODS` (lod.mode != off) | 6 | LastLodEvalTick | i64 | 1 | 0 |
| `LODS` (lod.mode != off) | 7 | CellLevel | u8 | Cells | 0 |
| `LODS` (lod.mode != off) | 8 | CellHoldUntil | i64 | Cells | 0 |
| `LODS` (lod.mode != off) | 9 | CellCongStreak | u8 | Cells | 0 |
| `LODS` (lod.mode != off) | 10 | FocusKind | u8 | 8 | 0 |
| `LODS` (lod.mode != off) | 11 | FocusCx0 | i32 | 8 | 0 |
| `LODS` (lod.mode != off) | 12 | FocusCz0 | i32 | 8 | 0 |
| `LODS` (lod.mode != off) | 13 | FocusCx1 | i32 | 8 | 0 |
| `LODS` (lod.mode != off) | 14 | FocusCz1 | i32 | 8 | 0 |
| `LODS` (lod.mode != off) | 15 | FocusCenterCx | i32 | 8 | 0 |
| `LODS` (lod.mode != off) | 16 | FocusCenterCz | i32 | 8 | 0 |
| `LODS` (lod.mode != off) | 17 | VehicleLod | u8 | HighWater | 0 |
| `LODS` (lod.mode != off) | 18 | MesoAllow | f32 | HighWater | 0 |
| `LODS` (lod.mode != off) | 19 | MesoNext | f32 | HighWater | -1 |
| `QMSO` (lod.meso_model = queue) | 1 | PendingDistMm | i64 | 1 | 0 |
| `QMSO` (lod.meso_model = queue) | 2 | Events | i64 | 1 | 0 |
| `QMSO` (lod.meso_model = queue) | 3 | Wakes | i64 | 1 | 0 |
| `QMSO` (lod.meso_model = queue) | 4 | QEnter | i32 | HighWater | 0 |
| `QMSO` (lod.meso_model = queue) | 5 | QReady | i32 | HighWater | 0 |
| `QMSO` (lod.meso_model = queue) | 6 | WheelTick | i32 | HighWater | -1 |
| `QMSO` (lod.meso_model = queue) | 7 | LaneLastExit | i32 | LaneCount | 0 |
| `SMET` | 1 | SignalWaitTicks | i64 | 4 | 0 |
| `SMET` | 2 | SignalGrants | i64 | 4 | 0 |
| `SMET` | 3 | SignalHist | i64 | 4 * 121 | 0 |
| `SMET` | 4 | ConnWaitSum | i64 | Count | 0 |
| `SMET` | 5 | ConnGrantCount | i32 | Count | 0 |
| `SMET` | 6 | ConnDelay | f32 | Count | 0 |
| `PEDS` (peds.enabled) | 1 | HighWater | i32 | 1 | 0 |
| `PEDS` (peds.enabled) | 2 | FreeCount | i32 | 1 | 0 |
| `PEDS` (peds.enabled) | 3 | FreeList | i32 | FreeCount | 0 |
| `PEDS` (peds.enabled) | 4 | Generation | i32 | HighWater | 0 |
| `PEDS` (peds.enabled) | 5 | State | u8 | HighWater | 0 |
| `PEDS` (peds.enabled) | 6 | Leg | u8 | HighWater | 0 |
| `PEDS` (peds.enabled) | 7 | Resident | i32 | HighWater | -1 |
| `PEDS` (peds.enabled) | 8 | Edge | i32 | HighWater | -1 |
| `PEDS` (peds.enabled) | 9 | T0 | i64 | HighWater | 0 |
| `PEDS` (peds.enabled) | 10 | S0Mm | i32 | HighWater | 0 |
| `PEDS` (peds.enabled) | 11 | VMmT | i32 | HighWater | 0 |
| `PEDS` (peds.enabled) | 12 | EvTick | i64 | HighWater | -1 |
| `PEDS` (peds.enabled) | 13 | PathCursor | i32 | HighWater | 0 |
| `PEDS` (peds.enabled) | 14 | PathCount | i32 | HighWater | 0 |
| `PEDS` (peds.enabled) | 15 | GoalEdge | i32 | HighWater | -1 |
| `PEDS` (peds.enabled) | 16 | GoalSMm | i32 | HighWater | 0 |
| `PEDS` (peds.enabled) | 17 | Next | i32 | HighWater | -1 |
| `PEDS` (peds.enabled) | 18 | Bus | i32 | HighWater | -1 |
| `PEDS` (peds.enabled) | 19 | AlightStop | i32 | HighWater | -1 |
| `PEDS` (peds.enabled) | 20 | TripTick | i64 | HighWater | 0 |
| `PPTH` (peds.enabled) | 1 | Steps | i32 | 1 | 0 |
| `PPTH` (peds.enabled) | 2 | Path | i32 | HighWater * Steps | 0 |
| `RESI` (peds.enabled) | 1 | Count | i32 | 1 | 0 |
| `RESI` (peds.enabled) | 2 | ReadyHead | i32 | 1 | -1 |
| `RESI` (peds.enabled) | 3 | ReadyTail | i32 | 1 | -1 |
| `RESI` (peds.enabled) | 4 | HomeEdge | i32 | Count | -1 |
| `RESI` (peds.enabled) | 5 | HomeSMm | i32 | Count | 0 |
| `RESI` (peds.enabled) | 6 | WorkEdge | i32 | Count | -1 |
| `RESI` (peds.enabled) | 7 | WorkSMm | i32 | Count | 0 |
| `RESI` (peds.enabled) | 8 | HomeZone | i32 | Count | -1 |
| `RESI` (peds.enabled) | 9 | WorkZone | i32 | Count | -1 |
| `RESI` (peds.enabled) | 10 | ModeOut | u8 | Count | 0 |
| `RESI` (peds.enabled) | 11 | ModeBack | u8 | Count | 0 |
| `RESI` (peds.enabled) | 12 | RouteOut | i32 | Count | -1 |
| `RESI` (peds.enabled) | 13 | StopAOut | i32 | Count | -1 |
| `RESI` (peds.enabled) | 14 | StopBOut | i32 | Count | -1 |
| `RESI` (peds.enabled) | 15 | RouteBack | i32 | Count | -1 |
| `RESI` (peds.enabled) | 16 | StopABack | i32 | Count | -1 |
| `RESI` (peds.enabled) | 17 | StopBBack | i32 | Count | -1 |
| `RESI` (peds.enabled) | 18 | Where | u8 | Count | 0 |
| `RESI` (peds.enabled) | 19 | ReadyTick | i64 | Count | 0 |
| `RESI` (peds.enabled) | 20 | Ped | i32 | Count | -1 |
| `RESI` (peds.enabled) | 21 | ReadyNext | i32 | Count | -2 |
| `XING` (peds.enabled) | 1 | XState | u8 | XCount | 0 |
| `XING` (peds.enabled) | 2 | QHead | i32 | XCount | -1 |
| `XING` (peds.enabled) | 3 | QTail | i32 | XCount | -1 |
| `XING` (peds.enabled) | 4 | QCount | i32 | XCount | 0 |
| `XING` (peds.enabled) | 5 | OnCross | i32 | XCount | 0 |
| `XING` (peds.enabled) | 6 | BusyUntil | i64 | XCount | 0 |
| `XING` (peds.enabled) | 7 | ClosedSince | i64 | XCount | 0 |
| `XING` (peds.enabled) | 8 | CarWindowUntil | i64 | XCount | 0 |
| `XING` (peds.enabled) | 9 | CarsWaiting | u8 | XCount | 0 |
| `XING` (peds.enabled) | 10 | WaitSum | i64 | XCount | 0 |
| `XING` (peds.enabled) | 11 | Releases | i32 | XCount | 0 |
| `XING` (peds.enabled) | 12 | MeasuredWait | i32 | XCount | 0 |
| `XING` (peds.enabled) | 13 | StopQHead | i32 | BusStopCount | -1 |
| `XING` (peds.enabled) | 14 | StopQTail | i32 | BusStopCount | -1 |
| `XING` (peds.enabled) | 15 | StopQCount | i32 | BusStopCount | 0 |
| `XING` (peds.enabled) | 16 | PaxHead | i32 | Vehicles.HighWater | -1 |
| `XING` (peds.enabled) | 17 | CrossWaitHist | i64 | 256 | 0 |
| `XING` (peds.enabled) | 18 | StopWaitHist | i64 | 256 | 0 |
| `XING` (peds.enabled) | 19 | BusyUntilIn | i64 | XCount | 0 |
| `XING` (peds.enabled) | 20 | BusyUntilOut | i64 | XCount | 0 |
| `PQUE` (peds.enabled) | 1 | Count | i32 | 1 | 0 |
| `PQUE` (peds.enabled) | 2 | Index | i32 | Count | 0 |
| `PQUE` (peds.enabled) | 3 | Generation | i32 | Count | 0 |
| `LOTS` (demand.model = residents) | 1 | Count | i32 | 1 | 0 |
| `LOTS` (demand.model = residents) | 2 | Seg | i32 | Count | 0 |
| `LOTS` (demand.model = residents) | 3 | SegGen | i32 | Count | 0 |
| `LOTS` (demand.model = residents) | 4 | Side | u8 | Count | 0 |
| `LOTS` (demand.model = residents) | 5 | S0Mm | i32 | Count | 0 |
| `LOTS` (demand.model = residents) | 6 | S1Mm | i32 | Count | 0 |
| `LOTS` (demand.model = residents) | 7 | Zone | u8 | Count | 0 |
| `LOTS` (demand.model = residents) | 8 | Density | u8 | Count | 0 |
| `LOTS` (demand.model = residents) | 9 | Building | i32 | Count | -1 |
| `LOTS` (demand.model = residents) | 10 | Flags | u8 | Count | 0 |
| `LOTS` (demand.model = residents) | 11 | CellX | i32 | Count | 0 |
| `LOTS` (demand.model = residents) | 12 | CellZ | i32 | Count | 0 |
| `LOTS` (demand.model = residents) | 13 | CellsF | u8 | Count | 0 |
| `LOTS` (demand.model = residents) | 14 | CellsD | u8 | Count | 0 |
| `LOTS` (demand.model = residents) | 15 | Orient | u8 | Count | 255 |
| `BLDG` (demand.model = residents) | 1 | HighWater | i32 | 1 | 0 |
| `BLDG` (demand.model = residents) | 2 | FreeCount | i32 | 1 | 0 |
| `BLDG` (demand.model = residents) | 3 | FreeList | i32 | FreeCount | 0 |
| `BLDG` (demand.model = residents) | 4 | Generation | i32 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 5 | Lot | i32 | HighWater | -1 |
| `BLDG` (demand.model = residents) | 6 | Type | i32 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 7 | State | u8 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 8 | StateTick | i64 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 9 | Occupants | i32 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 10 | Jobs | i32 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 11 | HeadRes | i32 | HighWater | -1 |
| `BLDG` (demand.model = residents) | 12 | HeadWork | i32 | HighWater | -1 |
| `BLDG` (demand.model = residents) | 13 | Account | i64 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 14 | RevenueEwma | i64 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 15 | ProfitEwma | i64 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 16 | VisitsDay | i32 | 2 * HighWater | 0 |
| `BLDG` (demand.model = residents) | 17 | RevenueDay | i64 | 2 * HighWater | 0 |
| `BLDG` (demand.model = residents) | 18 | CostDay | i64 | 2 * HighWater | 0 |
| `BLDG` (demand.model = residents) | 19 | LossDays | i16 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 20 | VacantDays | i16 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 21 | PayrollEwma | i64 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 22 | Cond | u16 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 23 | Ceil | u16 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 24 | BornDay | i32 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 25 | LifeFlags | u8 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 26 | Signif | i32 | HighWater | 0 |
| `BLDG` (demand.model = residents) | 27 | ArrivalsDay | i32 | 2 * HighWater | 0 |
| `RESD` (demand.model = residents) | 1 | HighWater | i32 | 1 | 0 |
| `RESD` (demand.model = residents) | 2 | FreeCount | i32 | 1 | 0 |
| `RESD` (demand.model = residents) | 3 | FreeList | i32 | FreeCount | 0 |
| `RESD` (demand.model = residents) | 4 | Generation | i32 | HighWater | 0 |
| `RESD` (demand.model = residents) | 5 | Home | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 6 | Work | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 7 | NextHome | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 8 | NextWork | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 9 | Flags | u8 | HighWater | 0 |
| `RESD` (demand.model = residents) | 10 | State | u8 | HighWater | 0 |
| `RESD` (demand.model = residents) | 11 | StateTick | i64 | HighWater | 0 |
| `RESD` (demand.model = residents) | 12 | EvTick | i64 | HighWater | -1 |
| `RESD` (demand.model = residents) | 13 | EvKind | u8 | HighWater | 0 |
| `RESD` (demand.model = residents) | 14 | PlanCount | u8 | HighWater | 0 |
| `RESD` (demand.model = residents) | 15 | PlanCursor | u8 | HighWater | 0 |
| `RESD` (demand.model = residents) | 16 | PlanKind | u8 | HighWater * 3 | 0 |
| `RESD` (demand.model = residents) | 17 | PlanMode | u8 | HighWater * 3 | 0 |
| `RESD` (demand.model = residents) | 18 | PlanDest | i32 | HighWater * 3 | -1 |
| `RESD` (demand.model = residents) | 19 | PlanSlot | u16 | HighWater * 3 | 0 |
| `RESD` (demand.model = residents) | 20 | LegMode | u8 | HighWater | 0 |
| `RESD` (demand.model = residents) | 21 | LegO | u16 | HighWater | 0 |
| `RESD` (demand.model = residents) | 22 | LegD | u16 | HighWater | 0 |
| `RESD` (demand.model = residents) | 23 | LegReq | i64 | HighWater | 0 |
| `RESD` (demand.model = residents) | 24 | LegWalk | i32 | HighWater | 0 |
| `RESD` (demand.model = residents) | 25 | LegWait | i32 | HighWater | 0 |
| `RESD` (demand.model = residents) | 26 | LegRide | i32 | HighWater | 0 |
| `RESD` (demand.model = residents) | 27 | Agent | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 28 | AgentGen | i32 | HighWater | 0 |
| `RESD` (demand.model = residents) | 29 | Account | i64 | HighWater | 0 |
| `RESD` (demand.model = residents) | 30 | IncomeEwma | i64 | HighWater | 0 |
| `RESD` (demand.model = residents) | 31 | SeekDays | i16 | HighWater | 0 |
| `RESD` (demand.model = residents) | 32 | EvictDays | i16 | HighWater | 0 |
| `RESD` (demand.model = residents) | 33 | Seq | i32 | HighWater | 0 |
| `RESD` (demand.model = residents) | 34 | NextSeq | i32 | 1 | 0 |
| `RESD` (demand.model = residents) | 35 | SeekCount | i32 | 1 | 0 |
| `RESD` (demand.model = residents) | 36 | SeekIndex | i32 | SeekCount | 0 |
| `RESD` (demand.model = residents) | 37 | SeekGeneration | i32 | SeekCount | 0 |
| `RESD` (demand.model = residents) | 38 | LegDest | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 39 | LegStopA | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 40 | LegStopB | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 41 | HomeFac | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 42 | HomeFacGen | i32 | HighWater | 0 |
| `RESD` (demand.model = residents) | 43 | CarFac | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 44 | CarFacGen | i32 | HighWater | 0 |
| `RESD` (demand.model = residents) | 45 | CarLot | i32 | HighWater | -1 |
| `RESD` (demand.model = residents) | 46 | CarWalkMm | i32 | HighWater | 0 |
| `RESD` (demand.model = residents) | 47 | LegStage | u8 | HighWater | 0 |
| `RESD` (demand.model = residents) | 48 | LegWaitFrom | i64 | HighWater | 0 |
| `RESD` (demand.model = residents) | 49 | LegParkWait | i32 | HighWater | 0 |
| `RESD` (demand.model = residents) | 50 | Ill | u8 | HighWater | 0 |
| `OUTS` (секция outside) | 1 | Jobs | i32 | CityOutsideCount | 0 |
| `OUTS` (секция outside) | 2 | Taken | i32 | CityOutsideCount | 0 |
| `OUTS` (секция outside) | 3 | ArrivalsDay | i32 | CityOutsideCount | 0 |
| `OUTS` (секция outside) | 4 | ArrivalsHour | i32 | CityOutsideCount | 0 |
| `OUTS` (секция outside) | 5 | Arrivals | i64 | 1 | 0 |
| `OUTS` (секция outside) | 6 | ArrivalTimeouts | i64 | 1 | 0 |
| `GOAL` (секция goals) | 1 | GraceHours | i32 | 1 | 0 |
| `GOAL` (секция goals) | 2 | AfterWin | u8 | 1 | 0 |
| `GOAL` (секция goals) | 3 | WinCount | u8 | 1 | 0 |
| `GOAL` (секция goals) | 4 | LoseCount | u8 | 1 | 0 |
| `GOAL` (секция goals) | 5 | CondKind | u8 | 16 | 0 |
| `GOAL` (секция goals) | 6 | CondKpi | u8 | 16 | 0 |
| `GOAL` (секция goals) | 7 | CondValue | i32 | 16 | 0 |
| `GOAL` (секция goals) | 8 | CondHoldHours | i32 | 16 | 0 |
| `GOAL` (секция goals) | 9 | HeldHours | i32 | 16 | 0 |
| `GOAL` (секция goals) | 10 | HoursElapsed | i32 | 1 | 0 |
| `GOAL` (секция goals) | 11 | Outcome | u8 | 1 | 0 |
| `GOAL` (секция goals) | 12 | OutcomeTick | i64 | 1 | -1 |
| `GOAL` (секция goals) | 13 | LoseIndex | i32 | 1 | -1 |
| `SKIM` (demand.model = residents) | 1 | Skim | i32 | NC * NC * 3 * P | 0 |
| `SKIM` (demand.model = residents) | 2 | K | i32 | NC * NC * P | 0 |
| `SKIM` (demand.model = residents) | 3 | LsRef | i32 | 1 | 0 |
| `SKIM` (demand.model = residents) | 4 | LsRefSet | u8 | 1 | 0 |
| `SKIM` (demand.model = residents) | 5 | CurPeriod | i64 | 1 | 0 |
| `SKIM` (demand.model = residents) | 6 | UpdateCursor | i32 | 1 | 0 |
| `SKIM` (demand.model = residents) | 7 | ObsKey | i32 | ObsCount | 0 |
| `SKIM` (demand.model = residents) | 8 | ObsSum | i64 | ObsCount | 0 |
| `SKIM` (demand.model = residents) | 9 | ObsCount | i32 | ObsCount | 0 |
| `SKIM` (demand.model = residents) | 10 | FrozenKey | i32 | FrozenN | 0 |
| `SKIM` (demand.model = residents) | 11 | FrozenSum | i64 | FrozenN | 0 |
| `SKIM` (demand.model = residents) | 12 | FrozenCount | i32 | FrozenN | 0 |
| `SKIM` (demand.model = residents) | 13 | LsC | i32 | NC * 3 | 0 |
| `SKIM` (demand.model = residents) | 14 | LsJobs | i32 | NC | 0 |
| `LEDG` (demand.model = residents) | 1 | External | i64 | 1 | 0 |
| `LEDG` (demand.model = residents) | 2 | City | i64 | 1 | 0 |
| `LEDG` (demand.model = residents) | 3 | FlowDay | i64 | 23 | 0 |
| `LEDG` (demand.model = residents) | 4 | FlowYesterday | i64 | 23 | 0 |
| `LEDG` (demand.model = residents) | 5 | FlowTotal | i64 | 23 | 0 |
| `LEDG` (demand.model = residents) | 6 | CursorResidents | i32 | 1 | 0 |
| `LEDG` (demand.model = residents) | 7 | CursorBuildings | i32 | 1 | 0 |
| `LEDG` (demand.model = residents) | 8 | CursorAccess | i32 | 1 | 0 |
| `LEDG` (demand.model = residents) | 9 | DemandR | i32 | 1 | 0 |
| `LEDG` (demand.model = residents) | 10 | DemandC | i32 | 1 | 0 |
| `LEDG` (demand.model = residents) | 11 | DemandI | i32 | 1 | 0 |
| `LEDG` (demand.model = residents) | 12 | ToursDay | i32 | 2 | 0 |
| `LEDG` (demand.model = residents) | 13 | LegsDay | i32 | 6 | 0 |
| `LEDG` (demand.model = residents) | 14 | LegCostDay | i64 | 6 | 0 |
| `LEDG` (demand.model = residents) | 15 | CellLoads | i32 | NC * P * 2 | 0 |
| `LEDG` (demand.model = residents) | 16 | DayStage | i32 | 1 | 0 |
| `LEDG` (demand.model = residents) | 17 | VisitsDayTotal | i32 | 2 | 0 |
| `LEDG` (demand.model = residents) | 18 | UnservedDayTotal | i32 | 2 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 1 | PolicyTaxRBp | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 2 | PolicyTaxCBp | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 3 | PolicyTaxIBp | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 4 | PolicyLandTaxBpDay | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 5 | PolicyFare | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 6 | PolicyFlags | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 7 | PolicyR0 | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 8 | PolicyR1 | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 9 | PendingTaxRBp | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 10 | PendingTaxCBp | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 11 | PendingTaxIBp | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 12 | PendingLandTaxBpDay | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 13 | PendingFare | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 14 | PendingFlags | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 15 | PendingR0 | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 16 | PendingR1 | u16 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 17 | HasPending | u8 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 18 | LandValue | i32 | NC | 0 |
| `CBGT` (PARM.CityBudget = 1) | 19 | IncomeEwma | i64 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 20 | ExpenseEwma | i64 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 21 | Carry | i64 | 23 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 22 | DebtDays | i32 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 23 | OverLimitDays | i32 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 24 | Insolvent | u8 | 1 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 25 | UndoTargetIndex | i32 | 64 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 26 | UndoTargetGeneration | i32 | 64 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 27 | UndoAmount | i64 | 64 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 28 | UndoExpireTick | i64 | 64 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 29 | FareEvadedDay | i32 | 2 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 30 | DebtEvictionsDay | i32 | 2 | 0 |
| `CBGT` (PARM.CityBudget = 1) | 31 | WarnLevel | u8 | 1 | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 1 | HighWater | i32 | 1 | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 2 | FreeCount | i32 | 1 | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 3 | FreeList | i32 | FreeCount | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 4 | Generation | i32 | HighWater | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 5 | Kind | u8 | HighWater | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 6 | Flags | u8 | HighWater | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 7 | Lot | i32 | HighWater | -1 |
| `PARK` (PARM.ParkingEnabled = 1) | 8 | Building | i32 | HighWater | -1 |
| `PARK` (PARM.ParkingEnabled = 1) | 9 | BuildingGen | i32 | HighWater | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 10 | Capacity | i32 | HighWater | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 11 | Assigned | i32 | HighWater | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 12 | Occupied | i32 | HighWater | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 13 | Reserved | i32 | HighWater | 0 |
| `PARK` (PARM.ParkingEnabled = 1) | 14 | Fee | i32 | HighWater | 0 |
| `LIFE` (PARM.LifecycleEnabled = 1) | 1 | FieldsCur | i32 | 4 * NC | 0 |
| `LIFE` (PARM.LifecycleEnabled = 1) | 2 | FieldsNext | i32 | 4 * NC | 0 |
| `LIFE` (PARM.LifecycleEnabled = 1) | 3 | Utility | i64 | 1 | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 1 | SiteBuilding | i32 | max_service_sites | -1 |
| `SERV` (PARM.LifecycleEnabled = 1) | 2 | SiteGen | i32 | max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 3 | SiteSvc | u8 | max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 4 | SiteLevel | u8 | max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 5 | SiteUpgradeMask | u8 | max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 6 | SiteUnitsMax | u8 | max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 7 | SiteDuty | u8 | max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 8 | SiteBedsUsed | i32 | max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 9 | SiteIcuUsed | i32 | max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 10 | SiteBedRelease | i32 | 16 * max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 11 | SiteIcuRelease | i32 | 16 * max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 12 | CallCount | i32 | 2 * records | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 13 | CallTarget | i32 | sum(CallCount) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 14 | CallTargetGen | i32 | sum(CallCount) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 15 | CallRef | i32 | sum(CallCount) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 16 | CallRefGen | i32 | sum(CallCount) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 17 | CallTick | i64 | sum(CallCount) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 18 | CallKind | u8 | sum(CallCount) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 19 | CallSvc | u8 | sum(CallCount) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 20 | CrewSite | i32 | max_service_vehicles | -1 |
| `SERV` (PARM.LifecycleEnabled = 1) | 21 | CrewState | u8 | max_service_vehicles | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 22 | CrewTarget | i32 | max_service_vehicles | -1 |
| `SERV` (PARM.LifecycleEnabled = 1) | 23 | CrewTargetGen | i32 | max_service_vehicles | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 24 | CrewSiteLot | i32 | max_service_vehicles | -1 |
| `SERV` (PARM.LifecycleEnabled = 1) | 25 | CrewVehicle | i32 | max_service_vehicles | -1 |
| `SERV` (PARM.LifecycleEnabled = 1) | 26 | CrewVehicleGen | i32 | max_service_vehicles | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 27 | CrewDueTick | i64 | max_service_vehicles | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 28 | CrewReqTick | i64 | max_service_vehicles | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 29 | CrewKind | u8 | max_service_vehicles | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 30 | SvcPct | u8 | records | 100 |
| `SERV` (PARM.LifecycleEnabled = 1) | 31 | SvcPctPending | u8 | records | 100 |
| `SERV` (PARM.LifecycleEnabled = 1) | 32 | SvcCostDay | i64 | 2 * records | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 33 | SvcCallsDone | i64 | records | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 34 | SvcRespTicks | i64 | records | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 35 | SvcCallsLate | i64 | records | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 36 | SvcCallsQueued | i64 | records | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 37 | SiteVarDay | i64 | 2 * max_service_sites | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 38 | SvcRespEwma | u16 | records * cells (0 без city.services) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 39 | CrewRef | i32 | max_service_vehicles | -1 |
| `SERV` (PARM.LifecycleEnabled = 1) | 40 | CrewRefGen | i32 | max_service_vehicles | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 41 | SvcCareEwma | u16 | cells (0 без city.services) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 42 | FireState | u8 | max_fires (0 без city.services) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 43 | FireBuilding | i32 | max_fires (0 без city.services) | -1 |
| `SERV` (PARM.LifecycleEnabled = 1) | 44 | FireGen | i32 | max_fires (0 без city.services) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 45 | FireStartTick | i64 | max_fires (0 без city.services) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 46 | FireCondStart | u16 | max_fires (0 без city.services) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 47 | FireRetry | i64 | max_fires (0 без city.services) | 0 |
| `SERV` (PARM.LifecycleEnabled = 1) | 48 | SiteAnnex | i32 | 8 * max_service_sites | -1 |
| `LUSE` (PARM.LandUseCount > 0) | 1 | Count | i32 | 1 | 0 |
| `LUSE` (PARM.LandUseCount > 0) | 2 | P0X | f32 | Count | 0 |
| `LUSE` (PARM.LandUseCount > 0) | 3 | P0Z | f32 | Count | 0 |
| `LUSE` (PARM.LandUseCount > 0) | 4 | P1X | f32 | Count | 0 |
| `LUSE` (PARM.LandUseCount > 0) | 5 | P1Z | f32 | Count | 0 |
| `LUSE` (PARM.LandUseCount > 0) | 6 | P2X | f32 | Count | 0 |
| `LUSE` (PARM.LandUseCount > 0) | 7 | P2Z | f32 | Count | 0 |
| `LUSE` (PARM.LandUseCount > 0) | 8 | P3X | f32 | Count | 0 |
| `LUSE` (PARM.LandUseCount > 0) | 9 | P3Z | f32 | Count | 0 |
| `LUSE` (PARM.LandUseCount > 0) | 10 | Kind | u8 | Count | 0 |
| `LUSE` (PARM.LandUseCount > 0) | 11 | Flags | u8 | Count | 0 |
| `CKPI` (demand.model = residents) | 1 | Head | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 2 | HoursFilled | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 3 | VehAtHourStart | i64 | 1 | 0 |
| `CKPI` (demand.model = residents) | 4 | MovingAtHourStart | i64 | 1 | 0 |
| `CKPI` (demand.model = residents) | 5 | DayHead | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 6 | DaysFilled | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 7 | DayBalance | i64 | 32 | 0 |
| `CKPI` (demand.model = residents) | 8 | RingLegsCar | i32 | 24 | 0 |
| `CKPI` (demand.model = residents) | 9 | RingLegsBus | i32 | 24 | 0 |
| `CKPI` (demand.model = residents) | 10 | RingLegsWalk | i32 | 24 | 0 |
| `CKPI` (demand.model = residents) | 11 | RingLegsWork | i32 | 24 | 0 |
| `CKPI` (demand.model = residents) | 12 | RingDurCarDs | i64 | 24 | 0 |
| `CKPI` (demand.model = residents) | 13 | RingDurBusDs | i64 | 24 | 0 |
| `CKPI` (demand.model = residents) | 14 | RingDurWalkDs | i64 | 24 | 0 |
| `CKPI` (demand.model = residents) | 15 | RingDurWorkDs | i64 | 24 | 0 |
| `CKPI` (demand.model = residents) | 16 | RingVehTicks | i64 | 24 | 0 |
| `CKPI` (demand.model = residents) | 17 | RingMovingTicks | i64 | 24 | 0 |
| `CKPI` (demand.model = residents) | 18 | RingIncLongWait | i32 | 24 | 0 |
| `CKPI` (demand.model = residents) | 19 | RingIncNoParking | i32 | 24 | 0 |
| `CKPI` (demand.model = residents) | 20 | RingIncLongCommute | i32 | 24 | 0 |
| `CKPI` (demand.model = residents) | 21 | RingIncTourCancelled | i32 | 24 | 0 |
| `CKPI` (demand.model = residents) | 22 | RingIncLongStopWait | i32 | 24 | 0 |
| `CKPI` (demand.model = residents) | 23 | CurLegsCar | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 24 | CurLegsBus | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 25 | CurLegsWalk | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 26 | CurLegsWork | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 27 | CurDurCarDs | i64 | 1 | 0 |
| `CKPI` (demand.model = residents) | 28 | CurDurBusDs | i64 | 1 | 0 |
| `CKPI` (demand.model = residents) | 29 | CurDurWalkDs | i64 | 1 | 0 |
| `CKPI` (demand.model = residents) | 30 | CurDurWorkDs | i64 | 1 | 0 |
| `CKPI` (demand.model = residents) | 31 | CurVehTicks | i64 | 1 | 0 |
| `CKPI` (demand.model = residents) | 32 | CurMovingTicks | i64 | 1 | 0 |
| `CKPI` (demand.model = residents) | 33 | CurIncLongWait | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 34 | CurIncNoParking | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 35 | CurIncLongCommute | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 36 | CurIncTourCancelled | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 37 | CurIncLongStopWait | i32 | 1 | 0 |
| `CKPI` (demand.model = residents) | 38 | RingIncBuildingWorn | i32 | 24 | 0 |
| `CKPI` (demand.model = residents) | 39 | CurIncBuildingWorn | i32 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 1 | MilestoneCount | i32 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 2 | ProfileIdxCount | i32 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 3 | AreaCount | i32 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 4 | ProfileCount | i32 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 5 | MinLegs | i32 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 6 | LockedFeatures | u64 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 7 | Population | i32 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 8 | Grant | i32 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 9 | CreditDays | i16 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 10 | Area | i8 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 11 | Flags | u8 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 12 | G0Kpi | u8 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 13 | G0IsMin | u8 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 14 | G0Value | i32 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 15 | G1Kpi | u8 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 16 | G1IsMin | u8 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 17 | G1Value | i32 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 18 | Features | u64 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 19 | ProfileFirst | i32 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 20 | ProfileCountOf | i32 | MilestoneCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 21 | ReachedTick | i64 | MilestoneCount | -1 |
| `PROG` (PARM.ProgressionEnabled = 1) | 22 | ProfileIdx | i32 | ProfileIdxCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 23 | AreaMinX | f32 | AreaCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 24 | AreaMinZ | f32 | AreaCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 25 | AreaMaxX | f32 | AreaCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 26 | AreaMaxZ | f32 | AreaCount | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 27 | Reached | i32 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 28 | FeaturesOpen | u64 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 29 | ProfilesOpen | u64 | ceil(ProfileCount / 64), >= 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 30 | CreditDaysBonus | i32 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 31 | AreaStage | i32 | 1 | 0 |
| `PROG` (PARM.ProgressionEnabled = 1) | 32 | GatedNotified | u64 | 1 | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 1 | CellMm | i32 | 1 | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 2 | OriginX | f32 | 1 | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 3 | OriginZ | f32 | 1 | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 4 | SamplesX | i32 | 1 | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 5 | SamplesZ | i32 | 1 | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 6 | BaseCm | i32 | 1 | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 7 | HeightUnitMm | i32 | 1 | 10 |
| `TERR` (PARM.TerrainEnabled = 1) | 8 | Ground | u16 | SamplesX * SamplesZ | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 9 | Water | u16 | SamplesX * SamplesZ | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 10 | WaterKind | u8 | SamplesX * SamplesZ | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 11 | Version | i32 | 1 | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 12 | OpSeq | i32 | 1 | 0 |
| `TERR` (PARM.TerrainEnabled = 1) | 20 | ProfileMaxGradePm | i32 | ProfileCount (0 в legacy) | 0 |
| `MANI` | 1 | Kind | u8 | ManifestCount | 0 |
| `MANI` | 2 | SavedIndex | i32 | ManifestCount | 0 |
| `MANI` | 3 | IdOffset | u32 | ManifestCount | 0 |
| `MANI` | 4 | IdLength | u32 | ManifestCount | 0 |
| `MANI` | 5 | Version | u32 | ManifestCount | 0 |
| `MANI` | 6 | ContentHash | u64 | ManifestCount | 0 |
| `MANI` | 7 | IdBlob | u8 | IdBlobLength | 0 |
<!-- schema:end -->
