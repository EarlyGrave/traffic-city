# Инструменты игрока: команды, превью, undo, реплей (спецификация, этап 4.3, ADR-0023)

Протокол клиент↔ядро для инструментов игрока. UI, визуал и раскладка кнопок — решение владельца, здесь не решаются:
спека фиксирует только **какие данные** клиент получает (превью, коды ошибок, события, обратные команды).
Опирается на: `core-api.md` §4 (команды), §5.6–5.7, §7; `routing.md` §1, §9.3, §9.6–9.8; `intersections.md` §5.7, §6.2;
`save-format.md` §1, §10, §13; `pedestrians.md` §2 (после волны 3.1); `cs2-lessons.md` T6. Экономика (4.2, поверх ADR-0011) и
зонирование (ADR-0011, 3.2) ещё не написаны — для них только точки расширения (§11).

## 0. Решения коротко

- Всё — `Enqueue(in Command)`; новые `CommandKind` с 25 добавлением, 64 Б, ссылки — handle (index + generation) (§1–2).
- Сеть с дырами: пулы слотов с поколениями + арена блоков по лестнице; производные — «по возрастанию индекса живых» (§3).
- Правка — одним из трёх механизмов: на месте / перенос / дренаж. Никого не удаляем (§4).
- Ошибки — `RejectReason`, без исключений; предупреждения — только в превью (§6).
- `Preview` между `Step`: стоимость, проблемы, последствия, привязка; без мутации, 0 Б (§7).
- Undo/redo — у хоста; обратные команды пишет ядро при применении; `RestoreOf` + события `*Remapped` (§8).
- Журнал `(Tick, Command)` = реплей; баг-репорт = автосейв + журнал (§9).
- Фаза 1 однопоточна; пересчёт производных — пакетом в конце фазы 1 (§10–12).

### Инварианты

| # | Инвариант | Проверка (§14) |
|---|---|---|
| TL1 | Без команд инструментов состояние, хэш, сейв и поведение побитово прежние | golden s1k/s10k, `HotPathGoldenHashTests` |
| TL2 | Инструменты не удаляют машин и пешеходов: убыль живых — только прибытием (`TripCompleted`, пешеход дошёл) | тест сохранения на сносе загруженной дороги |
| TL3 | `Preview` не меняет состояние: хэш и байты `Save` до/после равны; 0 Б | тест |
| TL4 | После любой последовательности правок `Save → Load → Save` побайтно равен, продолжение — те же хэши (производные = перестроенным) | фаззер правок |
| TL5 | Хэш не зависит от числа потоков (1/2/3) при правках во время движения | тест |
| TL6 | Реплей журнала с автосейва даёт те же контрольные хэши | golden-реплей |
| TL7 | Undo всей последовательности → сеть, эквивалентная исходной (топология и параметры, не индексы); redo → эквивалентная конечной | фаззер undo |
| TL8 | Каждое состояние со ссылкой на полосу/connector/сегмент/узел имеет обработчик переноса | тест по реестру схемы сейва |
| TL9 | 0 Б/тик в стационаре при периодических командах инструментов | `AllocationProbe` |

## 1. Набор команд этапа 4

Номера 0–11 заняты (`core-api.md` §4.1; 11 = `SetLodFocus`, в коде не реализован), 12 = `SetPedTarget` (P2), 23 = `PaintZone` и 24 = `SetTripExecution` (Z1).
Команды инструментов нумеруются **с 25** (решение оркестратора 2026-10-26, `stage3-plan.md` §5a); прежние черновые номера 12–22 сдвинуты на +13.
Новые kind — только добавлением в конец `CommandKind`; номера 13–22 остаются свободными (резерв).

| Kind | Команда | Механизм (§4) | Обратная (§8) |
|---|---|---|---|
| 1 | `BuildRoad` (+кривая, +врезка в построенный узел, +разрез сегмента `AutoSplit`) | перенос (узлы концов), разрез — §4.4 | `RemoveRoad` (+`MergeAtNode` для созданных разрезом узлов) |
| 3 | `SetSignalPlan` (+поколение узла) | на месте | `SetSignalPlan(прежний)` |
| 4 | `CreateBusRoute` (реализовать) | на месте | `RemoveBusRoute` |
| 5 | `AppendBusStop` (реализовать; сырой индекс полосы — генератор и тесты) | на месте | `RemoveBusStop` |
| 10 | `SetNodeControl` (+поколение узла) | на месте (+перестройка узла при смене приоритетов) | `SetNodeControl(прежний)` + `SetSignalPlan(прежний)` |
| 25 | `RemoveRoad` | дренаж сегмента | `BuildRoad(RestoreOf)` + настройки узлов, полос, остановок |
| 26 | `UpgradeRoad` (скорость, тип, полосы по направлениям) | на месте / перенос (+полосы) / дренаж (−полосы) | `UpgradeRoad(прежнее)` + `SetLaneFlags` |
| 27 | `SplitRoad` (узел в точке оси) | перенос с дренажом «коробки» узла (§4.4) | `MergeAtNode` |
| 28 | `SetArmControl` (приоритет/уступи/стоп на рукаве) | на месте | прежний |
| 29 | `SetTurnBan` (запрет манёвра рукав→рукав) | перестройка узла (§5) | прежний |
| 30 | `SetLaneFlags` (`BusOnly`, `NoLaneChangeOut`) | на месте | прежний |
| 31 | `InsertBusStop` (сегмент + направление + `LaneNo` + `S`, позиция в маршруте) | на месте | `RemoveBusStop` |
| 32 | `RemoveBusStop` | на месте (автобусы пропускают) | `InsertBusStop(RestoreOf)` |
| 33 | `RemoveBusRoute` | на месте (автобусы доезжают до следующей остановки и завершают рейс) | `CreateBusRoute(RestoreOf)` + `InsertBusStop`×k |
| 34 | `SetBusRouteParams` (интервал, тип ТС) | на месте | прежний |
| 35 | `MergeAtNode` (узел степени 2 с одинаковыми сегментами) | перенос с тишиной узла | `SplitRoad(RestoreOf)` |
| 23 | `PaintZone` — номер **закреплён** (3.2 Z1: перечисление и диспетчер есть, `Zoning.Apply` - заглушка `NotSupported`, тело - Z6); payload `PaintZoneCmd` ниже | — | — |
| 24 | `SetTripExecution` (3.2 Z1: номер закреплён за ядром жителей; `NotSupported` до Z5) | на месте (граница суток) | прежний режим |

Не инструменты (обратных нет, `InverseRecord.Flags.NoInverse`): `SpawnTrip`, `SpawnBus`, `SelectVehicle`, `ClearSelection`,
`SetTargetVehicleCount`, `SetLodFocus`.

## 2. Payload (`Api/Command.cs`, `Api/ToolTypes.cs`)

```csharp
public struct EntityRef { public int Index, Generation; }   // 8 B; Generation == 0 — без проверки (только генератор/тесты)

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BuildRoadCmd                 // 24 → 56 B
{
    public float Ax, Az, Bx, Bz, SpeedLimit;
    public byte LanesAB, LanesBA; public RoadKind Kind; public byte Flags;   // бит0 NoConnectors (тесты), бит1 AutoSplit, бит2 Curve
    public float Cx, Cz;                   // контрольная точка квадратичной Безье (Curve), иначе 0
    public EntityRef RestoreOf;            // сегмент, который команда восстанавливает (undo), или {-1,0}
    public EntityRef RestoreNodeA, RestoreNodeB;   // узлы концов, если их тоже пересоздать
}
public struct RemoveRoadCmd     { public EntityRef Seg; public int Flags; }                        // бит0 KeepNodes (не удалять осиротевшие узлы)
public struct UpgradeRoadCmd    { public EntityRef Seg; public float SpeedLimit; public byte LanesAB, LanesBA; public RoadKind Kind; public byte Flags; } // 0 / 255 / бит0 KeepKind — «не менять»
public struct SplitRoadCmd      { public EntityRef Seg; public float S; public EntityRef RestoreNode; } // S — по оси от NodeA, м
public struct MergeAtNodeCmd    { public EntityRef Node; public EntityRef RestoreSeg; }
public struct SetArmControlCmd  { public EntityRef Node; public byte ArmSlot; public byte Control; public ushort R; } // ArmSlot — номер рукава по часовой (intersections §1)
public struct SetTurnBanCmd     { public EntityRef Node; public byte FromArm, ToArm, Banned, R; }
public struct SetLaneFlagsCmd   { public EntityRef Seg; public byte Dir, LaneNo, Flags, Mask; }
public struct InsertBusStopCmd  { public EntityRef Route; public int Position; public EntityRef Seg; public byte Dir, LaneNo; public byte Kind, R; public float S; public EntityRef RestoreOf; } // Position −1 = в конец
public struct RemoveBusStopCmd  { public EntityRef Stop; }
public struct RemoveBusRouteCmd { public EntityRef Route; }
public struct SetBusRouteParamsCmd { public EntityRef Route; public int HeadwayTicks; public int VehicleType; } // 0 / −1 — не менять
public struct PaintZoneCmd      { public float X0, Z0, X1, Z1; public byte ZoneKind, Density, R0, R1; } // резерв ADR-0011
// SetNodeControlCmd += EntityRef-поколение (Node уже есть: добавить NodeGen в конец, 8 → 12 B); SetSignalPlanCmd += NodeGen (46 → 50 B);
// CreateBusRouteCmd += EntityRef RestoreOf (12 → 20 B).
```

- Тест размеров: каждый payload ≤ 56 Б, `Command` = 64 Б, `Marshal.SizeOf` = `Unsafe.SizeOf`. Фабрики `Command.Make*` без аллокаций.
- `BusStopData` и `BusRouteData` получают `Generation` (у `BusStopData` — вместо `Reserved` + выравнивание; столбец сейва).
- `SegmentData`: `Reserved` → `Flags` (бит0 `Closing`, бит1 `Curve`), + `Cx, Cz` (контрольная точка; для обратной команды и превью).
  `LaneData.Flags`: бит2 `Closed` (въезд запрещён), бит3 `Draining` (MOBIL уводит). `NodeData`: бит в новом `Flags` (`Quiesce`).
- Кривая: `Builder` сэмплирует Безье детерминированно: `n = clamp(ceil(L_хорды / 8 м), 2, 32)` точек, равномерно по параметру,
  без libm-функций кроме `sqrt` (core-api §9). Концы кривой задают направление рукава (касательная), а не хорда.

## 3. Сеть с дырами (`Network/NetworkStore.cs`, `Network/BlockArena.cs`, `Util/HandlePool.cs`)

1. **Слоты.** `Nodes`, `Segments`, `BusStops`, `BusRoutes` — через `HandlePool` (как машины: поколение, free-list LIFO, `HighWater`).
   `Generation` в `*Data` уже есть у узлов/сегментов/полос/маршрутов. Свободный слот — канонические значения (ссылки −1, остальное 0,
   кроме поколения), флаг `Alive = 0`. Счётчики `NodeCount/SegmentCount` становятся `HighWater`; добавить `AliveNodes/AliveSegments`.
2. **Арена блоков** для диапазонов: `Lanes` (блок полос направления сегмента; блок connector'ов узла — подряд, `LocalBit < 64`),
   `LanePoints/LanePointS`, `SegmentPoints`, `NodeArms` (+`ArmFlags/ArmDir`), `LaneNext`, `Conflicts`, `BusRouteStops`, `ZoneLanes`.
   Ёмкость блока — ступенька лестницы `CapFor(n)` (та же, что у оверлея: функция числа элементов, не истории). Рост внутри ступеньки —
   на месте; иначе новый блок: сначала LIFO-free-list класса, иначе хвост арены; старый блок → в free-list класса, его элементы —
   канонические нули. Арена кончилась → команда `Capacity` (`CapacityKind` соответствующий), превью сообщает заранее (`NearCapacity`).
   Генератор и `Create` пишут блоки подряд без запаса (как сейчас) — раскладка s1k/s10k без правок не меняется (TL1).
3. **Канон.** Раскладка (`First*`, дыры, free-list'ы в LIFO-порядке) — состояние: в хэше (при `World.ToolsTouched = 1`) и в сейве.
   «Раскладка = построенная заново» (save-format §13 п. 5) действует только для сетей без правок; тест `CanonicalLayoutTests` —
   ограничить сетями без правок, правки покрывает TL4.
4. **Производные — по возрастанию индекса живых слотов**: `IncHead/IncNext`, `CellNext/Cells`, `LaneGateNode`, LOD-привязки,
   `RouteGraph` (CSR), пешеходная сеть (§4.6). Локальное обновление после правки обязано дать то же, что полная перестройка
   (debug-сверка `World.VerifyDerived()` в тестах после каждой команды фаззера). Это закрывает save-format §13 п. 6.
5. **Клиент**: `NetworkView` отдаёт те же массивы до `HighWater`; мёртвые слоты (`Alive = 0`) и `Closing` клиент обязан учитывать.
   Добавить `ReadOnlySpan<byte> NodeAlive/SegmentAlive` или флаг в `*Data` (решает T2; одно из двух, в `core-api.md` §5.6).

## 4. Механизмы правки

### 4.1 На месте
Меняется параметр, индексы не меняются: скорость (`DesiredSpeed` машин на полосах пересчитывается при следующем переходе;
немедленно — у машин сегмента, O(машин на нём)), тип дороги, флаги полос, `SetArmControl` (пересчёт `ArmFlags`, `Rel` конфликтов,
сброс допусков узла — как `SetNodeControl`, `intersections.md` §5.7), планы светофоров, параметры маршрутов, остановки.
`Net.Version++` только если меняется то, что видит клиент или граф (скорость, тип — да; план светофора — нет).

### 4.2 Перенос (`Network/Relocate.cs`)
Когда у каждой машины есть однозначное новое место. Вход — `LaneMap` (scratch `int[MaxLanes]`, old → new или −1 «исчезает, обязана
быть пуста») и `SMap` (для разреза/слияния: `S' = S − offset`). Один проход переносит **все** ссылки:

| Владелец | Поля |
|---|---|
| `VehicleStore` | `Lane`, `S`, `GhostLane`, `DestLane/DestS`, `NextConn`, `GrantConnector` (connector, исчезнувший → `Granted = 0`) |
| `LaneOccupancy` | `LaneHead/LaneTail/LaneCount` (переезжают с полосой), `OccMask` |
| `RouteStore` | окна живых слотов (полосы и DirSeg); планы хранят DirSeg — меняются только при разрезе/слиянии (DirSeg половины) |
| Очереди | `PendingTrips.FromLane/FromS`, `RefillQueue`/`Rerouting` (машины — без изменений) |
| Сеть | `BusStops.Lane/S`, `ZoneLanes`, `LaneNext`, `ConnectorStore.InLane/OutLane` |
| Перекрёстки | `LaneSignals`, `OutReserved`, `ConnWaitSum/ConnGrantCount/ConnDelay` (по connector'у), `NodeBusy` |
| LOD / Q-meso | `LaneCell/LaneColor` (производные), `LaneLastExit`, `MesoNext` (машины) |
| Пешеходы (3.1) | рёбра и переходы по `(Side, S)` — §4.6 |

Полнота — **TL8**: в `SaveSchema` у столбцов появляется признак `RefKind` (`Lane`, `Connector`, `Segment`, `DirSeg`, `Node`, `Stop`);
его же использует `SaveValidator` (проверка `InvalidReference`). Тест перечисляет столбцы с `RefKind` и требует обработчик в `Relocate`.
Новая задача, добавляющая такой столбец, обязана добавить обработчик (строка в DoD, `save-format.md` §11 «Попутно»).

### 4.3 Дренаж (`Network/ToolOps.cs`)
Когда места нет: снос сегмента, уменьшение полос, исчезающий занятый connector, «коробка» разреза.
Операция `ToolOp` (SoA на `tools.max_tool_ops`, по умолчанию 256): `Id` (монотонный), `Kind` (`RemoveSegment`, `ShrinkLanes`,
`NodeRebuild`, `SplitBox`, `MergeNode`), цель (handle), параметры финала, `StartTick`, `LastProgressTick`, `Remaining`, `StallReported`.

**Закрытие** (при применении команды):
- полосы цели — `Closed` (у `ShrinkLanes` — только убираемые, ещё и `Draining`); сегмент — `Closing`;
- ворота (фаза 4): кандидат, чей connector ведёт в `Closed`-полосу, получает `DenyClosed` (новый `DenyReason`); допуски в закрытые
  полосы сбрасываются (`GatePhase.ResetNode` для узлов концов, `intersections.md` §5.7). Машины уже на connector'е — доезжают;
- маршрутизация: `SegCost(u) = +∞` для закрытых DirSeg (A* их не раскрывает, кастомизация оверлея — `+∞`), ячейки → `Dirty`
  в начало волны; **скан маршрутов** (один на фазу 1, по битовой маске закрытых DirSeg): живые слоты `RouteStore`, у которых окно,
  стек плана (`q = 0`) или вход/выход матрицы (`q ≥ 1`) содержит закрытый DirSeg, освобождаются, машина — в `Rerouting`
  (`Kind = NetworkChange`), едет по умолчанию; правило «по умолчанию» и `NextConnector` закрытые полосы не выбирают;
- назначения: `DestLane` на закрытом сегменте → новое назначение той же зоны (`Destinations`, без закрытых полос); у зоны не осталось
  открытых полос → ближайшая к центру зоны открытая полоса `LaneNo 0` (детерминированно, ничья — меньший индекс) + `ZoneAccessLost`;
- спавн: `ZoneLanes` зон перестраиваются без закрытых полос, `PendingTrips` с `FromLane` на них — переназначение полосы зоны;
- MOBIL (фаза 5, чисто по машине): с `Draining`-полосы — вынужденная смена к открытой (как «полоса кончается»); в `Closed` не перестраиваться;
- автобусы: остановки на закрытом сегменте удаляются из маршрутов сразу (обратная запись их восстанавливает); автобус у такой остановки
  дорабатывает `Dwell` и едет к следующей; маршрут < 2 остановок — `Suspended` (нет выпуска, рейсы на линии завершаются на конечной);
- пешеходы (после 3.1): §4.6.

**Финал** — в начале фазы 1 (§12) по состоянию конца прошлого тика, операции по возрастанию `Id`. Условие: на полосах цели
`LaneCount = 0` и нет `GhostLane` в них; `NodeBusy` пуст по connector'ам, ведущим в цель и из неё; на рёбрах и переходах цели нет
пешеходов. Затем — структурная правка (удалить сегмент, освободить блоки, перестроить узлы концов §5; узел без рукавов освобождается,
кроме `KeepNodes`), `SegmentRemoved`/`NodeRemoved`, `ToolOpFinished`.

**Застрявший дренаж не форсируется** (правило проекта): `Remaining` (машины + пешеходы на цели) не убывает `tools.drain_stall_s`
(60 с) → событие `DrainStalled(op, Remaining)` раз за период, `DrainStalls++`. Цель остаётся `Closing`; undo (`BuildRoad(RestoreOf)`
на живой закрывающийся сегмент = **переоткрытие**: снимает `Closed`, отменяет операцию) или ожидание — решает игрок.

### 4.4 Разрез и слияние
- `SplitRoad(seg, s)` и `BuildRoad` с `AutoSplit` (конец в `SnapRadius` от внутренней точки оси, дальше `min_segment_m` от концов):
  новый узел `N` в точке оси; первая половина сохраняет handle и блоки полос (длина короче), вторая — новый сегмент.
  **Коробка** `[s − R, s + R]` (`R` — отступ узла, `intersections.md` §1) не имеет места в новой сети: операция `SplitBox` ставит
  виртуальное препятствие на входе в коробку по каждой полосе (фаза 5 читает его как стоп-линию закрытого connector'а — чисто
  по машине), машины в коробке уезжают (обычно < 2 с), затем финал: перенос (`SMap`) машин второй половины и построение узла.
- `MergeAtNode(node)`: узел степени 2, оба сегмента с одинаковыми полосами, типом, скоростью и флагами, без светофора/знаков;
  иначе `InvalidArgument`. Тишина узла (connector'ы `Closed`, ждём опустения), затем перенос второй половины на первую (`S += L₁`).

### 4.5 Полосы: `UpgradeRoad`
- Только скорость/тип/флаги — на месте.
- **Больше полос**: новый блок направления, `LaneNo k → FirstLane_new + k` (новые полосы — слева, старшие `LaneNo`), перенос; узлы концов —
  перестройка (§5).
- **Меньше полос**: убираемые (старшие `LaneNo`) — `ShrinkLanes` (дренаж с `Draining`), финал обрезает блок (префикс сохраняется —
  индексы оставшихся не меняются), узлы концов — перестройка.
- Обе стороны меняются в одной команде; на сегменте с активной операцией — `Busy`.

### 4.6 Пешеходы (после мержа 3.1; правка `pedestrians.md` §2 — задача T11)
Сеть пешеходов остаётся производной, но индексы её рёбер меняются при перестройке. Правило: при закрытии — тротуары и переходы цели
получают стоимость `+∞` в `PedRouter`, пешеходы на них доходят, пути через них перестраиваются на конце текущего ребра;
при финале — локальная перестройка (узлы и сегменты правки) и **таблица `oldEdge → newEdge`** по `(Side, S0)` и `(Node, ArmSlot)`
для `PedStore.Edge`, путей (`PathSlot`), очередей переходов; O(пешеходов + рёбер правки). `PD8` (достижимость жителей) — в превью
(`PedAccessLost`) и проверка после финала (`ped_no_path`).

## 5. Перестройка узла (`Network/NetworkBuilder.Connectors.cs`, `Rules/Intersections/*`)

Нужна при врезке рукава, сносе рукава, смене числа полос рукава, `SetTurnBan`, смене приоритетов. Снимает `NotSupported`
для `BuildRoad` в построенный узел и правило «новые узлы выше построенных» (`CommandDispatcher.BuildRoad`).
1. Вычислить новый набор connector'ов узла (тот же код, что при постройке, по `NodeArms` с запретами `TurnBan`).
2. **Выжившие** — та же пара `(InLane, OutLane)` после `LaneMap`. Если все **занятые** старые connector'ы выжили — сразу: новый блок
   connector'ов (или на месте по лестнице), перенос, `Conflicts` и `Rel` узла заново, `GatePhase.ResetNode`.
3. Иначе — `NodeRebuild` (дренаж): обречённые занятые connector'ы `Closed`, узел работает в **старой** раскладке до их опустения
   (обычно ≤ 3 с), машины новых полос ждут у стоп-линии; затем шаг 2.
4. Светофор: `LocalBit` меняются → маски фаз переносятся по `(FromArm, ToArm, Maneuver)`, если оси узла не изменились; иначе план
   по умолчанию (`intersections.md` §6.2) с теми же режимом/параметрами и событие `SignalPlanReset(node)` (обратная запись хранит прежний план).
   Узел с ≤ 2 рукавами и `Signal` → режим сохраняется (пешеходный светофор), плана с одной осью достаточно.
5. Пределы: рукавов ≤ `tools.max_arms_per_node` (6), connector'ов ≤ 64 → `NodeLimit`; угол между соседними рукавами ≥
   `tools.min_arm_angle_deg` (25°) → иначе `TooSharp`.

## 6. Валидация и коды

Ошибка → `CommandRejected`, `SimEvent.A = (int)RejectReason`, `B = (int)Kind`; состояние не тронуто (валидатор полностью до мутации;
структура «проверить всё → применить» — обязательна, частичных применений нет). Новые `RejectReason` (добавлением):

| Код | Имя | Когда |
|---|---|---|
| 6 | `TooShort` | сегмент или половина разреза < `tools.min_segment_m` (20 м) |
| 7 | `TooSharp` | угол рукавов < `min_arm_angle_deg`; кривизна Безье: радиус < `min_curve_radius_m` |
| 8 | `Overlap` | ось пересекает существующий сегмент не в узле или идёт параллельно ближе ширины проезжей части |
| 9 | `NodeLimit` | рукавов > 6, connector'ов > 64 |
| 10 | `Busy` | цель уже в операции (снос закрывающегося, апгрейд дренируемого) |
| 11 | `CreatesTrap` | односторонний сегмент в узел без выездов (локальная проверка O(1), §7.3) |
| 25 | `InsufficientFunds` | экономика включена (4.2) и бюджета мало — пока не возникает |
| 26 | `Unreachable` | `InsertBusStop`/`CreateBusRoute`: нет пути от соседней остановки (по текущему графу) |
| 27 | `ZoneRule` | резерв ADR-0011 |
| 28 | `OutOfBounds` | вне `tools.world_bounds` (по умолчанию — bounding box сети + 2 км) |

Существующие: `InvalidHandle` — индекс вне диапазона, мёртвый слот или чужое поколение; `InvalidArgument` — NaN, нули, несовместимые
параметры; `Capacity` — пул/арена (+`CapacityExhausted`); `NotSupported` — `PaintZone` до ADR-0011.

## 7. Превью (`Api/Simulation.Tools.cs`, `Tools/ToolValidator.cs`, `Tools/Connectivity.cs`)

### 7.1 API
```csharp
public enum IssueSeverity : byte { Info = 0, Warning = 1, Error = 2 }
public enum IssueCode : ushort       // 1..99 = RejectReason (ошибки), 100+ = предупреждения/сведения
{   DeadEnd = 100, ZoneAccessLost = 101, StopAccessLost = 102, PedAccessLost = 103, DrainNeeded = 104 /* Value = машин+пешеходов */,
    RoutesAffected = 105, BusRouteShortened = 106, SignalPlanReset = 107, NodeRebuildDelayed = 108, OverlayRebuild = 109,
    NearCapacity = 110, UndoUnavailable = 111 /* обратная запись не влезет */, ZoneLotsLost = 112 /* резерв ADR-0011 */ }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct PreviewIssue { public IssueCode Code; public IssueSeverity Severity; public byte ObjKind; public EntityRef Obj; public float X, Z; public int Value; }

[StructLayout(LayoutKind.Sequential, Pack = 8)]
public struct PreviewResult
{
    public RejectReason Verdict;          // None — команда применится (предупреждения не мешают)
    public byte Mechanism;                // 0 на месте, 1 перенос, 2 дренаж
    public byte SnapKindA, SnapKindB;     // 0 новый узел, 1 существующий узел, 2 разрез сегмента
    public long Cost;                     // > 0 трата, < 0 возврат (целые единицы, §11)
    public float AX, AZ, BX, BZ, LengthM; // концы после привязки
    public EntityRef SnapA, SnapB;        // узел или разрезаемый сегмент
    public int NewNodes, NewSegments, NewLanes, NewConnectors;
    public int DrainVehicles, DrainPeds, BusStopsLost, ZonesLosingAccess;
    public int RoutesThrough;             // −1 — не считалось (нужен флаг Detail, O(маршрутов))
    public int IssueCount, IssuesWritten, AxisPointCount;
}

public sealed partial class Simulation
{
    // Между Step, тот же поток. Не меняет состояние; 0 Б. Очередь команд не учитывается (превью — против применённого состояния).
    public RejectReason Preview(in Command cmd, out PreviewResult r, Span<PreviewIssue> issues, Span<Vec2> axis = default, PreviewFlags flags = 0);
}
```
- `axis` — осевая полилиния будущей дороги **тем же сэмплированием**, что у `Builder` (призрак клиента совпадает с результатом).
- Валидатор один на `Preview` и применение (`ToolValidator.Check(in World, in Command, ref ToolCheck)` — чистая функция); превью
  дополнительно считает предупреждения и последствия. Тест: `Preview.Verdict == Rejected/Applied` для 10 000 случайных команд.
- FFI: `tc_sim_preview(sim, const Command*, PreviewResult*, PreviewIssue*, int32 cap, Vec2* axis, int32 axisCap, uint32 flags)`.

### 7.2 Что получает клиент для превью/ошибок
Только данные: `Verdict` и `IssueCode` (текст — локализация хоста), объект и точка проблемы (подсветить), стоимость, привязка
концов и ось, счётчики последствий. После применения — `CommandApplied/Rejected` с тем же `Tag`, события-следствия
(`SegmentCreated`, `SegmentClosing`, `SegmentRemoved`, `NodeRemoved`, `NodeRebuilt`, `SignalPlanReset`, `ToolOpFinished`,
`DrainStalled`, `ZoneAccessLost`, `BusStopCreated/Removed`, `*Remapped`) и обратная запись (§8). Прогресс дренажа —
`ReadOnlySpan<ToolOpView> ToolOps` (`Id`, `Kind`, цель, `Remaining`, `StartTick`) для индикатора на дороге.

### 7.3 Связность (урок T6)
- **Ошибки — только локальные** (детерминированные, O(1), годятся и для применения): `CreatesTrap` — односторонний DirSeg в узел,
  у которого после постройки нет выезда, кроме самого сегмента (двусторонний тупик — не ловушка: разворот есть).
- **Предупреждения — глобальные, только в превью.** Кэш компонент сильной связности по графу DirSeg (итеративный Tarjan, scratch
  `CompId[DirSegCap]`), строится лениво при первом превью после смены `Net.Version`; эталонная компонента — с наибольшим числом
  полос спавна зон (ничья — меньший id). Кэш — производное, вне хэша и сейва; на результат применения не влияет.
  - `BuildRoad`: новый конец вне эталонной компоненты → `DeadEnd`/`Info`; односторонний из изолированного узла — `Warning`.
  - `RemoveRoad`/`ShrinkLanes` до нуля направления: для каждого DirSeg `a→b` цели — ограниченный A* `a → b` без цели
    (`tools.preview_detour_work`, 20 000 релаксаций; `AStar` получает параметр «запрещённый сегмент»). Объезд найден по всем
    направлениям → связность не меняется. Нет/лимит → Tarjan на scratch без цели и сравнение: зоны и остановки, выпавшие из
    эталонной компоненты → `ZoneAccessLost`/`StopAccessLost` (объект, точка), `ZonesLosingAccess`.
  - Маршруты автобусов: нога, теряющая путь → `BusRouteShortened`.
- Решение «предупреждение или запрет» при потере доступа — у клиента (вопрос владельцу 3); ядро применяет команду.

## 8. Undo / redo (`Api/ToolTypes.cs`, `Tools/InverseBuilder.cs`; хост `src/Sim.Scenarios/Tools/UndoStack.cs`)

### 8.1 Обратные записи ядра
```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct InverseRecord { public uint Tag; public int First, Count; public byte Flags; public byte R0, R1, R2; } // бит0 Truncated, бит1 NoInverse
public ReadOnlySpan<InverseRecord> Inverses { get; }       // за последний Step, в порядке применения, по одной на команду (кроме NoInverse)
public ReadOnlySpan<Command> InverseCommands { get; }      // [First, First+Count) — в порядке, в котором их надо поставить в очередь
```
- Буфер на `tools.inverse_capacity` команд (4 096), записей ≤ `CommandQueueCapacity`; очищается в начале `Step` (как события).
  Запись > 64 команд или нет места → `Truncated` (undo этого действия недоступен; превью заранее — `UndoUnavailable`).
- Обратная строится **из фактического состояния в момент применения** (`InverseBuilder`, до мутации): таблица §1. Снос сегмента
  пишет `BuildRoad(геометрия, параметры, RestoreOf = seg, RestoreNodeA/B — если узлы будут освобождены)`, затем настройки узлов
  концов, которые изменятся (`SetNodeControl`, `SetSignalPlan`, `SetArmControl`, `SetTurnBan` с этим рукавом), `SetLaneFlags`
  для небазовых флагов, `InsertBusStop(RestoreOf)` для остановок цели с позициями в маршрутах.
- Обратные — обычные команды: проходят валидацию и журнал; `Tag` ставит хост.

### 8.2 Пересоздание и перенумерация
- Команда с `RestoreOf = H`: если `H` жив и цель в операции дренажа — **отмена операции** (переоткрытие, без пересоздания); если
  слот мёртв — пересоздание в новом слоте, событие `SegmentRemapped/NodeRemapped/BusStopRemapped/BusRouteRemapped (A = old, B = new)`.
- Внутри одной фазы 1 ядро ведёт scratch-таблицу `old → new` (очищается в конце фазы 1, не состояние): следующие команды
  той же пачки со ссылкой на `H` переводятся на новый handle. Поэтому хост ставит команды одной обратной записи **подряд в одном Step**.
- Хост по `*Remapped` правит handle во всех командах своих стеков (`CommandRefs.Remap(ref Command, in RemapTable)` — чистая функция
  в `Sim.Core/Api`, знает смещения `EntityRef` по `Kind`).

### 8.3 Стек хоста
- Действие игрока = группа команд (протяжка цепочки дорог — одна группа); запись стека = `{прямые команды, обратные из ядра}`.
- Undo: поставить обратные (порядок записей группы — обратный); их обратные из ядра = запись redo. Redo симметрично.
  Новое действие очищает redo. Глубина — 100 групп (старшие отбрасываются), памяти ≈ 100 × k × 64 Б.
- Undo **логическое**: восстанавливает сеть и настройки, не мгновенное состояние (уехавшие машины не возвращаются, фаза светофора
  начнётся заново, веса маршрутов — от текущей загрузки). Обратная может быть отклонена (линейный стек исключает конфликт с более поздними
  правками игрока, остаются ёмкость, деньги, `Busy`) → запись снимается, клиент получает `CommandRejected` с кодом.
- Детерминизм: ядро про undo не знает; журнал (§9) видит обычные команды → реплей совпадает.

## 9. Журнал команд = реплей (`src/Sim.Scenarios/Tools/ReplayLog.cs`, `Sim.Headless replay|bugreport`)

- Хост пишет каждую принятую `Enqueue` команду: `(Tick на момент Enqueue, Command 64 Б)`; отброшенные (`Enqueue == false`) — нет
  (`CommandsDropped` детерминирован, но команды не было). Превью не пишется. Формат — `save-format.md` §10: `RPLH` (базовый
  `state_hash`, тик начала, `world_version`, версия ядра, контрольные хэши каждые 1 200 тиков) + `CMDL`.
- Ре-сим с тика N: ближайший автосейв с `Tick ≤ N` + записи журнала с его тика. Журнал действителен только для той же версии
  поведения ядра (save-format §10); несовпадение версии → понятная ошибка `replay`.
- `Sim.Headless replay --save <x> --log <y> [--until T] [--check]`: сверка контрольных хэшей; первое расхождение → тик и
  `state_hash` обеих сторон, затем бинарный поиск по тикам между контрольными точками (сейв → ре-сим → хэш каждый тик).
- Баг-репорт (`bugreport`): последний автосейв + журнал с него + версия ядра + сценарий-источник (если есть) в одном контейнере.
- Клиент Godot ведёт журнал всегда (кольцо с последнего автосейва; при автосейве журнал обрезается до него).

## 10. Детерминизм, хэш, потоки, сейв

- Все правки — в фазе 1, однопоточно: финалы операций (по `Id`), затем очередь FIFO, затем пакетный пересчёт производных (§12).
  Ни одна правка не идёт в параллельных фазах; фазы 5/6 только читают `Closed/Draining`, виртуальные препятствия `SplitBox`
  (чисто по машине, ADR-0017). Скан маршрутов и перенос — однопоточные проходы по возрастанию индекса.
- Только целые и `float` из правил core-api §9; деньги — `long`. Порядок обхода фиксирован, ничьи — по меньшему индексу.
- **Хэш** (core-api §9): при `World.ToolsTouched = 1` (первая структурная правка) — `ToolOps`, free-list'ы пулов и арен, `Alive`,
  флаги `Closing/Closed/Draining/Quiesce`, `Cx/Cz`, поколения остановок/маршрутов, `TurnBan`. Без правок хэш прежний (TL1).
- **Сейв**: блок `TOOL` (операции, `ToolsTouched`), столбцы флагов/кривых/поколений/`TurnBan`, free-list'ы (`NETW`/арены), дыры —
  каноническими нулями. `world_version` — следующая после пешеходов (ожидаемо 3), миграция: всё живо, free-list'ы пусты, операций нет.
  Корпус: `w3-small-edited` (после фаззера правок, с активной операцией дренажа).
- **Потоки**: scratch инструментов (`LaneMap`, маски закрытых DirSeg, кэш компонент, A*-объезд) — один экземпляр, вне воркеров.

## 11. Стоимость и точки расширения

- **Деньги** — целые `long` в единицах бюджета (валюту определяет 4.2). Таблица — секция сценария `tools.costs`
  (`scenario-format.md`, загрузчик `Sim.Scenarios`; значения — заглушки до баланса 4.2):
  `road_per_m_lane {street, arterial}`, `curve_factor_pct`, `node_signal`, `arm_sign`, `turn_ban`, `bus_stop`, `bus_route`,
  `demolish_refund_pct`, `upgrade_refund_pct`. `UpgradeRoad` = стоимость нового − возврат старого.
- **Экономика (4.2)**: `World.Economy.Enabled = 0` до 4.2. Включённая — при применении `Budget < Cost` → `InsufficientFunds`,
  иначе списание в фазе 1 (то же место, детерминированно). Возврат за undo — политика 4.2 (признак — `RestoreOf` в команде:
  обратная может стоить как новая стройка или 0 — решит ADR экономики, вопрос 5 §16).
- **Зонирование (ADR-0011)**: `PaintZone` (номер 23 занят), `IssueCode.ZoneLotsLost`, `RejectReason.ZoneRule`; снос дороги, вдоль
  которой есть участки, — ADR-0011 определит судьбу участков (точка вызова — финал `RemoveSegment`).

## 12. Фаза 1 (правка `core-api.md` §7)

| Шаг | Что | Сложность |
|---|---|---|
| 1a | Финалы `ToolOps` с выполненным условием (по `Id`), структурные правки, `NetDirty = 1` | O(операций) + O(правки) |
| 1b | Очередь команд FIFO: валидация → обратная запись → мутация сети (структура — сразу в `NetworkStore`) → события | O(локально) на команду |
| 1c | Если `NetDirty`: **один раз** — `RouteGraph.Rebuild` + `RecalcCosts`, оверлей (дописать/дыры, ячейки → начало волны), скан маршрутов по маске закрытых DirSeg, `ZoneLanes`, LOD-ячейки, пешеходная сеть, `SpawnerData` (лениво) | O(DirSeg + дуг) + O(Σ окон и планов) при закрытиях |

Команда, валидации которой нужен граф (`InsertBusStop`, `CreateBusRoute` с `Unreachable`), при `NetDirty` вызывает `EnsureGraph()`
(та же пересборка раньше; результат детерминирован). `Net.Version++` — на каждую структурную правку (как сейчас).

### 12.1 Пересчёты: инкрементально или полностью

| Производное | Как | Комментарий |
|---|---|---|
| `NetworkStore`, connector'ы, конфликты | локально (сегмент, узлы концов) | §3, §5 |
| `RouteGraph` CSR | полностью, один раз на фазу 1 | O(DirSeg + дуг); инкрементальный CSR — только если замер > бюджета |
| Веса `SegCost` | `RecalcCosts(SegmentLoads)` после пересборки (как H2) | закрытые — `+∞` |
| Оверлей | локально: новые входы/выходы в запас, удалённые — дыры `+∞`, грязные ячейки в начало волны | **полная перестройка раскладки** при нехватке запаса — синхронно только раскладка O(DirSeg); кастомизация — волной в бюджете (`customize_budget_per_tick`), запросы до её конца — плоским A* (`OverlayDisabled` временно); правка `routing.md` §9.3 |
| Скан маршрутов | полностью по живым слотам, только при закрытиях | O(Σ WinCount + PlanCount) |
| Пешеходная сеть | локально + таблица переноса | §4.6 |
| LOD / Q-meso | локально (`OnNetworkGrown` + удаление из CSR ячеек) | |
| Кэш компонент (превью) | полностью, лениво по `Net.Version` | вне хэша |

### 12.2 Бюджеты (цели; цифры — только замер верификатора T14)
- Фаза 1 с одной правкой дороги на s100k (4 потока, без волны) — ≤ 2 мс; пачка 20 команд протяжки — ≤ 5 мс.
- `Preview` при тёплом кэше — ≤ 0.5 мс на s100k; первый после смены версии (Tarjan) — замерить и записать.
- Ни одна команда не запускает синхронную работу сверх O(DirSeg + дуг + Σ окон/планов): полной кастомизации в фазе 1 нет.
- 0 Б: всё — в `Create` по ёмкостям (`max_tool_ops`, `inverse_capacity`, scratch по `MaxLanes/DirSegCap`), включая превью.
- Хост сам ограничивает темп (не больше N команд на кадр); ядро команды не откладывает.

## 13. Метрики (`SimCounters`, секция `tools` JSON бенча)
`ToolCommandsApplied/Rejected`, `ToolOpsStarted/Finished`, `DrainStalls`, `DrainTicksMax`, `RelocatedVehicles`, `RouteReleasesOnClose`,
`NodeRebuilds`, `NodeRebuildsDelayed`, `OverlayLayoutRebuilds`, `InverseTruncated`, `PreviewCalls` (хост), `Phase1ToolWork` (условные
единицы: элементы правки + DirSeg пересборки + шаги скана).

## 14. Тест-план (`tests/Sim.Core.Tests/Tools/`)

| Тест | Что |
|---|---|
| `ToolPayloadSizes` | все payload ≤ 56 Б, `Command` 64 Б |
| `NoTools_GoldenUnchanged` | TL1: golden s1k/s10k без команд — прежние хэши |
| `Preview_DoesNotMutate` | TL3: хэш и `Save` до/после; `AllocationProbe` 0 Б; `Verdict` = итог применения на 10 000 случайных команд |
| `EditFuzz_SaveLoadRoundTrip` | TL4: 3 сида × 300 случайных команд инструментов на малой сети с ~200 машинами, сейв на каждом 10-м тике → `Load` → 50 тиков = эталон; `VerifyDerived` после каждой команды |
| `EditFuzz_ThreadsIndependent` | TL5: те же прогоны при 1/2/3 потоках — равные хэши |
| `RemoveBusyRoad_NoVehicleLost` | TL2: снос загруженной артерии s1k посреди прогона: `Alive + TripsCompleted` сохраняется, финал наступает (число тиков — в отчёт), `stuck_episodes` после финала не выше базовых |
| `DrainStall_NotForced` | тупик, забитый машинами без выезда: `DrainStalled`, никто не удалён, undo переоткрывает |
| `Relocate_Complete` | TL8: столбцы с `RefKind` ↔ обработчики `Relocate` |
| `Upgrade_Split_Merge_Relocation` | машины на месте после переноса (позиция в мире ±1 см), I6 не нарушается (core-api §7.6) |
| `NodeRebuild_SignalPlanMapped` | врезка рукава в узел со своим планом: маски перенесены или `SignalPlanReset` |
| `UndoFuzz_Equivalent` | TL7: случайная последовательность → undo всего → эквивалентная сеть (канон по геометрии в мм и параметрам); redo → конечная |
| `Replay_Golden` | TL6: прогон с журналом → `replay` с автосейва → контрольные хэши равны |
| `Tools_ZeroAlloc` | TL9: s1k, команда инструмента каждые 100 тиков (строить/сносить по кругу) — 0 Б/тик |
| `Connectivity_Preview` | ловушка → `CreatesTrap`; снос моста между двумя частями → `ZoneAccessLost` с объектами |

## 15. Задачи (sonnet; T14 — haiku). Все трогают API ядра или одни файлы — **последовательно**, кроме отмеченных

Общее DoD каждой: `dotnet build -warnaserror`, `dotnet format --verify-no-changes`, тесты своей области, правка своего раздела в
`core-api.md`/`routing.md`/`save-format.md` (приложение A — столбцы), строка в журнале `world_version`, TL1 (golden не меняется).
Старт — **после мержа волны 3.1** (пешеходы меняют `Api/`, `World/`, сейв w2, порядок тика).

| ID | Задача | Файлы | Приёмка / проверки | Зависит |
|---|---|---|---|---|
| T1 | Api-типы: новые `CommandKind`, payload (§2), `EntityRef`, `RejectReason` 6–15, `SimEventKind` (`SegmentClosing/Removed`, `NodeRemoved/Rebuilt`, `SignalPlanReset`, `ToolOpFinished`, `DrainStalled`, `ZoneAccessLost`, `BusStopCreated/Removed`, `*Remapped`), `IssueCode`, `PreviewResult/Issue`, `InverseRecord`, `CommandRefs.Remap`; диспетчер: новые kind → `NotSupported`; поколения в `SetNodeControl/SetSignalPlan` (0 — без проверки); `core-api.md` §4/§5.7/§12 | `src/Sim.Core/Api/Command.cs`, `SimEvent.cs`, `ToolTypes.cs` (новый), `CommandDispatcher.cs`, тесты | `ToolPayloadSizes`, golden без изменений | 3.1 |
| T2 | Сеть с дырами: `HandlePool` для узлов/сегментов/остановок/маршрутов, `BlockArena` с лестницей и LIFO, `Alive`, производные по возрастанию индекса, `World.VerifyDerived`, `ToolsTouched` и хэш, сейв (столбцы, `world_version++`, миграция, корпус), `RefKind` в схеме + `SaveValidator`; `NetworkView` (`Alive`) | `Network/*`, `Util/HandlePool.cs`, `World/*`, `Save/*`, `Metrics/StateHash.cs` | TL1, T1–T4 сейва, `CanonicalLayoutTests` (без правок), новый тест арены | T1 |
| T3 | `Relocate` + перестройка узла (§5): врезка в построенный узел (снять `NotSupported`), выжившие connector'ы, `NodeRebuild`-дренаж, перенос масок светофора / `SignalPlanReset`, `SetArmControl`, `SetTurnBan`, `SetLaneFlags` | `Network/Relocate.cs`, `Network/NetworkBuilder*.cs`, `Rules/Intersections/*`, `Api/CommandDispatcher.cs` | `Relocate_Complete`, `NodeRebuild_SignalPlanMapped`, I6 | T2 |
| T4 | Дренаж и снос: `ToolOps`, закрытие (ворота `DenyClosed`, `SegCost +∞`, скан маршрутов, назначения, спавн/`ZoneLanes`, MOBIL `Draining`, остановки/маршруты `Suspended`), финал в 1a, `DrainStalled`, `RemoveRoad`, `BuildRoad(RestoreOf)` = переоткрытие; фаза 1 = 1a/1b/1c | `Network/ToolOps.cs`, `Api/CommandDispatcher.cs`, `Rules/Intersections/GatePhase.cs`, `Rules/Mobil.cs`, `Routing/RoutePhase.cs`, `Routing/Destinations.cs`, `Agents/Spawner.cs`, `Transit/*` | `RemoveBusyRoad_NoVehicleLost`, `DrainStall_NotForced`, TL5 на сносе | T3 |
| T5 | `UpgradeRoad` (на месте / +полосы переносом / −полосы `ShrinkLanes`), `SplitRoad` + `AutoSplit` + `SplitBox` (препятствие в фазе 5), `MergeAtNode`, кривые Безье в `BuildRoad` | `Api/CommandDispatcher.cs`, `Network/*`, `Rules/BehaviourPhase.cs` | `Upgrade_Split_Merge_Relocation`, `EditFuzz_SaveLoadRoundTrip` (первая версия) | T4 |
| T6 | Оверлей и граф под правки: дыры `+∞` для удалённых DirSeg, пакетная пересборка в 1c, асинхронная полная перестройка (раскладка синхронно + волна, плоский A* до конца), `AStar` с запрещённым сегментом; `routing.md` §9.3/§9.6 | `Routing/Overlay*.cs`, `Routing/AStar.cs`, `Routing/RoutePhase.cs` | H-T1…H-T3 оверлея после правок (матрицы = эталон после волны), 0 Б на волну | T5 |
| T7 | Валидатор и превью: `ToolValidator` (общий с применением), `Preview`, стоимость из `tools.costs` (загрузчик + `scenario-format.md`), `axis`, кэш компонент (Tarjan), объезд, предупреждения; FFI-запись в `core-api.md` §12 | `src/Sim.Core/Tools/*`, `Api/Simulation.Tools.cs`, `src/Sim.Scenarios/ScenarioLoader.cs` | `Preview_DoesNotMutate`, `Connectivity_Preview` | T6 |
| T8 | Обратные записи (`InverseBuilder`), `RestoreOf` и scratch-перенумерация в фазе 1, `*Remapped`; хост `UndoStack` (группы, глубина 100, remap) | `src/Sim.Core/Tools/InverseBuilder.cs`, `Api/*`, `src/Sim.Scenarios/Tools/UndoStack.cs` | `UndoFuzz_Equivalent` | T7 |
| T9 | Автобусные инструменты: реализовать `CreateBusRoute/AppendBusStop`, `InsertBusStop`, `RemoveBusStop`, `RemoveBusRoute`, `SetBusRouteParams`, `Unreachable`, `Suspended`, поколения остановок/маршрутов | `Transit/*`, `Api/CommandDispatcher.cs`, `Network/NetworkStore.cs` | тесты маршрутов: вставка/удаление на ходу, автобус на удалённой остановке не застревает, TL2 | T8 |
| T10 | Журнал и реплей: `ReplayLog` (`RPLH`/`CMDL`), `Sim.Headless replay` (контрольные хэши, поиск расхождения), `bugreport` | `src/Sim.Scenarios/Tools/ReplayLog.cs`, `src/Sim.Headless/*` | `Replay_Golden`; ручной прогон `replay` на s1k с 50 командами (вывод — в отчёт) | T1 (**можно параллельно** с T2–T9: только хост) |
| T11 | Пешеходы под правки (§4.6): закрытие тротуаров/переходов, условие финала, локальная перестройка сети и таблица `oldEdge → newEdge`, `PedAccessLost`; правка `pedestrians.md` §2 | `src/Sim.Core/Peds/*` | PD1–PD8 с правками, `EditFuzz` с `s1k-peds` | T4 (+3.1) |
| T12 | Фаззер и сводные тесты: `EditFuzz_*` полный (все команды), `Tools_ZeroAlloc`, корпус `w3-small-edited` | `tests/Sim.Core.Tests/Tools/*`, `tests/SaveCorpus/` | TL4, TL5, TL9 | T9, T11 |
| T13 | Клиент: адаптер протокола без UI-дизайна — постановка команд, превью на движении курсора, коды → текст-заглушка, призрак по `axis`, индикатор `ToolOps`, undo/redo через `UndoStack`, журнал; мёртвые слоты и `Closing` в мешах | `game/Scripts/Tools/*` | `dotnet build game/TrafficCity.csproj`; скриншот `scripts/godot-shot.sh` (снос с дренажом) | T8 (T9 для автобусов) |
| T14 | (haiku) Верификатор: полный `dotnet test`, `bench-gate.sh` s1k, замеры §12.2 на s100k (фаза 1 одной правки и пачки 20, превью тёплый/холодный), `RemoveBusyRoad` — тики до финала; цифры — в отчёт этапа | — | реальные числа, без округления | T12 |

## 16. Открытые вопросы владельцу

| № | Вопрос | Рекомендация | Трейдофф |
|---|---|---|---|
| 1 | Снос дороги с машинами: дренаж (ждать, пока уедут) или мгновенно с удалением машин (как CS)? | Дренаж (правило «без деспауна»), с индикатором и `DrainStalled` | Снос загруженной улицы — десятки секунд; забитый тупик может не опустеть, пока игрок не уберёт причину |
| 2 | Разрез сегмента и врезка — мгновенно (перенос) с короткой «коробкой» (< 2 с) — устраивает? | Да | Сложнее код переноса (реестр ссылок), зато апгрейд/врезка в забитую дорогу работает сразу |
| 3 | Потеря доступа зоной/остановкой при сносе: предупреждение (подтверждение в клиенте) или запрет? | Предупреждение; ловушка одностороннего тупика — запрет | Игрок может отрезать район намеренно (перестройка); запрет мешал бы поэтапной перестройке |
| 4 | Undo: глубина 100 групп, без ограничения по времени, логическое (не откат мира) — так? | Да | Undo сноса не вернёт уехавшие машины и фазу светофора; полный откат = загрузка сейва |
| 5 | Возврат денег за undo (после 4.2): 100% в течение N секунд или как обычная стройка/снос? | 100% за последнее действие, если с него не было `Step` > 10 с игрового времени | Иначе «undo-эксплойт» (строить/отменять бесплатно); решать в ADR экономики |
| 6 | Превью цепочки дорог (протяжка): суммой одиночных превью (4.3) или честным превью пачки на теневой сети (позже)? | Сумма одиночных + проверка связности уже после применения | Связи между новыми сегментами цепочки в превью не видны (ловушка внутри цепочки — только `CommandRejected`) |
