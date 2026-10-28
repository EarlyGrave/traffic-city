# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| 5.2 масштаб s100k ≤ 15 мс (фаза 3 P1–P2, контроллер LOD S4–S5, лестница S7, s1m) | облако | claude/cool-newton-91p1tr | src/Sim.Core/Routing/**, src/Sim.Core/Lod/**, src/Sim.Core/Metrics/**, src/Sim.Core/Util/WorkerPool*, src/Sim.Core/World/PhaseList.cs, src/Sim.Core/Api/{Simulation,CommandDispatcher,Command,LodDesc}.cs, src/Sim.Core/Save/** (блоки LOD), src/Sim.Core/Rules/Intersections/{GatePhase,GateJobs,GateScratch,SignalPhase}.cs, src/Sim.Core/Agents/{TransitionPhase,ArrivalPhase,Spawner}.cs, src/Sim.Core/Api/{SnapshotPhase,RenderViewState}.cs, src/Sim.Headless/Bench/**, scenarios/s100k*, s1m*, stress-1m*, tests по ним | 2026-10-28 |
| 5.1c предохранитель перегрузки (вариант B, агент L47) | локал | local/overload-valve | src/Sim.Core/City/** (TripLegs, туры), src/Sim.Core/Demand/**, scenarios/*-res*.json (без s100k-res), tests по City/Demand, docs/adr (новый), docs/design/res-congestion.md | 2026-10-28 |
| 6.5 прогрессия: спека готова (агент 176), реализация PG1–PG7 | облако | claude/cool-newton-91p1tr | docs/design/progression.md, src/Sim.Core/Progression/** (новый), src/Sim.Core/Metrics/**, scenarios/game-city.json | 2026-10-28 |
| Номера, занятые облаком (ещё не в dev): ADR-0032 (параллельная фаза 3), ADR-0033 (LOD auto), ADR-0034 (прогрессия); world_version 11 — прогрессия (PG1; LOD S5a в бэклоге возьмёт следующий); RejectReason 17, SimEventKind 59–62 — прогрессия | облако | claude/cool-newton-91p1tr | — | 2026-10-28 |
| 6.1 (UI1 влит, L50); дальше локал по 6.1: T13 (клиент), сейв/меню, время, UI2, UI6, затем UI4, UI3, UI5 | локал | local/ui1-shell | game/** (Scripts/Ui/**, SimHost.cs, project.godot — InputMap, тема) | 2026-10-28 |
