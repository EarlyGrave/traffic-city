# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| N3 s100k: stuck 33, пик тика 167 мс, S3 (Q: stuck и насыщенный поток) | облако | claude/adoring-ritchie-cxzib8-n3 | src/Sim.Core/Lod/**, src/Sim.Core/Metrics/**, src/Sim.Headless/Bench/**, tests по ним | 2026-10-27 |
| Ночь облака (план, по очереди): N3 s100k stuck/пики + S3 (Lod/QueueMeso, Metrics, Sim.Headless/Bench), N4 S6 (scenarios/s100k, s1m), N5 облачные долги (golden headings Linux, s10k-peds 4 потока, rebaseline s1k) | облако | claude/adoring-ritchie-cxzib8-* | см. задачи | 2026-10-27 |
| RP7 инструменты с профилями (L35) | локал | local/rp7-tools-profiles | src/Sim.Core/Tools/**, src/Sim.Core/Api/Command*.cs | 2026-10-28 |
| K2 парковка: резерв и ноги micro (L36) | локал | local/k2-parking-legs | src/Sim.Core/Parking/**, src/Sim.Core/Agents/**, src/Sim.Core/City/TripLegs.cs, src/Sim.Core/Save/**, tests/SaveCorpus/w10-* | 2026-10-28 |
