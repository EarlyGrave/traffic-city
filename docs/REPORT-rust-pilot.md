# Rust-пилот ядра (R1–R8): отчёт

Дата: 2026-10-28. Среда: облачный контейнер (4 vCPU, Linux x64, glibc), `dev` e30d02c. Проверка — агент 163 (R8), сводка — оркестратор.
Цель пилота (ADR-0022, `docs/design/rust-pilot.md`): проверить, что правила портируемости `Sim.Core` соблюдаются на деле, а не перенести ядро.

## Итог
- Порт сделан: `tc-core` (Util, сеть, конфликты, IDM), `tc-scenarios` (JSON-загрузчик, профили дорог), `tc-ffi` (C ABI `Create/Step/Enqueue/снапшот`).
- На Linux C# и Rust совпадают **побитово** на всём, что сверяется: `desc_hash`, `network_hash`, `conflicts_hash`, вектора Util, IDM (10 000 строк),
  `headings_hash` в строгом режиме и даже `pow` для `delta ∉ {2,4}` (1 000 строк, 0 расхождений).
- Единственный источник расхождений между платформами — системная libm (`atan2`, `pow`): Windows UCRT ≠ glibc. Это не ошибка порта, а свойство
  .NET и Rust на разных ОС; сверка таких значений — только в строгом режиме на Linux.

## Проверки (R8, реальные прогоны)
| Проверка | Результат | Время |
|---|---|---|
| `scripts/rust-check.sh` (fmt, clippy, test, сторонние крейты) | OK | 1 с (тёплый `target/`), 5 с (холодный) |
| то же с `TC_STRICT_HEADINGS=1` | OK | 1 с |
| `cargo test` по крейтам | 53 теста, 0 падений (tc-core 18, tc-ffi 3, tc-scenarios 32) | < 1 с |
| `cargo build --release -p tc-ffi` | OK | 2.6 с |
| ffi-smoke (C# через C ABI) | 137 проверок, 0 ошибок, 18 сценариев | 14 с со сборкой |
| ffi-smoke с намеренной паникой (`test-panic`) | 141 проверка, 0 ошибок | 1 с |
| C# `RustPilotGolden` (обычный и строгий) | 40 / 40 | 22 с / 6 с |
| CI-job `rust` (GitHub) | success в 4 из 4 последних прогонов | 48–66 с |

CI-job `rust` не запускает `TC_STRICT_HEADINGS=1` и полный `rust-check.sh` (только `--deps-only`); строгая сверка сейчас — руками в облаке.

## Найденные расхождения и как закрыты
| # | Что | Причина | Решение |
|---|---|---|---|
| 1 | `headings_hash` Windows ≠ Linux | `MathF.Atan2` зовёт системную libm (UCRT ≠ glibc) | сверка только при `TC_STRICT_HEADINGS=1`; golden снимать на Linux (облако) |
| 2 | IDM `pow` при `delta ∉ {2,4}` | `MathF.Pow` ↔ `powf`, системная libm | отдельный файл `idm-pow.json`, счётчик, не гейт; на Linux 0 / 1 000 |
| 3 | float → int вне диапазона | net8 даёт `int.MinValue`, net10 насыщает | в C# — `DetMath.ToIntSat/ToLongSat` (насыщение, NaN → 0 = Rust `as`), аудит Roslyn (3.1c) |
| 4 | `Max/Min` с NaN и ±0 | `MathF.Max` ≠ `f32::max` | `detmath::max_net/min_net`, `f32::max/min` запрещены clippy |
| 5 | крейт `libm` ≠ glibc | atan2 расходится в 2 776 из 10⁶, sin — в 13 534 из 10⁶ | `libm` не используется; std f32 = та же glibc, что у .NET |
| 6 | `network_hash` по сырым байтам struct | зависимость от раскладки памяти | хэш по полям в прежней раскладке (C#) |
| 7–8 | ADR-0028 (corner_radius), профили RP3 | изменения ядра после R3–R5 | порт догнал (cbc56f4, 5e86fcf), golden сходятся |
| 9 | временный обход конфликтов в R4 | — | удалён в R5 |
| 10 | новый член workspace меняет `Cargo.lock` | `--locked` падает до коммита lock | процессное, в lessons |

Разбор JSON-чисел (§6.4) расхождений не дал.

## Нарушения правил портируемости в C#-ядре
- **Исправлено:** float → int без насыщения (3.1c); хэш сети по сырым байтам.
- **Открыто: libm в вычислениях, влияющих на хэш состояния** (вопрос 1 в `rust-pilot.md` §11, решение владельца не принято).
  По grep (не проверено, какие пути входят в хэш): `Gen/CityGenerator.cs` (Cos/Sin), `Gen/GridPlan.cs`, `Gen/TransitGenerator.cs`,
  `City/LotBuilder.cs`, `Rules/Intersections/SignalMasks.cs`, `Network/LaneGeometry.cs`, `Network/NetworkStore.cs` (Atan2),
  `Rules/Idm.cs`, `Peds/ResidentStore.cs`, `Agents/Spawner.cs` (`MathF.Pow`). ADR-0021 запрещает exp/ln в хэшируемых решениях, про `Pow` и `Atan2` молчит.
  Следствие: сейв/реплей Windows ↔ Linux может разойтись, если такие значения попадают в решения симуляции.
- **Не проверено: `Array.Sort(keys, payload)`** (`City/CityInit.cs`, `City/TripLegs.cs`, `Demand/ObsTable.cs`). При равных ключах с разной нагрузкой
  порядок зависит от алгоритма сортировки платформы; спека §6.2 требует свой детерминированный алгоритм.
- Рендер (`SnapshotPhase`, `PedSnapshot`) использует Atan2/Cos/Sin — допустимо (вне хэша, ADR-0010); с N1 курс машин — свой полином без libm.

## Рекомендации
1. Решить вопрос 1 (`rust-pilot.md` §11): ввести детерминированные `atan2`/`pow`/`sin`/`cos` в `DetMath` для хэшируемых путей (или доказать, что эти места в хэш не входят).
2. Аудит `Array.Sort` с нагрузкой: стабильная сортировка по (ключ, нагрузка) или свой алгоритм.
3. В CI-job `rust` добавить шаг со строгой сверкой (`TC_STRICT_HEADINGS=1`) — CI работает на Linux, это бесплатно.
4. Порт дальше пилота — только после 1–2: иначе каждый новый модуль будет догонять расхождения libm.
