# Захваченные задачи (локал ↔ облако)

Правила — CLAUDE.md, «Две среды». Перед стартом: `git pull origin dev`, проверить таблицу, дописать строку, запушить в `dev`.
При мерже задачи в `dev` строку удалить.

| Задача | Среда | Ветка | Папки/файлы | С какого числа |
|---|---|---|---|---|
| RP8: *-res на профили, golden, замеры (агент L40) | локал | local/rp8-profiles | scenarios/*-res*, golden/корпус тестов, tests/**, rust golden, docs/design/road-profiles.md | 2026-10-28 |
| CI A/B бенча s1k (ADR-0031) | облако | claude/adoring-ritchie-cxzib8-ciab | .github/workflows/ci.yml, scripts/bench-ab.sh, scripts/bench-gate.sh (не менять поведение), docs/adr/0031 | 2026-10-28 |
