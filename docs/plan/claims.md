# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| N4 S6: бюджет маршрутов L5a (фаза 3 s100k/s1m ≤ 2/3 мс) | облако | claude/adoring-ritchie-cxzib8-n4 | scenarios/s100k.json, scenarios/s1m.json, src/Sim.Core/Routing/** (только бюджеты/параметры), src/Sim.Headless/Bench/** | 2026-10-28 |
| Ночь облака (план, по очереди): N3 s100k stuck/пики + S3 (Lod/QueueMeso, Metrics, Sim.Headless/Bench), N4 S6 (scenarios/s100k, s1m), N5 облачные долги (golden headings Linux, s10k-peds 4 потока, rebaseline s1k) | облако | claude/adoring-ritchie-cxzib8-* | см. задачи | 2026-10-27 |
