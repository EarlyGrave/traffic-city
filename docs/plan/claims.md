# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| E1 бюджет города: поверхность (L21) | локал | local/e1-budget-surface | src/Sim.Core/Api/{CityDesc,CityInfo,Command,CommandDispatcher,Simulation.City}.cs, src/Sim.Core/City/{Ledger,CityBudget,CityWorld,CityHash}.cs, src/Sim.Core/Save/**, src/Sim.Scenarios/ScenarioLoader.City.cs, tests/SaveCorpus/** | 2026-10-27 |
| RP6 + VS3/VS4: лента и тротуар по профилю, разметка, зебры (L22) | локал | local/rp6-road-visual | game/Scripts/{ChunkMesh,RoadChunks,PedChunks,CrosswalkMesh}.cs, game/Shaders/{road,sidewalk}.gdshader, game/Scripts/Palette.cs | 2026-10-27 |
| N1 перф L3b: SnapshotJob.Slot (−16% p95 s1k) | облако | claude/adoring-ritchie-cxzib8-n1 | src/Sim.Core/Api/SnapshotPhase.cs, tests/Sim.Core.Tests/Api/Snapshot* | 2026-10-27 |
| N2 переходы s10k-peds: ожидания ≥ 60 с (техдолг 1–2 REPORT-stage3) | облако | claude/adoring-ritchie-cxzib8-n2 | src/Sim.Core/Rules/** (пешеходные сигналы, переходы), src/Sim.Core/Peds/**, tests/Sim.Core.Tests/{Rules,Peds}/** | 2026-10-27 |
| Ночь облака (план, по очереди): N3 s100k stuck/пики + S3 (Lod/QueueMeso, Metrics, Sim.Headless/Bench), N4 S6 (scenarios/s100k, s1m), N5 облачные долги (golden headings Linux, s10k-peds 4 потока, rebaseline s1k) | облако | claude/adoring-ritchie-cxzib8-* | см. задачи | 2026-10-27 |
