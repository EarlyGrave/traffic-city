# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| 5.2 масштаб s100k ≤ 15 мс (фаза 3 P1–P2, контроллер LOD S4–S5, лестница S7, s1m) | облако | claude/cool-newton-91p1tr | src/Sim.Core/Routing/**, src/Sim.Core/Lod/**, src/Sim.Core/Metrics/**, src/Sim.Core/Util/WorkerPool*, src/Sim.Core/World/PhaseList.cs, src/Sim.Core/Api/{Simulation,CommandDispatcher,Command,LodDesc}.cs, src/Sim.Core/Save/** (блоки LOD), src/Sim.Headless/Bench/**, scenarios/s100k*, s1m*, stress-1m*, tests по ним | 2026-10-28 |
