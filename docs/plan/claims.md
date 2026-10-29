# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| 6.5 прогрессия PG1–PG7 (ночь, волны 3–5: N5, N7, N8) | облако | claude/cool-newton-91p1tr | docs/design/progression.md, src/Sim.Core/Progression/** (новый), src/Sim.Core/Metrics/**, scenarios/game-city.json | 2026-10-28 |
| Номера, занятые облаком (ещё не в dev): ADR-0032 (параллельная фаза 3), ADR-0033 (LOD auto), ADR-0034 (прогрессия); world_version 11 — прогрессия (PG1; LOD S5a в бэклоге возьмёт следующий); RejectReason 17, SimEventKind 59–62 — прогрессия | облако | claude/cool-newton-91p1tr | — | 2026-10-28 |
| 6.1–6.2 клиент: «Новая игра → готовый город» + тосты вех с именами + инспекторы в `TryLocate` (L68), UI16 панель города (L69). `src/Sim.Host/**` не трогаем (TR9 облака) | локал | local/ready-city, local/ui16-city | game/** | 2026-10-29 |
| TR рельеф и вода — TR0/TR0b влиты; TR1 влит; в работе TR2 (агент 191) ∥ TR9 (агент 192) | облако | claude/cool-newton-91p1tr-tr* | docs/design/terrain.md, src/Sim.Core/Terrain/** (новый), src/Sim.Scenarios/** (TR2), src/Sim.Host/** (TR9), scenario-format.md, save-format.md | 2026-10-29 |
| Номера, занятые облаком (TR): ADR-0036; world_version 12 — рельеф (TR1; клапан ADR-0035 возьмёт следующую); RejectReason 18–22, IssueCode 116–120, CommandKind 37–40, Feature.Terraform 11, PARM 241+, хэш п. 14, SegmentFlags.Bridge 8 | облако | claude/cool-newton-91p1tr-tr* | — | 2026-10-29 |
