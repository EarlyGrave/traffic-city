# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| YS0 замер 30k/50k жителей | локал | local/ys0-perf | scenarios/ys0-*.json, perf/results/ys0/ | 2026-10-29 |
| YS3 рельеф Юрьева 10 км | локал | local/ys3-terrain | tools/dem/**, scenarios/terrain/yuryev-10k*, scenarios/yuryev-10k-terrain.json | 2026-10-29 |
| Y3 въезд с края и работа «Москвы» | локал | local/yuryev-y3 | src/Sim.Core/** (City, Agents, Save, Api), src/Sim.Scenarios/**, src/Sim.Host/** (API) | 2026-10-29 |
