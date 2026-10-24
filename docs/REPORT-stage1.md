# Отчёт этапа 1

> Документ пополняется по мере закрытия шагов roadmap. Все цифры — из реальных прогонов.

## Шаг 1. Каркас, правила, хуки, CI

### Проверки после мержа веток каркаса и хуков (локально, облачный контейнер)
- `dotnet build traffic-city.sln -warnaserror` → `0 Error(s)`, 0 предупреждений.
- `dotnet test traffic-city.sln` → `Passed: 1, Failed: 0`.
- `dotnet build game/TrafficCity.csproj` (Godot.NET.Sdk 4.7.2 из NuGet) → без ошибок и предупреждений.
- `dotnet format --verify-no-changes` → чисто.
- `bash .claude/hooks/test-hooks.sh` → `Итого: 54/54 PASS`.
- BannedApiAnalyzers: временный `new System.Random()` + LINQ в Sim.Core → `error RS0030` (проверено агентом каркаса, откачено).

### Демонстрация хуков (намеренные нарушения в живой сессии)
| Нарушение | Кто | Результат |
|-----------|-----|-----------|
| Write `src/Sim.Core/HookDemo.cs` с `using Godot;` | оркестратор | `BLOCKED: в src/Sim.Core/** запрещены 'using Godot' ...` (guard-core) |
| Write `src/Sim.Core/HookDemo.cs` без Godot | оркестратор | `BLOCKED: оркестратор не пишет код — делегируй субагенту ...` (guard-orchestrator) |
| Write `perf/baseline.json` | оркестратор | `BLOCKED: perf/baseline.json нельзя править вручную ...` |
| force push своей ветки | оркестратор | `BLOCKED: git push --force / -f / --force-with-lease запрещены.` (guard-bash) |
| Write `src/Sim.Core/HookDemo.cs` с `using Godot;` | субагент (haiku, есть `agent_id`) | `BLOCKED: в src/Sim.Core/** запрещены 'using Godot' ...` |
| `.githooks/commit-msg` на сообщении с трейлером соавторства Claude | скрипт | `ОТКАЗ — трейлер «Co-Authored-By: Claude» запрещён.`, exit 1; чистое сообщение → exit 0 |
| `post-build.sh` на `.cs` с `int x = ;` | ручной вызов хука | `error CS1525: Invalid expression term ';'`, exit 2 |
| `git commit` | оркестратор | pre-commit прогнал format + build + test, `pre-commit: ok` |

Файлы-нарушители не создавались (блок до записи); дерево чистое.

### Находки
- У субагента post-build молчал: worktree агентов создаются от `main` (там ещё нет csproj).
  Исправлено процессом — каждый бриф начинается с fast-forward worktree на рабочую ветку.
  Ручной вызов хука в worktree с проектами даёт exit 2 с ошибкой компилятора.
- guard-bash эвристический: срабатывает и на текст команды внутри heredoc/кавычек
  (например, если в Bash-команде записывается markdown с примером force push). Это ложный блок,
  а не пропуск — безопасная сторона. Обход: такие тексты писать инструментом Write.
