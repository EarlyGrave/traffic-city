# Горячий путь s10k: разбор и план (шаг 2.4b)

Статус: спека архитектора, 2026-10-24. Решение — ADR-0016. Затрагивает `core-api.md` §3, §7, §7.6 и
`intersections.md` §4, §7.2, §8, §9 (правки внесены тем же коммитом). Шаг 2.4c (разбор хвоста p95, выбор резерва) — §8, ADR-0017.

Цель шага 2.4: s10k tick p95 ≤ 4 мс при 10 000 активных (сейчас 7.85 мс, нужно ×1.96), 0 B/тик, детерминизм,
s1k не хуже. Карт-бланш владельца на редизайн; здесь выбраны правки, которые **не меняют результат симуляции
побитово** (хэш состояния тот же) — это делает приёмку дешёвой и честной: golden-хэш вместо статистики.

## 1. Исходные данные

### 1.1 Профиль (агент 52, s10k после рампы, `dotnet-sampled-thread-time`, доли тика)

| Фаза | Доля | Из неё |
|---|---|---|
| 5 Behaviour (вклинена PGO в `Step`) | ≈33% | self 16.1%, `ConflictGap` 9.8%, `ObstacleGap` 6.2%, `Mobil` 0.8% |
| 6 Integrate | 27% | self 17.4%, `ConflictGap` 8.7% |
| 4 Gate | 14.6% | self 8.6%, `TryGrant` 4.3% |
| 10 Snapshot | 9.2% | |
| 7 Transition | 7.8% | `AssignConnectors` 5.4% |
| 3 Route | 4% | |
| прочее (1, 2, 8, 9) | ≈4.4% | |

Профиль смещён к safe point (`lessons/env-and-tools.md`): доли — ориентир. Поэтому ниже — **детерминированные счётчики работы**.

### 1.2 Счётчики работы (замер этого шага)

Как снято: дерево `579dc98` выгружено в scratchpad, временный статический зонд-счётчик (не в репо) в
`GateRules`, `BehaviourPhase`, `LeaderSearch`, `GatePhase`, `SnapshotPhase`, `RouteNav`; `bench --scenario s10k
--warmup-ticks 2400 --warmup-until-full --ticks 600 --repeat 1` (рампа 5 900 тиков, активных 9 820 → 9 998).
Два прогона дали **одинаковые** числа (детерминизм). Средние за тик окна:

| Счётчик | /тик | Комментарий |
|---|---|---|
| машин в фазе 5 | 9 970 | |
| `ConflictGap` из фазы 5 | 3 174 | 829 машин на connector'ах + 2 495 допущенных на Normal-полосах (без части без конфликтов) |
| `ConflictGap` из фазы 6 | **3 174** | **те же вызовы с теми же аргументами** — полный дубль |
| записей конфликтов просмотрено (фаза 5; столько же в 6) | 10 639 | 3.35 на вызов; Cross 7 778, Merge 2 861 |
| из них различных записей за тик | 8 124 | мемо «на запись за тик» сэкономило бы ≤ 24% — мало |
| машин-участников, просмотренных в `CrossBlocked` | **282** | почти все списки чужих connector'ов **пусты** |
| вызовов, где **все** записи «холостые» (на чужом connector'е никого, допущенных на него нет) | **2 122 (67%)** | работа = только чтение структур, без результата |
| «холостых» записей (по всем записям вызова) | 13 530 | |
| `ObstacleGap` из фазы 5 | 9 970 | каждая машина; ещё 9 970 в фазе 6 |
| — недопущенная далеко (вне дистанции решения) | 4 367 | ответ FreeGap после `GateNode` (3 зависимых чтения) |
| — стоп-линия | 2 163 | |
| — полоса не к узлу с воротами | 116 | в s10k почти все узлы с воротами (`minor_control: yield`) |
| голов полос (`Ahead == -1`) | 4 845 | **половина машин** — одна на полосе (сеть разрежена) |
| переходов по цепочке полос в `LeaderSearch` (фаза 5; столько же в 6) | 8 732 | 1.8 на голову; каждый — `NextLane` → окно маршрута |
| — 1-й переход с Normal-полосы: `NextConn[i]` == `NextLane(i, lane, 1)` | 4 024 / 4 024 | **0 расхождений**, 0.2 /тик без `NextConn` |
| кандидатов ворот | 1 121 | `TryGrant` 494, записей конфликтов в них 3 114 |
| проверок окна маршрута (`QueueRefill`) в `AssignConnectors` | 8 407 | каждая машина каждый тик |
| пересчётов `NextConn` | 855 | ≈ машины на connector'ах (там `NextConn` всегда −1 и «устаревший») |
| слотов снапшота с `Speed == 0 && Lateral == 0` | 1 555 (15.6%) | пропуск неподвижных дал бы ≤ 1.5% тика — не в плане |

Tick p95 этих прогонов (с зондом, CPU делился с другим агентом): 9.5 / 10.3 мс — для ориентира, не для отчёта.
В обоих окнах `alloc_bytes_per_tick` 23–30 B — **проверить на чистом коде** (открытый вопрос §6).

## 2. Разбор

### 2.1 Почему `ConflictGap` зовётся из двух фаз
`GateRules.ObstacleGap` — единый источник «виртуального препятствия ворот» (стоп-линия либо конфликтная точка,
занятая машиной с меньшим билетом). Фаза 5 берёт из него IDM-ускорение, фаза 6 — жёсткий клэмп I6 (§7.6
core-api): машина не пересекает стоп-линию без допуска и конфликтную точку при любых IDM-параметрах. Фаза 6
вызывает его заново с `SOld` — копией `S` на начало фазы 6. Но фаза 5 **ничего не двигает** (пишет только `Accel`,
`LaneChangeDir`), поэтому `SOld == S` фазы 5, а `GateState/GrantConnector/Ticket`, списки полос и маршруты между
фазами 5 и 6 не меняются. Итог: **`LeaderSearch.FindFor` и `ObstacleGap` в фазе 6 — точный повтор фазы 5**.
Единственные отличия: (а) фаза 5 ограничивает видимость стоп-линии дистанцией решения, фаза 6 — нет;
(б) фаза 6 обрабатывает и автобусы на остановке (`AtStop`), которые фаза 5 пропускает до поиска лидера.

### 2.2 Что пересчитывается каждый тик, хотя могло бы храниться
| Что | Где | Сколько | Чем заменить |
|---|---|---|---|
| лидер + препятствие ворот (зазоры клэмпа) | фаза 6 | 9 970 × (`FindFor` + `ObstacleGap`) | взять из фазы 5 (D2) |
| «есть ли кто на чужом connector'е» | `ConflictGap` | 10 639 записей, 67% вызовов впустую | маска занятых connector'ов узла (D3) |
| `Lanes[other].FromLane/Length`, `Nodes[..].FirstConnector` | `ConflictGap`, `MergeGap` | на каждую запись | SoA `ConnectorStore` по группе (D4) |
| `GateNode(lane)` (`Lanes[lane]` → `LaneNext` → `Lanes[next]` → `Nodes[node]`) | фазы 4, 5, 6, 7 | ~10 000 × 3–4 фазы | статический `LaneGateNode[lane]` (D5) |
| `NextLane(i, lane, 1)` (маршрут → окно → `Lanes[c]`) | `LeaderSearch`, ворота | 4 024 × 2 | `NextConn[i]` — уже посчитан (D6) |
| положение в окне маршрута (`QueueRefill`) | конец фазы 7 | 8 407 | только у машин, сменивших полосу/окно (D7) |

### 2.3 Раскладка данных
- `LaneData` — AoS 60 B. На s10k `MaxLanes` = 48 000 → **2.9 МБ**: больше L2, случайный доступ (полоса машины,
  полоса лидера, чужой connector) — промах в L3. В горячих фазах из неё нужны 2–3 поля (`Kind`, `Length`,
  `ConflictGroup`/`ToLane`/`FromLane`) — остальные 50 B тянутся зря.
- `VehicleStore` — SoA (≈ 30 массивов × 10 000): обход по индексу потоковый; соседи (лидер, участники конфликта)
  — случайные чтения, но рабочее множество горячих массивов (S, Speed, Length, Lane, Ahead, Ticket, GateState)
  ≈ 250 КБ — живёт в L2.
- `ConnectorStore` — уже SoA (`InLane`, `OutLane`, `Node`, `FirstConflict`, `ConflictCount`, `ConflictMask`),
  но горячий код его не использует, а ходит в `LaneData`/`NodeData`.
- Ветвления: в фазе 5 на машину ≈ 6 data-dependent веток (жива, на полосе, `AtStop`, есть лидер, вид полосы,
  допуск, автобус); все хорошо предсказуемы, кроме вида полосы/допуска. SIMD тут не поможет — путь состоит из
  gather-чтений по индексам.

### 2.4 Где O(n·k) вместо O(n)
- Фаза 6: O(N·(лидер + ворота)) вместо O(N) — дубль фазы 5.
- `ConflictGap`: O(вызовы × записи × участники); участников почти нет — стоимость в O(вызовы × записи)
  случайных чтений, 2/3 которых ничего не дают.
- `AssignConnectors`: O(N) случайных чтений окна маршрута на каждом тике, хотя меняется оно у ≈ 1–8% машин.
- Остальное линейно и разумно: ворота — O(N) проход + O(кандидаты × записи), снапшот — O(N), MOBIL — O(N/N_mobil).

## 3. Решения (подробно; сводка и альтернативы — ADR-0016)

Все решения D2–D7 — **побитово эквивалентны** текущему коду: тот же хэш состояния на каждом тике, те же счётчики
`SimCounters`. Это и есть критерий приёмки (§5).

### D1. Инструменты: время фаз и счётчики работы
- `Simulation.OnPhaseEnd : PhaseProbe?` (core-api §3) — реализовать. `PhaseList.RunTick` после каждой фазы
  (номер по §7; подфазы 2b/4a/8b — в составе своих номеров) делает `w.PhaseProbe?.Invoke(n)`. Вызов 10 раз за тик,
  делегат задаётся хостом один раз — 0 B. Ядро время не меряет.
- `SimWork` — новая структура `long`-счётчиков работы (не `SimCounters`: **не входит в хэш**, может меняться при
  оптимизациях): `Simulation.Work` (`ref readonly`). Минимальный набор (имена JSON):
  `behaviour_vehicles`, `leader_lookahead_hops`, `obstacle_gap_calls`, `conflict_gap_calls_behaviour`,
  `conflict_gap_calls_integrate`, `conflict_gap_mask_rejects` (с D3), `conflict_entries_scanned`,
  `conflict_occupants_scanned`, `gate_candidates`, `gate_trygrant_calls`, `gate_trygrant_entries`,
  `route_refill_checks`, `next_conn_recomputes`, `snapshot_slots`. В горячих циклах — инкремент локальной `int`
  и сложение в поле один раз в конце фазы/вызова (стоимость ≤ 1% тика).
- `bench`: флаг `--phases` → `perf.phase_ms.{commands,spawn,route,gate,behaviour,integrate,transition,arrival,
  metrics,snapshot}.{p50,p95,mean}` (хост: `Stopwatch.GetTimestamp()` в зонде, буферы `double[ticks]` на фазу,
  выделенные до окна) и всегда — `work_per_tick.{...}` (средние за окно, из `SimWork`). `compare` печатает разницу
  `work_per_tick` и `phase_ms`, **не падает** по ним (работа меняется намеренно).
- `Sim.Headless hash --scenario <s> --ticks N [--every K] [--warmup-until-full]` — печатает
  `ComputeStateHash()` каждые K тиков: эквивалентность на s10k при полной загрузке без бенча.
- Тест `HotPathGoldenHashTests`: s1k (3 000 тиков) и `demo-grid`/фикстура ворот — хэши-константы, снятые на
  коммите **до** первой правки (D2). Меняются только осознанно (как baseline): отдельный коммит с объяснением.

### D2. Фаза 5 считает зазоры клэмпа, фаза 6 — потоковая
- `VehicleStore`: `float[] ClampLeadGap`, `byte[] ClampFlags` (бит0 «лидер есть», бит1 «лидер виртуальный»),
  `float[] ClampGateGap` (`Idm.FreeGap` = нет). `SOld` удаляется.
- Фаза 5 для **каждой** машины, которую обрабатывает фаза 6 (жива, `Lane ≥ 0`, **включая `AtStop`**), пишет
  `li.Gap`/флаги `FindFor(S, i)` и «сырой» (без ограничения видимости) зазор `ObstacleGap`. Ограничение видимости
  для IDM — отдельным выходом: `ObstacleGap(..., out bool beyondDecision)`; IDM берёт FreeGap при
  `beyondDecision`, клэмп — сырой зазор. Для `AtStop`: `Accel = 0`, `LaneChangeDir = 0`, но зазоры считаются.
- Фаза 6: `v' = clamp(v + a·dt)`, `s' = s + v'·dt`, затем **те же два клэмпа в том же порядке и тем же
  выражением** (`sMax = s + gap − (virtual ? 0 : MinPhysicalGap)`; затем `sMax = s + gateGap`), `HardClamps++`
  на каждый сработавший — иначе разойдутся `SimCounters` и float-результат. Никаких чтений чужих машин.
- Корректность I6: зазоры посчитаны по позициям на начало тика (фаза 5 никого не двигает) — ровно то, что
  требует §7.6 (`S_old`).
- Выигрыш: фаза 6 27% → ≈ 2–3% (потоковый проход), фаза 5 +≈1% (запись трёх массивов): **−20…−25% тика**.

### D3. Маска занятых connector'ов узла
- `GateScratch`: `ulong[] NodeBusy` (`[MaxNodes]`, биты = `LocalBit`), `int[] BusyTouched` + счётчик (обнуление
  на следующем тике, как `OutTouched`).
- Фаза 4 (`GatePhase`) ставит бит connector'а `c`: машина на connector'е `c` с воротами (в проходе сбора — она
  уже там смотрится); допущенная машина с `GrantConnector == c` (после проверки «допуск от этой полосы»); каждый
  новый допуск в `TryGrant`. Маска — **надмножество** участников `CrossBlocked`/`MergeGap` (они видят только
  машины на `c` и допущенные на `c` с входной полосы), поэтому пропуск по нулевому биту точен.
- `ConflictGap`: `if ((cs.ConflictMask[g] & NodeBusy[node]) == 0) return FreeGap;` и в цикле — `continue`
  для записи, чей бит не занят. Маска валидна от конца фазы 4 до конца фазы 6 (между ними списки connector'ов
  и допуски не меняются; после D2 `ConflictGap` зовётся только в фазе 5).
- Выигрыш: 67% вызовов — O(1), остальные пропускают холостые записи: `ConflictGap` 9.8% → ≈ 3%: **−6…−7%**.

### D4. Горячие чтения — из SoA `ConnectorStore`, а не из `LaneData`
- Группа чужого connector'а: `gOther = g − LocalBit[g] + OtherLocal` (connector'ы узла подряд; проверить
  `Debug.Assert`). Читать `cs.InLane[gOther]`, `cs.Lane[gOther]`; добавить `float[] Length` (длина connector'а)
  и `float[] InLength` (длина входной полосы) в `ConnectorStore` (заполняются в `BuildConflicts`, при
  `BuildRoad` — там же). `Nodes[cs.Node[g]].FirstConnector` → `cs.Lane[g] − cs.LocalBit[g]`.
- `ConflictEntry` не меняется (8 B). То же применить в `TryGrant` (4a/4b).
- Выигрыш входит в D3 (оставшиеся записи без промахов по `LaneData`) + `TryGrant` ≈ −1%.

### D5. `LaneGateNode` и горячая копия длины полосы
- `NetworkStore`: `int[] LaneGateNode` (`[MaxLanes]`: для Normal — узел с воротами в конце или −1; для connector'а
  — его узел, если у узла ворота, иначе −1), `float[] LaneLength`. Пересчёт: построение сети, `BuildRoad`,
  `SetNodeControl` (команды фазы 1) — для затронутых полос. Производные данные, в хэш не входят.
- `GateRules.GateNode/IsGatedConnector` → одно чтение; в фазах 4, 5, 7 и `ObstacleGap` — через них.
  `ld.Length` в горячих местах → `LaneLength[lane]`.
- Выигрыш: `ObstacleGap` self 6.2% → ≈ 2.5%, проход сбора ворот 8.6% → ≈ 6%: **−5…−7%**.

### D6. Первый переход `LeaderSearch` (и кандидат ворот) — через `NextConn`
- `NextConn[i]` уже хранит `NextLane(i, Lane[i], 1, useRoute: true)` для Normal-полосы (пересчёт в конце фазы 7,
  сброс при смене маршрута). Замер: 4 024/4 024 совпадений, 0 расхождений. В `LeaderSearch.Find` для
  `hop == 1 && useRoute && self на Normal-полосе` и в сборе кандидатов ворот брать `NextConn[i]`, если он `≥ 0`,
  иначе — прежний `NextLane`. Инвариант: на входе фазы 4/5 `NextConn` посчитан для текущей `Lane` либо −1 (смены
  полосы — только в фазе 7 до `AssignConnectors`; спавн ставит его сам; фаза 3 при смене маршрута сбрасывает в −1).
  В Debug — `Debug.Assert` на равенство с `NextLane` (тесты идут в Debug — проверка эквивалентности на каждом вызове).
- Выигрыш: часть self фазы 5 (цепочка `RouteStepIndex` → `Steps` → `Lanes`): **−2…−4%**.

### D7. `AssignConnectors` — только для изменившихся
- Флаг `VehicleFlags`/байт `RouteDirty[i]`, ставится везде, где меняется вход `QueueRefill`/`NextConnector`:
  переход полосы и смена полосы (фаза 7), спавн, новый маршрут/окно/дозаполнение и снятие `RefillQueued`
  (фаза 3), промах (`RouteMissed`), автобусная логика (`BusStopPhase` сбрасывает `NextConn`). `AssignConnectors`
  обходит машины по возрастанию индекса, как сейчас (порядок очереди дозаполнения — часть детерминизма), но
  работу делает только при флаге; флаг снимается.
- Риск: забытый источник изменения → расхождение хэша; поэтому в Debug-сборке — полный старый расчёт с
  `Debug.Assert` на равенство решения.
- Выигрыш: 5.4% → ≈ 1.5%: **−3…−4%**.

### Не в плане (см. ADR-0016, «Альтернативы»)
Мемо `CrossBlocked` на запись за тик (≤ 24% повторов — вытеснено D3); полное слияние фаз 5+6 (нужен двойной
буфер `Speed` и чтение всеми правилами «старых» массивов, сверх D2 экономит один потоковый проход ≈ 1%); SIMD;
пропуск неподвижных в снапшоте (≤ 1.5%); снижение частоты проверок ворот (ломает I6). **Резерв** (если после D1–D7
p95 > 4 мс): обход фазы 5 по полосам (фаза 5 чистая — результат не зависит от порядка) и детерминированное
распараллеливание фазы 5 на 2 потока — отдельным ADR-дополнением.

## 4. Ожидаемый суммарный выигрыш

| | доля тика (профиль) | после | Δ |
|---|---|---|---|
| D2 | Integrate 27% | ≈ 3% (+1% в фазе 5) | −20…−25% |
| D3+D4 | `ConflictGap` 9.8% + `TryGrant` 4.3% | ≈ 3% + 3.3% | −7…−8% |
| D5 | `ObstacleGap` 6.2% + Gate self 8.6% | ≈ 2.5% + 6% | −5…−7% |
| D6 | часть Behaviour self 16.1% | | −2…−4% |
| D7 | `AssignConnectors` 5.4% | ≈ 1.5% | −3…−4% |
| **Итого** | | | **−37…−48%** |

7.85 мс × (0.52…0.63) ≈ **4.1–4.9 мс**, середина ≈ 4.4 мс (×1.8). Цель ×1.96 одними D2–D7 достижима только
на верхней границе. Поэтому: D1 первым (фазовые времена переранжируют оценку), после D2–D7 — замер, и при
p95 > 4 мс — резерв (обход по полосам, затем 2 потока фазы 5: фаза 5 после D2–D6 ≈ 30% нового тика, 2 потока
дают ещё −12…−15%). Доли профиля смещены к safe point, поэтому диапазоны широкие.

## 5. План для исполнителей (sonnet), строго последовательно

Общее: ff на рабочую ветку; после мержа 2.4a (stuck) — она правит `GatePhase`/`TransitionPhase`, иначе конфликты
и смена golden-хэша. Каждая задача D2–D7: golden-хэш **не меняется**, `SimCounters` те же, 0 B/тик, тесты зелёные,
`dotnet format`. Замеры времени — только верификатор (haiku) после мержа: `bench s10k --warmup-until-full
--phases` + `hash` + bench-gate s1k.

| # | Задача | Файлы | Тесты | Ожидаемо | Приёмка |
|---|---|---|---|---|---|
| H1 | **D1** зонд фаз, `SimWork`, `bench --phases`/`work_per_tick`, `compare` печатает, команда `hash`, golden-хэш-тест (снять константы **до** правок) | `Api/Simulation.cs`, `Api/SimWork.cs` (новый), `World/World.cs`, `World/PhaseList.cs`, счётчики в `BehaviourPhase`, `GateRules`, `LeaderSearch`, `GatePhase`, `RouteNav`, `SnapshotPhase`; `Sim.Headless/Bench/*`, `HashCommand.cs` | зонд вызывается 10 раз/тик по порядку; `SimWork` одинаков в двух прогонах; 0 B с зондом; golden-хэш s1k | 0 | хэш не изменился; s1k bench-gate OK (overhead в шуме); JSON с `phase_ms`, `work_per_tick` |
| H2 | **D2** зазоры клэмпа в фазе 5, потоковая фаза 6, удалить `SOld` | `Rules/BehaviourPhase.cs`, `Agents/IntegratePhase.cs`, `Rules/Intersections/GateRules.cs` (`ObstacleGap` с `beyondDecision`), `Agents/VehicleStore.cs` | golden-хэш; I6-тесты; автобус `AtStop` с отрицательным зазором (HardClamps как раньше); 0 B | −20…−25% | `conflict_gap_calls_integrate = 0`; хэш тот же |
| H3 | **D3+D4** маска занятых connector'ов, SoA-чтения `ConnectorStore` | `Rules/Intersections/GateScratch.cs`, `GatePhase.cs`, `GateRules.cs`, `ConnectorStore.cs` | golden-хэш; юнит: маска ⊇ участников (перебор в тесте) на фикстурах T1–T8 | −7…−8% | `conflict_entries_scanned` −60% и больше; `conflict_gap_mask_rejects` ≈ 2/3 вызовов |
| H4 | **D5+D6** `LaneGateNode`/`LaneLength`, первый переход через `NextConn` | `Network/NetworkStore.cs`, `NetworkBuilder*.cs`, `Api/CommandDispatcher.cs` (`SetNodeControl`/`BuildRoad`), `GateRules.cs`, `LeaderSearch.cs`, `GatePhase.cs`, `TransitionPhase.cs` | golden-хэш; `Debug.Assert` `NextConn == NextLane`; тест: `SetNodeControl` пересчитывает `LaneGateNode` | −7…−11% | `leader_lookahead_hops` с `NextLane` −45%; хэш тот же |
| H5 | **D7** `AssignConnectors` по флагу | `Routing/RouteNav.cs`, `RoutePhase.cs`, `Agents/TransitionPhase.cs`, `Spawner.cs`, `Transit/BusStopPhase.cs` | golden-хэш; Debug-сверка с полным расчётом | −3…−4% | `route_refill_checks` −80% |

После H5 — верификатор: s10k p95 при 10 000 активных. ≤ 4 мс — шаг закрыт (отчёт before/after по `phase_ms` и
`work_per_tick`). > 4 мс — архитектор (opus) выбирает резерв по фазовым временам.

## 6. Открытые вопросы
1. ~~В прогонах счётчиков (код с зондом) `alloc_bytes_per_tick` s10k = 23–30 B.~~ Закрыто (H1): на чистом коде `274dc1b`
   `bench --scenario s10k --warmup-until-full --ticks 1000` = **0 B/тик**, gen0/1/2 = 0 (активных 9 818 -> 9 999); после H1
   (зонд + `SimWork`) тоже 0 B на 2 000 тиков. 23–30 B давал временный статический зонд-счётчик архитектора (вне репо).
2. 2.4a (stuck) может менять правила ворот → golden-хэш H1 снимать после её мержа.
3. Порт на Rust: всё выше — плоские массивы и маски, без указателей и потоков; зонд фаз — `extern "C" fn(i32, *mut c_void)`.

## 7. Состояние: H1 выполнена (инструмент D1)
Реализовано как в D1, с уточнениями: `LeaderInfo.Hops` несёт число переходов цепочки полос (без смены сигнатур `Find`);
`ObstacleGap`/`ConflictGap` получили `ref SimWork`; `conflict_gap_calls_behaviour/integrate` — разность сырого
`SimWork.ConflictGapCallsRaw` до/после цикла фазы; `conflict_entries_scanned` считает все просмотренные записи цикла
(включая пропущенные `continue`), поэтому в 2.3 раза больше, чем «различных» (§1.2). `work_per_tick` лежит в `perf.work_per_tick`
и пишется всегда, `perf.phase_ms` — только с `--phases` (зонд добавляет ~10 чтений таймера на тик; tick_ms такого прогона для гейта не годится).
`compare` печатает `work.*` и `phase_ms.*.p50/p95` справочно (не влияет на exit code). Команда `hash` и golden-тест
(`tests/Sim.Headless.Tests/HotPathGoldenHashTests.cs`): хэши сняты на `274dc1b` до правок и совпали после H1, в том числе
при полной загрузке s10k (`hash --scenario s10k --warmup-until-full --ticks 400 --every 200`: tick=6100 `861672e99fa95a39`,
tick=6300 `c73832373fb67bd3`). **С 2.6r R1** (снятие допуска на красном) эталон полной загрузки s10k другой: tick=6100 `930f16b7951f5fd7`,
tick=6300 `abdc420cf70ba05b` (1, 2 и 3 потока); значения `861672e99fa95a39`/`c73832373fb67bd3` ниже в записях H1–P6 - приёмка тех шагов, исторические.

«До» для H2–H5: `bench --scenario s10k --warmup-until-full --ticks 2000 --repeat 1 --phases` (активных 9 818 -> 9 997,
рампа 5 900 тиков, 0 B/тик, stuck 0, tick p50/p95 5.12/7.96 мс — общий 4-vCPU контейнер, load ~3), мс на тик:

| фаза | p50 | p95 | mean |
|---|---|---|---|
| 1 commands | 0.001 | 0.001 | 0.001 |
| 2 spawn | 0.005 | 0.012 | 0.006 |
| 3 route | 0.243 | 0.685 | 0.281 |
| 4 gate | 0.784 | 1.246 | 0.870 |
| 5 behaviour | 1.628 | 2.654 | 1.813 |
| 6 integrate | 1.421 | 2.363 | 1.608 |
| 7 transition | 0.380 | 0.620 | 0.419 |
| 8 arrival | 0.044 | 0.080 | 0.051 |
| 9 metrics | 0.118 | 0.213 | 0.137 |
| 10 snapshot | 0.372 | 0.635 | 0.417 |

`work_per_tick`: behaviour_vehicles 9 981, leader_lookahead_hops 16 536, obstacle_gap_calls 19 975,
conflict_gap_calls_behaviour 3 149, conflict_gap_calls_integrate 3 149, conflict_gap_mask_rejects 0 (до H3),
conflict_entries_scanned 24 246, conflict_occupants_scanned 4 083, gate_candidates 1 085, gate_trygrant_calls 1 085,
gate_trygrant_entries 1 358, route_refill_checks 9 994, next_conn_recomputes 809, snapshot_slots 9 996.

## 7.1 H2 выполнена (D2: зазоры клэмпа из фазы 5)
Реализовано как в D2: `VehicleStore.ClampLeadGap/ClampFlags/ClampGateGap` (сброс в `Idm.FreeGap`/0 при освобождении слота), `SOld` и
`Array.Copy` удалены; `GateRules.ObstacleGap` вместо `visibilityLimited` возвращает сырой зазор и `out bool beyondDecision`
(IDM фазы 5 пропускает стоп-линию дальше дистанции решения, клэмп берёт зазор всегда). Фаза 5 считает лидера и ворота и для `AtStop`
(`Accel = 0`, `LaneChangeDir = 0`); `behaviour_vehicles` по-прежнему без `AtStop`, `leader_lookahead_hops`/`obstacle_gap_calls` их включают.
`IntegratePhase.Run` теперь `(net, v, types, p, ref c)`: `occ`, `routes`, `work`, `connectors` не нужны. Тесты `PhasesTests` (клэмп фазы 6)
перед `IntegratePhase.Run` вызывают `BehaviourPhase.Run`, потому что зазоры пишет фаза 5.

Приёмка: golden-хэши `HotPathGoldenHashTests` (s1k, s10k) не менялись; `hash --scenario s10k --warmup-until-full --ticks 400 --every 200`:
tick=6100 `861672e99fa95a39`, tick=6300 `c73832373fb67bd3` (как до правок); `dotnet test` зелёный (Core 393, Headless 107); 0 B/тик.

«После»: `bench --scenario s10k --warmup-until-full --ticks 2000 --repeat 1 --phases` (активных 9 818 -> 9 997, 0 B/тик, stuck 0,
tick p50/p95 4.73/6.39 мс при load ~3 на общем контейнере; «до» 5.12/7.96), мс на тик (mean, в скобках «до»):
behaviour 2.076 (1.813), **integrate 0.165 (1.608)**, gate 0.960 (0.870), transition 0.540 (0.419), snapshot 0.436 (0.417), route 0.294 (0.281);
рост прочих фаз - шум общего контейнера. `work_per_tick`: `conflict_gap_calls_integrate` 3 149 -> **0**, `leader_lookahead_hops` 16 536 -> 8 278,
`obstacle_gap_calls` 19 975 -> 9 994, `conflict_entries_scanned` 24 246 -> 12 123, `conflict_occupants_scanned` 4 083 -> 2 041,
`conflict_gap_calls_behaviour` 3 149 -> 3 149. `scripts/bench-gate.sh --baseline perf/baseline.json` (s1k): OK.

## 7.2 H3 выполнена (D3+D4: маска занятых connector'ов, SoA-чтения ворот)
Реализовано как в D3/D4. `GateScratch.NodeBusy/BusyTouched` (обнуление по `BusyTouched` в начале фазы 4). Бит `LocalBit` ставит `GatePhase.MarkBusy`
в проходе сбора (машина на connector'е; допущенная на Normal-полосе после проверки «допуск от этой полосы») и в `TryGrant` при каждом новом допуске.
Обновлять маску между фазой 4 и фазой 5 не нужно: списки connector'ов, `GateState` и `GrantConnector` меняются только в фазе 7 и в `TryGrant`;
после D2 `ConflictGap` зовётся только из фазы 5, фаза 6 маску не читает. Маска валидна от конца фазы 4 до конца фазы 5 (в спеке - «до конца фазы 6»,
это то же самое, раз фаза 6 потоковая).
`ConflictGap`/`ObstacleGap` получили параметр `ulong[]? nodeBusy`; `null` (юнит-тесты без фазы 4) = без отсечения. Выход: `ConflictMask[g] & NodeBusy[node] == 0`
(`conflict_gap_mask_rejects++`), в цикле - `continue` для записи с незанятым битом (после проверки дистанции/`break`). D4: `ConnectorStore.Length/InLength`
(заполняются в `Register`, в хэш не входят); `ConflictGap`, `MergeGap`, `CrossBlocked`, `TryGrant` берут `InLane/OutLane/Length/InLength/Lane/LocalBit` по
`gOther = g - LocalBit[g] + OtherLocal` вместо `LaneData`. Тест `NodeBusyMaskTests`: после каждой фазы 4 маска ⊇ участников (перебор), `ObstacleGap` с маской == без маски.

Приёмка: golden-хэши `HotPathGoldenHashTests` не менялись; `hash --scenario s10k --warmup-until-full --ticks 400 --every 200`: tick=6100 `861672e99fa95a39`,
tick=6300 `c73832373fb67bd3`; `dotnet test` зелёный (Core 395, Headless 107); 0 B/тик; `dotnet format` чисто; `scripts/bench-gate.sh` (s1k): OK.

«После» (`bench --scenario s10k --warmup-until-full --ticks 2000 --repeat 1 --phases`, активных -> 9 997, 0 B/тик, stuck 0, tick p50/p95 4.42/5.57 мс при load ~5
на общем контейнере; после H2 было 4.73/6.39), мс на тик (mean, в скобках после H2): behaviour 1.736 (2.076), gate 1.035 (0.960), integrate 0.174 (0.165),
transition 0.509 (0.540), snapshot 0.427 (0.436), route 0.280 (0.294). Выигрыш виден в фазе 5; gate не изменился (шум контейнера).
`work_per_tick` (после H2 -> H3): `conflict_gap_mask_rejects` 0 -> **2 046** (65% из 3 149 вызовов), `conflict_entries_scanned` 12 123 -> **3 844** (-68%),
`conflict_occupants_scanned` 2 041 -> 1 130, `conflict_gap_calls_behaviour` 3 149 -> 3 149, `gate_trygrant_entries` 1 358 (как было).

## 7.3 H4 выполнена (D5+D6: `LaneGateNode`/`LaneLength`, первый переход через `NextConn`)
Реализовано как в D5/D6. `NetworkStore.LaneGateNode` (`int[MaxLanes]`, заполнен -1) и `LaneLength` (`float[MaxLanes]`) - производные, в `Hash()` не входят.
`LaneLength` пишут `EmitLane` и построение connector'ов. `LaneGateNode` пересчитывает `NetworkStore.RefreshLaneGateNode(node)` (connector'ы узла и их входные полосы:
`Control != None` ? узел : -1) в конце `BuildConnectorsEx` (после `RefreshArmFlags`, который может нормализовать `Control`; это же покрывает `BuildRoad` - он строит
connector'ы только новых узлов, достроенный узел отклоняется `NotSupported`) и в `NetworkBuilder.SetNodeControl` (им пользуется команда `SetNodeControl`).
`GateRules`: `GateNodeOfNormal` / `IsGatedConnector(net, lane)` - одно чтение массива + `Debug.Assert` против прежнего расчёта (`GateNodeSlow`, `IsGatedConnectorSlow`);
`GateNode(net, lane)` оставлен для тестов (для connector'а по-прежнему -1). Через них идут `ObstacleGap`, сбор кандидатов фазы 4, `TryAdvanceLane` (фаза 7);
`ld.Length` в `ObstacleGap`, фазе 4 (кандидат, `TryGrant`) и `LeaderSearch` заменён на `LaneLength`.
D6: `RuleEnv.FirstHop(i, lane)` - `NextConn[i]`, если `>= 0` (в Debug - `Debug.Assert(NextConn == NextLane(i, lane, 1, true))`), иначе прежний `NextLane`. Используют
`LeaderSearch.Find` (при `hop == 1 && useRoute && lane == V.Lane[self]`) и выбор connector'а кандидата ворот в `GatePhase`.
Тесты: `LaneDerivedArraysTests` (массивы == прежний расчёт для всех полос после `Create`, после `BuildRoad`, после `SetNodeControl` Signal/Yield/Stop/RHR, переключение
None <-> Yield через `Builder.SetNodeControl`). Debug-сверка нашла нарушение инварианта D6 только в тестовой фикстуре `GateFixture.Place` (выставляла `NextConn` в произвольный
connector без маршрута, хотя ядро берёт тогда connector по умолчанию) - фикстура теперь ставит `ChooseConnector`; ядро инвариант не нарушало.

Приёмка: golden-хэши `HotPathGoldenHashTests` не менялись; `hash --scenario s10k --warmup-until-full --ticks 400 --every 200`: tick=6100 `861672e99fa95a39`, tick=6300
`c73832373fb67bd3` (active 9 997); `dotnet test traffic-city.sln` зелёный в Debug со всеми сверками (Core 402, Headless 107); `dotnet build -warnaserror` и `dotnet format --verify-no-changes` чисто.

«После» (`bench --scenario s10k --warmup-until-full --ticks 2000 --repeat 1 --phases`, активных 9 818 -> 9 997, 0 B/тик, stuck 0, tick p50/p95/p99 3.47/4.41/5.39 мс при load ~3
на общем контейнере; после H3 было 4.42/5.57 при load ~5), мс на тик (mean, в скобках после H3): behaviour 1.287 (1.736), gate 0.758 (1.035), integrate 0.159 (0.174),
transition 0.482 (0.509), snapshot 0.373 (0.427), route 0.271 (0.280). Падение всех фаз, включая не тронутые snapshot/route, показывает, что часть выигрыша - разная загрузка
контейнера, а не код; чистый вклад H4 оценивать по фазам, где менялся код (behaviour, gate, transition), и по `work_per_tick`.
`work_per_tick` не меняется (H4 убирает зависимые чтения, а не вызовы): `leader_lookahead_hops` 8 278, `obstacle_gap_calls` 9 994, `conflict_gap_calls_behaviour` 3 149,
`conflict_gap_mask_rejects` 2 046, `conflict_entries_scanned` 3 844, `gate_trygrant_entries` 1 358, `next_conn_recomputes` 809 - как после H3. Счётчик `leader_lookahead_hops`
считает переходы, а не вызовы `NextLane`, поэтому «-45%» из таблицы §5 по нему не наблюдается; эффект D6 - в замене цепочки `RouteStepIndex -> Steps -> Lanes` на одно чтение.
`scripts/bench-gate.sh --baseline perf/baseline.json` (s1k): OK.

## 7.4 H5 выполнена (D7: `AssignConnectors` по флагу) и итог шага 2.4
Реализовано как в D7. `VehicleStore.RouteDirty` (`byte[]`, вне хэша; свежий слот и `ResetSlot` = 1). Флаг ставится: каждым присвоением `Lane` в `LaneOccupancy`
(`PushTail`/`PushHead`/`InsertBetween`/`BeginLaneChange`/`CancelLaneChange` - это смена полосы, переход, спавн, MOBIL); при каждом изменении `Route`/`RouteCursor`/`RouteGen`
(`RouteNav.Advance` - три `RouteCursor++` и `RouteMissed`, `RoutePhase`: новый маршрут из очереди поездок/перемаршрутизации и плановая замена, `BusStopPhase`, `Spawner`);
при снятии `RefillQueued` и отметке `RouteMissed` в `RefillWindows` (окно/план меняет `Router.Refill`); командами `BuildRoad` (`GrowOverlay`), `SetNodeControl`, `SetSignalPlan` -
`MarkAllRouteDirty()` (все машины). Сама `AssignConnectors` снимает флаг после обработки и оставляет его, если `RefillQueue` полна (постановка в очередь не удалась - повтор на
следующем тике, как раньше). `QueueRefill` разделён на чистый предикат `NeedsRefill` и постановку. В Debug для каждой машины без флага `Debug.Assert(UnchangedByFullRecalc)`:
нет `RouteMissed`, `NeedsRefill == false`, `NextConn` валиден либо равен `NextConnector` - то есть полный старый расчёт ничего бы не изменил. Порядок очереди дозаполнения
прежний (обход по возрастанию индекса, в очередь попадают только изменившиеся - неизменившиеся и раньше не ставились). Тест `RouteDirtyTests`: в стационаре проверок < 1/4 живых;
после `BuildRoad` - не меньше 90% машин на полосах.

Приёмка: golden-хэши `HotPathGoldenHashTests` не менялись; `hash --scenario s10k --warmup-until-full --ticks 400 --every 200`: tick=6100 `861672e99fa95a39`, tick=6300
`c73832373fb67bd3` (active 9 997). `Report_S1k` (`TC_STEP3_REPORT=1`, Release, 36 000 тиков = 30.2 мин игрового времени) до и после правки совпал во всех счётчиках: spawned 7 253,
completed 6 220, trips/min 206.19, avg_speed 32.88, reroutes 1 372, timed_reroutes 835, queries 11 024, grants 106 049, denied_exit 408 376, inversions 121, hard_clamps 1 055,
lane_changes 9 580, connector_entries 103 335, **stuck 0** (tick mean/p50/p95 до 186.9/160.3/295.5 мкс, после 164.8/139.7/279.4). `dotnet test traffic-city.sln` в Debug со всеми сверками
зелёный (Core 404, Headless 107); `dotnet build -warnaserror`, `dotnet format --verify-no-changes` чисто; 0 B/тик; `scripts/bench-gate.sh --baseline perf/baseline.json` (s1k): OK.

Замер «до/после» (`bench --scenario s10k --warmup-until-full --ticks 2000 --repeat 1 --phases`, поочерёдно «до» (дерево `3d206a6`), «после», 2 раунда; load average 1.4-1.8, активных 9 818 -> 9 997,
0 B/тик, stuck 0), мс на тик:

| | tick p50 | tick p95 | tick p99 | transition mean | behaviour mean | gate mean |
|---|---|---|---|---|---|---|
| до, раунд 1 | 3.344 | 5.004 | 5.663 | 0.483 | 1.302 | 0.786 |
| после, раунд 1 | 3.144 | 4.739 | 5.642 | 0.221 | 1.371 | 0.780 |
| до, раунд 2 | 3.010 | 4.742 | 5.934 | 0.441 | 1.190 | 0.718 |
| после, раунд 2 | 2.962 | 4.329 | 4.959 | 0.198 | 1.274 | 0.737 |

Вклад D7 - фаза 7: **-0.26 ms** (0.48/0.44 -> 0.22/0.20), остальные фазы в пределах шума контейнера (behaviour в «после» даже выше на 0.07-0.08 - шум). `work_per_tick`: `route_refill_checks` 9 993.6 -> **51.0**
(-99.5%), `next_conn_recomputes` 809.4 -> 46.3 (рост проверок/пересчётов прежних машин с `NextConn = -1` исчез: результат у них не менялся); остальные счётчики те же.
tick p95 ниже на 0.26-0.41 мс при том же шуме (-5…-9%) - в рамках ожидания -3…-4% по доле фазы плюс разброс.

### Итог шага 2.4b (P2-P5): s10k, 10 000 активных
Исходно (H1, «до» всего): tick p50/p95 5.12/7.96 мс. После H2 4.73/6.39, после H3 4.42/5.57, после H4 3.47/4.41 (разная загрузка контейнера, load 3-5), после H5 (этот шаг, load ~1.5):
**p50 2.96-3.14, p95 4.33-4.74 мс** (два прогона), p99 4.96-5.64. Цель шага ≤ 4 мс p95 **не достигнута** на этом контейнере (4.33 и 4.74 против 4.0), хотя с 7.96 мс снижение -40…-46%; фазовая структура
(mean, мс): behaviour 1.27-1.37, gate 0.74-0.78, snapshot 0.35-0.39, route 0.25-0.26, transition 0.20-0.22, integrate 0.15-0.16, metrics 0.13-0.14. По плану §5 дальше - решение архитектора (резерв:
обход фазы 5 по полосам, затем 2 потока фазы 5, ADR-0016). Основной остаток - фаза 5 (behaviour, ~45% тика) и ворота.

## 8. Шаг 2.4c: откуда хвост p95 и выбор резерва (решение — ADR-0017)

### 8.1 Разбор p95 − p50 (замер архитектора, код `3e949e4`)
Как снято: дерево выгружено в scratchpad, в копии `bench` пишет по каждому тику окна время тика, 10 фаз (`--phases`) и дельты
`SimCounters` (`PathfindWork`, `CustomizeWork`, `CostRecalcs`, ...); `bench --scenario s10k --warmup-until-full --ticks 2000 --repeat 1
--phases`, тики окна 5 901–7 900, активных 9 997, load ≈ 1. Два одинаковых прогона (A, B) сравниваются **по номеру тика**: симуляция
детерминирована, поэтому совпадающая по тикам часть времени — работа, несовпадающая — среда.

| | A | B |
|---|---|---|
| tick p50 / p95 / mean, мс | 3.53 / 4.61 / 3.54 | 3.23 / 4.67 / 3.44 |
| p95 / mean | 1.30 | 1.36 |

Тяжёлые тики (≥ p95, 101 шт.) против тиков около медианы (±0.15 мс), mean мс по фазам:

| фаза | A тяжёлые | A медиана | Δ | B тяжёлые | B медиана | Δ | r(A,B) по тику |
|---|---|---|---|---|---|---|---|
| 3 route | 0.648 | 0.290 | +0.36 | 0.676 | 0.333 | +0.34 | **0.78** |
| 4 gate | 1.199 | 0.804 | +0.40 | 1.098 | 0.738 | +0.36 | 0.02 |
| 5 behaviour | 2.037 | 1.407 | +0.63 | 2.152 | 1.257 | +0.89 | 0.02 |
| 6 integrate | 0.189 | 0.161 | +0.03 | 0.187 | 0.151 | +0.04 | 0.04 |
| 7 transition | 0.385 | 0.236 | +0.15 | 0.356 | 0.199 | +0.16 | 0.01 |
| 9 metrics | 0.222 | 0.146 | +0.08 | 0.202 | 0.130 | +0.07 | 0.05 |
| 10 snapshot | 0.687 | 0.424 | +0.26 | 0.699 | 0.356 | +0.34 | 0.01 |
| тик | 5.441 | 3.531 | +1.91 | 5.440 | 3.221 | +2.22 | 0.10 |

Выводы:
1. **Хвост — в основном среда, а не периодическая работа.** Время фаз 4–10 на одном тике двух прогонов не коррелирует (r ≤ 0.05);
   тяжёлые тики A и B совпадают в 17 из 101 (случайно — 5). Внутри прогона медленные тики идут сериями (автокорреляция времени без
   фазы 3: лаг 1 — 0.64/0.76, лаг 20 — 0.26/0.35, лаг 100 — 0.11/0.26, серии тяжёлых тиков до 6/17 подряд) и замедляют все фазы разом
   (корреляция behaviour с gate/transition/metrics внутри тика 0.32–0.50). Это эпизоды замедления общего 4-vCPU контейнера длиной
   в десятки-сотни тиков; шум мультипликативный (p95 ≈ 1.3–1.4 × mean), поэтому хвост уменьшается вместе со средним.
2. **Периодическая работа ядра**: (а) MOBIL разнесён по индексу (`(tick + i) % 10`), фаза 5 по `tick % 10` ровная (1.36–1.46 мс без
   рисунка), тяжёлые тики по `tick % 10` распределены равномерно; (б) пересчёт весов раз в 600 тиков (тики 6 001, 6 601, 7 201, 7 801) +
   волна кастомизации 14 тиков × ≈ 20 000 релаксаций (`customize_budget_per_tick`) ≈ +0.7 мс на 56 из 2 000 тиков (2.8%); (в) работа
   запросов маршрута p50 7 000, p95 26 000, max 60 000 релаксаций/тик. Модель фазы 3 по тикам: `route ≈ 0.055 + 0.0175 мкс·pwork +
   0.035 мкс·cwork + 0.12 мкс·unpack` мс. Это единственная детерминированная (r = 0.78) часть хвоста.
3. **Контрфактический пересчёт p95 по тем же тикам** (A / B): фаза 3 идеально ровная −0.18 / −0.25 мс; кастомизация с бюджетом 5 000
   −0.07 / −0.08; идеально ровная кастомизация вместе с капом работы запросов 16 000 −0.13 / −0.11; фаза 5 ровная (медиана на каждом тике) −0.36 / −0.49; фаза 5 × 0.8
   −0.35 / −0.36. Размазывание маршрутной работы бюджетами даёт ≤ −0.13 мс и меняет маршруты (хэш) — мало.

### 8.2 Цена частей фазы 5 и обход по полосам (замер)
Метод «двойного вызова»: в копии чистая функция вызывается дважды (результат второго — в сток), прирост фазы 5 нормирован на сумму
фаз 4, 6–10 того же прогона (нормировка снимает разную загрузку контейнера; 2 прогона на вариант). Нормированная фаза 5: базовая 0.788,
+лидер 0.908, +`ObstacleGap` 0.869, +IDM 0.825, +MOBIL 0.813. Доля в фазе 5: **поиск лидера ≈ 15%, `ObstacleGap` ≈ 10%, IDM ≈ 5%,
MOBIL ≈ 3%** (≈ 0.04 мс, ≈ 1 000 решений за тик), остальное ≈ 2/3 — проход по памяти (первое касание ≈ 30 массивов `VehicleStore`,
случайные чтения лидера/полосы). Второй вызов идёт по горячему кэшу, так что доли — нижняя оценка.

Обход фазы 5 по полосам (от головы к хвосту по `Behind`/`GhostBehind`, затем машины без полосы) — **хэш s10k тот же**
(tick 6 100 `861672e99fa95a39`, 6 300 `c73832373fb67bd3`), т.е. фаза 5 от порядка не зависит, но нормированная фаза 5 0.786 → 0.843
(**+7%**, 3 пары прогонов): переход по списку — цепочка зависимых чтений, а SoA-массивы машин читаются вразброс. Отвергнут.

### 8.3 Прототип: фаза 5 на 2 потоках (копия, не в репо)
Тело цикла фазы 5 вынесено в функцию машины; главный поток и один воркер берут чанки по 256 индексов атомарным счётчиком; воркер
крутится в ожидании эпохи; главный не ждёт воркер, если тот не успел заявиться (CAS). `SimWork` воркера в замере отброшен (вне хэша).
Хэш s10k тот же (`861672e99fa95a39`, `c73832373fb67bd3`). Поочерёдно 1/2 потока, 3 раунда, load 1.2–1.5:

| | tick p50 | tick p95 | tick p99 | behaviour mean | прочие фазы (4, 6–10) mean |
|---|---|---|---|---|---|
| 1 поток | 3.37 / 2.90 / 2.88 | 4.71 / 4.35 / 4.38 | 5.03 / 4.75 / 4.80 | 1.415 / 1.250 / 1.291 | 1.73 / 1.55 / 1.60 |
| 2 потока | 2.95 / 2.76 / 2.45 | **3.68 / 3.71 / 3.47** | 4.01 / 4.28 / 4.18 | 0.854 / 0.871 / 0.782 | 1.72 / 1.70 / 1.54 |

Фаза 5 ускоряется в 1.57 раза (холодный L2 второго ядра, чанки), прочие фазы не страдают; воркер не успел заявиться в 9–18 тиках
из 7 900 (главный сделал всё сам). p95 −19% в среднем по парам.

### 8.4 Решение и ожидаемый результат
ADR-0017: исполнитель диапазонов с постоянными воркерами, фаза 5 (P6), затем фазы 6 и 10 (P7). Отвергнуты: обход по полосам (+7%),
MOBIL реже (≤ 1% тика, меняет поведение), размазывание маршрутной работы (≤ −0.13 мс p95, меняет маршруты), SoA в проходе сбора ворот
(оценка −0.05…−0.1 мс; ворота — позже, параллельно по цветам LOD-ячеек, `sim-lod.md` §10 п.5).
Ожидаемо s10k (load ≈ 1.5): после P6 p95 ≈ 3.5–3.7 мс (замер прототипа), после P7 ≈ **3.3–3.6 мс** (оценка: фазы 6 и 10 mean
0.16 + 0.41 мс ускоряются в ≈ 1.5 раза).

### 8.5 План для исполнителей (sonnet), последовательно
| # | Задача | Файлы | Тесты | Приёмка |
|---|---|---|---|---|
| P6 | **Исполнитель + фаза 5 на потоках.** `SimRuntimeOptions { int WorkerThreads }` (1 по умолчанию: поток не создаётся), перегрузка `Simulation.Create(desc, in opts)`; `Util/WorkerPool` по ADR-0017 п.1 (воркеры в `Create`, `Join` в `Dispose`; динамические чанки, `C` = 256; CAS-заявка воркера; будить в начале `Step`, spin внутри `Step`, park между `Step` — сравнить с «всегда spin» замером 2×3 прогонов и выбрать); предвыделенный объект задания фазы 5; тело цикла — функция машины; `SimWork` — по экземпляру на воркер, сумма после фазы. `BannedSymbols.txt`: `T:System.Threading.ThreadPool`, `T:System.Threading.Tasks.Parallel`. `bench --threads N` (JSON `threads`; `compare` отказывает при разных `threads`), `hash --threads N` | `Api/Simulation.cs`, `Api/SimRuntimeOptions.cs` (новый), `Util/WorkerPool.cs` (новый), `World/World.cs`, `Rules/BehaviourPhase.cs`, `Sim.Headless/Bench/*`, `HashCommand.cs`, `BannedSymbols.txt` | golden-хэши `HotPathGoldenHashTests` при `WorkerThreads` 1, 2, 3 — те же константы; тест независимости от порядка: выходы фазы 5 (`Accel`, `LaneChangeDir`, `Clamp*`) при прямом и обратном обходе равны побитово (s1k после 3 000 тиков); 0 B/тик при 2 потоках через `GC.GetTotalAllocatedBytes(true)`; 50 циклов `Create`/`Dispose` с 2 потоками — число потоков процесса не растёт; `SimWork` при 1 и 2 потоках равен | `hash --scenario s10k --warmup-until-full --ticks 400 --every 200 --threads 2`: `861672e99fa95a39`, `c73832373fb67bd3`; `dotnet test` зелёный; s1k bench-gate (threads 1) OK — рефакторинг тела без регрессии |
| P7 | **Фазы 6 и 10 на исполнителе.** Тела циклов `IntegratePhase` и `SnapshotPhase` — функции машины; `HardClamps` — целый аккумулятор воркера, сумма после фазы; `SnapshotSlots`/`SnapshotTick`/`SelectedRoute` — после параллельной части на главном | `Agents/IntegratePhase.cs`, `Api/SnapshotPhase.cs` | те же golden-хэши при 1/2/3 потоках; `Counters.HardClamps` равен; снапшот при 1 и 2 потоках побайтно равен | хэш s10k тот же при `--threads 2`; 0 B |

Порядок: P6 → верификатор → P7 → верификатор (haiku). Верификатор: `bench --scenario s10k --warmup-until-full --ticks 2000 --repeat 1
--phases` поочерёдно `--threads 1` и `--threads 2`, 3 раунда, `uptime` до/после; s10k засчитан, если p95 ≤ 4 мс при 2 потоках минимум в 2 из 3
раундов при load ≤ 2; плюс bench-gate s1k (threads 1) и `Report_S1k` 30 мин (stuck 0, счётчики как в §7.4 — правка не меняет поведение).
Если после P7 p95 > 4 мс при load ≤ 2 — следующий кандидат: `WorkerThreads = 3` (замер), затем фаза 4 по цветам ячеек (новое ADR-дополнение).

### 8.6 P6 выполнена: исполнитель и фаза 5 на воркерах
Реализовано по ADR-0017: `Util/WorkerPool` (`W - 1` потоков, чанки по 256 индексов, эпоха+номер чанка в одном `long`, воркер берёт чанк CAS'ом,
после завершения задания счётчик «закрывается»; главный ждёт только уже взятые чанки), `BehaviourJob` (функция машины `Vehicle(i)`, `SimWork` по
воркеру + сведение `Collect` после фазы), `SimRuntimeOptions.WorkerThreads` (≤ 1 — потоков нет, > 16 обрезается), `Simulation.Create(desc, in opts)`,
`bench/hash --threads N`, JSON `threads` (`compare` отказывает при разных; нет поля = 1). `BannedSymbols.txt`: `Thread`, `ThreadPool`, `Parallel`
запрещены, в `WorkerPool.cs` — локальный `#pragma warning disable RS0030`.

**Политика «spin или сон между `Step`»** (замер: `bench --scenario s10k --warmup-until-full --ticks 2000 --repeat 1 --phases --threads 2`, временный
переключатель, 3 раунда поочерёдно, load 1.2–2.3): park (воркеры спят на `ManualResetEventSlim` между `Step`, `Wake` в начале, `Rest` в конце) tick p95
3.590 / 3.656 / 3.334 мс, p50 2.465 / 2.614 / 2.433; «всегда spin» p95 3.636 / 3.604 / 3.608, p50 2.521 / 2.446 / 2.440. Разницы нет (в пределах шума), park
не жжёт второе ядро между кадрами клиента (в игре `Step` зовётся 20 Гц) → **выбран park**, переключатель убран. Воркеры крутятся (`SpinWait.SpinOnce(-1)`,
без `Sleep`) внутри `Step`, то есть и во время последовательных фаз 1–4 (≈ 1.5 мс). Пробуждение прямо перед фазой 5 не пробовали.

**Приёмка P6** (код `ecb30fa`):
- `hash --scenario s10k --warmup-until-full --ticks 400 --every 200 --threads {1,2,3}`: tick=6100 `861672e99fa95a39`, tick=6300 `c73832373fb67bd3`, active 9997 — во всех трёх.
- Тесты (`dotnet test traffic-city.sln`): Sim.Core.Tests 414, Sim.Headless.Tests 114 зелёные. Новые: golden-хэши s1k и s10k при 1/2/3 потоках,
  `hash --threads`, `compare` отказывает при разных `threads`, `WorkerThreadsTests` (покрытие диапазона ровно один раз на 3000 запусках × 4 конфигурации,
  фаза 5 в обратном порядке индексов даёт побитово те же `Accel/LaneChangeDir/ClampLeadGap/ClampFlags/ClampGateGap` и ту же сумму `SimWork`, хэш и
  `SimWork` равны при 2 и 3 потоках после 3000 тиков s1k, 0 B за окно 100 тиков при 2 потоках через `GC.GetTotalAllocatedBytes(true)` (минимум по окнам, коллекция без
  параллельности), 50 циклов Create/Dispose с 2 потоками — `WorkerPool.LiveThreads` возвращается к исходному).
- `scripts/bench-gate.sh --baseline perf/baseline.json` (1 поток): p95 0.2817 мс против baseline 0.63036 (-55.3%, load 4.7 в момент запуска), 0 B/тик — OK.

**Замер s10k, поочерёдно 1/2 потока** (`--phases`, `--ticks 2000 --repeat 1 --warmup-until-full`, `uptime` перед каждым; load average 1/5/15 мин: 2.60/4.04/3.25,
2.24/3.89/3.22, 2.26/3.81/3.20, 1.90/3.63/3.15, 1.92/3.55/3.14, 1.66/3.38/3.09):

| раунд | потоки | tick p50 | tick p95 | tick p99 | behaviour mean | прочие фазы mean | B/тик |
|---|---|---|---|---|---|---|---|
| 1 | 1 | 3.083 | 4.825 | 5.343 | 1.377 | 1.995 | 0 |
| 1 | 2 | 3.061 | **3.926** | 4.584 | 0.883 | 2.124 | 0 |
| 2 | 1 | 3.533 | 4.748 | 5.367 | 1.455 | 2.110 | 0 |
| 2 | 2 | 2.542 | **3.668** | 4.669 | 0.794 | 1.926 | 0 |
| 3 | 1 | 3.012 | 4.614 | 5.380 | 1.331 | 1.950 | 0 |
| 3 | 2 | 3.140 | **3.928** | 4.452 | 0.877 | 2.185 | 0 |

p95 с 2 потоками ≤ 4 мс во всех 3 раундах (load 1.7–2.6); относительно 1 потока −0.90 / −1.08 / −0.69 мс (в среднем −0.89 мс, −19%), фаза 5 ×1.56 / ×1.83 / ×1.52,
что совпадает с прототипом (§8.3). Это до P7 (фазы 6 и 10 ещё на одном потоке).

### 8.7 P7 выполнена: фазы 6 и 10 на воркерах
Реализовано по образцу `BehaviourJob` (код `9d3b596`): `IntegrateJob` (`Agents/IntegratePhase.cs`; `World.Integrate`) и `SnapshotJob` (`Api/SnapshotPhase.cs`; `World.SnapshotJob`),
оба `IRangeJob` на `WorkerPool` с чанками 256. Перегрузка `IntegratePhase.Run(net, v, types, p, ref c)` для тестов/бенчей осталась (`[ThreadStatic]` задание; `types` теперь `VehicleTypeDesc[]`).

**Общие записи в фазах 6 и 10 (проверено чтением дерева вызовов) и что с ними сделано:**
1. `SimCounters.HardClamps` (фаза 6, `c.HardClamps++`, два места) - единственный общий аккумулятор: теперь целое по воркеру (`long[]`, шаг 16 элементов против false sharing),
   сумма в `IntegrateJob.Collect` после фазы на главном потоке. Целое - не зависит от порядка.
2. `SnapshotSlots` (`w.SnapshotSlots[..] = high`), `w.Work.SnapshotSlots += high`, `w.SnapshotTick` - остались в `SnapshotPhase.Run` после параллельной части на главном.
3. `PointHint[i]` (кэш подсказки куска полилинии в `LaneGeometry.EvaluatePos`, `ref v.PointHint[i]`, фаза 10) - запись в слот своей машины, не общая; результат от подсказки не зависит.
4. `StillTicks[i]`, `S[i]`, `Speed[i]` (фаза 6) и `buf[i]` (фаза 10) - слот своей машины; фаза 6 читает `ClampLeadGap/ClampFlags/ClampGateGap[i]` фазы 5, чужих `S` не читает.
5. `BusStopRules.IsServing/TargetStop` (фаза 6) и `SnapshotPhase.RenderFlags` (фаза 10) - только чтения. Ленивых кэшей, общих scratch-буферов, PRNG, `HighWater`-записей в фазах нет.
   `VehicleRenderData` 32 байта без padding: поэтому побайтовое сравнение снапшота корректно.

**Тесты** (`WorkerThreadsTests`): `Phases6And10_HardClampsAndSnapshot_EqualForOneAndManyThreads` (s1k, каждые 500 тиков до 3000, при 2 и 3 потоках: `HardClamps` равен и > 0,
`Vehicles` и `PreviousVehicles` побитово равны 1 потоку, хэш равен); `Phases6And10_ReverseOrder_GivesBitwiseSameOutputs` (s1k после 3000 тиков: `S/Speed/StillTicks` и сумма
`HardClamps` фазы 6, буфер снапшота фазы 10 при обратном обходе по одной машине равны прямому; для фазы 10 буфер перед обратным обходом залит `0xFF`);
существующие golden s1k/s10k при 1/2/3 потоках и `Step_ZeroAllocations_WithTwoThreads_TotalProcessBytes` теперь покрывают и фазы 6/10.

**Приёмка:**
- `hash --scenario s10k --warmup-until-full --ticks 400 --every 200 --threads {1,2,3}`: во всех трёх tick=6100 `861672e99fa95a39`, tick=6300 `c73832373fb67bd3`, active 9997.
- `dotnet build traffic-city.sln -warnaserror` 0/0; `dotnet format --verify-no-changes` чисто; `dotnet build game/TrafficCity.csproj -warnaserror` 0/0;
  `dotnet test traffic-city.sln`: Sim.Core.Tests 417, Sim.Headless.Tests 114 зелёные.
- `scripts/bench-gate.sh --baseline perf/baseline.json` (1 поток): p95 0.2465 мс против baseline 0.63036 (-60.9%), 0 B/тик - OK.

**Замер s10k, поочерёдно 1/2 потока** (`bench --scenario s10k --warmup-until-full --ticks 2000 --repeat 1 --phases`, `uptime` перед каждым прогоном, load average 1/5/15):

| раунд | потоки | load перед | tick p50 | tick p95 | tick p99 | behaviour mean | integrate mean | snapshot mean | сумма фаз mean | B/тик |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 1 | 4.52/5.06/4.15 | 3.044 | 4.480 | 5.560 | 1.280 | 0.174 | 0.407 | 3.205 | 0 |
| 1 | 2 | 3.65/4.82/4.09 | 2.343 | **3.483** | 4.916 | 0.789 | 0.121 | 0.259 | 2.516 | 0 |
| 2 | 1 | 3.35/4.70/4.06 | 2.745 | 4.174 | 4.687 | 1.137 | 0.161 | 0.368 | 2.899 | 0 |
| 2 | 2 | 2.74/4.48/4.00 | 2.483 | **3.806** | 5.967 | 0.796 | 0.121 | 0.263 | 2.669 | 0 |
| 3 | 1 | 2.63/4.39/3.98 | 2.954 | 4.350 | 4.930 | 1.234 | 0.169 | 0.403 | 3.123 | 0 |
| 3 | 2 | 2.17/4.17/3.92 | 2.444 | **3.389** | 3.884 | 0.779 | 0.119 | 0.251 | 2.540 | 0 |

(uptime после последнего прогона: 2.20/4.08/3.89.) p95 при 2 потоках ≤ 4 мс во всех 3 раундах (при load 2.2-3.7: выше критерия «load ≤ 2», замер шумный); относительно 1 потока
-1.00 / -0.37 / -0.96 мс. Фазы: integrate ×1.44 / 1.33 / 1.42, snapshot ×1.57 / 1.40 / 1.60 (оценка ADR: ×1.5). Хэш s10k не изменился.
