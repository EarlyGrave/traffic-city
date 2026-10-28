# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| N3 s100k: stuck 33, пик тика 167 мс, S3 (Q: stuck и насыщенный поток) | облако | claude/adoring-ritchie-cxzib8-n3 | src/Sim.Core/Lod/**, src/Sim.Core/Metrics/**, src/Sim.Headless/Bench/**, tests по ним | 2026-10-27 |
| Ночь облака (план, по очереди): N3 s100k stuck/пики + S3 (Lod/QueueMeso, Metrics, Sim.Headless/Bench), N4 S6 (scenarios/s100k, s1m), N5 облачные долги (golden headings Linux, s10k-peds 4 потока, rebaseline s1k) | облако | claude/adoring-ritchie-cxzib8-* | см. задачи | 2026-10-27 |
<<<<<<< HEAD
| R7 Rust-пилот: IDM-вектора C# → Rust (rust-pilot.md §10) | облако | claude/adoring-ritchie-cxzib8-r7 | rust/**, src/Sim.Headless/Golden/**, tests/golden/rust-pilot/** | 2026-10-28 |
| RP10b API ядра для клиента: каталог профилей, module_m/начало сетки в NetworkView, клетки участка в LotRenderData + клиент (L39) | локал | local/rp10b-view-api | src/Sim.Core/Api/{NetworkView,LotRenderData,Simulation.City}*.cs, game/Scripts/{RoadProfileLayouts,LotChunks,WorldGrid,ChunkBuilder}.cs | 2026-10-28 |
=======
>>>>>>> origin/dev
