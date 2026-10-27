# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| N2 переходы s10k-peds: ожидания ≥ 60 с (техдолг 1–2 REPORT-stage3) | облако | claude/adoring-ritchie-cxzib8-n2 | src/Sim.Core/Rules/** (пешеходные сигналы, переходы), src/Sim.Core/Peds/**, tests/Sim.Core.Tests/{Rules,Peds}/** | 2026-10-27 |
| N3 s100k: stuck 33, пик тика 167 мс, S3 (Q: stuck и насыщенный поток) | облако | claude/adoring-ritchie-cxzib8-n3 | src/Sim.Core/Lod/**, src/Sim.Core/Metrics/**, src/Sim.Headless/Bench/**, tests по ним | 2026-10-27 |
| Ночь облака (план, по очереди): N3 s100k stuck/пики + S3 (Lod/QueueMeso, Metrics, Sim.Headless/Bench), N4 S6 (scenarios/s100k, s1m), N5 облачные долги (golden headings Linux, s10k-peds 4 потока, rebaseline s1k) | облако | claude/adoring-ritchie-cxzib8-* | см. задачи | 2026-10-27 |
| K1 парковка: поверхность и данные + landuse в сейв (L27) | локал | local/k1-parking | src/Sim.Core/Parking/**, src/Sim.Core/Api/{ParkingDesc,CityDesc,ScenarioDesc,SimCounters}.cs, src/Sim.Core/City/{CityResidents,CityInit,BuildingStore,LotBuilder}.cs, src/Sim.Core/Agents/{VehicleStore,PendingTrips}.cs, src/Sim.Core/Save/**, src/Sim.Scenarios/ScenarioLoader.Parking.cs, content/base/buildings.json, tests/SaveCorpus/** | 2026-10-28 |
| VS6 x-ray ближних высоток (L31) | локал | local/vs6-xray | game/Shaders/building.gdshader, game/Scripts/RtsCamera.cs | 2026-10-28 |
