# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| L8-долг облака (update-baseline s1k, golden Rust на Linux) + финальная проверка бюджетов на тихой машине (s1k, s10k-peds, повтор S1) | облако | claude/adoring-ritchie-cxzib8 | perf/baseline.json, rust/**/golden*, perf/results/, docs/REPORT-* | 2026-10-27 |
| E1 бюджет города: поверхность (L21) | локал | local/e1-budget-surface | src/Sim.Core/Api/{CityDesc,CityInfo,Command,CommandDispatcher,Simulation.City}.cs, src/Sim.Core/City/{Ledger,CityBudget,CityWorld,CityHash}.cs, src/Sim.Core/Save/**, src/Sim.Scenarios/ScenarioLoader.City.cs, tests/SaveCorpus/** | 2026-10-27 |
| RP6 + VS3/VS4: лента и тротуар по профилю, разметка, зебры (L22) | локал | local/rp6-road-visual | game/Scripts/{ChunkMesh,RoadChunks,PedChunks,CrosswalkMesh}.cs, game/Shaders/{road,sidewalk}.gdshader, game/Scripts/Palette.cs | 2026-10-27 |
| UI: исследование сити-билдеров + черновик спеки (L23) | локал | local/ui-research | docs/design/ui-*.md | 2026-10-27 |
