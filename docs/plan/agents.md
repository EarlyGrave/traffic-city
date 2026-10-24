# Учёт агентов

Лимиты одновременной работы: fable ≤ 3, sonnet ≤ 8, haiku ≤ 20.

| # | Агент (ветка) | Модель | Задача | Статус |
|---|---------------|--------|--------|--------|
| 1 | skeleton (worktree) | sonnet | 1.1 каркас репо, сборка, CI, git-хуки | done, merged |
| 2 | hooks (worktree) | sonnet | 1.2 хуки Claude Code | done, merged |
| 4 | hook-demo (worktree) | haiku | демонстрация хуков на пути субагента | done |
| 3 | core-api | fable | 1.3 спека API ядра + формат сценариев + ADR-0002 | done, merged |
| 5 | util-api (worktree) | sonnet | 2-AB: Util + Api-типы | wip |
| 6 | citygen-spec (worktree) | fable | спека генератора города + ADR-0003 | wip |
| 7 | intersections-spec (worktree) | fable | спека перекрёстков и маршрутизации + ADR-0004/0005 | wip |
| 8 | scenarios (worktree) | haiku | scenarios/*.json по scenario-format.md | wip |
