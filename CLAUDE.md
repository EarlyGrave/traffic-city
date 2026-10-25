# traffic-city — правила для Claude Code

3D-ситибилдер, сердце — симуляция трафика. Прототип: Godot 4.7 + C#; ядро позже переедет на Rust
(godot-rust/gdext), поэтому `Sim.Core` с первого дня пишется как будущий FFI-крейт.
Полное ТЗ этапа 1: `docs/plan/brief-stage1.md`. План: `docs/plan/roadmap.md`. Стек: `docs/STACK.md`.
**Грабли: `docs/plan/lessons.md` — прочитай перед работой; споткнулся и решил — допиши туда.**

## Структура
- `src/Sim.Core/` — чистая библиотека симуляции (net8.0). Не знает про Godot, файлы, время, консоль.
- `src/Sim.Headless/` — раннер: `run`, `bench`, `compare`, `update-baseline` (net10.0).
- `tests/Sim.Core.Tests/` — xUnit. `bench/Sim.Bench/` — BenchmarkDotNet.
- `game/` — Godot-проект (не в sln, в CI не собирается), ссылается на Sim.Core.
- `scenarios/*.json` — сценарии и параметры ТС. `perf/baseline.json`, `perf/results/`.
- `docs/adr/` — ADR, `docs/design/` — спецификации подсистем.

## Архитектурные правила (портируемость на Rust)
1. В `Sim.Core` — ни одного `using Godot`, никакого ввода, рендера, файлов (хук + analyzer).
2. Data-oriented: агенты — `struct` в плотных массивах (SoA, где выгодно). Сущности — handle
   (index + generation). Никаких ссылок между объектами, наследования сущностей, `class` для агентов.
3. Горячий цикл — 0 байт на тик в стационаре. Запрещены (BannedSymbols.txt): LINQ, System.Random,
   DateTime.Now, Console, Task/async, System.IO, Stopwatch. По ревью: string, замыкания, boxing,
   исключения в горячем пути.
4. Детерминизм: dt = 1/20 с, свой seeded PRNG, фиксированный порядок обновления; тест хэша состояния.
   Многопоточность — только через `Util/WorkerPool` ядра (ADR-0017): в параллельных фазах машина пишет лишь свои
   данные, общие аккумуляторы — целые; хэш не зависит от числа потоков (тест при 1/2/3 потоках).
5. Узкое C-подобное API: `Simulation.Create(ScenarioDesc)`, `Step(int ticks)`, `Enqueue(in Command)`,
   наружу — только снапшоты `ReadOnlySpan<VehicleRenderData>`. Клиент не трогает внутренности ядра.
6. Сценарии и параметры — JSON, независимый от языка (тот же формат будет читать Rust-версия).
7. Каждое значимое решение — короткий ADR в `docs/adr/` (контекст, решение, последствия).
8. Сторонние NuGet в Sim.Core — только через ADR.

## Команды
```bash
./scripts/bootstrap.sh                                   # git hooks + restore (один раз)
dotnet build traffic-city.sln -warnaserror               # сборка, 0 предупреждений
dotnet test traffic-city.sln                             # тесты
dotnet format traffic-city.sln --verify-no-changes       # стиль
dotnet build game/TrafficCity.csproj                     # сборка Godot-клиента
bash scripts/bench-gate.sh --out perf/results/s1k.json --baseline perf/baseline.json   # bench-smoke s1k + compare (единые параметры, до 3 попыток при провале p95)
# справочно, то же вручную:
dotnet run --project src/Sim.Headless -c Release -- bench --scenario s1k --ticks 2000 --warmup-ticks 2400 --repeat 3 --warmup-until-full --out perf/results/s1k.json
dotnet run --project src/Sim.Headless -c Release -- compare --result perf/results/s1k.json --baseline perf/baseline.json
dotnet run --project src/Sim.Headless -c Release -- update-baseline ...   # только осознанно, см. ниже
dotnet run --project bench/Sim.Bench -c Release -- --filter '*'          # микробенчмарки
bash .claude/hooks/test-hooks.sh                         # самопроверка хуков Claude Code
```

## Бюджеты
- s1k (1 000 машин + 10 автобусных маршрутов): tick p95 ≤ 1 мс, 0 B/тик, stuck = 0 за 30 мин игрового времени.
- s10k / s100k / s1m: бюджетов на этапе 1 нет, только базовые цифры в отчёте.
- **Целевое железо (решение владельца 2026-10-25):** ноутбук среднего класса и выше — 6–8 физических ядер
  (Steam HW Survey 07–08.2026: 8 ядер ≈ 28%, 6 ядер ≈ 27.5% — самые массовые), часто гибридные P/E-ядра.
  Бюджеты этапа 2+ — с воркерами ядра: до 4 потоков симуляции (остальное — рендер, главный поток, ОС);
  на одном ядре — только справочная цифра. Облачный контейнер (4 vCPU) меряет до 3–4 потоков.
- `compare`: регрессия tick p95 > 10% или любые аллокации → exit 1; разный `ramp_complete` у результата и baseline → exit 1.
- Одиночный p95 в общем облачном контейнере шумит на ±25–40% (скорость VM, соседи), поэтому допуск 10% применяется
  через `scripts/bench-gate.sh`: провал по p95 засчитывается, только если не прошла ни одна из 3 попыток.
- Все цифры — только из реальных прогонов. Не придумывать, не округлять «на глаз».

## Baseline
`perf/baseline.json` правится только командой `update-baseline` (ручная правка блокируется хуком).
Коммит с новым baseline — отдельный, в сообщении объяснение, почему цифры изменились.

## Definition of Done
- `dotnet build -warnaserror` без предупреждений, `dotnet format --verify-no-changes` чисто.
- Тесты зелёные (включая детерминизм и 0 B/тик).
- bench-smoke s1k + `compare` без регрессии — через `scripts/bench-gate.sh`.
- При архитектурном решении — ADR. Baseline менялся только осознанно.
- В описании PR — before/after из bench.

## Хуки (`.claude/settings.json`, `.claude/hooks/`)
- PreToolUse Edit|Write: запрет `using Godot` в `src/Sim.Core/**`, запрет правки `perf/baseline.json`;
  основная сессия (нет `agent_id`) пишет только `docs/**` и `CLAUDE.md`.
- PreToolUse Bash: защита main — нет push в main/master, нет force push, нет `branch -D main`, нет `reset --hard` на main.
- PostToolUse Edit|Write `*.cs`: сборка затронутого проекта, ошибка → exit 2.
- Stop: если изменены `.cs` — test + `scripts/bench-gate.sh` (bench-smoke s1k + compare с повторами).
- SessionStart (облако): доустановка .NET SDK из `global.json`.

## Оркестрация
- Основная сессия — оркестратор: планирует, раздаёт задачи субагентам, проверяет по фактам, мержит.
  Сама пишет только `docs/plan/`, `docs/adr/`, `docs/REPORT-*.md`, `CLAUDE.md`.
- Модели (решение владельца 2026-10-24): **opus** — архитектура, спеки, сложные алгоритмы (fable больше не используем:
  дорого и не дорабатывает); **sonnet** — реализация по спеке, тесты, клиент, CI, разбор красных прогонов;
  **haiku** — верификатор (прогон тестов/бенчей/отчётных сценариев и пересказ цифр), механические правки,
  тривиальные конфликты мержа, JSON, доки. Лимиты: fable ≤ 3, sonnet ≤ 8, haiku ≤ 20 одновременно.
  Учёт — `docs/plan/agents.md`.
- Параллельные агенты — в git worktree, каждый в своей ветке. Задачи, трогающие API ядра или одни и те же
  файлы, — только последовательно. После каждого мержа — build + test + bench-smoke.
- Экономия лимита (подтверждено владельцем 2026-10-25): проверки пачкой, а не после каждой правки; исполнители проверяют только свою область; тяжёлая проверка (полный прогон, отчётные прогоны s1k,
  бенчи, многократные прогоны) — один агент-верификатор на haiku после мержа волны; красный прогон → разбор sonnet. Брифы короткие, со ссылкой на конкретные разделы спек.
- Перед большим изменением — checkpoint: ветка `checkpoint/<дата>-<суть>` + push (теги не пушатся).

## Git
- Conventional Commits, маленькие коммиты. Без трейлеров соавторства Claude (`.githooks/commit-msg` отклонит).
- В main — только через PR, squash-мерж, все check-runs = success. Force push и переписывание истории запрещены.
