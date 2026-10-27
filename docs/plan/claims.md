# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| L8-долг облака (update-baseline s1k, golden Rust на Linux) + финальная проверка бюджетов на тихой машине (s1k, s10k-peds, повтор S1) | облако | claude/adoring-ritchie-cxzib8 | perf/baseline.json, rust/**/golden*, perf/results/, docs/REPORT-* | 2026-10-27 |
| E1 бюджет города: поверхность (L21) | локал | local/e1-budget-surface | src/Sim.Core/Api/{CityDesc,CityInfo,Command,CommandDispatcher,Simulation.City}.cs, src/Sim.Core/City/{Ledger,CityBudget,CityWorld,CityHash}.cs, src/Sim.Core/Save/**, src/Sim.Scenarios/ScenarioLoader.City.cs, tests/SaveCorpus/** | 2026-10-27 |
