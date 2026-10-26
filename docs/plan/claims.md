# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| баг NextConn после RestoreOf/дренажа | облако | claude/adoring-ritchie-cxzib8 (WIP пушится по шагам; при обрыве — продолжать с ветки, описание в stage4-plan.md «2026-10-27 (ночь)») | src/Sim.Core/Network, src/Sim.Core/Routing, tests/Sim.Core.Tests/Tools | 2026-10-27 |
| 3.2 Z8 приёмка зонирования | облако | claude/adoring-ritchie-cxzib8-z8 (WIP пушится по шагам; при обрыве — продолжать с ветки, спека zoning-demand.md строка Z8, §10.2, §11, §19) | scenarios/*-res.json, tests/Sim.Core.Tests/{City,Demand,Fixtures}, tests/SaveCorpus | 2026-10-27 |
