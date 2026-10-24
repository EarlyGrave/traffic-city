# Учёт агентов

Лимиты одновременной работы: fable ≤ 3, sonnet ≤ 8, haiku ≤ 20.

| # | Агент (ветка) | Модель | Задача | Статус |
|---|---------------|--------|--------|--------|
| 1 | skeleton (worktree) | sonnet | 1.1 каркас репо, сборка, CI, git-хуки | done, merged |
| 2 | hooks (worktree) | sonnet | 1.2 хуки Claude Code | done, merged |
| 4 | hook-demo (worktree) | haiku | демонстрация хуков на пути субагента | done |
| 3 | core-api | fable | 1.3 спека API ядра + формат сценариев + ADR-0002 | done, merged |
| 5 | util-api | sonnet | 2-AB: Util + Api-типы | done, merged (77 тестов) |
| 6 | citygen-spec (worktree) | fable | спека генератора города + ADR-0003 | wip |
| 7 | intersections-spec (worktree) | fable | спека перекрёстков и маршрутизации + ADR-0004/0005 | wip |
| 8 | scenarios | haiku | scenarios/*.json по scenario-format.md | done, merged (TODO: max_signalized_nodes для s10k+ после спеки генератора) |
| 9 | network (worktree) | sonnet | 2-C: Network store/builder/geometry | wip |
| 10 | vehicle-store (worktree) | sonnet | 2-D: VehicleStore, LaneOccupancy, PendingTrips | wip |
| 11 | json-loader (worktree) | sonnet | 2-I: Headless JSON → ScenarioDesc | wip |
