# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| corner_radius: отступ узла по ширине дорог (резкий правый поворот) | локал | local/corner-radius | src/Sim.Core/Network/**, src/Sim.Core/Tools/ToolValidator.cs, src/Sim.Scenarios/**, scenarios/, tests (сеть, golden, SaveCorpus), docs/adr, docs/design/scenario-format.md; baseline — потом в облаке | 2026-10-27 |
