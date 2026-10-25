# Процедурный генератор города (спецификация, этап 1, задача G)

Статус: принято (ADR-0003). Реализуется в `src/Sim.Core/Gen/` (`namespace Sim.Core.Gen`):
`CityGenerator.cs`, `ZoneGenerator.cs`, `TransitGenerator.cs`, `GridPlan.cs` (промежуточный граф).
Документ исполним без вопросов: все константы, порядок обращений к PRNG и порядок обхода
зафиксированы. Отклонение от любого пункта требует правки этого файла.

Связанные документы: `docs/design/core-api.md` (§3.1 `ScenarioDesc`, §6.1 граф сети, §6.6 зоны и
транспорт, §8 PRNG, §13 расположение файлов), `docs/design/scenario-format.md` (§2.1 секция `city`,
§2.3 лестница), ТЗ `docs/plan/brief-stage1.md`.

## 0. Границы и инварианты

| # | Требование | Как обеспечивается |
|---|------------|--------------------|
| G1 | Детерминизм: сеть зависит только от `Seed` и `CityGenDesc` (не от числа машин, не от ёмкостей, кроме как «влезло/не влезло») | только стрим `CityGen`; фиксированный порядок обхода и обращений к PRNG (§3.9); зоны — стрим `Zones`, маршруты — стрим `Transit` |
| G2 | Граф связен; каждая полоса достижима из каждой | все сегменты двусторонние, все повороты кроме разворота разрешены, тупиков нет (степень узла ≥ 2), union-find-починка после удаления улиц (§3.4) |
| G3 | Нет вырожденной геометрии | мин. длина сегмента `MinSegmentLength = 30 м` (фактически ≥ 95 м), мин. угол между соседними рукавами `MinArmAngle = 25°`, макс. рукавов `MaxArms = 6`, нет кратных рёбер, нет петель (§3.6) |
| G4 | Ёмкости не превышаются | предварительная проверка плана против `MaxNodes/MaxSegments`; `-1` от `NetworkBuilder` → `CreateStatus.NetworkTooLarge`; никаких исключений кроме `ArgumentException` на невалидный `CityGenDesc` |
| G5 | Запреты ядра | в `Gen/` нет LINQ, `System.Random`, `List<T>`/`Dictionary<,>`, строк, замыканий; только массивы, выделенные один раз на вызов `Generate` (аллокации в `Create` разрешены) |
| G6 | Разделение ответственности | генератор только **назначает** `NodeControl` и стартовые параметры цикла светофора; состав фаз, конфликты, правила приоритета — спека перекрёстков (шаг 3). Остановки записываются как `(lane, s, kind)`; геометрия кармана — спека транспорта (шаг 4) |

Единицы: метры, секунды, м/с, радианы, тики (`20/с`). Плоскость `(X, Z)`, карта центрирована в
начале координат: `x ∈ [−W/2, W/2]`, `z ∈ [−H/2, H/2]`.

## 1. Вход: `CityGenDesc` (один в один с JSON `city`, scenario-format.md §2.1)

```csharp
namespace Sim.Core.Api;

public enum SignalPlacement : int { ArterialXArterial = 0, AllArterial = 1, None = 2 }
public enum MinorControl   : int { Yield = 0, Stop = 1, RightHand = 2 }

public struct CityGenDesc
{
    public bool  Enabled;              // false → сеть из ScenarioDesc.Network, генератор не вызывается
    public float SizeW, SizeH;         // size_m, м (до умножения на Scale)
    public float Scale;                // множитель к SizeW/SizeH; > 0
    public float BlockM;               // шаг сетки улиц, м
    public int   ArterialEvery;        // каждая N-я линия — артерия; >= 2
    public float ArterialSpeed;        // м/с (загрузчик: kmh / 3.6)
    public float StreetSpeed;          // м/с
    public float LaneWidth;            // м
    public int   IrregularNodes;       // число нерегулярных узлов (значение как есть, генератор его не масштабирует)
    public float IrregularJitterM;     // макс. смещение нерегулярного узла, м
    public float NodeJitterM;          // фоновый джиттер внутренних узлов, м (дефолт 0; в коде NodeJitter)
    public float RemovedStreetShare;   // 0..0.5
    public int   ConnectorSamples;     // точек полилинии connector'а
    public SignalPlacement Signals;
    public MinorControl    MinorControl;
}
```

Валидация в `Create` (нарушение → `ArgumentException` из `Create`, `CreateStatus.InvalidNetwork`
из `TryCreate`): `SizeW, SizeH ≥ 2·BlockM`, `Scale > 0`, `BlockM ≥ 50`, `ArterialEvery ≥ 2`,
скорости `> 0`, `LaneWidth ∈ [2.5, 5]`, `IrregularNodes ≥ 0`, `IrregularJitterM ∈ [0, 0.45·BlockM]`, `NodeJitterM ∈ [0, 0.45·BlockM]`,
`RemovedStreetShare ∈ [0, 0.5]`, `ConnectorSamples ∈ [2, 32]`.

Производные величины (вычисляются один раз, записываются в `CityGenResult`):

```
W  = SizeW · Scale                 H  = SizeH · Scale
Nx = max(2, round(W / BlockM))     Nz = max(2, round(H / BlockM))      // кварталов по осям
bx = W / Nx                        bz = H / Nz                         // фактический шаг (карта ровно W×H)
g  = NodeJitterM                   // «фоновый» джиттер внутренних узлов (по умолчанию 0: улицы прямые; ADR-0003, поправка 2026-10-24)
```

Линии сетки: `i = 0..Nx` (вертикальные, `x_i = −W/2 + i·bx`), `j = 0..Nz` (горизонтальные,
`z_j = −H/2 + j·bz`). Линии `0` и `Nx`/`Nz` — **периметр** (всегда улицы).

**Артериальные линии**: `i` артериальная ⇔ `0 < i < Nx` и `i % ArterialEvery == ArterialEvery / 2`
(целочисленное деление). Если ни одна `i` не подходит — артериальной назначается `i = Nx / 2`.
Аналогично для `j`. При дефолтах (`Nx = 20`, `every = 4`) это линии `{2, 6, 10, 14, 18}` — 5 линий
на ось, симметрично относительно центра, по 2 квартала до края, 25 узлов артерия×артерия.
Число артериальных линий по осям — `Ax`, `Az`.

Сегмент сетки артериальный ⇔ он лежит на артериальной линии (горизонтальное ребро строки `j`
артериально ⇔ `j` артериальная; вертикальное ребро столбца `i` ⇔ `i` артериальная).
Артерия: `2+2` полосы, `ArterialSpeed`, `RoadKind.Arterial`. Улица: `1+1`, `StreetSpeed`, `Street`.

## 2. Константы генератора (`CityGenerator`, `internal const`)

| Имя | Значение | Смысл |
|-----|----------|-------|
| `MinSegmentLength` | 30 м | G3; проверяется после каждой операции и перед эмиссией |
| `MinArmAngle` | 25° (0.436 рад) | минимальный угол между соседними рукавами узла |
| `MaxArms` | 6 | максимум рукавов узла (`NodeData.ArmCount`); гарантирует ≤ 64 connector'ов (6 входящих полос × 5 выходов = 30) |
| `MaxArmSlots` | 8 | слотов смежности на узел в плане (6 + запас на время операции) |
| `StraightProb` | 0.7 | вероятность «прямо» при прогулке автобусного маршрута по артериям |
| `TerminalStopS` | 40 м | отступ конечных остановок от начала/конца полосы |
| `StopSpacingTolerance` | 0.75 | промежуточная остановка ставится, если пройдено ≥ 0.75·`stop_spacing_m` |
| `MinRouteLenShare` | 0.6 | маршрут принимается, если его длина ≥ 0.6 от целевой |
| `RouteAttempts` | 8 | попыток сгенерировать маршрут |
| `Tail1MaxEdges` | 3 | макс. рёбер улицы от начала маршрута до первой артерии |
| `Tail2Edges` | 2 | рёбер улицы после схода с артерии |
| `DefaultGreenTicks` | 480 (24 с) | зелёный фазы на перекрёстке артерия×артерия |
| `MajorGreenTicks` / `MinorGreenTicks` | 720 / 280 (36 / 14 с) | для светофора на Т-узле улица→артерия (`all_arterial`) |

Жёлтый и всё-красное берутся из `RulesDesc.DefaultYellowTicks / DefaultAllRedTicks` (60 / 20).

## 3. Алгоритм построения сети (`CityGenerator.Generate`)

Генератор работает на **плане** (`GridPlan`): свои плоские массивы, выделенные в начале
`Generate`. Все операции нерегулярности и проверки — на плане; `NetworkBuilder` получает уже
финальный, проверенный граф в фиксированном порядке (§3.7). Это избавляет `NetworkBuilder` от
операций удаления/слияния (этап 1 их не имеет) и делает каждую проверку локальной.

### 3.1 План

```csharp
namespace Sim.Core.Gen;

internal sealed class GridPlan
{
    public readonly int Nx, Nz, NodeCap, EdgeCap;
    // узлы: индекс p = j * (Nx + 1) + i (row-major, j — строка по Z)
    public readonly float[] X, Z;            // позиция (после джиттера/сдвигов)
    public readonly byte[]  NodeAlive;       // 0/1
    public readonly byte[]  NodeFlags;       // бит0 Border, бит1 OnArterialLine, бит2 Shifted, бит3 Merged
    public readonly byte[]  Deg;             // число живых рёбер
    public readonly int[]   Adj;             // [NodeCap * MaxArmSlots] индексы рёбер, -1 = пусто; живые слоты — префикс длины Deg
    public readonly int[]   NodeOut;         // индекс узла в NetworkStore после эмиссии или -1
    // рёбра: сначала горизонтальные (j = 0..Nz, i = 0..Nx-1: e = j*Nx + i), затем вертикальные
    //        (j = 0..Nz-1, i = 0..Nx: e = (Nz+1)*Nx + j*(Nx+1) + i)
    public readonly int[]   EdgeA, EdgeB;    // концы, EdgeA < EdgeB всегда (при переподключении — переупорядочить)
    public readonly byte[]  EdgeAlive;
    public readonly byte[]  EdgeKind;        // RoadKind
    public readonly int[]   EdgeOut;         // индекс сегмента в NetworkStore или -1
    // рабочие буферы
    public readonly int[]   Queue;           // [NodeCap] BFS
    public readonly int[]   UnionParent;     // [NodeCap]
    public readonly int[]   Candidates;      // [max(EdgeCap, NodeCap)] для выборок
    public readonly float[] ArmAngles; public readonly int[] ArmOrder; // [MaxArmSlots] сортировка рукавов
}
```

`NodeCap = (Nx+1)(Nz+1)`, `EdgeCap = (Nz+1)·Nx + Nz·(Nx+1)`. Все массивы — один `new` каждый.
Операции над смежностью: `AddAdj(p, e)` (в первый свободный слот, `Deg++`), `RemoveAdj(p, e)`
(сдвиг хвоста влево, `Deg--`) — порядок слотов значим только внутри проверок, где рукава
сортируются по углу явно.

### 3.2 Шаг 1 — решётка с джиттером

Для каждого узла `p` в порядке `j = 0..Nz`, `i = 0..Nx` (row-major):
1. Базовая позиция `(x_i, z_j)`.
2. **Ровно два** вызова PRNG: `dx = rng.NextRange(−g, g)`, `dz = rng.NextRange(−g, g)`
   (вызываются всегда, даже если не используются — число обращений не зависит от положения узла).
3. Применение: внутренний узел (`0<i<Nx`, `0<j<Nz`) — оба; узел на левой/правой стороне периметра
   (`i ∈ {0, Nx}`, `0<j<Nz`) — только `dz`; на нижней/верхней (`j ∈ {0, Nz}`, `0<i<Nx`) — только
   `dx`; угловые — ничего. Граничные узлы остаются **ровно** на линии `x = ±W/2` / `z = ±H/2`.
4. Флаги: `Border` для периметра; `OnArterialLine` если `i` или `j` артериальная.

Рёбра: все `EdgeCap` рёбер создаются живыми в порядке §3.1 с `EdgeKind` по правилу §1.
Горизонтальное ребро `(i,j)–(i+1,j)`: `A = p(i,j)`, `B = p(i+1,j)`; вертикальное `(i,j)–(i,j+1)`.

### 3.3 Шаг 2 — удаление внутренних улиц («дырки» в сетке)

1. Кандидаты (в порядке индексов рёбер, в `Candidates`): живое ребро, `EdgeKind == Street`,
   оба конца **не** `Border`. Пусть их `C`. Периметр, артерии и «въезды» с периметра никогда
   не удаляются (на них держится спавн с края).
2. `count = round(RemovedStreetShare · C)`. Частичный Фишер–Йетс: для `t = 0..count−1`:
   `r = rng.NextInt(t, C)`, `swap(Candidates[t], Candidates[r])`.
3. Для `t = 0..count−1` по порядку: `e = Candidates[t]`; если `Deg[A] > 2 && Deg[B] > 2` — удалить
   (`EdgeAlive = 0`, `RemoveAdj` с обоих концов), иначе пропустить. Удалённые рёбра запоминаются в
   массиве `Removed[]` в порядке удаления. Итог: ни одного узла степени < 2 (тупиков нет).

### 3.4 Шаг 3 — гарантия связности (проверка + починка)

Union-find по живым рёбрам (`UnionParent`, сжатие путей, объединение «меньший корень под больший»
— порядок фиксирован индексами). Затем для `e` из `Removed[]` **в порядке удаления**: если
`Find(A) != Find(B)` — восстановить ребро (`EdgeAlive = 1`, `AddAdj`, `Union`), `RepairedEdges++`.
Полная решётка связна, поэтому после прохода граф связен (каждое восстановленное ребро соединяет
две компоненты, а множество всех рёбер связно). Контрольная BFS от узла `0` по `Queue` —
`Debug.Assert(reached == aliveNodes)`.

### 3.5 Шаг 4 — нерегулярные узлы

1. Кандидаты (порядок индексов узлов): живой, не `Border`, не `OnArterialLine`, `Deg == 4`.
   Пусть их `M`; `K = min(IrregularNodes, M)`.
2. Частичный Фишер–Йетс над `Candidates` на `K` элементов (`rng.NextInt(t, M)`).
3. Для `k = 0..K−1`: `p = Candidates[k]`; **всегда** два вызова `r = rng.NextRange(0.5, 1) ·
   IrregularJitterM`, `θ = rng.NextRange(0, 2π)`. Операция по чётности `k`:
   - `k` чётный — **сдвиг**: `X[p] += r·cos θ`, `Z[p] += r·sin θ`, флаг `Shifted`.
   - `k` нечётный — **слияние с восточным соседом**: `q = p + 1` (узел `(i+1, j)`). Условия:
     `q` жив, не `Border`, не `OnArterialLine`, не `Merged`/`Shifted`, ребро `p–q` живое,
     `Deg[p] + Deg[q] − 2 ≤ MaxArms`, и ни один сосед `q` (кроме `p`) не является соседом `p`
     (иначе появилось бы кратное ребро). Если условия не выполнены — вместо слияния делается
     сдвиг (с уже вытянутыми `r, θ`). Слияние: ребро `p–q` удаляется; каждое живое ребро `q–w`
     переподключается к `p` (`EdgeA/EdgeB` обновляются с сохранением `A < B`, `AddAdj(p, e)`,
     `RemoveAdj(q, e)`); `NodeAlive[q] = 0`; `X[p], Z[p]` = середина отрезка `p q` (до слияния);
     флаг `Merged`. Результат — узел с 5–6 рукавами, два из которых сходятся под ~53°.
4. После операции — `Validate(p)` и `Validate(w)` для всех соседей `w` (§3.6). При провале операция
   откатывается целиком (сохранённые до операции `X/Z` узла; для слияния — обратное
   переподключение: реализовать через копию затронутых слотов смежности, т.е. ≤ 2·`MaxArmSlots`
   значений) и считается в `IrregularSkipped`. Связность операции не меняют.

Артериальные узлы и периметр нерегулярными не бывают: артерии остаются прямыми коридорами для
автобусов и «зелёной волны», а край — прямой для спавна.

### 3.6 Проверка геометрии узла `Validate(p)`

1. Для каждого живого ребра `e` узла: `len(e) ≥ MinSegmentLength`, иначе `false`.
2. `Deg[p] ≤ MaxArms`, иначе `false`.
3. Углы рукавов `atan2(Z[w]−Z[p], X[w]−X[p])` → `ArmAngles[0..Deg)`, сортировка вставками
   (`ArmOrder`), разности соседних (включая замыкание через 2π) ≥ `MinArmAngle`, иначе `false`.
   Для `Deg == 2` дополнительно требуется угол ≥ `MinArmAngle` (разворот «в себя» запрещён;
   прямой проезд и излом допустимы).
4. Нет двух рёбер с одинаковой парой концов (кратные) и нет ребра `A == B` (петля).

Перед эмиссией (§3.7) `Validate` прогоняется по всем живым узлам; провал здесь невозможен по
построению, но проверяется `Debug.Assert`'ом и в тестах.

### 3.7 Шаг 5 — эмиссия в `NetworkBuilder`

Порядок строго фиксирован — от него зависят индексы узлов/сегментов/полос и, значит, хэш.

1. Проверка ёмкостей: `aliveNodes ≤ Capacities.MaxNodes`, `aliveEdges ≤ Capacities.MaxSegments`,
   иначе `CreateStatus.NetworkTooLarge`.
2. Узлы в порядке `p = 0..NodeCap−1`, живые: `NodeOut[p] = b.AddNode(new Vec2(X[p], Z[p]),
   NodeControl.None)` (управление назначается на шаге 6). `−1` → `NetworkTooLarge`.
3. Рёбра в порядке `e = 0..EdgeCap−1`, живые:
   `EdgeOut[e] = b.AddSegment(NodeOut[A], NodeOut[B], lanes, lanes, speed, kind)`, где
   `lanes = kind == Arterial ? 2 : 1`, `speed = kind == Arterial ? ArterialSpeed : StreetSpeed`.
   Сегмент — прямой отрезок (2 точки оси). `−1` → `NetworkTooLarge`.
4. `b.BuildAllConnectors()` — для узлов `0..NodeCount−1`: рукава по часовой стрелке, connector'ы по
   умолчанию core-api §6.1 (правая полоса → направо и прямо; левая → налево и прямо; `1+1` — всё;
   разворотов нет). `false` → `NetworkTooLarge`.
5. Контроль: `b.LaneCount`, `b.LanePointCount` записываются в результат.

### 3.8 Шаг 6 — назначение управления узлами

Класс узла определяется **набором `RoadKind` его рукавов** (после эмиссии, по `NodeArms`):

| Рукава | `ArmCount` | `NodeControl` |
|--------|-----------|---------------|
| любые | 2 | `None` (проезд/излом, конфликтов нет) |
| только `Street` | 3..6 | `RightHandRule` |
| `Street` + `Arterial` (Т-узел улица→артерия, крест артерия×улица) | 3..4 | `Signals == AllArterial` → `Signal`; иначе по `MinorControl`: `Yield`→`Yield`, `Stop`→`Stop`, `RightHand`→`RightHandRule` |
| только `Arterial` (артерия×артерия) | 4 | `Signals != None` → `Signal`; иначе `RightHandRule` |

Какие именно рукава «второстепенные» на `Yield`/`Stop`-узле, определяет спека перекрёстков по
`RoadKind` сегмента рукава (улица уступает артерии) — генератор ничего дополнительно не пишет.

Светофорные узлы получают запись в `CityGenResult.SignalPlans` (в порядке возрастания индекса
узла), число записей не должно превышать `Capacities.MaxSignalizedNodes` (иначе `NetworkTooLarge`):

```csharp
internal struct SignalPlanSeed
{
    public int    Node;
    public byte   PhaseCount;       // всегда 2 на этапе 1: фаза 0 — рукава оси X (запад/восток), фаза 1 — оси Z
    public ushort Green0, Green1;   // тики
    public ushort YellowTicks, AllRedTicks;
    public ushort OffsetTicks;      // сдвиг цикла (зелёная волна)
}
```

- Артерия×артерия: `Green0 = Green1 = DefaultGreenTicks` (24 с), цикл `2·(24+3+1) = 56 с`.
  `OffsetTicks = round(ia · ArterialEvery · bx / ArterialSpeed · 20) mod CycleTicks`, где `ia` —
  порядковый номер артериальной линии узла по оси X (`0..Ax−1`). Это «зелёная волна» на восток
  вдоль каждой горизонтальной артерии при движении с `ArterialSpeed` (600 м / 16.7 м/с = 36 с).
- Т-узел / крест улица→артерия при `AllArterial`: фаза 0 — рукава артерии (`MajorGreenTicks`),
  фаза 1 — рукава улицы (`MinorGreenTicks`); `OffsetTicks` — по тому же правилу.
- **2.5 (ADR-0018):** `SignalPlanSeed` дополняется `Mode` (`city.signal_mode`: `adaptive` — дефолт, `fixed`) и `CycleTicks`.
  Офсеты по `city.signal_coordination`: `alternate` (дефолт) — `intersections.md` §6.7 (`C = 2T`, `Offset = ((x−x₀)+(z−z₀))/v_p mod C`,
  `Fixed`: `Green = C/2 − Yellow − AllRed`); `legacy` — правило выше (golden-хэши этапа 1/2.4); `none` — `Offset = 0`, без цикла.
  PRNG не трогается (§3.9 не меняется).
- Состав фаз (какие connector'ы зелёные, разрешённые левые) — спека перекрёстков (`SignalStore`,
  core-api §6.7); `WorldBuilder` передаёт `SignalPlans` туда. До появления шага 3 `SignalPlans`
  просто хранится в результате, а `NodeData.Signal = -1`.

### 3.9 Порядок обращений к PRNG `CityGen` (нормативно)

1. `2·NodeCap` вызовов `NextRange` (§3.2), row-major.
2. `count` вызовов `NextInt` (§3.3, частичный Фишер–Йетс).
3. `K` вызовов `NextInt` (§3.5 п.2), затем `2·K` вызовов `NextRange` (§3.5 п.3).

Больше генератор сети PRNG не трогает. Добавление вызова — изменение этого раздела и голден-хэша
в тестах (§8).

## 4. Масштаб и оценки по лестнице

`Scale` умножает размер карты; шаг квартала, скорости, доля удалённых улиц — неизменны
(scenario-format §2.3). `IrregularNodes` задаётся в сценарии явно (≈ `12·Scale²`). Оценки для
дефолтов (`BlockM = 150`, `ArterialEvery = 4`, `RemovedStreetShare = 0.15`, `Signals =
ArterialXArterial`, `connector_samples = 8`); ёмкости — из scenario-format §2.3:

| Величина | формула | s1k (×1) | s10k (×2) | s100k (×5) | s1m (×16) |
|----------|---------|----------|-----------|------------|-----------|
| `Nx = Nz` | `20·Scale` | 20 | 40 | 100 | 320 |
| артериальных линий на ось `A` | `#{i: i%4==2}` | 5 | 10 | 25 | 80 |
| узлов решётки | `(N+1)²` | 441 | 1 681 | 10 201 | 103 041 |
| узлов после слияний (≈ `−K/2`) | | ≈435 | ≈1 657 | ≈10 051 | ≈101 541 |
| **ёмкость `max_nodes`** | | 700 | 2 800 | 17 k | 170 k |
| рёбер решётки | `2N(N+1)` | 840 | 3 280 | 20 200 | 205 440 |
| из них артерий | `2·A·N` | 200 | 800 | 5 000 | 51 200 |
| кандидатов на удаление | `2(N−1−A)(N−2)` | 504 | 2 204 | 14 504 | 152 004 |
| сегментов после удаления и слияний | | ≈758 | ≈2 925 | ≈17 870 | ≈181 100 |
| **ёмкость `max_segments`** | | 1 300 | 5 200 | 32 k | 330 k |
| полос Normal | `4·арт + 2·улиц` | ≈1.9 k | ≈7.5 k | ≈45.7 k | ≈465 k |
| connector'ов (≈12/узел) | | ≈5.2 k | ≈19.9 k | ≈121 k | ≈1.22 M |
| карманов (стрим Transit, ≈0.7 × остановок) | | ≈80 | ≈300 | ≈1.9 k | ≈19 k |
| **полос всего / `max_lanes`** | | ≈7.2 k / 12 k | ≈27.7 k / 48 k | ≈169 k / 300 k | ≈1.70 M / 3 M |
| точек полос (`2·normal + 8·conn + 6·bay`) | | ≈46 k | ≈176 k | ≈1.07 M | ≈10.8 M |
| **ёмкость `max_lane_points`** | | 80 k | 320 k | 2 M | 15 M (≈10.8 M расчёт, ≈9.6 M факт) |
| светофоров (`A²`) | | 25 | 100 | 625 | 6 400 |
| **ёмкость `max_signalized_nodes`** | | 64 | 160 | 800 | 8 000 |
| зон (§5) `Zx·Zz + 2Zx + 2Zz` | `Z = W/500` | 60 | 192 | 1 020 | 9 600 |
| **ёмкость `max_zones`** | | 64 | 256 | 1 024 | 10 000 |

Все оценки влезают в ёмкости с запасом ≥ 1.5×, кроме `max_zones` на s100k (1 020 из 1 024 — ровно
по формуле, запас не нужен: число зон детерминировано и не зависит от PRNG). При
`Signals = AllArterial` светофоров `2·A·(N+1) − A²` (s1k: 185) — ёмкость в таком сценарии надо
поднимать вручную. Реализация обязана посчитать фактические числа и положить их в
`CityGenResult`; тест §8 сверяет их с таблицей в пределах ±15 %.

Сложность: все шаги O(узлы + рёбра) (union-find ≈ O(α)); для s1m ≈ 10⁵ узлов и 2·10⁵ рёбер —
миллисекунды, доминирует `BuildAllConnectors` (1.2 M Безье × 8 точек).

## 5. Зоны (`ZoneGenerator.Generate`, стрим `Zones`)

Зоны — источники и стоки поездок. Два вида: **внутренние** клетки и **внешние** (край карты —
машины появляются «из-за карты» и уезжают за неё).

### 5.1 Разбиение

```
Zx = ceil(W / ZoneM)         Zz = ceil(H / ZoneM)
cx = W / Zx                  cz = H / Zz                        // фактическая клетка (HalfSize = cx/2; при W=H квадрат)
```

Порядок индексов (фиксирован, `ZoneHandle.Index`):
1. Внутренние: `zi = kz·Zx + kx`, `kx = 0..Zx−1`, `kz = 0..Zz−1`; `Center = (−W/2 + (kx+0.5)·cx,
   −H/2 + (kz+0.5)·cz)`, `IsExternal = 0`.
2. Внешние, по сторонам: юг (`z = −H/2`, `Zx` кусков по оси X), восток (`x = +W/2`, `Zz` по Z),
   север (`Zx`), запад (`Zz`); центр — середина куска на линии края, `HalfSize = cx/2` (или `cz/2`).
   `IsExternal = 1`.

Итого `Zx·Zz + 2·(Zx+Zz)` зон; если больше `Capacities.MaxZones` → `CreateStatus.InvalidDemand`.

### 5.2 Полосы спавна (`ZoneLanes`)

Массив `ZoneLanes : int[]` длиной `Capacities.MaxLanes` (каждая полоса попадает не более чем в
одну зону), диапазон зоны — `FirstSpawnLane, SpawnLaneCount`; зоны заполняются в порядке индексов,
полосы внутри зоны — по возрастанию индекса полосы.

- **Внешняя зона** (сначала они, чтобы «забрать» свои полосы): для каждого узла `Border` (не
  углового), лежащего на куске края (проекция позиции узла на ось края попадает в `[k·c, (k+1)·c)`,
  последний кусок включает правую границу), берётся его **въездной** сегмент — единственный рукав,
  второй конец которого не `Border`. Полосы спавна — все `Normal`-полосы этого сегмента,
  направленные **от** граничного узла (1 у улицы, 2 у артерии). Угловые узлы въезда не имеют.
- **Внутренняя зона**: все `Normal`-полосы (оба направления) сегментов `Street`, середина оси
  которых лежит в клетке (`floor((xm + W/2)/cx)`, `floor((zm + H/2)/cz)`, клампы на край),
  кроме уже отданных внешним зонам. Если таких нет — те же условия для `Arterial`; если и их нет —
  `SpawnLaneCount = 0`. Это «парковки»: машина появляется в начале улицы внутри клетки (фаза 2
  спавна ставит её на `s = Length_type + MinGap`, core-api §7).

**Соглашение о стоках** (для OD-генератора, задача H): полоса прибытия для зоны — полоса того же
сегмента, что и полоса спавна, `LaneNo 0`, **противоположного** направления, `DestS = Length`.
Генератор гарантирует, что все полосы спавна лежат на двусторонних сегментах. Для внешней зоны это
означает выезд через тот же граничный узел «за карту».

### 5.3 Веса

Один вызов `rng.NextRange(−j, j)` на зону в порядке индексов (`j = DemandDesc.WeightJitter`),
**всегда**, даже если вес обнуляется:
- внутренняя: `Weight = SpawnLaneCount > 0 ? 1 + u : 0`;
- внешняя: `Weight = SpawnLaneCount · (1 + u)` (артериальные въезды весят вдвое больше уличных).

Выбор пар, `external_share`, `min_trip_m`, gravity — OD-генератор (задача H, стрим `Spawn`);
генератор зон только даёт `ZoneData[]` и `ZoneLanes`.

### 5.4 `NodeData.Zone`

Для каждого узла: `Border` → индекс внешней зоны своего куска края (угловой узел → южный/северный
кусок, содержащий его `x`); иначе → индекс внутренней клетки, содержащей позицию.

При `City.Enabled == false` (явная сеть) `ZoneGenerator` вызывается с `W, H` = bounding box узлов
сети, внешние зоны имеют `SpawnLaneCount = 0` (внешних въездов у ручной карты нет) — поведение
scenario-format §4.

## 6. Автобусные маршруты (`TransitGenerator.Generate`, стрим `Transit`)

Маршрут — линейная последовательность остановок от конечной A до конечной B (разворот и
обратное направление — спека шага 4). Маршруты идут по артериям, с коротким «хвостом» по улицам в
начале и конце — так появляются и карманы (на артериях), и остановки у обочины (на улицах).

Классификация узлов по `NetworkStore` (без плана): `IsBorder(n)` ⇔ `|x| ≥ W/2 − 0.5` или
`|z| ≥ H/2 − 0.5`; `IsArterialNode(n)` ⇔ есть рукав с `Kind == Arterial`; `IsStreetNode(n)` ⇔
нет артериальных рукавов. Массив стартовых кандидатов `StartNodes` — внутренние уличные узлы с
`ArmCount ≥ 3` в порядке индексов.

Рабочие буферы: `VisitStamp : int[MaxSegments]` (ребро посещено в маршруте `r` ⇔
`VisitStamp[seg] == r+1` — очистка O(1)), `PathSeg : int[]`, `PathDir : byte[]`
(ёмкость `4·(Nx+Nz) + Tail1MaxEdges + Tail2Edges`).

### 6.1 Прогулка (для маршрута `r`, попытки `a = 0..RouteAttempts−1`)

Порядок вызовов PRNG на попытку фиксирован; каждый пункт вызывает ровно указанное число раз.

1. `targetStops = rng.NextInt(StopsMin, StopsMax + 1)`; `targetLen = targetStops · StopSpacing`.
2. `start = StartNodes[rng.NextInt(0, StartNodes.Length)]`; `heading = rng.NextInt(0, 4) · π/2`
   (0 — восток, π/2 — «север» (+Z), π — запад, 3π/2 — юг).
3. **Хвост 1**: от `start` до `Tail1MaxEdges` раз: `Straight(node, prevSeg, heading)` — рукав с
   наименьшим `|Δугла|` к `heading` среди непосещённых и `≠ prevSeg`, принимается при
   `|Δ| ≤ 60°`; перейти, `heading` = направление пройденного ребра. Остановиться, как только
   текущий узел `IsArterialNode`. Если артерия не достигнута — попытка неудачна (переход к
   следующей попытке; PRNG п.4–5 **всё равно не вызывается** — число вызовов зависит от исхода, это
   допустимо, т.к. исход детерминирован).
4. **Артериальная прогулка**: пока `len < targetLen` и узел не `IsBorder`: два вызова
   `u = rng.NextFloat01()`, `side = rng.NextFloat01()` (всегда оба). Кандидаты — артериальные
   непосещённые рукава `≠ prevSeg`: `straight` (`|Δ| ≤ 45°`), `right` (`Δ ∈ (−135°, −45°)`),
   `left` (`Δ ∈ (45°, 135°)`) относительно текущего `heading` (угол `Δ` берётся в `(−π, π]`,
   положительный — против часовой стрелки в плоскости `(X, Z)`). Выбор: если есть `straight` и
   (`u < StraightProb` или поворотов нет) → `straight`; иначе `right` если `side < 0.5` и он есть,
   иначе `left` если есть, иначе `right`; если кандидатов нет — прогулка завершена. Длина
   маршрута ограничена `PathSeg.Length` рёбрами.
5. **Хвост 2**: `t = rng.NextInt(0, 4)` (всегда). Кандидаты — уличные непосещённые рукава `≠
   prevSeg`, в порядке по часовой стрелке (`NodeArms`); выбирается `cand[t % candCount]`; затем
   до `Tail2Edges − 1` раз `Straight` по улицам (при отсутствии — стоп). Если уличных рукавов нет —
   хвоста нет.
6. Попытка **успешна**, если `len ≥ MinRouteLenShare · targetLen` и рёбер ≥ 3. Иначе попытка
   запоминается как лучшая по `len`, и после `RouteAttempts` неудач берётся лучшая с рёбер ≥ 2;
   если и её нет — маршрут не создаётся (`RoutesFailed++`, маршрутов станет меньше `Routes`).

### 6.2 Остановки

Остановка ставится на **правую** полосу (`LaneNo 0`) направления движения: для ребра, пройденного
из узла `p` в `q`, это `FirstLaneAB`, если `p == Segment.NodeA`, иначе `FirstLaneBA`. `s` —
координата вдоль полосы (совпадает с осевой, core-api §6.2).

1. Первая остановка: первое ребро, `s = min(TerminalStopS, Length/2)`.
2. Промежуточные: для рёбер `1..n−2` — кандидат `s = Length/2`; ставится, если расстояние от
   предыдущей остановки (вдоль маршрута) ≥ `StopSpacingTolerance · effSpacing`, где
   `effSpacing = max(StopSpacing, routeLen / (StopsMax − 1))` (гарантирует ≤ `StopsMax`).
   На первом и последнем рёбрах промежуточных остановок нет.
3. Последняя остановка: последнее ребро, `s = max(Length − TerminalStopS, Length/2)`.
4. `Kind`: `stop_kind = curb` → `Curb`; `bay` → `Bay`; `mixed` → `Bay`, если сегмент
   `Arterial`, иначе `Curb`.
5. Контроль: `StopCount ≤ min(StopsMax, Capacities.MaxBusStopsPerRoute)`; суммарно
   `≤ Capacities.MaxBusStops`, маршрутов `≤ Capacities.MaxBusRoutes`, иначе
   `CreateStatus.InvalidTransit`. Две остановки одной полосы отстоят ≥ 50 м (следует из
   `MinSegmentLength`, `TerminalStopS` и правил 1–3; проверяется `Debug.Assert`).

Запись: `BusRouteData { FirstStop, StopCount, HeadwayTicks = TransitDesc.HeadwayTicks, VehicleType
= TransitDesc.BusType, ActiveBuses = 0, LastDispatchTick = -1 }`; `BusStopData { Lane, S, Kind,
Route, Waiting = 0, LastArrivalTick = -1 }`; `BusRouteStops` — подряд. Геометрия кармана
(`LaneKind.BusBay`) строится спекой шага 4 по этим записям; на этапе 2 дополнительных полос нет
(в оценках §4 они уже учтены с запасом).

`routes_explicit` в сценарии → `TransitGenerator` не вызывается, записи берутся из `TransitDesc`.

## 7. API

```csharp
namespace Sim.Core.Gen;

internal struct CityGenResult
{
    public float Width, Height, BlockX, BlockZ;
    public int   Nx, Nz, ArterialLinesX, ArterialLinesZ;
    public int   NodeCount, SegmentCount, LaneCount, ConnectorCount, LanePointCount;
    public int   RemovedStreets, RepairedEdges, ShiftedNodes, MergedNodes, IrregularSkipped;
    public int   SignalizedNodes;
    public SignalPlanSeed[] SignalPlans;      // длина SignalizedNodes; выделяется в Generate
}

internal struct ZoneGenResult  { public int ZoneCount, InternalZones, ExternalZones, SpawnLanesTotal, EmptyZones; }
internal struct TransitGenResult { public int Routes, Stops, BayStops, CurbStops, RoutesFailed, RoutesBelowMinStops; }

internal static class CityGenerator
{
    /// Строит сеть по desc.City через builder. Возвращает Ok или NetworkTooLarge/InvalidNetwork.
    /// Все аллокации — внутри (план, буферы); после возврата ссылок на них не остаётся, кроме result.SignalPlans.
    public static CreateStatus Generate(in CityGenDesc city, in RulesDesc rules, in SimCapacities caps,
                                        NetworkBuilder b, ref Pcg32 rngCityGen, out CityGenResult result);
}

internal static class ZoneGenerator
{
    /// Для сгенерированной сети: width/height из CityGenResult; для явной — bounding box узлов.
    public static CreateStatus Generate(in DemandDesc demand, float width, float height, NetworkStore net,
                                        ZoneStore zones, ref Pcg32 rngZones, out ZoneGenResult result);
}

internal static class TransitGenerator
{
    public static CreateStatus Generate(in TransitDesc transit, float width, float height, NetworkStore net,
                                        TransitStore store, ref Pcg32 rngTransit, out TransitGenResult result);
}
```

`WorldBuilder` (задача E) вызывает их в порядке: `CityGenerator` (или явная сеть через тот же
`NetworkBuilder`) → `ZoneGenerator` → `TransitGenerator` (если `Transit.Routes > 0` и нет
`RoutesExplicit`). `ZoneStore` — хранилище `ZoneData[MaxZones]` + `ZoneLanes[MaxLanes]` +
`ZoneCount` (`Sim.Core.World` или `Gen/`, по выбору задачи E; поля — как в core-api §6.6).

### 7.1 Контракт `NetworkBuilder`, на который опирается генератор

Реализует задача C (`Network/NetworkBuilder.cs`); перечисленное ниже — минимальная поверхность,
имена и семантика нормативны для C и G:

```csharp
namespace Sim.Core.Network;

internal sealed class NetworkBuilder
{
    public NetworkBuilder(NetworkStore store, float laneWidth, int connectorSamples);
    public int NodeCount { get; }  public int SegmentCount { get; }  public int LaneCount { get; }  public int LanePointCount { get; }

    public int  AddNode(Vec2 position, NodeControl control);                          // индекс или -1 (MaxNodes)
    /// Прямой сегмент a→b; создаёт lanesAB + lanesBA Normal-полос (офсет ±(LaneNo+0.5)·laneWidth, 2 точки,
    /// Length = длина оси), регистрирует рукав у обоих узлов. -1 при нехватке сегментов/полос/точек или a == b.
    public int  AddSegment(int a, int b, byte lanesAB, byte lanesBA, float speedLimit, RoadKind kind);
    /// Сортирует NodeArms узла по часовой стрелке и строит connector'ы по умолчанию (core-api §6.1); false при нехватке.
    public bool BuildConnectors(int node);
    public bool BuildAllConnectors();                                                  // узлы 0..NodeCount-1 по порядку
    public void SetNodeControl(int node, NodeControl control);
    public int  ArmCount(int node);  public int ArmSegment(int node, int k);  public Vec2 NodePosition(int node);
}
```

Если задача C выберет иные имена, G добавляет тонкие обёртки с этими именами в `NetworkBuilder`
отдельным коммитом — спеку править не нужно.

## 8. Тест-план (`tests/Sim.Core.Tests/Gen/*`)

Все тесты строят сеть через `NetworkBuilder` на `NetworkStore` с ёмкостями s1k из
scenario-format §2.2 и дефолтным `CityGenDesc` (seed `20261024`), если не указано иное.

1. **Детерминизм**: два прогона с одним seed → побитово равные `X/Z` узлов, `NodeA/NodeB`,
   полосы, `SignalPlans`, зоны, маршруты; `rng.State` после генерации равны. Разные seed → разные
   позиции (хотя бы одна отличается).
2. **Голден-хэш сети**: FNV-1a 64 по `NodeData.Position`, `Control`, `SegmentData.NodeA/NodeB/
   LanesAB/LanesBA/Kind`, `LaneData.FromLane/ToLane` в порядке индексов равен константе,
   записанной в тест при первой реализации (комментарий: менять только вместе с §3.9).
3. **Связность**: BFS по `LaneNext` от полосы 0 достигает **все** `Normal`-полосы; BFS по
   сегментам от узла 0 достигает все узлы; `RepairedEdges` фиксируется в выводе теста.
4. **Нет вырождений**: все `Segment.Length ≥ MinSegmentLength` (и ≥ 95 м при дефолтах); у каждого
   узла `2 ≤ ArmCount ≤ 6`; углы между соседними рукавами ≥ 25°; нет кратных рёбер; нет узла с
   `ArmCount == 1`.
5. **Классы дорог и полосы**: каждый сегмент `Arterial` имеет `2+2`, `Street` — `1+1`; скорости
   `16.67`/`11.11` м/с (±1e-3); все сегменты двусторонние; число артериальных сегментов `≈ 2·A·N`
   точно (артерии не удаляются): 200.
6. **Счётчики против таблицы §4** для s1k (±15 %): узлы, сегменты, полосы, connector'ы, точки;
   `SignalizedNodes == 25`; `RemovedStreets ∈ [60, 76]`; `ShiftedNodes + MergedNodes +
   IrregularSkipped == 12`.
7. **Управление узлами**: каждый узел с ≥ 2 артериальными рукавами и без уличных — `Signal`;
   смешанные — `Yield` (дефолт); чисто уличные с `ArmCount ≥ 3` — `RightHandRule`; `ArmCount == 2`
   — `None`. `SignalPlans[i].Node` возрастают; `Green0 == Green1 == 480`; `OffsetTicks < CycleTicks`.
8. **Параметры**: `Signals = None` → 0 светофоров; `AllArterial` с `MaxSignalizedNodes = 64` →
   `NetworkTooLarge`; с `256` → `185` светофоров; `RemovedStreetShare = 0` → `840 − MergedNodes`
   сегментов; `IrregularNodes = 0` → все углы 90° ± 8°.
9. **Ёмкости**: `MaxSegments = 500` → `NetworkTooLarge`, без исключений; `MaxLanes` мало → то же.
10. **Scale**: `Scale = 2` → `Nx = 40`, `A = 10`, узлов `≈ 1 657 ± 5 %`, 100 светофоров; время
    `Generate` для `Scale = 5` фиксируется в выводе (не ассерт).
11. **Зоны**: 60 зон (36 + 24), индексы внешних ≥ 36; у каждой внешней зоны на стороне с
    артериальным въездом `SpawnLaneCount ≥ 2`; суммарно внешних полос спавна = числу въездов
    (`4·(N−1)` неугловых граничных узлов → `4·19·1 + 4·5·1` дополнительных артериальных = 96);
    у всех полос спавна есть противоположная полоса того же сегмента; каждая полоса ≤ 1 зоны;
    `Weight` внутренних ∈ `{0} ∪ [0.5, 1.5]` (0 только при `SpawnLaneCount == 0`); все `Node.Zone` валидны.
12. **Маршруты**: 10 маршрутов; у каждого `2 ≤ StopCount ≤ 14`; остановки монотонно по маршруту,
    ни одно ребро дважды; `Kind == Bay` ⇔ сегмент артериальный (при `mixed`); на каждом маршруте
    есть хотя бы одна артериальная остановка; `25 ≤ S ≤ Length − 25`; `HeadwayTicks == 6000`;
    два прогона — одинаковые маршруты.
13. **Отсутствие запрещённых API**: покрывается анализатором (BannedSymbols) при сборке.
14. **Бенч** (`bench/Sim.Bench`): `CityGenerator.Generate` s1k и `Scale = 2` — время и
    аллокации (аллокации ненулевые по определению, фиксируются как число).

## 9. Открытые вопросы (не блокируют реализацию)

1. Остановка «в кармане» уменьшает ли число полос артерии на участке — решает спека шага 4;
   генератор оставляет вокруг остановки ≥ 25 м до узла.
2. Зелёная волна только по оси X; при калибровке шага 3 можно перейти на офсеты по обеим осям или
   на расчёт от центра карты.
3. Слияние узлов даёт 5–6-рукавные узлы только класса улица×улица (`RightHandRule`); если спека
   перекрёстков ограничит `ArmCount ≤ 4`, слияние отключается константой (`MergeEnabled`), сдвиг
   остаётся — оценки §4 почти не меняются.

## 10. Уточнения по итогам реализации (задача 2-G)

Нормативные уточнения, на которые опирается порт на Rust:

1. **Имена `CityGenDesc`** в коде: `SizeX/SizeZ`, `BlockSize`, `IrregularJitter`, `CitySignals`, а не `SizeW/SizeH`, `BlockM`, `SignalPlacement` из §1.
2. **Отступы и длины.** `NetworkBuilder` обрезает ось на `NodeSetback = 7 м` у каждого узла: `Segment.Length` на 14 м короче расстояния между узлами. `MinSegmentLength = 30 м` (§2) относится к расстоянию между узлами плана. Слияние даёт рёбра ≈75 м (не ≥ 95): фактический минимум `Segment.Length` на s1k — 96.4 м (расстояние 110.4 м).
3. **Округление** `round` — «от нуля» (`MidpointRounding.AwayFromZero`, как `f32::round`).
4. **Нерегулярность**: кандидат, оказавшийся уже мёртвым/сдвинутым/слитым к своей очереди, пропускается (`IrregularSkipped++`), два вызова PRNG на `k` выполняются всегда. Если условия слияния не выполнены — сдвиг; если слияние отменено валидацией — пропуск (без сдвига).
5. **Счётчики**: `RemovedStreets` = удалено минус восстановлено; `SegmentCount = 2N(N+1) − RemovedStreets − MergedNodes`.
6. **Светофоры**: фазы привязаны к оси, а не к «артерии»: `Green0` — рукава оси X, `Green1` — оси Z. Т-узел при `AllArterial`: артерия вдоль X → `(Major, Minor)`, вдоль Z → `(Minor, Major)`. Цикл `= Green0 + Green1 + 2·(Yellow + AllRed)`. `ia` для смещения = число артериальных колонок строго левее колонки узла. При `AllArterial` узел артерии, потерявший обе улицы (2 рукава), управления `None` — число светофоров может быть < `2·A·(N+1) − A²` (s1k: 181 из 185).
7. **Connector'ы**: реально ≈10 на узел (s1k: 4 328, 38.5 k точек), оценки §4 завышены (≈12/узел).
8. **Зоны**: (`ZoneData.HubNode` удалён в H3 вместе с кэшем магистралей, routing.md §15.) `ZoneLanes` лежат в порядке индексов зон. Результат — `GenZones` (`Zones`, `ZoneLanes`), также выставляются `NodeData.Zone` и `NetworkStore.ZoneCount`.
9. **Маршруты**: метка посещения `VisitStamp` уникальна на **попытку** (а не на маршрут), иначе провалившаяся попытка портит следующую. Буфер пути — `SegmentCount` (длина маршрута фактически ограничена `targetLen`). Число промежуточных остановок жёстко ограничено `min(StopsMax, MaxBusStopsPerRoute) − 1`. Результат — `GenTransit` (`Routes`, `Stops`, `RouteStops`, а также `PathSeg/PathDir/PathFirst/PathCount` — рёбра маршрута).
10. **Статусы**: невалидный `CityGenDesc` → `CreateStatus.InvalidNetwork` из `Generate` (исключение бросает `Create`).
