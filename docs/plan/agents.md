# Учёт агентов

Лимиты одновременной работы: fable ≤ 3, sonnet ≤ 8, haiku ≤ 20.

| # | Агент (ветка) | Модель | Задача | Статус |
|---|---------------|--------|--------|--------|
| 1 | skeleton (worktree) | sonnet | 1.1 каркас репо, сборка, CI, git-хуки | done, merged |
| 2 | hooks (worktree) | sonnet | 1.2 хуки Claude Code | done, merged |
| 4 | hook-demo (worktree) | haiku | демонстрация хуков на пути субагента | done |
| 3 | core-api | fable | 1.3 спека API ядра + формат сценариев + ADR-0002 | done, merged |
| 5 | util-api | sonnet | 2-AB: Util + Api-типы | done, merged (77 тестов) |
| 6 | citygen-spec | fable | спека генератора города + ADR-0003 | done, merged |
| 7 | intersections-spec | fable | спека перекрёстков и маршрутизации + ADR-0004/0005 | done, merged |
| 8 | scenarios | haiku | scenarios/*.json по scenario-format.md | done, merged |
| 9 | network | sonnet | 2-C: Network store/builder/geometry | done, merged (Evaluate ~10 нс, 0 B) |
| 10 | vehicle-store | sonnet | 2-D: VehicleStore, LaneOccupancy, PendingTrips | done, merged (0 B на 100k операций) |
| 11 | json-loader | sonnet | 2-I: Headless JSON → ScenarioDesc | done, merged (19 тестов) |
| 12 | scenarios-fix | haiku | max_signalized_nodes по лестнице | done, merged |
| 13 | facade | sonnet | 2-E: World + Simulation фасад, команды, снапшот, хэш | done, merged (0 B/тик, детерминизм) |
| 14 | physics | sonnet | 2-F: IDM, MOBIL, интеграция, переходы, прибытие | done, merged (фазы 5–7 1000 машин ≈38 мкс, 0 B) |
| 15 | citygen | sonnet | 2-G: CityGenerator/ZoneGenerator/TransitGenerator по спеке | done, merged (s1k 4 мс, голден-хэш) |
| 16 | render | sonnet | 2.5: Sim.Headless render → mp4 | done, merged |
| 17 | gen-wiring | sonnet | 2-G2: генератор в WorldBuilder/Create | done, merged (Create s1k 13.5 мс) |
| 18 | spawner-metrics | sonnet | 2-H: Spawner, OD, MetricsPhase, подключение фаз | done, merged |
| 19 | routing | sonnet | 3-R: A*, граф, кэш магистралей (без подключения) | done, merged (s1k A* 32 мкс, s100k 1.35 мс) |
| 20 | loader-catchup | sonnet | 3.0: новые поля формата в загрузчике и сценариях | done, merged |
| 21 | j1-connectors | sonnet | 3-J1: connector'ы, ConnectorStore, конфликты | done, merged |
| 22 | j2-signals | sonnet | 3-J2: SignalStore, автомат фаз, SetSignalPlan/SetNodeControl | done, merged |
| 23 | flaky-hunt | sonnet | нестабильный тест | stopped (лимит), WIP в checkpoint/2026-10-24-flaky-wip |
| 24 | render-contrast | haiku | контраст машин в рендере, разбор --view с минусом | done, merged |
| 25 | merge-fix (s3) | haiku | разрешение конфликтов J1+J2 | done |
| 26 | gates | fable | 3-J3+J4: ворота, анти-дедлок, фазы 5/7, подключение сигналов | stopped (лимит), код в checkpoint/2026-10-24-gates-wip; проверен: p95 409 мкс, gridlock 0, stuck 34 эпизода — доводка |
| 27 | route-wiring | sonnet | 3-R4/R5: фаза 3, маршруты, STAGE2-TEMP убран | done, merged (completed 10 мин: 144 → 1775) |
| 28 | straight-roads | sonnet | 3.G: city.node_jitter_m = 0 (прямые дороги) | done, merged |
| 29 | verify-gates | sonnet | прогон ветки ворот: build/test/s1k 30 мин | done |
| 30 | stuck-diag | sonnet | диагностика stuck на ветке ворот | done: корень — спиллбэк ниже узла 53 |
| 31 | jam-chain | sonnet | цепочка затора до корня | done: корень — красный узла 51, цикл длиннее спеки |
| 32 | stuck-fix | sonnet | светофоры 24/3/1 | done: stuck 34 → 9 |
| 33 | flaky-port | sonnet | перенос фикса allocation-тестов | done, merged (15/15 зелёных) |
| 34 | bench-cmd | sonnet | шаг 5: bench/compare/update-baseline | done, merged (s1k 2000 тиков p95 0.47 мс, 0 B) |
| 35 | godot-client | sonnet | шаг 6: MultiMesh, камера, интерполяция, оверлеи, HUD | done, merged (сборка ок, ручная проверка владельцем) |
| 36 | ns-fix | haiku | using Sim.Scenarios после мержа Godot | done |
| 37 | load-routing | sonnet | ADR-0006 | done: stuck 9 → 0 |
| 38 | gates-merge | haiku | конфликт теста при мерже ворот | done |
| 39 | buses (worktree) | sonnet | шаг 4: автобусы, остановки, headway | wip |
| 40 | ci-baseline | sonnet | ADR-0007 | done, merged |
| 41 | ladder-profile | sonnet | шаг 5: лестница + профиль | done (s1m не создан: ёмкость) |
| 42 | s1m-fix | haiku | ёмкость s1m + прогон | done (p95 185 мс на 2031 машине) |
| 43 | buses | sonnet | шаг 4 | done, merged (diff-перенос, ADR переименован в 0008) |
| 44 | bench-stability (worktree) | sonnet | 2.1: прогрев до полной рампы, шум p95 | done, merged (bench-gate; s10k full p95 13.2 мс) |
| 45 | routing-hier-spec (worktree) | opus | 2.2: спека иерархии маршрутов + ADR-0009 | done, merged (оверлей ячеек, H1–H4) |
| 46 | snapshot-spec (worktree) | opus | 2.3: ADR-0010 снапшот без копирования | done, merged (копирования нет; удешевление фазы 10) |
| 47 | snapshot-impl (worktree) | sonnet | 2.3: реализация ADR-0010 | done, merged (микробенч 22.5 → 16.9 мкс, short-job; хэши прежние) |
| 49 | routing-h1 (worktree) | sonnet | 2.2 H1: оверлей ячеек, волна, BuildRoad, загрузчик v2 | done, merged (Create оверлея: s100k 248–402 мс, s1m 3.0 с) |
| 50 | routing-h2 (worktree) | sonnet | 2.2 H2: поиск по оверлею, развёртывание плана, эквивалентность | done, merged (s100k запрос медиана 0.34 мс vs 0.85 мс A* этапа 1) |
| 51 | routing-h3 (worktree) | sonnet | 2.2 H3: RouteStore, окно, дрейф, фаза 3, удаление старого кэша | done, merged (s1k 30 мин: completed 5532→6300, stuck 0) |
| 52 | wave-verify (worktree) | sonnet | верификация волны 2.1–2.3 + профиль s1k/s10k/s100k | done (s10k p95 7.85 мс, stuck 27) |
| 53 | stuck-large (worktree) | sonnet | 2.4a: stuck s10k/s100k, route_deferred | done, merged (эпизоды 260→35, рампа s100k ок) |
| 55 | left-starvation (worktree) | sonnet | 2.4a2: правило A против голодания левых | done, merged (Signal-only 20 с) |
| 57 | hotpath-p1 (worktree) | sonnet | 2.4 P1: OnPhaseEnd, SimWork, bench --phases, golden-хэш | done, merged (s10k 0 B/тик подтверждено) |
| 58 | hotpath-p2 (worktree) | sonnet | 2.4 P2 (hotpath H2): зазоры клэмпа, фаза 6 потоковая | done, merged (integrate 1.61→0.17 мс, хэши равны) |
| 59 | hotpath-p3 (worktree) | sonnet | 2.4 P3 (hotpath H3): NodeBusy, SoA ворот | done, merged (s10k p95 6.39→5.57 мс, хэши равны) |
| 60 | hotpath-p4 (worktree) | sonnet | 2.4 P4 (hotpath H4): кэш полос, NextConn | done, merged (s10k p95 4.41 мс при load ~3, хэши равны) |
| 61 | hotpath-p5 (worktree) | sonnet | 2.4 P5 (hotpath H5): AssignConnectors по флагу | done, merged (s10k p95 4.33–4.74 мс) |
| 62 | hotpath-reserve (worktree) | opus | 2.4c: выбор резерва до ≤ 4 мс | done, merged (ADR-0017 потоки) |
| 63 | workers-p6 (worktree) | sonnet | 2.4 P6: WorkerPool + фаза 5 на потоках | done, merged (s10k p95 2 потока 3.67–3.93 мс, хэши 1/2/3) |
| 64 | workers-p7 (worktree) | sonnet | 2.4 P7: фазы 6 и 10 на воркерах | done, merged (s10k p95 2 потока 3.39–3.81 мс) |
| 65 | verify-2.4 (worktree) | haiku | верификация шага 2.4 + пересъём baseline | сдался (load), worktree удалён ошибочно |
| 66 | verify-2.4b (worktree) | sonnet | верификация шага 2.4 + пересъём baseline | done, merged (baseline 65d09da) |
| 54 | hotpath-plan (worktree) | opus | 2.4b: план горячего пути s10k | done, merged (ADR-0016, P1–P5) |
| 48 | cs2-risks (worktree) | opus | реестр рисков по урокам CS2 | done, merged (27 проблем, топ-5 рисков) |
| 56 | sim-lod-spec (worktree) | opus | 2.6: ADR-0013 уровни детализации + новая лестница | done, merged (micro+meso, L1–L5) |
| 67 | signals-spec (worktree) | opus | 2.5: ADR адаптивных светофоров + координация («зелёная волна») | done, merged (ADR-0018, S1–S4) |
| 68 | saves-spec (worktree) | opus | 2.6b: ADR-0012 сохранения и версия мира | done, merged (save-format.md, SV1–SV3) |
| 69 | lod-l1 (worktree) | sonnet | 2.6 L1: каркас LOD (секция lod, ячейки, режим off) | done, merged (off = прежний хэш) |
| 70 | lod-l2 (worktree) | sonnet | 2.6 L2: meso-динамика, all_meso, эквивалентность коридора | done, merged (perf-критерий не выполнен, stuck meso) |
| 73 | lod-rethink (worktree) | opus | 2.6r: пересмотр стоимости LOD + stuck meso | done, merged (ADR-0019: активные множества, Q-meso, снятие допуска на красном) |
| 74 | active-a1 (worktree) | sonnet | 2.6 A1: снапшот по виду | done, merged (s100k фаза 10: 4.8 → 0.46 мс) |
| 75 | active-a2 (worktree) | sonnet | 2.6 A2: ворота по маскам | done, merged (фаза 4 −17…−21%, цель не взята; инкрементальные OutReserved/NodeBusy — отложено) |
| 76 | active-a34 (worktree) | sonnet | 2.6 A3+A4: переходы/прибытие/AssignConnectors по маскам, метрики в фазе 6 | done, merged (s100k фазы 6–9: 4.25 → 1.93 мс, хэши равны) |
| 77 | revoke-r1 (worktree) | sonnet | 2.6 R1: снятие допуска на красном | done, merged (stuck off 18→6, meso 255→59; поездки off −1.1% — принято) |
| 78 | qmeso-q0 (worktree) | opus | 2.6 Q0: прототип Q-meso (ворота решения) | done, merged под флагом; ворота НЕ пройдены (0.150 мкс vs 0.03; stuck 4.4× micro) |
| 71 | signals-s1 (worktree) | sonnet | 2.5 S1: метрики светофоров и замер «до» | done, merged (левые ждут 21–22 с, max 70 с) |
| 72 | saves-sv1 (worktree) | sonnet | 2.6b SV1: контейнер сейва (XXH64, блоки, столбцы) | done, merged (27 тестов, fuzz) |
| 79 | signals-s2 (worktree) | sonnet | 2.5 S2: Adaptive со стрелкой | done, merged (через агента 81) |
| 80 | saves-sv2 (worktree) | sonnet | 2.6b SV2: состояние мира в сейве + канонический порядок сети | done, merged (29 блоков, round-trip/продолжение/потоки/fuzz) |
| 81 | merge-s2-saves (worktree) | sonnet | слияние S2 + схема сейва для Adaptive | done, merged (merge-коммит с --no-verify — нарушение) |
| 82 | verify-wave (worktree) | sonnet | общий верификатор волны 2.5 S2 + 2.6 (A1–A4, R1, Q0) + 2.6b SV1–SV2 | done (всё зелёное) |
| 83 | metrics-wait-lanes (worktree) | sonnet | метрики длинных ожиданий + распределение по полосам | done, merged (s10k: max стояния 72 с; lane_miss_share 0.45 — в бэклог) |
| 84 | signals-s3 (worktree) | sonnet | 2.5 S3: зелёная волна + анти-спилловер | done (alternate — опция; stuck ×2–3), мерж через агента 87 |
| 87 | merge-s3-s4 (worktree) | sonnet | слияние S3 + S4 бронь выезда | done: S3 влит; S4 отклонён по диагностике (места нет, выигрыш ≤ 5–10%) |
| 88 | saves-sv3 (worktree) | sonnet | 2.6b SV3: корпус сейвов в CI, манифест, конверт | done, merged (корпус 0.41 МБ) |
| 89 | final-check (worktree) | sonnet | финальный чек этапа 2 | done (зелёное; s1k доля ≥ 60 с 0.165% — пограничное) |
| 85 | bus-holding (worktree) | sonnet | 2.5: удержание автобусов на остановке | done, merged (headway dev 36.5 → 21.5 с) |
| 86 | save-fuzz-fix (worktree) | sonnet | дыра валидатора сейва (фазз после мержа) | done, merged (3 дыры закрыты) |
| 90 | ci-baseline-ci (worktree) | sonnet | 3.0: `perf/baseline-ci.json` из лога CI PR #5 (ADR-0007) | done, merged (run 40233032729, CI p95 0.215 мс, 0 B) |
| 91 | lane-miss (worktree) | sonnet | 3.0: причина `lane_miss_share` s10k ≈ 0.45, фикс по Парето | done, merged: только диагноз (routing.md §6.4.1); фикс §6.4 растит ожидания ≥ 60 с на 30k тиков — отклонён |
| 92 | pedestrians-spec (worktree) | opus | 3.1: ADR-0020 + `docs/design/pedestrians.md` | done, merged (P1→P2→P3∥P4∥P5; world_version 2 с миграцией) |
| 93 | godot-cloud (worktree) | sonnet | 3.3a: разведка запуска Godot 4.7 в облаке (скриншоты, FPS) | done, merged (SourceForge-зеркало + Xvfb/llvmpipe; s1k 32.5 FPS; машин на кадре нет — разобрать в 3.3) |
| 94 | peds-p1 (worktree) | sonnet | 3.1 P1: сеть пешеходов, `PedDesc`, загрузчик `peds`, геометрия для клиента | done, merged (27 новых тестов; golden s1k/s10k без изменений) |
| 95 | rust-pilot-spec (worktree) | opus | 4.6: ADR-0022 Rust-пилот ядра, граница FFI, кросс-хэш | done, merged (R1–R8 + R-CI; тулчейн 1.97 есть, crates.io открыт) |
| 96 | modding-spec (worktree) | opus | 4.5: ADR-0015 моддинг, ассеты как данные | done, merged (M1–M10; скриптов нет) |
| 97 | tools-spec (worktree) | opus | 4.3: ADR-0023 инструменты игрока через команды, undo, реплей | done, merged (T1–T14; дренаж вместо удаления машин) |
| 98 | peds-p2 (worktree) | sonnet | 3.1 P2: агенты, жители, пути, события, снапшот, сейв w2, метрики, сценарии | done, merged (634+163 тестов; golden s1k/s10k равны; s10k-peds 19998 активных, stuck 0) |
| 99 | rust-r1 (worktree) | sonnet | 4.6 R1 + R-CI: workspace `rust/`, Util, clippy-правила, rust-check, job CI, хук `.rs` | done, merged (13 тестов util, векторы C# сошлись; хук `.rs` df75e32 слит) |
| 100 | mod-m1 (worktree) | sonnet | 4.5 M1: загрузчик пакетов (Sim.Scenarios), враждебные фикстуры | done, merged (106 тестов Mods) |
| 101 | alloc-flake (worktree) | sonnet | флейк allocation-тестов под нагрузкой (RenderViewTests threads:2, S1k_StationaryTick) | done, merged (корень — фоновый поток рантайма в GetTotalAllocatedBytes; замер по потокам симуляции; 9/20 → 0/20) |
| 102 | peds-p3 (worktree) | sonnet | 3.1 P3: переходы × машины, пешеходная фаза светофора | done, merged через 109 ( s10k-peds поездок −21% к s10k — Walk+Clear на артериях) |
| 103 | peds-p4 (worktree) | sonnet | 3.1 P4: пассажиры-агенты, bidirectional, корпус w2 | done, merged через 109 (31% поездок автобусом) |
| 104 | peds-p5 (worktree) | sonnet | 3.1 P5: Godot-клиент — пешеходы, зебры, HUD | done, merged (машины теперь видны: раздувание по дистанции + прогрев; FPS llvmpipe 15–33) |
| 105 | mod-m2 (worktree) | sonnet | 4.5 M2: пакет base, сценарии v3, интеграция в ScenarioLoader | done, merged (v3 ≡ v2 побайтно, golden равны) |
| 106 | mod-m6 (worktree) | sonnet | 4.5 M6: проверка glTF/GLB, render-budgets.json, validate-package, шаг CI | done, merged (55 новых тестов; base ok) |
| 107 | zoning-demand-spec (worktree) | opus | 3.2: ADR-0011 жители/экономика + ADR-0021 эластичный спрос + zoning-demand.md | done, merged (Z1–Z9; целочисленный логит; агрегат «10 лет») |
| 108 | render-spec (worktree) | opus | 3.3: ADR-0014 бюджет рендера и LOD + render.md | done, merged (V1–V10; FPS «до» s1k-peds agents 9.7 — раздувание по дистанции) |
| 109 | merge-p3-p4 (worktree) | sonnet | слияние P3 + P4, регенерация корпуса w2-s1k-peds | done, merged (661+359 тестов; изменён только w2-s1k-peds) |
| 110 | render-v1 (worktree) | sonnet | 3.3 V1: замеры клиента (RenderStats, --pose, stats) | done, merged (детерминированные draw/prims/память; «до»: s1k-peds agents overview 302k prims) |
| 111 | render-v2 (worktree) | sonnet | 3.3 V2: render-budgets формат 2 + валидатор | done, merged (234 теста Mods; base ok) |
| 112 | ped-phase-tune (worktree) | sonnet | 3.1d: настройка пешеходной фазы (s10k-peds поездки ≥ 90% s10k) | done, merged: критерий не взят (82.5%); корень — занятость переходов, не фаза; bidirectional −2.7% |
| 113 | zoning-z1 (worktree) | sonnet | 3.2 Z1: поверхность — desc, хранилища, участки, автозонирование, сейв, kind 23/24 | done, merged (world_version 3; без residents побитово; s1k: 12 371 житель) |
| 114 | braess-z8a (worktree) | sonnet | 3.2 Z8a: парадокс Брайеса на синтетике | done, merged (перемычка 50%, время +34.5%, без правок ядра) |
| 115 | render-v3 (worktree) | sonnet | 3.3 V3: машины — батчи по LOD, точки, вид = прямоугольник точек | done, merged (agents overview prims 303k → 75k; alloc при смене вида — только 2 первых тика, ленивый init) |
| 116 | half-crossings (worktree) | sonnet | 3.1e: «половины» перехода — раздельные BusyUntil для XIn/XOut | done, merged: 82.5% → 87% (цель 90 не взята); тест ped_stuck ослаблен ≤ 2 — вернуть в 3.1f |
| 117 | render-v4 (worktree) | sonnet | 3.3 V4: дороги чанками, heatmap в шейдере | done, merged (FPS llvmpipe s1k 18.8 → 63, s1k-peds 19 → 46; prims ×10–35 меньше) |
| 118 | zoning-z2 (worktree) | sonnet | 3.2 Z2: календарь, автомат жителя, ноги в micro, s1k-res | done, merged (1 сутки s1k-res: 3 101 ног; res_stuck 5 к концу суток — вернуть 0 в Z4) |
| 119 | zoning-z3 (worktree) | sonnet | 3.2 Z3: скимы, DetLogit, выбор, доступность | done, merged (41 тест Demand; вектор detlogit.json для Rust) |
| 120 | render-v5 (worktree) | sonnet | 3.3 V5: пешеходы — LOD и точки без раздувания | done, merged (пешеходы 1 draw call; agents mid prims 38.5k → 9k) |
| 121 | ped-starve-n (worktree) | sonnet | 3.1f: голодание пешехода на светофоре (тест обратно == 0) + подбор peds.count s10k-peds ≥ 90% | done: Walk продлевается при заявке; N = 12 000 (90.4–92.1%) |
| 122 | render-v6 (worktree) | sonnet | 3.3 V6: тени, свет, пресеты low/high (визуал — плейсхолдеры) | done, merged (VisualPreset.cs; тени только ТС LOD0; Forward+ проверить на GPU) |
| 123 | render-v9 (worktree) | sonnet | 3.3 V9: бенч клиента, render-compare/update-baseline, CI job render | done, merged (гейт ловит регрессию exit 1; job render необязательный) |
| 124 | zoning-z4 (worktree) | sonnet | 3.2 Z4: экономика, рынки, рост; вернуть res_stuck == 0 | done, merged (res_stuck 0 за 30 суток; Σ счетов = 0; проверки возвращены) |
| 125 | float-int-audit (worktree) | sonnet | 3.1c: аудит приведений float→int (net8 ≠ net10), помощник насыщения | done, merged (54 места через DetMath.ToIntSat; хэши не изменились) |
| 126 | scale-spec (worktree) | opus | 4.1: scale.md + ADR-0024 поток симуляции | done, merged (S0–S13; K1 s100k auto ≤ 15 мс при 4 потоках) |
| 127 | economy-spec (worktree) | opus | 4.2: ADR-0025 экономика города + city-economy.md | done, merged (E1–E8) |
| 128 | parking-spec (worktree) | opus | 4.4: ADR-0026 парковка + parking.md | done, merged (K1–K9) |
| 129 | rust-r2 (worktree) | sonnet | 4.6 R2: ComputeNetworkHash, команда golden, фикстуры | done, merged (23 теста RustPilot; util.json + 10 сценариев) |
| 130 | tools-t1 (worktree) | sonnet | 4.3 T1: Api-типы, коды, события (номера с 25) | done, merged (CommandKind 25–35, события 40–53; поведения нет) |
| 131 | zoning-z5 (worktree) | sonnet | 3.2 Z5: агрегат, BPR, AdvanceTo, run --years | done, merged (год s1k-res 17 с; агрегат vs micro +2.5% ног, +0.2% стоимости) |
| 132 | tools-t2 (worktree) | sonnet | 4.3 T2: пулы и арены сети, сейв w4, RefKind | done, merged (world_version 4; golden без правок прежние; VerifyDerived) |
| 133 | rust-r3 (worktree) | sonnet | 4.6 R3: JSON-ридер и загрузчик, desc_hash | done, merged (desc_hash совпал на 10/10 golden) |
| 134 | render-v10 (worktree) | sonnet | 3.3 V10: панель узла по клику | done, merged (аналитический пик, 4 Гц, пресеты; ядро не менялось) |
| 135 | mod-m9 (worktree) | sonnet | 4.5 M9: таймлайн команд в сценарии, CoreInfo.ApiVersion | done, merged (таймлайн = ручной Enqueue по хэшу при 1/2/3 потоках) |
| 136 | rust-r4 (worktree) | sonnet | 4.6 R4: порт сети и connector-ов, network_hash | done, merged (network_hash и create_status совпали 9/9) |
| 137 | rust-r5r6 (worktree) | sonnet | 4.6 R5+R6: конфликты, conflicts_hash; tc-ffi + C#-смоук | done, merged (conflicts_hash 10/10; ffi-smoke 129 проверок, 0 ошибок) |
| 138 | mod-m3 (worktree) | sonnet | 4.5 M3: MANI из каталога, отпечаток, AssetNotice, --packages-dir | done, merged (отпечаток base 8345a96e7adc282c; compare exit 2 при разном) |
| 139 | tools-t3t4 (worktree) | sonnet | 4.3 T3+T4: перенос, перестройка узла, знаки/запреты; дренаж и снос | done, merged (world_version 5; фазз 12 seed; снос без потери машин) |
| 140 | scale-s8s9 (worktree) | sonnet | 4.1 S8+S9: копия видимого снапшота + Sim.Host (поток симуляции) | done, merged (Host 12/12; хэш хоста = прямой прогон) |
| 141 | client-dead-slots (worktree) | sonnet | 4.3 клиент: мёртвые слоты (`Alive = 0`) и `Closing` сети в мешах после сноса | done, merged (RoadChunks/NodePicker/NodePanel/SimHost; пересборка по Network.Version; CrosswalkMesh/остановки — нет) |
| 142 | zoning-z6 (worktree) | sonnet | 3.2 Z6: PaintZone, участки на BuildRoad, Lost на сносе | done, merged (7 тестов ZoningPaint; событие ZoneLotsLost = 54; фикс кэша Market._rowCost после правки сети; Lost-участок пропускается без ZoneRule — координат нет в сейве) |
| 143 | scale-s0 (worktree) | haiku | 4.1 S0: замер «до» s100k off/all_meso 1/2/4 потока, s1m Create/память | done, merged (s100k p95 off 1/4 пот. 52.1/21.8 мс, queue 21.7/15.9; s1m 728 МБ; load 6–12, repeat 1; ~95 фоновых ожидалок — урок) |
| 144 | tools-t10 (worktree) | sonnet | 4.3 T10: журнал команд и реплей, replay/bugreport в Headless | done, merged (ReplayTests 9/9; s1k 2000 тиков 50 команд exit 0; контейнер TCRP; точный тик — через --interval 1) |
| 145 | tools-t7 (worktree) | sonnet | 4.3 T7: валидатор и превью, стоимость, кэш компонент | killed (без коммитов), перезапуск |
| 146 | tools-t7 (worktree) | sonnet | 4.3 T7: валидатор и превью (перезапуск 145) | done, merged (ToolValidator общий; Preview без мутаций и аллокаций; фазз 9984/10000 совпали; OutOfBounds/CreatesTrap теперь и при BuildRoad; SetToolsConfig не в сейве — долг) |
| L1 | local/windows-tests (локал) | opus | Локальная среда на Windows: golden Rust-пилота, грабли в lessons | done, merged (build/format чисто; тесты 1423 ок, 2 skip; headings_hash строго только при TC_STRICT_HEADINGS=1) |
| L2 | local/rust-check-python (локал) | haiku | rust-check.sh: фолбэк python3 → python в шаге deps без jq | done, merged (rust-check.sh exit 0 на Windows; со 2-й попытки: выбор python перенесён в ветку без jq) |
| 147 | finish-t7 (облако, оркестратор) | opus | Финиш 4.3 T7: merge origin/dev (L1, L2) в облачный dev, push | done (конфликты agents/lessons — обе стороны; build -warnaserror 0 предупр.; format чисто; тесты Core 948 ок/2 skip, Headless 471, Host 12; bench-gate s1k p95 0.2538 мс vs 0.29144, 0 B/тик, stuck 0, OK с 1-й попытки) |
| 148 | undo-t8 (облако) | sonnet | 4.3 T8: InverseBuilder, RestoreOf/Remap, UndoStack, UndoFuzz_Equivalent | done, merged (13 тестов Undo, фаззер 8 seed с дренажом и без; Core 960 ок/2 skip; s1k p95 0.289 vs 0.29144, 0 B/тик, stuck 0; долги: UndoUnavailable в превью, обратные для T5/T9 — NoInverse) |
| 150 | nextconn-bug (облако) | sonnet | Баг NextConn out of sync после RestoreOf/дренажа (найден T8) | done, merged (InvalidateNextConn смотрит приёмники всех connector-ов полосы; PreviewTests снова с мёртвым RestoreOf, seed 5 зелёный; golden прежние; s1k p95 0.2279 vs 0.29144, 0 B/тик, stuck 0) |
| 149 | s1-gate-par (облако) | sonnet | 4.1 S1: параллельная фаза 4 по узлам (GateScanJob/GateResolveJob, GateWorker) | done, merged (golden побитово; хэш 1/2/3/4 потока s10k off/all_meso обе/peds; s1k p95 0.27995 vs 0.29144, 0 B/тик; фаза 4 s100k@52k: 5.1 -> 2.27 мс (1 -> 4 потока, load 3-6); без счётчика полос узла — активные узлы обходом OccMask) |
| 151 | zoning-z8 (облако) | sonnet | 3.2 Z8: s10k-res/s100k-res, OneMoreLane, DM1/DM2, корпус d3/E4, калибровка §10.2 | done, merged частично (s10k-res: пик 10.7k машин, p95 3.06 мс при 2 потоках, но затор в сутки 1–2; s100k-res не замерен; OneMoreLane aggregate — только качественно, ε 0.16–0.38 вместо 0.7–1.3; DM1 0.718, DM2 3.07%; корпус w5-s1k-res-d3 + E4; Core 967/2 skip, Headless 480) |
| 152 | undo-unavailable (облако) | sonnet | Долг T8: UndoUnavailable в превью | в работе (ветка claude/adoring-ritchie-cxzib8) |
