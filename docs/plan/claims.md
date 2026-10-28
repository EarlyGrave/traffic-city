# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| 6.5 прогрессия: спека готова (агент 176), реализация PG1–PG7 — **пауза** (ждёт команды владельца) | облако | claude/cool-newton-91p1tr | docs/design/progression.md, src/Sim.Core/Progression/** (новый), src/Sim.Core/Metrics/**, scenarios/game-city.json | 2026-10-28 |
| Номера, занятые облаком (ещё не в dev): ADR-0032 (параллельная фаза 3), ADR-0033 (LOD auto), ADR-0034 (прогрессия); world_version 11 — прогрессия (PG1; LOD S5a в бэклоге возьмёт следующий); RejectReason 17, SimEventKind 59–62 — прогрессия | облако | claude/cool-newton-91p1tr | — | 2026-10-28 |
| 6.1 (UI1, UI2, UI6, T13, сейв и меню влиты; UI4 — L55); дальше локал по 6.1: T13 (клиент), сейв/меню, время, UI2, UI6, затем UI4, UI3, UI5 | локал | local/ui1-shell | game/** (Scripts/Ui/**, SimHost.cs, project.godot — InputMap, тема) | 2026-10-28 |
