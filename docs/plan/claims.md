# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| L8-долг облака (update-baseline s1k, golden Rust на Linux) + финальная проверка бюджетов на тихой машине (s1k, s10k-peds, повтор S1) | облако | claude/adoring-ritchie-cxzib8 | perf/baseline.json, rust/**/golden*, perf/results/, docs/REPORT-* | 2026-10-27 |
| Баг: пешеходы часами у переходов на s1k-res (L14) | локал | local/ped-crossing-stuck | src/Sim.Core/Peds/**, src/Sim.Core/Rules/Ped*, tests/Sim.Core.Tests/Peds/** | 2026-10-27 |
