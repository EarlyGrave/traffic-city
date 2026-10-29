# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| 6.5 прогрессия PG1–PG7 (ночь, волны 3–5: N5, N7, N8) | облако | claude/cool-newton-91p1tr | docs/design/progression.md, src/Sim.Core/Progression/** (новый), src/Sim.Core/Metrics/**, scenarios/game-city.json | 2026-10-28 |
| Номера, занятые облаком (ещё не в dev): ADR-0032 (параллельная фаза 3), ADR-0033 (LOD auto), ADR-0034 (прогрессия); world_version 11 — прогрессия (PG1; LOD S5a в бэклоге возьмёт следующий); RejectReason 17, SimEventKind 59–62 — прогрессия | облако | claude/cool-newton-91p1tr | — | 2026-10-28 |
| 6.1–6.2 клиент (UI1, UI2, UI6, T13, сейв/меню, UI4, UI3 влиты); дальше UI5 | локал | local/ui1-shell | game/** (Scripts/Ui/**, SimHost.cs, project.godot — InputMap, тема) | 2026-10-28 |
| ночь, далее: N3 T5 (Tools, Network), N4 EV1 (SimEvent 63+, Metrics), N6 T9 (Tools, Transit) | облако | claude/cool-newton-91p1tr-n* | см. stage6-plan.md «План облака на ночь» | 2026-10-28 |
| ночь N8: PG6 + E7 — scenarios/game-city.json, подготовка «готового города», калибровка вех/казны/жалоб на 3 seed | облако | claude/cool-newton-91p1tr-n8 | scenarios/game-city.json, src/Sim.Scenarios (ReadyCity), src/Sim.Core/City (только дефолты параметров, если нужно), docs/design/progression.md §7/§10, tests | 2026-10-29 |
