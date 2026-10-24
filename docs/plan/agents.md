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
| 44 | bench-stability (worktree) | sonnet | 2.1: прогрев до полной рампы, шум p95 | wip |
| 45 | routing-hier-spec (worktree) | opus | 2.2: спека иерархии маршрутов + ADR-0009 | wip |
| 46 | snapshot-spec (worktree) | opus | 2.3: ADR-0010 снапшот без копирования | done, merged (копирования нет; удешевление фазы 10) |
| 47 | snapshot-impl (worktree) | sonnet | 2.3: реализация ADR-0010 | wip |
| 48 | cs2-risks (worktree) | opus | реестр рисков по урокам CS2 | done, merged (27 проблем, топ-5 рисков) |
