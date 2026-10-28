# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| 5.1 разбор затора s10k-res (агент L45, только разбор, без правок ядра) | локал | local/res-congestion | docs/design/res-congestion.md, временные пробы (не коммитятся) | 2026-10-28 |
| 5.2 масштаб s100k ≤ 15 мс (фаза 3, timed-reroute, stuck s100k, s1m) | облако | claude/... (назначит облако) | src/Sim.Core/Routing/**, src/Sim.Core/Lod/**, src/Sim.Core/Metrics/**, src/Sim.Headless/Bench/**, scenarios/s100k*, s1m*, tests по ним | 2026-10-28 |
