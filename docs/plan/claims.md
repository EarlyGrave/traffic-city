# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| 5.2 масштаб s100k ≤ 15 мс (фаза 3 P1–P2, контроллер LOD S4–S5, лестница S7, s1m) | облако | claude/cool-newton-91p1tr | src/Sim.Core/Routing/**, src/Sim.Core/Lod/**, src/Sim.Core/Metrics/**, src/Sim.Core/Util/WorkerPool*, src/Sim.Core/World/PhaseList.cs, src/Sim.Core/Api/{Simulation,CommandDispatcher,Command,LodDesc}.cs, src/Sim.Core/Save/** (блоки LOD), src/Sim.Core/Rules/Intersections/{GatePhase,GateJobs,GateScratch}.cs, src/Sim.Core/Agents/{TransitionPhase,ArrivalPhase,Spawner}.cs, src/Sim.Core/Api/{SnapshotPhase,RenderViewState}.cs, src/Sim.Headless/Bench/**, scenarios/s100k*, s1m*, stress-1m*, tests по ним | 2026-10-28 |
| 5.1c предохранитель перегрузки (вариант B, агент L47) | локал | local/overload-valve | src/Sim.Core/City/** (TripLegs, туры), src/Sim.Core/Demand/**, scenarios/*-res*.json (без s100k-res), tests по City/Demand, docs/adr (новый), docs/design/res-congestion.md | 2026-10-28 |
| O1 регрессия снапшота L3b (+16 % p95 s1k, агент L48) | локал | local/snapshot-perf | src/Sim.Core/Api/SnapshotPhase.cs, src/Sim.Core/Api/RenderViewState.cs, bench/Sim.Bench/** | 2026-10-28 |
| O2 S10 клиент на SimThreadHost (агент L49) | локал | local/s10-client-thread | game/Scripts/**, src/Sim.Host/** | 2026-10-28 |
