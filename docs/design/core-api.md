# Sim.Core — публичное API и модель данных (спецификация, этап 1)

Статус: принято (ADR-0002). Документ — единственный источник истины для реализации
`src/Sim.Core`. Реализующие агенты не меняют сигнатуры из блоков кода без правки этого файла
и ADR. Всё, что помечено **[шаг 3]** / **[шаг 4]**, здесь — только точки расширения и структуры
данных; правила проектируются отдельными спеками.

Сопутствующие документы: `docs/design/scenario-format.md` (JSON сценариев),
`docs/adr/0002-core-api-and-data-model.md`, ТЗ — `docs/plan/brief-stage1.md`.

## 0. Инварианты ядра (повторение ТЗ в терминах этого документа)

| # | Инвариант | Как обеспечивается |
|---|-----------|--------------------|
| I1 | Ни одного `using Godot`, ввода, файлов, времени | хук + BannedApiAnalyzers; ядро получает `ScenarioDesc` уже распарсенным |
| I2 | Сущности — struct в плотных массивах, адресация через handle | §2, §6 |
| I3 | 0 B/тик в стационаре | все буферы выделяются в `Create` по ёмкостям из `ScenarioDesc` (§10); в `Step` нет `new`, нет `string`, нет замыканий, нет `params`, нет boxing, нет `throw` |
| I4 | Детерминизм: `dt = 1/20 с`, PCG32, фиксированный порядок | §7, §8; запрет `Parallel`/`Task`; порядок обхода — всегда по возрастанию индекса |
| I5 | Узкое API как будущий FFI | §3, §12: только blittable-struct, `ReadOnlySpan<T>` = указатель + длина |
| I6 | Машины не проходят сквозь друг друга | §7.6 — жёсткий клэмп интеграции по позиции лидера на начало тика |

Запрещено в `Sim.Core` (BannedSymbols.txt + ревью): `System.Linq.*`, `System.Random`,
`System.DateTime`, `System.Console`, `System.Threading.Tasks.*`, `System.IO.*`,
`System.Diagnostics.Stopwatch`, `System.Collections.Generic.List<T>.Sort` без компаратора-struct,
`string.Format`/интерполяция в горячем пути. Исключения (`ArgumentException`) допустимы **только**
в `Simulation.Create` и в `Debug.Assert`-проверках, которые вырезаются в Release.

Единицы: метры, секунды, м/с, радианы. Координатная плоскость — `(X, Z)`, `Y` — высота
(совпадает с Godot: Y вверх). Курс `Heading` — угол направления движения `atan2(dz, dx)`
в плоскости `(X, Z)`; клиент сам переводит в поворот вокруг Y (`rotation.y = -Heading`).

## 1. Обзор

```
   Sim.Headless / game (Godot)            Sim.Core
   ──────────────────────────             ────────────────────────────────────────────
   JSON → ScenarioDesc  ───Create()────►  Simulation
   Command (struct)     ───Enqueue()───►    CommandQueue (ring, FIFO, фикс. ёмкость)
                        ───Step(n)──────►    Tick loop: §7 (10 фаз)
   ReadOnlySpan<…>      ◄── Vehicles ───    Snapshot buffers (prev/curr, double-buffered)
                        ◄── SegmentLoads, SignalStates, SelectedRoute, Events, Network
   SimCounters/SimStats ◄── Counters ───    Metrics (без wall-clock)
   ulong                ◄── ComputeStateHash()
```

Ядро — одна «мир-машина»: все подсистемы хранят состояние в плотных массивах внутри
`Simulation` (композиция internal-структур), снаружи видны только фасад и blittable-типы из
`Sim.Core.Api`.

## 2. Handle и пулы

### 2.1 Типы handle

Каждый вид сущности имеет свой тип handle (компилятор не даст перепутать `VehicleHandle`
и `LaneHandle`). Все одинаковы по раскладке: 8 байт, `int Index; int Generation`.

```csharp
namespace Sim.Core.Api;

[StructLayout(LayoutKind.Sequential, Size = 8)]
public readonly struct VehicleHandle : IEquatable<VehicleHandle>
{
    public readonly int Index;        // слот в SoA-массивах, 0..MaxVehicles-1
    public readonly int Generation;   // нечётное = живой слот на момент выдачи; 0 = Null

    public VehicleHandle(int index, int generation) { Index = index; Generation = generation; }

    public static VehicleHandle Null => default;          // Index 0, Generation 0
    public bool IsNull => Generation == 0;
    public ulong Packed => ((ulong)(uint)Generation << 32) | (uint)Index;
    public static VehicleHandle FromPacked(ulong v) => new((int)(uint)v, (int)(uint)(v >> 32));

    public bool Equals(VehicleHandle o) => Index == o.Index && Generation == o.Generation;
    public override bool Equals(object? o) => o is VehicleHandle h && Equals(h);
    public override int GetHashCode() => Index ^ (Generation * 397);
    public static bool operator ==(VehicleHandle a, VehicleHandle b) => a.Equals(b);
    public static bool operator !=(VehicleHandle a, VehicleHandle b) => !a.Equals(b);
}
```

Точно такие же структуры (копия с заменой имени): `NodeHandle`, `SegmentHandle`, `LaneHandle`,
`ZoneHandle`, `BusRouteHandle`, `BusStopHandle`, `RouteHandle`. Connector-полоса — это `Lane`
с `LaneKind.Connector`, отдельного handle у неё нет. Перекрёсток = `Node` (у узла может не быть
правил, тогда это просто геометрический стык).

| Сущность | Handle | Пул | Освобождается на этапе 1? |
|----------|--------|-----|---------------------------|
| Vehicle (машина, автобус) | `VehicleHandle` | да, горячий | да — по прибытии |
| Node | `NodeHandle` | да | нет (снос дорог — этап 2+) |
| Segment | `SegmentHandle` | да | нет |
| Lane (в т.ч. connector, карман остановки) | `LaneHandle` | да | нет |
| Zone | `ZoneHandle` | да | нет |
| BusRoute | `BusRouteHandle` | да | нет |
| BusStop | `BusStopHandle` | да | нет |
| Route (кэшированный маршрут) | `RouteHandle` | да, горячий | да — вытеснение из кэша |

### 2.2 Пул (`Sim.Core.Util.HandlePool`)

```csharp
namespace Sim.Core.Util;

/// Free-list пул слотов фиксированной ёмкости. Поколение слота: 0 = никогда не выдавался,
/// нечётное = занят, чётное = свободен. Allocate и Free оба делают generation++,
/// поэтому handle с чётным Generation невалиден по определению, а Generation == 0 — Null.
internal sealed class HandlePool   // class, не struct: readonly-поле со struct давало бы защитные копии и терялись бы мутации
{
    private readonly int[] _generation;   // [capacity]
    private readonly int[] _freeList;     // [capacity], LIFO-стек свободных индексов
    private int _freeCount;
    private int _highWater;               // число когда-либо выданных слотов (0.._capacity)
    private int _aliveCount;

    public HandlePool(int capacity);
    public int Capacity { get; }
    public int AliveCount => _aliveCount;
    public int HighWater => _highWater;    // верхняя граница для обходов "по всем слотам"

    /// false = ёмкость исчерпана (вызывающий инкрементирует счётчик и не аллоцирует).
    public bool TryAllocate(out int index, out int generation);
    public void Free(int index);           // Debug.Assert(IsAlive(index))
    public bool IsAlive(int index) => (uint)index < (uint)_highWater && (_generation[index] & 1) == 1;
    public bool IsAlive(int index, int generation) => IsAlive(index) && _generation[index] == generation;
    public int GenerationOf(int index) => _generation[index];
}
```

Правила:
- Порядок выдачи: `_freeCount > 0 ? pop() : _highWater++` — сначала переиспользуем
  освобождённые слоты (LIFO: горячие слоты остаются кэш-локальными, `_highWater` не растёт
  без нужды), и только при пустом free-list берём новый. Альтернатива «сначала новые, free-list
  потом» отвергнута: при churn она разгоняет `_highWater` до ёмкости и удлиняет обходы.
  Детерминизм сохраняется, т.к. порядок `Free` фиксирован.
- Обход «по всем живым» — `for i in 0.._highWater: if IsAlive(i)`. Ветвление дешевле, чем
  поддержка плотного списка живых; при 1M и высоком churn рассмотреть swap-remove-плотность
  **[этап 2]**.
- Переполнение `Generation` (2^31 перевыдач одного слота) считаем невозможным; при переполнении
  `int` поколение перескакивает на 1/2 с сохранением чётности.

### 2.3 Невалидный handle

Handle невалиден, если `IsNull`, либо `Index` вне `[0, Capacity)`, либо
`pool.GenerationOf(Index) != Generation` (слот переиспользован или свободен). Любая публичная
операция с невалидным handle — no-op с инкрементом `SimCounters.InvalidHandleOps`
(плюс `SimEvent.CommandRejected`, если это команда). Исключений нет.

## 3. Фасад `Simulation`

```csharp
namespace Sim.Core.Api;

public sealed class Simulation : IDisposable
{
    public const float Dt = 0.05f;               // с; единственный поддерживаемый шаг
    public const int TicksPerSecond = 20;

    // ---- жизненный цикл ----
    /// Валидирует desc, выделяет ВСЕ буферы по desc.Capacities, строит сеть (генератор или
    /// явная), зоны, автобусные маршруты, засевает PRNG. Единственное место, где разрешены
    /// аллокации и ArgumentException.
    public static Simulation Create(in ScenarioDesc desc);
    /// То же, без исключений (для FFI и для хоста, который хочет код ошибки).
    public static CreateStatus TryCreate(in ScenarioDesc desc, out Simulation? sim);
    public void Dispose();                       // идемпотентен; после него любой вызов — no-op

    // ---- управление ----
    public long Tick { get; }                    // число выполненных тиков с момента Create
    public void Step(int ticks);                 // ticks >= 1; выполняет §7 ticks раз
    public bool Enqueue(in Command cmd);         // false = очередь полна (команда отброшена, счётчик++)
    public int PendingCommands { get; }

    // ---- снапшоты (валидны до следующего Step) ----
    public long SnapshotTick { get; }                               // тик, которому соответствует Vehicles
    public ReadOnlySpan<VehicleRenderData> Vehicles { get; }         // [Capacities.MaxVehicles], индекс = слот
    public ReadOnlySpan<VehicleRenderData> PreviousVehicles { get; } // состояние на SnapshotTick-1
    public int ActiveVehicleCount { get; }

    public ReadOnlySpan<SegmentLoad> SegmentLoads { get; }           // [MaxSegments], индекс = Segment.Index
    public ReadOnlySpan<SignalState> SignalStates { get; }           // [SignalCount], см. §5.4
    public ReadOnlySpan<byte> LaneSignals { get; }                   // [MaxLanes], LaneSignal enum (§5.4)
    public RouteView SelectedRoute { get; }                          // маршрут выбранной машины, §5.5
    public NetworkView Network { get; }                              // статическая геометрия, §5.6
    public ReadOnlySpan<SimEvent> Events { get; }                    // события последнего Step, §5.7

    // ---- метрики ----
    public ref readonly SimCounters Counters { get; }                // монотонные счётчики, §11
    public void GetStats(out SimStats stats);                        // производные значения, §11
    public ReadOnlySpan<uint> NodeThroughput { get; }                // [MaxNodes] машин, проехавших узел (всего)
    public ReadOnlySpan<BusRouteStats> BusRouteStats { get; }        // [MaxBusRoutes]

    // ---- детерминизм ----
    public ulong ComputeStateHash();                                 // §9, O(N), не вызывать каждый тик в проде

    // ---- запросы (read-only, O(1)) ----
    public bool IsAlive(VehicleHandle h);
    public bool TryGetVehicleInfo(VehicleHandle h, out VehicleInfo info);   // для инспектора/HUD
    public bool TryPickVehicle(float x, float z, float radius, out VehicleHandle h); // O(N) по живым, для клика
}

public enum CreateStatus : int
{
    Ok = 0,
    InvalidCapacity = 1,        // ёмкость <= 0 или > лимита реализации
    NetworkTooLarge = 2,        // генератор/явная сеть не влезли в ёмкости
    InvalidVehicleType = 3,
    InvalidNetwork = 4,         // ссылка на несуществующий узел, нулевая длина сегмента и т.п.
    InvalidTransit = 5,
    InvalidDemand = 6,
}
```

Контракт времени: ядро **не измеряет** wall-clock. `Sim.Headless` меряет `Step(1)` своим
`Stopwatch`; клиент Godot вызывает `Step(1)` из своего аккумулятора времени (20 Гц).
`Step(n)` с `n > 1` — просто цикл; снапшот `PreviousVehicles` после него относится к `Tick-1`,
а не к состоянию до вызова.

Потокобезопасность: `Simulation` однопоточный. Чтение спанов и `Step` не должны пересекаться
(клиент читает в том же потоке, где зовёт `Step`). Параллелизм фаз внутри тика — **[этап 2]**,
API это не меняет.

### 3.1 `ScenarioDesc` (входные данные `Create`)

Плоские данные, уже распарсенные хостом (`Sim.Headless` или клиентом) из JSON
(`docs/design/scenario-format.md`). Строк нет: имена типов ТС и зон хост превращает в индексы.
Массивы — обычные `T[]` (в FFI — указатель + длина). Все поля в единицах СИ и тиках.

```csharp
namespace Sim.Core.Api;

public struct ScenarioDesc
{
    public ulong Seed;
    public int DurationTicks;              // информативно для хоста; ядро само не останавливается
    public SimCapacities Capacities;
    public CityGenDesc City;               // City.Enabled == false → сеть берётся из Network
    public NetworkDesc Network;            // явная сеть (тесты, ручные карты); может быть пустой
    public DemandDesc Demand;
    public TransitDesc Transit;
    public RulesDesc Rules;
    public VehicleTypeDesc[] VehicleTypes; // индекс = VehicleType; >= 1 элемент
}

public struct SimCapacities
{
    public int MaxVehicles;                // машины + автобусы
    public int MaxNodes, MaxSegments, MaxLanes;
    public int MaxLanePoints;              // суммарно точек полилиний всех полос
    public int MaxZones, MaxBusRoutes, MaxBusStops;
    public int MaxBusStopsPerRoute;        // по умолчанию 32
    public int MaxRoutes, MaxRouteSteps;   // кэш маршрутов: число маршрутов и суммарно шагов
    public int MaxPendingTrips;            // очередь ожидающих спавна поездок
    public int CommandQueueCapacity;       // по умолчанию 1024
    public int EventCapacity;              // по умолчанию 4096 за Step
    public int PathfindBudgetPerTick;      // запросов A* на тик [шаг 3]
    public int MaxSignalizedNodes;
}

public struct VehicleTypeDesc            // см. scenario-format.md §3 — значения и обоснование
{
    public VehicleCategory Category;       // Car = 0, Bus = 1
    public float Length, Width;            // м
    public float MaxSpeed;                 // м/с, физический предел
    public float SpeedFactor;              // желаемая v0 = min(MaxSpeed, SpeedFactor * limit)
    public float Accel;                    // IDM a, м/с²
    public float ComfortDecel;             // IDM b, м/с²
    public float HeadwayTime;              // IDM T, с
    public float MinGap;                   // IDM s0, м
    public float Delta;                    // IDM δ
    public float Politeness;               // MOBIL p
    public float LaneChangeThreshold;      // MOBIL Δa_th, м/с²
    public float SafeDecel;                // MOBIL b_safe, м/с²
    public float RightBias;                // MOBIL Δa_bias, м/с² (асимметричное правило)
    public int LaneChangeDurationTicks;    // визуальная/логическая длительность манёвра
}

public struct RulesDesc
{
    public float MinPhysicalGap;           // жёсткий минимум между бамперами при клэмпе, м (0.5)
    public int StuckThresholdTicks;        // 60 с → 1200
    public float StuckSpeedThreshold;      // м/с (0.1)
    public float GridlockStuckShare;       // доля stuck среди живых для gridlock_flag (0.05)
    public int GridlockMinStuck;           // абсолютный минимум (10)
    public int GridlockHoldTicks;          // держаться должно столько тиков (1200)
    public int MobilEveryTicks;            // MOBIL считается раз в N тиков на машину (10)
    public int DefaultYellowTicks, DefaultAllRedTicks; // светофоры по умолчанию [шаг 3]
    public float YieldGapTime;             // «уступи дорогу» — временной зазор, с [шаг 3]
}
```

`CityGenDesc`, `NetworkDesc` (`NodeDesc[]`, `SegmentDesc[]`), `DemandDesc` (`ZoneDesc[]`,
`OdPairDesc[]`), `TransitDesc` (`BusRouteDesc[]`) — поля один-в-один с JSON-секциями
`city`, `network`, `demand`, `transit` в `scenario-format.md` (там же — типы и дефолты).
Правило соответствия: JSON `snake_case` ↔ C# `PascalCase`, секунды ↔ тики
(`ticks = round(seconds * 20)`), км/ч ↔ м/с (`/ 3.6`). Конвертацию делает загрузчик, не ядро.

## 4. Команды

### 4.1 Структура

Плоский tagged-union фиксированного размера **64 байта**, blittable, одинаковый для C# и C.

```csharp
namespace Sim.Core.Api;

public enum CommandKind : int
{
    None = 0,
    BuildRoad = 1,              // две точки → сегмент (с привязкой концов к существующим узлам)
    SpawnTrip = 2,              // явная поездка зона→зона (или полоса→зона)
    SetSignalPlan = 3,          // фиксированный план фаз узла [шаг 3]
    CreateBusRoute = 4,         // новый маршрут; остановки — AppendBusStop [шаг 4]
    AppendBusStop = 5,          // добавить остановку к маршруту [шаг 4]
    SpawnBus = 6,               // выпустить автобус на маршрут немедленно [шаг 4]
    SelectVehicle = 7,          // маршрут этой машины попадает в SelectedRoute
    ClearSelection = 8,
    SetTargetVehicleCount = 9,  // целевое число живых машин (спавнер догоняет)
    SetNodeControl = 10,        // тип регулирования узла: none/yield/stop/signal [шаг 3]
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
public struct Command
{
    [FieldOffset(0)] public CommandKind Kind;
    [FieldOffset(4)] public uint Tag;        // произвольная метка клиента, эхо в SimEvent.Tag

    [FieldOffset(8)] public BuildRoadCmd BuildRoad;                   // 24 B
    [FieldOffset(8)] public SpawnTripCmd SpawnTrip;                   // 24 B
    [FieldOffset(8)] public SetSignalPlanCmd SetSignalPlan;           // 32 B
    [FieldOffset(8)] public CreateBusRouteCmd CreateBusRoute;         // 12 B
    [FieldOffset(8)] public AppendBusStopCmd AppendBusStop;           // 16 B
    [FieldOffset(8)] public SpawnBusCmd SpawnBus;                     //  4 B
    [FieldOffset(8)] public SelectVehicleCmd SelectVehicle;           //  8 B
    [FieldOffset(8)] public SetTargetVehicleCountCmd SetTargetVehicleCount; // 4 B
    [FieldOffset(8)] public SetNodeControlCmd SetNodeControl;         //  8 B
    // байты 8..63 — payload (56 B макс.); пересечение полей разрешено: внутри только blittable.
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BuildRoadCmd
{
    public float Ax, Az, Bx, Bz;      // концы оси, м; концы прилипают к узлу в радиусе SnapRadius (5 м)
    public float SpeedLimit;          // м/с
    public byte LanesAB, LanesBA;     // полос в направлении A→B и B→A (0 допустимо для одностороннего)
    public RoadKind Kind;             // Street = 0, Arterial = 1 (ширина, приоритет)
    public byte Flags;                // бит0: не создавать connector'ы автоматически (тесты)
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SpawnTripCmd
{
    public int FromZone;              // индекс зоны или -1, если задан FromLane
    public int ToZone;                // индекс зоны (обязателен)
    public int FromLane;              // индекс полосы или -1
    public float FromS;               // м вдоль FromLane
    public int VehicleType;           // индекс в ScenarioDesc.VehicleTypes
    public int Count;                 // сколько машин поставить в очередь поездок (1..N)
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
public struct SetSignalPlanCmd       // [шаг 3] состав фаз (какие connector'ы зелёные) задаёт спека перекрёстков
{
    public int Node;
    public SignalMode Mode;           // Off = 0, Fixed = 1, FlashYellow = 2 (int)
    public ushort OffsetTicks;        // сдвиг цикла
    public byte PhaseCount;           // 1..8
    public byte Reserved;
    public ushort Green0, Green1, Green2, Green3, Green4, Green5, Green6, Green7; // зелёный по фазам, тики
    public ushort YellowTicks, AllRedTicks;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct CreateBusRouteCmd { public int HeadwayTicks; public int VehicleType; public int Flags; }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct AppendBusStopCmd { public int Route; public int Lane; public float S; public BusStopKind Kind; } // Curb = 0, Bay = 1 (int)

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SpawnBusCmd { public int Route; }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SelectVehicleCmd { public int Index; public int Generation; }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetTargetVehicleCountCmd { public int Count; }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SetNodeControlCmd { public int Node; public NodeControl Control; } // None=0, Yield=1, Stop=2, Signal=3, RightHandRule=4 (int)
```

Для удобства хоста — статические фабрики без аллокаций (префикс `Make`, т.к. имя `Command.BuildRoad` занято полем): `Command.MakeBuildRoad(in BuildRoadCmd, uint tag = 0)`,
`Command.MakeSelectVehicle(VehicleHandle h, uint tag = 0)` и т.д. (возвращают `Command` по значению).

### 4.2 Семантика очереди

- Кольцевой буфер ёмкостью `Capacities.CommandQueueCapacity` (по умолчанию 1024), FIFO.
- `Enqueue` в полную очередь: команда **отбрасывается**, `Counters.CommandsDropped++`,
  возвращается `false`. Ядро никогда не растёт и не блокируется.
- Все команды, лежащие в очереди на момент начала `Step`, применяются в **фазе 1 первого тика**
  этого `Step` строго в порядке поступления; команды, поставленные во время `Step`, невозможны
  (однопоточность). При `Step(n)` все команды применяются на первом из n тиков.
- Каждая команда даёт ровно одно событие: `CommandApplied`/`CommandRejected` с эхом `Tag`
  и кодом причины (`SimEvent.A = (int)RejectReason`), плюс события-следствия
  (`NodeCreated`, `SegmentCreated`, `BusRouteCreated`), из которых клиент узнаёт созданные индексы.
- `BuildRoad` во время движения допустим: только добавление (снос — этап 2). После применения
  `Network.Version++`, кэш маршрутов инвалидируется целиком (**[шаг 3]** — определить дешевле).

## 5. Снапшоты

### 5.1 `VehicleRenderData` — 32 байта

```csharp
namespace Sim.Core.Api;

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 32)]
public struct VehicleRenderData
{
    public float X, Y, Z;            // 0..11  геометрический центр машины, м (S хранит передний бампер: центр = S − Length/2 вдоль полосы)
    public float Heading;            // 12..15 atan2(dz, dx), радианы, [-π, π]
    public float Speed;              // 16..19 м/с (для окраски/анимации колёс)
    public int Index;                // 20..23 слот = индекс в этом же массиве
    public ushort Generation;        // 24..25 младшие 16 бит поколения (для сопоставления prev/curr)
    public byte Type;                // 26     индекс VehicleTypeDesc
    public byte Flags;               // 27     VehicleRenderFlags
    public float LateralOffset;      // 28..31 м, смещение влево(+)/вправо(-) от оси текущей полосы (смена полосы); уже учтено в X,Z — для отладочных оверлеев
}

[Flags] public enum VehicleRenderFlags : byte
{
    None = 0,
    Active = 1,          // слот живой; если 0 — остальные поля не читать
    Braking = 2,         // a < -1 м/с²
    ChangingLane = 4,
    Stuck = 8,
    AtStop = 16,         // автобус стоит на остановке
    Selected = 32,
    OnConnector = 64,
}
```

Массив имеет длину **ровно `MaxVehicles`**, индекс элемента = слот машины; неживые слоты имеют
`Flags == 0` (остальные байты — нули). Такой выбор (а не компактный список) даёт клиенту
бесплатное сопоставление prev/curr по индексу и один `MultiMesh` с `instance_count = MaxVehicles`
(неживые — масштаб 0). Цена: клиент обходит `MaxVehicles` элементов за кадр (1M × 32 B = 32 MB
на буфер — для headless s1m снапшот не читается, см. `Flags` ниже; компактный список —
**[этап 2]** при необходимости, API позволит добавить `ActiveSlots : ReadOnlySpan<int>`).

### 5.2 Двойная буферизация

Ядро держит `_render[2]` и индекс текущего. В фазе 10 каждого тика пишется буфер
`cur = tick & 1`; `Vehicles` → `_render[SnapshotTick & 1]`, `PreviousVehicles` → другой.
`SnapshotTick == Tick`. Интерполяция на клиенте:

```
alpha = clamp((renderTime - timeOf(SnapshotTick)) / Dt, 0, 1)
if prev[i].Flags.Active && cur[i].Flags.Active && prev[i].Generation == cur[i].Generation:
    pos = lerp(prev[i].pos, cur[i].pos, alpha); heading = lerpAngle(...)
else: pos = cur[i].pos    // только что заспавнилась / перевыдан слот
```

При `Step(n>1)` клиент получает prev = tick-1, cur = tick (ядро не хранит более старые буферы).
Снапшот пишется всегда, в том числе в headless (O(N) простых записей, оценочно ~1 % тика);
флаг отключения — **[этап 2]**, если профиль s1m покажет, что это заметно.

### 5.3 Загрузка сегментов (оверлей heatmap)

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
public struct SegmentLoad
{
    public float Density;      // машин на полосо-километр (все направления)
    public float SpeedRatio;   // средняя скорость машин на сегменте / лимит; 1.0 если машин нет
    public ushort Count;       // машин на сегменте сейчас
    public ushort Reserved;
}
```

Индекс = `Segment.Index`, длина `MaxSegments`. Пересчитывается в фазе 9 (O(N машин), без деления
на машину: аккумуляторы `SumSpeed[seg]`, `Count[seg]`, потом один проход по сегментам).

### 5.4 Светофоры (оверлей фаз)

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 2, Size = 8)]
public struct SignalState
{
    public int Node;             // индекс узла
    public byte Phase;           // текущая фаза
    public SignalInterval Interval; // Green = 0, Yellow = 1, AllRed = 2, Off = 3 (byte)
    public ushort TicksToChange;
}
public enum LaneSignal : byte { None = 0, Red = 1, Yellow = 2, Green = 3 }   // LaneSignals[laneIndex]
```

`SignalStates` — массив по регулируемым узлам (длина `Counters.SignalizedNodes`), `LaneSignals` —
по всем полосам (для connector-полос — состояние, для обычных — `None`). Клиент красит
connector'ы. Детали фаз — **[шаг 3]**.

### 5.5 Маршрут выбранной машины — без аллокаций

Маршруты живут в `RoutePool` (§6.5): одном массиве `RouteStep[MaxRouteSteps]`, маршрут = диапазон
`(First, Count)`. Машина хранит `RouteHandle` + `Cursor`. Поэтому «отдать маршрут» = отдать
срез существующего массива:

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 8)]
public struct RouteStep { public int Lane; public int Node; } // полоса (обычная или connector) и узел, в который она ведёт (-1 для последнего)

public readonly ref struct RouteView
{
    public readonly VehicleHandle Vehicle;        // Null, если ничего не выбрано / машина прибыла
    public readonly ReadOnlySpan<RouteStep> Steps; // полный маршрут от начала
    public readonly int Cursor;                   // индекс шага, на котором машина сейчас
    public bool IsEmpty => Steps.Length == 0;
}
```

`RouteStep` — на уровне полос, а не сегментов, чтобы оверлей рисовал реальную траекторию и чтобы
выбор полосы перед поворотом был частью маршрута (**[шаг 3]**: маршрутизатор ищет по графу
направленных сегментов, затем «раскладывает» на полосы; структура шага не меняется).
Если выбранная машина перестроила маршрут, `Steps` указывает на новый диапазон — валидность
среза только до следующего `Step`.

### 5.6 Статическая геометрия для клиента

```csharp
public readonly ref struct NetworkView
{
    public readonly int Version;                        // ++ при каждом изменении сети
    public readonly ReadOnlySpan<NodeData> Nodes;       // [NodeCount]
    public readonly ReadOnlySpan<SegmentData> Segments; // [SegmentCount]
    public readonly ReadOnlySpan<LaneData> Lanes;       // [LaneCount]
    public readonly ReadOnlySpan<Vec2> LanePoints;      // полилинии полос, диапазоны из LaneData
    public readonly ReadOnlySpan<float> LanePointS;     // кумулятивная длина в каждой точке
    public readonly ReadOnlySpan<ZoneData> Zones;
    public readonly ReadOnlySpan<BusStopData> BusStops;
    public readonly ReadOnlySpan<BusRouteData> BusRoutes;
    public readonly ReadOnlySpan<int> BusRouteStops;    // индексы остановок, диапазоны из BusRouteData
}
```

Это те же массивы, что использует ядро (§6), отданные read-only. Клиент перестраивает меши
дорог только при смене `Version`.

### 5.7 События

```csharp
public enum SimEventKind : int
{
    CommandApplied = 1, CommandRejected = 2,
    NodeCreated = 3, SegmentCreated = 4, BusRouteCreated = 5,
    VehicleSpawned = 10, TripCompleted = 11, VehicleStuck = 12, VehicleUnstuck = 13,
    GridlockOn = 20, GridlockOff = 21,
    CapacityExhausted = 30,      // A = (int)CapacityKind, B = сколько отброшено за тик
    EventsOverflow = 31,         // всегда последний элемент, если события не влезли
}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 24)]
public struct SimEvent
{
    public SimEventKind Kind;
    public uint Tag;             // эхо Command.Tag или 0
    public long Tick;
    public int A, B;             // зависят от Kind: индексы/поколения/коды
}
```

Буфер `EventCapacity` очищается в начале каждого `Step`. Переполнение — последняя запись
`EventsOverflow`, остальные теряются (`Counters.EventsDropped += n`).

## 6. Модель данных ядра

Всё — `internal` структуры-хранилища (SoA или AoS) внутри `Simulation`; публичны только
read-only типы `*Data` из §5.6. Массивы выделяются один раз по ёмкостям.

### 6.1 Граф сети

```csharp
namespace Sim.Core.Network;

public enum RoadKind : byte { Street = 0, Arterial = 1 }
public enum NodeControl : int { None = 0, Yield = 1, Stop = 2, Signal = 3, RightHandRule = 4 }
public enum LaneKind : byte { Normal = 0, Connector = 1, BusBay = 2 }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct NodeData
{
    public Vec2 Position;           // ось узла
    public float Elevation;         // Y, этап 1 = 0
    public NodeControl Control;     // по умолчанию из генератора: Signal на артерия×артерия, Yield/Stop на второстепенных, RightHandRule на улица×улица
    public int FirstArm, ArmCount;  // диапазон в NodeArms (сегменты, инцидентные узлу, по часовой стрелке)
    public int FirstConnector, ConnectorCount; // диапазон полос-connector'ов этого узла (подряд в Lanes)
    public int Signal;              // индекс в SignalStates или -1
    public int Zone;                // ближайшая зона (для спавна на краю) или -1
    public int Generation;          // копия из пула, для NodeHandle
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct SegmentData
{
    public int NodeA, NodeB;
    public float Length;            // длина оси, м (lane.Length для обеих сторон = Length — см. 6.2)
    public float SpeedLimit;        // м/с
    public RoadKind Kind;
    public byte LanesAB, LanesBA;   // число полос; 0 допустимо для односторонней
    public byte Reserved;
    public int FirstLaneAB;         // полосы направления A→B: FirstLaneAB .. +LanesAB-1, LaneNo 0 = правая
    public int FirstLaneBA;         // аналогично для B→A
    public int FirstPoint, PointCount; // осевая полилиния (в LanePoints не хранится: SegmentPoints отдельно)
    public int Generation;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct LaneData
{
    public LaneKind Kind;
    public byte LaneNo;             // 0 = крайняя правая (по ходу движения)
    public byte Direction;          // 0 = A→B, 1 = B→A (у connector'а — 0)
    public byte Flags;              // бит0 BusOnly, бит1 NoLaneChangeOut
    public int Segment;             // владелец (Normal/BusBay) или -1
    public int Node;                // владелец (Connector) или -1
    public int FromLane, ToLane;    // только Connector: откуда и куда; иначе -1
    public float Length;            // м; для Normal == Segment.Length (параметризация по оси, см. 6.2)
    public float SpeedLimit;        // м/с (connector: min(limit входа, limit по кривизне) [шаг 3])
    public int FirstPoint, PointCount; // диапазон в LanePoints/LanePointS (>= 2)
    public int Left, Right;         // соседние полосы того же направления (по ходу) или -1; у connector'а -1
    public int FirstNext, NextCount;// исходящие connector'ы (диапазон в LaneNext) — у Normal; у Connector: NextCount == 1, LaneNext[FirstNext] = ToLane
    public int ConflictGroup;       // [шаг 3] индекс набора конфликтов connector'а, иначе -1
    public int Generation;
}
```

Вспомогательные плоские массивы: `NodeArms : int[]` (индексы сегментов), `LaneNext : int[]`
(индексы connector'ов, упорядочены как: прямо, направо, налево, разворот — **[шаг 3]** уточнит),
`SegmentPoints : Vec2[]`.

Connector-полосы: по одной на разрешённый манёвр `(входная полоса → выходная полоса)`. Генерация
по умолчанию (`NetworkBuilder.BuildConnectors(node)`): правая полоса → направо и прямо; левая →
налево и прямо; средние → прямо; на `1+1` одна полоса делает всё. Геометрия — кубическая Безье
`P0 = конец входной, P3 = начало выходной, P1/P2 = P0/P3 ± направление·d`, `d = |P3−P0|/3`,
сэмплируется в `ConnectorSamples` точек (по умолчанию 8). Это даёт `s`-параметризацию
без спец-случаев: connector — такая же полилиния, как и обычная полоса.

### 6.2 Геометрия полосы и параметризация `s`

- Каждая полоса — полилиния `LanePoints[FirstPoint .. +PointCount)` с кумулятивной длиной
  `LanePointS` (`LanePointS[First] = 0`, последняя = `Length`).
- **Соглашение для параллельных полос**: все `Normal`-полосы одного сегмента имеют
  `Length = Segment.Length` (длина оси), а их полилинии — ось, смещённая на
  `±(LaneNo + 0.5) * LaneWidth`. Фактическая длина смещённой полилинии отличается на кривых,
  но `s` всех полос сегмента **одинаково параметризована** через ось. Это делает соседей
  на смежной полосе сравнимыми по `s` напрямую (MOBIL без проекций) и делает смену полосы
  переносом `s` без пересчёта. Погрешность скорости на кривых ≤ `LaneWidth/R`, для городских
  радиусов ≥ 20 м — ≤ 20 %, принято для этапа 1.
- `LaneGeometry.Evaluate(int lane, float s, ref int pointHint, out Vec2 pos, out Vec2 dir)`:
  идёт от `pointHint` вперёд/назад, пока `LanePointS[hint] <= s < LanePointS[hint+1]`.
  Машина хранит `PointHint` → амортизированно O(1). Для `Y` на этапе 1 — `Node.Elevation` = 0.

### 6.3 Машины — SoA (`Sim.Core.Agents.VehicleStore`)

Один слот на машину, все массивы длиной `MaxVehicles`, индекс = `VehicleHandle.Index`.
Группировка по фазам доступа (кэш-локальность горячих полей IDM):

```csharp
namespace Sim.Core.Agents;

internal sealed class VehicleStore
{
    public readonly HandlePool Pool;

    // --- горячие, читаются IDM каждый тик ---
    public readonly int[]   Lane;          // текущая (целевая при смене) полоса
    public readonly float[] S;             // позиция ПЕРЕДНЕГО бампера вдоль полосы, м
    public readonly float[] Speed;         // v >= 0
    public readonly float[] Accel;         // a, результат фазы 5, применяется в 6
    public readonly float[] Length;        // копия из типа (чтобы не ходить в VehicleTypes)
    public readonly byte[]  Type;
    public readonly float[] DesiredSpeed;  // v0 на текущей полосе = min(MaxSpeed, SpeedFactor*limit); пересчёт при смене полосы

    // --- порядок на полосе (intrusive double-linked list, §6.4) ---
    public readonly int[] Ahead, Behind;          // соседи на Lane; -1 = нет
    public readonly int[] GhostLane;              // полоса-источник во время смены или -1
    public readonly int[] GhostAhead, GhostBehind;// соседи в списке GhostLane

    // --- смена полосы ---
    public readonly sbyte[] LaneChangeDir;        // -1 вправо, +1 влево, 0 нет
    public readonly ushort[] LaneChangeTicks;     // прошло тиков манёвра
    public readonly ushort[] NextMobilTick;       // тик % MobilEveryTicks, когда считать MOBIL (разнесено по индексу)

    // --- маршрут ---
    public readonly int[] Route;                  // RouteHandle.Index или -1 (нет маршрута: едет по умолчанию прямо)
    public readonly int[] RouteGen;
    public readonly int[] RouteCursor;
    public readonly int[] DestZone;
    public readonly int[] DestLane; public readonly float[] DestS;   // точка прибытия

    // --- геометрия/рендер ---
    public readonly int[] PointHint;
    public readonly float[] Lateral;              // текущее поперечное смещение, м (анимация смены полосы)

    // --- метрики/состояние ---
    public readonly long[] SpawnTick;
    public readonly ushort[] StillTicks;          // тиков подряд с |Δs| < StuckSpeedThreshold*Dt
    public readonly byte[]  Flags;                // VehicleFlags: Stuck, AtStop, Selected, IsBus...
    public readonly int[]   BusRoute;             // [шаг 4] маршрут автобуса или -1
    public readonly int[]   BusNextStop;          // [шаг 4] порядковый номер следующей остановки
    public readonly ushort[] DwellTicks;          // [шаг 4] остаток стоянки
    public readonly ushort[] Passengers;          // [шаг 4]
}
```

Тип `VehicleInfo` (для `TryGetVehicleInfo`) — публичная копия подмножества полей одной машины
(lane, s, speed, accel, route cursor, dest zone, flags, spawn tick).

### 6.4 «Машина на полосе»: порядок и поиск лидера за O(1)

Каждая полоса хранит `LaneHead[lane]` (машина с наибольшим `S`, ближе всего к концу) и
`LaneTail[lane]` (наименьший `S`) и `LaneCount[lane]`. Машины связаны через `Ahead/Behind`.
Инвариант: `S[Ahead[i]] >= S[i]` для всех `i` на одной полосе (нестрого — одинаковые `S`
возможны только в момент спавна и разрешаются порядком вставки).

Операции (все O(1), кроме вставки в середину):
- **лидер** `i` = `Ahead[i]`; если `-1` — лидер ищется на следующей полосе маршрута:
  `LaneTail[nextLane]` (расстояние = `Length[lane] − S[i] + S[tail]`), и если там пусто — ещё одна
  полоса вперёд (всего глубина просмотра `LookaheadLanes = 2`, дальше считаем дорогу свободной).
- **въезд на полосу с начала** (переход `s ≥ Length`): `Unlink(i)` из старой, `PushTail(i)` в новую
  (машина входит с `s' = s − Length` — всегда позади всех, т.к. остальные уже проехали дальше).
  Порядок переходов внутри тика — по возрастанию индекса машины; две машины, одновременно
  попавшие на одну полосу из разных connector'ов — **[шаг 3]** правило «не въезжай, если выезд
  занят» гарантирует зазор; на этапе 2 (без перекрёстков) сегменты соединяются 1:1.
- **смена полосы** (MOBIL решил): соседи на целевой полосе уже найдены (ниже) →
  `InsertBetween(i, newAhead, newBehind)` O(1). Машина **одновременно** остаётся в списке
  исходной полосы как ghost (`GhostLane/GhostAhead/GhostBehind`) до конца манёвра
  (`LaneChangeDurationTicks`), поэтому следующие сзади на обеих полосах видят её как препятствие —
  это даёт I6 без 2D-коллизий. По завершении — `GhostUnlink(i)`.
- **деспаун**: `Unlink(i)` (+ `GhostUnlink`), `Pool.Free`.

Поиск соседей на смежной полосе для MOBIL (`FindNeighbours(target, s, out ahead, out behind)`):
полосы одного сегмента имеют общую параметризацию `s` (§6.2), поэтому идём по списку целевой
полосы от `LaneTail` вверх, пока `S[cur] < s` — это `behind`, следующий — `ahead`. Сложность
O(k), k ≤ машин на полосе (городской сегмент 100–300 м → ≤ 40 машин); MOBIL вызывается раз в
`MobilEveryTicks = 10` тиков на машину (разнесено по индексу, `(tick + i) % 10 == 0`), поэтому
амортизированная стоимость ≪ IDM. Оптимизация **[этап 2]**: подсказка `LateralHint[i]` —
последний найденный сосед, от которого идти в обе стороны.

### 6.5 Маршруты (`Sim.Core.Routing.RoutePool`) — хранилище, без алгоритма

```csharp
internal sealed class RoutePool
{
    public readonly HandlePool Pool;                   // MaxRoutes
    public readonly int[] First, Count;                // диапазон в Steps
    public readonly int[] RefCount;                    // машин, использующих маршрут (кэш по OD-паре делит один маршрут)
    public readonly int[] FromZone, ToZone;            // ключ кэша; -1 для индивидуальных маршрутов
    public readonly int[] NetworkVersion;              // при каком Network.Version построен
    public readonly RouteStep[] Steps;                 // MaxRouteSteps, арена с bump-аллокатором и компактификацией
    public int StepsUsed;
}
```

Арена `Steps` — bump-аллокация; когда свободного хвоста не хватает, выполняется компактификация
живых маршрутов (O(StepsUsed), редкая, детерминированная, в фазе 3). Кэш OD-пар — открытая
хэш-таблица фиксированного размера `MaxRoutes * 2` на массивах `int[]` (ключ `from*MaxZones+to`),
вытеснение — по `RefCount == 0` и возрасту. Алгоритм A*, бюджет и веса — **[шаг 3]**;
здесь фиксируется только то, что маршрут = `RouteStep[]` на уровне полос, доступен без аллокаций.

### 6.6 Зоны, поездки, транспорт — структуры

```csharp
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct ZoneData
{
    public Vec2 Center; public float HalfSize;      // квадратная ячейка
    public float Weight;                            // вес генерации/притяжения
    public int FirstSpawnLane, SpawnLaneCount;      // полосы, где можно появиться (диапазон в ZoneLanes)
    public byte IsExternal;                         // краевая зона (машины появляются «из-за карты»)
    public byte Reserved0, Reserved1, Reserved2;
}

internal struct PendingTrip { public int FromZone, ToZone, FromLane; public float FromS; public byte Type; } // очередь MaxPendingTrips, FIFO

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BusStopData { public int Lane; public float S; public BusStopKind Kind; public int Route; public ushort Waiting; public ushort Reserved; public long LastArrivalTick; }

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BusRouteData { public int FirstStop, StopCount; public int HeadwayTicks; public int VehicleType; public int ActiveBuses; public long LastDispatchTick; public int Generation; }
```

Правила стоянки, посадки, кармана — **[шаг 4]**; поля `Waiting`, `DwellTicks`, `Passengers`
зарезервированы, чтобы спека шага 4 не меняла раскладку.

### 6.7 Сигналы (**[шаг 3]**, хранилище)

`SignalStore`: по регулируемым узлам — `Node`, `Mode`, `PhaseCount`, `GreenTicks[8]`,
`YellowTicks`, `AllRedTicks`, `Phase`, `Interval`, `TicksLeft`; по фазам — битовая маска
connector'ов узла (`ulong`, ≤ 64 connector'ов на узел — предел зафиксирован). Состав фаз по
умолчанию (противоположные рукава вместе) — спека перекрёстков.

## 7. Порядок обновления тика

`Step(1)` выполняет фазы строго в этом порядке; каждая фаза — отдельный метод
`internal static void PhaseX(ref World w)` в своём файле, чтобы агенты работали параллельно.
Обход сущностей внутри фазы — по возрастанию индекса (слоты `0.._highWater`), пропуск неживых.

| # | Фаза | Читает | Пишет | Файл |
|---|------|--------|-------|------|
| 1 | **Команды**: применить всю очередь FIFO, события `CommandApplied/Rejected` | CommandQueue | Network, Signals, Transit, Selection, Events | `Api/CommandDispatcher.cs` |
| 2 | **Спавн**: (a) генератор OD: пока `Alive < Target` и `Pending` не полна — сгенерировать поездку (PRNG `Spawn`), ramp по `SpawnRampTicks`; (b) автобусы: по `HeadwayTicks` выпустить на маршрут **[шаг 4]**; (c) из `PendingTrips` FIFO ставить машины на `FromLane` при `s=Length_type+MinGap`, если `LaneTail` достаточно далеко (`S[tail] − L_tail ≥ s + MinGap`), иначе поездка остаётся в очереди | Zones, Lanes, Pending | VehicleStore, LaneLists | `Agents/Spawner.cs` |
| 3 | **Маршруты**: машины без маршрута (`Route == -1`) запрашивают его из кэша, при промахе — A* в пределах `PathfindBudgetPerTick`; остальные ждут (стоят на старте, не считаются stuck пока `Route == -1`) **[шаг 3]**; компактификация арены при нехватке | Network, RoutePool | RoutePool, VehicleStore.Route* | `Routing/RoutePhase.cs` |
| 4 | **Перекрёстки и сигналы**: таймеры фаз, `LaneSignals`; для каждого connector'а — флаг «можно въезжать» (сигнал, yield/stop, помеха справа, выезд занят) **[шаг 3]**; на шаге 2 фаза пустая | Signals, LaneLists | Signals, LaneSignals, ConnectorOpen[] | `Rules/Intersections/*.cs` |
| 5 | **Поведение (read-only по состоянию)**: для каждой машины — IDM-ускорение по лидеру (§6.4) и по виртуальному препятствию (закрытый connector → стоп-линия в конце полосы; остановка автобуса; конец маршрута); каждые `MobilEveryTicks` — решение о смене полосы (записывается только `LaneChangeDir`-намерение). Пишет **только** `Accel[]` и `LaneChangeDir[]`: это делает фазу параллелизуемой по машинам **[этап 2]** | S, Speed, Lane, списки, Signals | Accel, LaneChangeDir | `Rules/Idm.cs`, `Rules/Mobil.cs`, `Rules/BehaviourPhase.cs` |
| 6 | **Интеграция (write)**: `v' = max(0, v + a·dt)`; `s' = s + v'·dt`; **клэмп I6** (§7.6); `Lateral` анимация; `StillTicks` | Accel | S, Speed, Lateral | `Agents/IntegratePhase.cs` |
| 7 | **Переходы**: (a) начало смены полосы для `LaneChangeDir != 0` — вставка в целевую, ghost в исходной; завершение манёвров; (b) `s' ≥ Length` → перенос на следующую полосу маршрута (`RouteCursor++`, `s' −= Length`, PushTail, пересчёт `DesiredSpeed`, `PointHint = 0`); фиксация `NodeThroughput[node]++` при выходе с connector'а | S, Route | Lane, списки, RouteCursor | `Agents/TransitionPhase.cs` |
| 8 | **Прибытие/деспаун**: `Lane == DestLane && S >= DestS` → `TripCompleted`, счётчики времени поездки, `Free`; автобус на конечной — **[шаг 4]**. Деспаун застрявших запрещён | S, Dest | VehicleStore, Counters, Events | `Agents/ArrivalPhase.cs` |
| 9 | **Метрики**: stuck/unstuck (§11), gridlock, `SegmentLoads`, `BusRouteStats`, счётчики | всё | Counters, SegmentLoads | `Metrics/MetricsPhase.cs` |
| 10 | **Снапшот**: заполнить `_render[tick & 1]` (позиция через `LaneGeometry.Evaluate` с `PointHint` + `Lateral`), `SelectedRoute` | S, Lane, Lateral | RenderBuffers | `Api/SnapshotPhase.cs` |

`Tick++` — после фазы 10. События фаз 2–9 попадают в `Events` того же `Step`.

### 7.6 Гарантия «не проходят сквозь друг друга» (I6)

Пусть `L` — лидер машины `i` на начало фазы 6 (на той же полосе — `Ahead[i]`, иначе tail
следующей полосы; ghost-машины учитываются). IDM в непрерывном времени не даёт столкновений,
но дискретный шаг и переходы между полосами — дают. Поэтому в фазе 6:

```
sMax = S_old[L] − Length[L] − MinPhysicalGap       (в координатах полосы i; через границу полосы: + Length[lane_i])
s'   = min(s + v'·dt, sMax)
v'   = (s' − s) / dt                                 (если клэмп сработал, v' уменьшается; v' ≥ 0 т.к. sMax ≥ s по инварианту)
Counters.HardClamps++ при срабатывании
```

Используется позиция лидера **на начало тика** (`S_old`): лидер за тик только увеличивает `s`
(нет движения назад: `v ≥ 0`), поэтому клэмп по `S_old` всегда консервативен и **не зависит от
порядка обработки** — фаза 6 параллелизуема. Инвариант `sMax ≥ s` держится индуктивно: при спавне
и вставке в середину полосы (смена полосы) проверяется зазор `≥ MinPhysicalGap` с обеих сторон,
иначе манёвр/спавн откладывается. Тест: прогон s1k 36 000 тиков, после каждого тика проверка
`S[Ahead[i]] − Length[Ahead[i]] ≥ S[i]` для всех машин (в тесте — O(N) проверка, не в ядре).

## 8. PRNG — PCG32

Выбор: PCG32 (XSH-RR, 64-бит состояние, 64-бит инкремент) — 2 умножения на число, тривиально
портируется в Rust (`rand_pcg::Pcg32` даёт побитово те же значения при тех же seed/stream),
независимые подпотоки через инкремент. xoshiro128** отвергнут: нет «потоков», нужен отдельный
SplitMix для сидирования.

```csharp
namespace Sim.Core.Util;

public struct Pcg32
{
    private ulong _state, _inc;      // _inc нечётный

    private const ulong Mult = 6364136223846793005UL;

    /// Стандартная инициализация PCG (pcg32_srandom_r): inc = (stream << 1) | 1; state = 0; step; state += seed; step.
    public static Pcg32 Create(ulong seed, ulong stream);

    public uint NextUInt()                              // pcg32_random_r
    {
        ulong old = _state;
        _state = old * Mult + _inc;
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }
    public uint NextBelow(uint bound) => (uint)(((ulong)NextUInt() * bound) >> 32); // умножение-сдвиг; смещение ≤ 2^-32, принято ради портируемости и отсутствия циклов
    public int  NextInt(int minInclusive, int maxExclusive) => minInclusive + (int)NextBelow((uint)(maxExclusive - minInclusive));
    public float NextFloat01() => (NextUInt() >> 8) * (1.0f / 16777216.0f);           // [0,1), 24 бита
    public float NextRange(float min, float max) => min + (max - min) * NextFloat01();
    public ulong State => _state; public ulong Inc => _inc;                           // для хэша и дампа
}
```

Сидирование: `ScenarioDesc.Seed` → подпотоки с фиксированными `stream`-номерами
(`RngStream` enum): `CityGen = 1`, `Zones = 2`, `Spawn = 3` (OD-пары, тип ТС, стартовая полоса),
`Behaviour = 4` (зарезервировано: шум в параметрах водителей — этап 2), `Transit = 5`,
`Routing = 6` (разрешение ничьих). Каждая подсистема владеет своим `Pcg32` в фиксированном поле
`World` — никаких общих генераторов, чтобы добавление вызова в одной подсистеме не меняло
поток другой. Генератор города использует только `CityGen`, поэтому сеть зависит от seed и
`CityGenDesc`, но не от числа машин.

## 9. Хэш состояния — FNV-1a 64

Выбор: FNV-1a 64 (одно умножение на байт, 5 строк кода, идентично на Rust). xxHash64 быстрее,
но хэш нужен только в тестах/compare, а не каждый тик. Хэшируются **битовые образы** `float`
(`BitConverter.SingleToInt32Bits`) — никаких округлений.

```csharp
namespace Sim.Core.Util;

public struct Fnv1a64
{
    public const ulong Offset = 14695981039346656037UL, Prime = 1099511628211UL;
    private ulong _h;
    public static Fnv1a64 Create() => new() { _h = Offset };
    public void Add(byte b)  { _h = (_h ^ b) * Prime; }
    public void Add(int v)   { Add((byte)v); Add((byte)(v >> 8)); Add((byte)(v >> 16)); Add((byte)(v >> 24)); }   // little-endian
    public void Add(uint v); public void Add(long v); public void Add(ulong v);
    public void Add(float v) => Add(BitConverter.SingleToInt32Bits(v));
    public ulong Value => _h;
}
```

Порядок включения (фиксирован, менять только с правкой этого раздела):
1. `Tick`, `Network.Version`.
2. Состояния всех PRNG в порядке `RngStream` (`State`, `Inc`).
3. Машины по слотам `0.._highWater`: для живых — `Generation, Lane, S, Speed, Accel, Type, Route,
   RouteCursor, LaneChangeDir, GhostLane, Flags, StillTicks`; для неживых — только `Generation`.
4. По полосам `0..LaneCount`: `LaneHead, LaneTail, LaneCount`.
5. Сигналы: `Phase, Interval, TicksLeft` по регулируемым узлам.
6. Transit: по остановкам `Waiting, LastArrivalTick`; по маршрутам `ActiveBuses, LastDispatchTick`.
7. `SimCounters` целиком, по полям в порядке объявления.
8. Очередь поездок `PendingTrips` (содержимое FIFO в порядке очереди).

Не входят: снапшоты, события, `SegmentLoads` (производные), геометрия сети (детерминирована
seed'ом; при желании — отдельный `ComputeNetworkHash()`, не требуется этапом 1).
Требование: `float`-арифметика в ядре без `MathF.FusedMultiplyAdd` и без зависимости от
вектора SIMD (JIT может менять порядок) — только скалярные операции в фиксированном порядке;
результат должен совпадать x64 ↔ arm64 на .NET (IEEE-754, без x87). Тест детерминизма:
два `Simulation` из одного `ScenarioDesc`, 2 000 тиков, равные хэши после каждых 100 тиков.

## 10. Аллокации и ёмкости

- `Create` выделяет все массивы по `SimCapacities`; `Step` не аллоцирует. Тест: `GC.GetAllocatedBytesForCurrentThread()`
  до/после 1 000 тиков в стационаре == 0 (первые 200 тиков прогрева исключены: JIT-тиринг).
- Исчерпание ёмкости — **никогда не исключение**, а предсказуемое поведение + счётчик + событие
  `CapacityExhausted(kind)`:

| Ёмкость | Что при нехватке |
|---------|------------------|
| `MaxVehicles` | поездка остаётся в `PendingTrips`; `Counters.SpawnDeferredTicks++` |
| `MaxPendingTrips` | новая поездка отбрасывается, `TripsDropped++` |
| `MaxNodes/Segments/Lanes/LanePoints` | `BuildRoad` → `CommandRejected(Reason = Capacity)`; в `Create` — `CreateStatus.NetworkTooLarge` |
| `MaxRoutes/MaxRouteSteps` | вытеснение маршрутов с `RefCount == 0`; если нет — машина ждёт следующего тика (`RouteDeferred++`) |
| `CommandQueueCapacity` | `Enqueue` → false, `CommandsDropped++` |
| `EventCapacity` | `EventsOverflow` |
| `PathfindBudgetPerTick` | остаток ждёт следующего тика (нормальный режим, не ошибка) |

- Память на машину (цель `bytes_per_vehicle` в бенче): поля §6.3 ≈ 100 B + 2 × 32 B снапшота ≈
  **164 B** + доля маршрутов. Для 1M → ~170 MB, приемлемо; геометрия сети — отдельно.
- Все `internal` хранилища — `sealed class` с `readonly` массивами (один `new T[]` на массив в
  `Create`). Никаких `List<T>`, `Dictionary<,>`, `Queue<T>` в ядре — только массивы и свои
  кольцевые буферы (`Util/RingBuffer<T>`, `Util/FixedHashMap`).

## 11. Метрики

Ядро копит только то, что не требует wall-clock; производные значения — `GetStats`.

```csharp
namespace Sim.Core.Api;

[StructLayout(LayoutKind.Sequential)]
public struct SimCounters                 // все монотонные, кроме явно помеченных "текущее"
{
    public long Ticks;
    public long VehiclesSpawned, TripsCompleted, TripsDropped;
    public long TripTimeTicksTotal;       // сумма длительностей завершённых поездок
    public long DistanceTotalMm;          // пройденный путь всеми, мм (целое → детерминированный хэш; накапливается как round(v·dt·1000))
    public long VehicleTicksTotal;        // Σ живых машин по тикам (для средней скорости: Distance / (VehicleTicks·dt))
    public long MovingVehicleTicksTotal;  // то же, только v > StuckSpeedThreshold
    public int  ActiveVehicles;           // текущее
    public int  StuckVehicles;            // текущее
    public long StuckEventsTotal;         // переходов в stuck
    public long StuckTicksTotal;          // Σ тиков в состоянии stuck
    public int  GridlockFlag;             // текущее: 0/1
    public long GridlockTicksTotal;
    public long LaneChanges, LaneChangesAborted;
    public long HardClamps;               // срабатываний клэмпа I6 (диагностика: должно быть мало)
    public long PathfindQueries, PathfindCacheHits, RouteDeferred;
    public long PathfindExpandedNodes;    // Σ раскрытых узлов A* (замена "времени" поиска)
    public long ConnectorEntries;         // Σ проездов через connector'ы (= Σ NodeThroughput)
    public long CommandsApplied, CommandsRejected, CommandsDropped, EventsDropped;
    public long SpawnDeferredTicks, InvalidHandleOps;
    public int  SignalizedNodes, NodeCount, SegmentCount, LaneCount, ZoneCount;  // текущее
    public long BusDispatched, BusStopArrivals;
    public long BusHeadwayDeviationTicksTotal;   // Σ |факт − план| по прибытиям на остановки [шаг 4]
    public long BusPassengersBoarded;
}

public struct SimStats                    // производные, считаются в GetStats за O(1) из Counters
{
    public float AvgSpeedKmh;             // DistanceTotalMm / 1000 / (VehicleTicksTotal·Dt) · 3.6
    public float AvgTripTimeS;            // TripTimeTicksTotal·Dt / TripsCompleted
    public float TripsCompletedPerMin;    // TripsCompleted / (Ticks·Dt/60)
    public int   StuckVehicles;
    public int   GridlockFlag;            // 0/1 (int, а не bool: blittable-раскладка для FFI)
    public float BusHeadwayDeviationS;    // BusHeadwayDeviationTicksTotal·Dt / BusStopArrivals
    public float PathfindCacheHitRate;
    public float HardClampsPerVehicleMin;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct BusRouteStats { public int ActiveBuses; public long Arrivals; public long HeadwayDeviationTicksTotal; public long LastDispatchTick; }
```

Определения:
- **stuck**: `StillTicks[i] >= StuckThresholdTicks` (1200 = 60 с), где `StillTicks` растёт, если за
  тик `v' < StuckSpeedThreshold` (0,1 м/с), и сбрасывается в 0 иначе. Исключения: автобус с
  `DwellTicks > 0` (стоянка — не застревание) и машина с `Route == -1` (ждёт маршрут — считается
  в `RouteDeferred`, а не stuck). Ожидание на красном **считается**: при корректных планах
  (цикл ≤ 120 с, зелёный ≥ 10 с) очередь за 60 с должна сдвинуться хотя бы на одну машину;
  если нет — это и есть проблема трафика, которую метрика должна показать. Событие `VehicleStuck`
  при переходе, `VehicleUnstuck` при сбросе.
- **gridlock_flag**: `StuckVehicles ≥ max(GridlockMinStuck, GridlockStuckShare · ActiveVehicles)`
  непрерывно в течение `GridlockHoldTicks` (60 с). Снимается, когда условие ложно 60 с.
- **throughput перекрёстков**: `NodeThroughput[node]++` в фазе 7 при переходе с connector'а на
  выходную полосу. Headless делит на минуты прогона и сортирует топ-10 (сортировка — вне ядра).
- **bus headway**: на каждой остановке `LastArrivalTick`; при прибытии автобуса
  `dev = |tick − LastArrivalTick − HeadwayTicks|` (первое прибытие не учитывается),
  `BusHeadwayDeviationTicksTotal += dev`, `BusStopArrivals++` **[шаг 4]**.
- `pathfind_ms_p95` и `tick_ms` — измеряет `Sim.Headless` (оборачивает `Step`; для pathfind —
  **[шаг 3]**: ядро отдаёт `PathfindQueries`/`PathfindExpandedNodes` за тик, headless считает
  время фазы через `Counters` до/после — либо, если понадобится точность, добавим
  `Simulation.StepPhased(Phase)` для профилирования; не для продакшена).

## 12. Отображение на C ABI (будущий FFI)

Все публичные struct из `Sim.Core.Api` blittable с явным `Pack`/`Size` — они и есть C-структуры
(`tc_` префикс). Handle = `struct { int32_t index; int32_t generation; }`. `ReadOnlySpan<T>` =
`(const T* ptr, int32_t len)`. Ошибки — коды, не исключения.

```c
typedef struct tc_sim tc_sim;                                   // opaque
int32_t  tc_sim_create(const tc_scenario_desc* desc, tc_sim** out);   // CreateStatus
void     tc_sim_destroy(tc_sim*);
void     tc_sim_step(tc_sim*, int32_t ticks);
int32_t  tc_sim_enqueue(tc_sim*, const tc_command* cmd);        // 1 = ok, 0 = dropped
int64_t  tc_sim_tick(const tc_sim*);
const tc_vehicle_render_data* tc_sim_vehicles(const tc_sim*, int32_t* out_len);
const tc_vehicle_render_data* tc_sim_vehicles_prev(const tc_sim*, int32_t* out_len);
const tc_segment_load*  tc_sim_segment_loads(const tc_sim*, int32_t* out_len);
const tc_signal_state*  tc_sim_signal_states(const tc_sim*, int32_t* out_len);
const uint8_t*          tc_sim_lane_signals(const tc_sim*, int32_t* out_len);
const tc_route_step*    tc_sim_selected_route(const tc_sim*, int32_t* out_len, int32_t* out_cursor, tc_vehicle_handle* out_vehicle);
const tc_sim_event*     tc_sim_events(const tc_sim*, int32_t* out_len);
void     tc_sim_network(const tc_sim*, tc_network_view* out);   // struct из пар ptr+len
void     tc_sim_counters(const tc_sim*, tc_sim_counters* out);
void     tc_sim_stats(const tc_sim*, tc_sim_stats* out);
uint64_t tc_sim_state_hash(const tc_sim*);
```

`tc_scenario_desc` — та же структура, где `T[]` заменены на `(const T*, int32_t)`; строк нет.
Указатели из `tc_sim_*` валидны до следующего `tc_sim_step`/`tc_sim_destroy`. В C#-версии
для Godot ядро вызывается напрямую (ProjectReference), FFI не нужен; слой `extern "C"` появится
вместе с Rust-ядром, а C#-клиент перейдёт на `[DllImport]`-обёртку с той же формой вызовов.

## 13. Расположение файлов и namespace

```
src/Sim.Core/
  Api/            namespace Sim.Core.Api        — всё публичное: Simulation.cs (фасад, Step-цикл), ScenarioDesc.cs,
                                                  Handles.cs, Command.cs, CommandDispatcher.cs (фаза 1), Snapshots.cs
                                                  (VehicleRenderData, SegmentLoad, SignalState, RouteView, NetworkView),
                                                  SimEvent.cs, SimCounters.cs, SnapshotPhase.cs (фаза 10), VehicleInfo.cs
  World/          namespace Sim.Core.World      — World.cs: internal sealed class, композиция всех хранилищ + PRNG; WorldBuilder.cs (Create: ёмкости, порядок инициализации)
  Network/        namespace Sim.Core.Network    — NodeData/SegmentData/LaneData, NetworkStore.cs, NetworkBuilder.cs (AddNode/AddSegment/BuildConnectors/snap), LaneGeometry.cs
  Gen/            namespace Sim.Core.Gen        — CityGenerator.cs (2.2), ZoneGenerator.cs
  Agents/         namespace Sim.Core.Agents     — VehicleStore.cs, LaneOccupancy.cs (списки §6.4), Spawner.cs (фаза 2), IntegratePhase.cs (6), TransitionPhase.cs (7), ArrivalPhase.cs (8), PendingTrips.cs
  Rules/          namespace Sim.Core.Rules      — Idm.cs, Mobil.cs, BehaviourPhase.cs (5); Rules/Intersections/ — SignalStore.cs, SignalPhase.cs, Priority.cs (шаг 3)
  Routing/        namespace Sim.Core.Routing    — RoutePool.cs, RouteCache.cs, AStar.cs (шаг 3), RoutePhase.cs (3)
  Transit/        namespace Sim.Core.Transit    — TransitStore.cs (маршруты/остановки), BusDispatch.cs, BusStopPhase.cs (шаг 4)
  Metrics/        namespace Sim.Core.Metrics    — MetricsPhase.cs (9), StateHash.cs
  Util/           namespace Sim.Core.Util       — Pcg32.cs, Fnv1a64.cs, HandlePool.cs, RingBuffer.cs, FixedHashMap.cs, Vec2.cs, FixedMath.cs
```

`Vec2`: `public readonly struct Vec2 { public readonly float X, Z; public Vec2(float x, float z); }` (имена полей `X`/`Z`
намеренно — плоскость земли Godot). Публичные `RoadKind`, `NodeControl`, `LaneKind` и `*Data` из §6.1/§6.6 физически лежат в `Api/NetworkData.cs` (namespace `Sim.Core.Network`), т.к. на них ссылаются `Command` и `NetworkView`. `FixedHashMap` — `long→int`, `TrySet/TryGet/Remove`. Публичные типы — только в `Api/`, `Network/*Data`,
`Util/Pcg32|Fnv1a64|Vec2`; остальное `internal` (+ `InternalsVisibleTo("Sim.Core.Tests")`,
`"Sim.Bench"`).

Файлы фаз независимы: каждая фаза — `internal static class XxxPhase { public static void Run(World w) }`.
`Simulation.Step` — только последовательность вызовов `Run`. Агент, реализующий фазу, трогает
только свою папку и, при необходимости новых полей, — `VehicleStore`/`World` через отдельный
маленький коммит с пометкой в брифе.

## 14. Порядок реализации и параллелизация

| ID | Задача | Папки | Зависит от | Параллельно с |
|----|--------|-------|------------|---------------|
| A | Util: `Pcg32` (тест-вектора против референсного pcg32 C), `Fnv1a64`, `HandlePool`, `RingBuffer`, `FixedHashMap`, `Vec2` | Util/ | — | B, C |
| B | Api-типы: handles, `Command` (+фабрики, тест размера 64 B и `Marshal.SizeOf`), `ScenarioDesc` и вложенные desc, `VehicleRenderData` (тест 32 B), `SimEvent`, `SimCounters/SimStats`, `CreateStatus` | Api/ | — | A, C |
| C | Network: `*Data`, `NetworkStore`, `NetworkBuilder` (AddNode/AddSegment со snap, полосы с офсетом, connector'ы Безье), `LaneGeometry.Evaluate`; тесты геометрии | Network/ | B (типы) | A, D |
| D | Agents-хранилище: `VehicleStore`, `LaneOccupancy` (PushTail/Unlink/InsertBetween/Ghost, инвариант порядка — property-тест), `PendingTrips` | Agents/ | A | C |
| E | `World` + `Simulation` фасад: `Create/TryCreate` (валидация, ёмкости), `Step` с пустыми фазами, очередь команд + `CommandDispatcher` (BuildRoad, SelectVehicle, SetTargetVehicleCount), события, снапшот (фаза 10), `StateHash`, `Dispose`; тесты: 0 B/тик, детерминизм хэша, переполнение очереди | World/, Api/, Metrics/StateHash | A, B, C, D | F, G |
| F | `Idm`, `Mobil`, `BehaviourPhase`, `IntegratePhase`, `TransitionPhase` (без перекрёстков: полосы соединены 1:1 connector'ами «прямо»), `ArrivalPhase`; тест I6 на прямой и кольце; микробенч IDM | Rules/, Agents/ | D (C для фикстуры) | E, G |
| G | `CityGenerator`, `ZoneGenerator` (fable, спека 2.2) | Gen/ | C | E, F |
| H | `Spawner` + OD-генератор + `MetricsPhase` (stuck/gridlock/SegmentLoads) | Agents/, Metrics/ | E, F | I |
| I | Headless-загрузчик JSON → `ScenarioDesc` (в `Sim.Headless`, System.Text.Json разрешён) + `run/bench` | src/Sim.Headless | B | C–H |
| J | **[шаг 3]** Intersections (спека fable → sonnet), Routing A*/кэш | Rules/Intersections, Routing/ | E, F, G | — |
| K | **[шаг 4]** Transit | Transit/ | J | — |

Критическая цепочка: A/B → C/D → E → F → H. Задачи A, B, C, D, G, I можно раздать одновременно
(разные папки). E и F — одновременно, если F работает на фикстуре `World` с ручной сетью из
`NetworkBuilder` (без фасада). Правило: один агент — одна папка; изменения в `VehicleStore`/`World`
согласуются через этот документ (новое поле → строка в §6.3).

## 15. Открытые вопросы (не блокируют шаг 2)

1. Компактный список живых слотов для снапшота при 1M (сейчас — полный массив `MaxVehicles`).
2. Инвалидация кэша маршрутов при `BuildRoad` целиком vs. по затронутым зонам — **[шаг 3]**.
3. Стоит ли считать stuck ожидание на красном (сейчас — да; при ложных срабатываниях ввести
   исключение «стоит перед закрытым connector'ом, сигнал исправен»).
4. Нужен ли `StepPhased` для профилирования фаз в headless или достаточно `dotnet-trace`.
5. Шум параметров водителей (стрим `Behaviour`) — этап 2; поля под него не резервируем, кроме стрима PRNG.
