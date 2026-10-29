# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| 6.5 прогрессия PG1–PG7 (ночь, волны 3–5: N5, N7, N8) | облако | claude/cool-newton-91p1tr | docs/design/progression.md, src/Sim.Core/Progression/** (новый), src/Sim.Core/Metrics/**, scenarios/game-city.json | 2026-10-28 |
| Номера, занятые облаком (ещё не в dev): ADR-0032 (параллельная фаза 3), ADR-0033 (LOD auto), ADR-0034 (прогрессия); world_version 11 — прогрессия (PG1; LOD S5a в бэклоге возьмёт следующий); RejectReason 17, SimEventKind 59–62 — прогрессия | облако | claude/cool-newton-91p1tr | — | 2026-10-28 |
| TR рельеф и вода — TR0–TR1 влиты, W1 (TR2, TR9) и W2 (TR3) влиты; **пауза** — следующая W3: TR4 ∥ TR6 | облако | claude/cool-newton-91p1tr-tr* | docs/design/terrain.md, src/Sim.Core/Terrain/** (пауза; Network/** и Tools/** свободны) | 2026-10-29 |
| Номера, занятые облаком (TR): ADR-0036; world_version 12 — рельеф (TR1; клапан ADR-0035 возьмёт следующую); RejectReason 18–22, IssueCode 116–120, CommandKind 37–40, Feature.Terraform 11, PARM 241+, хэш п. 14, SegmentFlags.Bridge 8 | облако | claude/cool-newton-91p1tr-tr* | — | 2026-10-29 |
| UI-фреймворк F1: токены, ThemeBuilder, иконки, шрифт, витрина | локал | local/ui-f1 | game/Scripts/Ui/Kit/** (новый), game/Scripts/Ui/{UiTheme,UiPalette,UiScale}.cs, game/assets/ui/** | 2026-10-29 |
