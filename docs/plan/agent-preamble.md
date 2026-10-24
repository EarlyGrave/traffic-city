# Общие правила для субагентов (читать первым)

1. **Сначала синхронизируй worktree:** `git merge --ff-only claude/adoring-ritchie-cxzib8`.
   Worktree создаётся от `main`, а актуальная работа — в этой ветке. Если ff невозможен — остановись и сообщи.
2. Прочитай `CLAUDE.md` (правила, команды, DoD) и `docs/plan/lessons.md` (грабли).
   Спеки: `docs/design/core-api.md` (API ядра — обязательна для любой задачи в Sim.Core),
   `docs/design/scenario-format.md` (JSON), полное ТЗ — `docs/plan/brief-stage1.md`.
3. Трогай только файлы из своего брифа. Нужна правка чужой папки или спеки — сделай минимальным
   отдельным коммитом и явно перечисли в отчёте.
4. Sim.Core: никакого Godot/IO/LINQ/Random/DateTime/Console/Task (анализатор RS0030), struct + handle,
   без аллокаций в `Step`, без исключений в горячем пути, детерминизм.
5. Перед каждым коммитом: `dotnet build traffic-city.sln -warnaserror`, `dotnet test traffic-city.sln`,
   `dotnet format traffic-city.sln --verify-no-changes` (если не ок — `dotnet format traffic-city.sln`).
   pre-commit это и так проверит. **`--no-verify` не использовать.**
6. Conventional Commits, маленькие коммиты. Никаких трейлеров Co-Authored-By / упоминаний Claude.
   Не пушь, не трогай main, не делай force push, не создавай теги.
7. `perf/baseline.json` вручную не править (только `update-baseline`). `scenarios/` и `docs/design/` — только если это в брифе.
8. Споткнулся о неочевидную проблему окружения/инструментов и решил — допиши строку в `docs/plan/lessons.md`.
9. Отчёт в конце: имя ветки, список коммитов, **реальные выводы** проверок (build/test, числа из тестов/бенчей),
   отклонения от брифа и открытые вопросы. Не придумывай цифры.
