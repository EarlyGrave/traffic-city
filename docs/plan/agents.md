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
| 14 | physics (worktree) | sonnet | 2-F: IDM, MOBIL, интеграция, переходы, прибытие | wip |
| 15 | citygen (worktree) | sonnet | 2-G: CityGenerator/ZoneGenerator/TransitGenerator по спеке | wip |
| 16 | render (worktree) | sonnet | 2.5: Sim.Headless render → mp4 | wip |
