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

## Шаг 2. Sim.Core: граф дорог, генератор города, IDM/MOBIL (без правил перекрёстков)

Собрано из задач A–I (`docs/design/core-api.md` §14) + генератор (ADR-0003) + headless-рендер.
Тесты на момент закрытия: Sim.Core.Tests 254, Sim.Headless.Tests 56 — зелёные (1 нестабильное падение
из 11 прогонов, расследуется отдельной задачей).

### Генератор города (реальные прогоны, Debug, из отчёта агента 2-G)
| | s1k | s10k | s100k | s1m |
|---|---|---|---|---|
| узлов | 438 | 1666 | 10109 | 102098 |
| сегментов | 762 | 2938 | 18003 | 182373 |
| полос всего | 6252 | 24542 | 152336 | 1550356 |
| светофоров | 25 | 100 | 625 | 6400 |
| зон | 60 | 192 | 1020 | 9600 |
| время генерации | 4 мс | 20 мс | 130 мс | 1783 мс |

`Simulation.Create` (Release): s1k 13.5 мс, s10k 17.4 мс, s100k 107.7 мс.

### s1k, 1000 машин (Release, Stopwatch в тесте `Report_S1k`, отчёт агента 2-H)
| | 10.2 мин | 30.2 мин |
|---|---|---|
| активных машин | 1000 | 1000 |
| создано / завершено поездок | 1144 / 144 | 1440 / 440 |
| avg speed | 44.76 км/ч | 45.22 км/ч |
| trips/min | 14.16 | 14.59 |
| stuck / gridlock | 0 / 0 | 0 / 0 |
| HardClamps (клэмп I6 сработал) | 2602 | 8469 |

Время тика: 30 мин — p50 135 мкс, p95 231 мкс, p99 315 мкс, max 17.2 мс (JIT/фон тест-хоста; без tiered JIT
max 6.1 мс). 0 B/тик в стационаре подтверждён тестом. Официальные bench-цифры — в шаге 5 (`Sim.Headless bench`).

Ограничения шага 2 (STAGE2-TEMP): маршрутов нет — машины выбирают connector случайно, прибытие = въезд на
любую полосу зоны назначения, поэтому завершаемость поездок низкая. Перекрёстки без правил: на слиянии
connector'ов возможны касания на границе полос (I6 гарантирован внутри полос). Закрывается шагом 3.

### Микробенчмарки (BenchmarkDotNet `--job short`, разброс большой)
| Бенч | Mean | Alloc |
|---|---|---|
| LaneGeometry.Evaluate × 10k полос | 97.8 мкс | 0 |
| LaneOccupancy InsertSorted+Unlink (40 машин) | 51.4 нс | 0 |
| Фазы 5–7, 1000 машин на кольце | 37.6 мкс | 0 |
| A* случайная пара s1k / s100k | 32.0 мкс / 1348 мкс | 0 |
| RouteBuilder, кэш-хит s1k | 2.0 мкс | 0 |

### Видео
`dotnet run --project src/Sim.Headless -c Release -- render --scenario s1k --seconds 60 --warmup-s 240 --out perf/results/s1k-step2.mp4`
(рендер ~70–90 кадров/с при 1280², включая кодирование ffmpeg).
