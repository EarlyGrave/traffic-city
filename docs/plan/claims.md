# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| L8-долг облака (update-baseline s1k, golden Rust на Linux) + финальная проверка бюджетов на тихой машине (s1k, s10k-peds, повтор S1) | облако | claude/adoring-ritchie-cxzib8 | perf/baseline.json, rust/**/golden*, perf/results/, docs/REPORT-* | 2026-10-27 |
| BB1: виртуальные карманы остановок (bus-bays.md §6), ADR-0029 | локал | local/bb1-bays | src/Sim.Core (Rules/BusStop*, Agents, Api/SnapshotPhase.cs, Save), src/Sim.Scenarios, tests, docs/adr | 2026-10-27 |
