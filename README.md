# traffic-city

3D-градостроительный симулятор с упором на симуляцию трафика. Прототип на Godot 4 + C#;
ядро симуляции (`Sim.Core`) не зависит от Godot и спроектировано так, чтобы позже переехать на Rust.

Цель этапа 1: 1 000 машин и базовый общественный транспорт на процедурном городе, с замерами
производительности и качества трафика. Стек и версии — [docs/STACK.md](docs/STACK.md), решения — [docs/adr/](docs/adr/).

## Структура

| Путь | Назначение |
|------|------------|
| `src/Sim.Core/` | чистая библиотека симуляции (`net8.0`, без Godot, ввода-вывода и LINQ) |
| `src/Sim.Headless/` | консольный раннер: `run`, `bench`, `compare`, `update-baseline` |
| `tests/Sim.Core.Tests/` | xUnit-тесты |
| `bench/Sim.Bench/` | микробенчмарки BenchmarkDotNet |
| `game/` | Godot-проект (`TrafficCity.csproj`), ссылается на `Sim.Core`; не входит в `traffic-city.sln` и в CI |
| `scenarios/` | JSON-сценарии и параметры ТС |
| `perf/` | `baseline.json` и `results/` |
| `docs/` | план, ADR, стек, отчёты |

## Быстрый старт

Нужен .NET SDK 10 (см. `global.json`) и .NET 8 runtime.

```bash
./scripts/bootstrap.sh          # git-хуки (core.hooksPath=.githooks) + dotnet restore
```

## Команды

```bash
dotnet build traffic-city.sln -warnaserror      # сборка, предупреждения = ошибки
dotnet test traffic-city.sln                    # юнит-тесты
dotnet format traffic-city.sln --verify-no-changes

# микробенчмарки
dotnet run --project bench/Sim.Bench -c Release -- --filter '*'

# headless-раннер
dotnet run --project src/Sim.Headless -c Release -- --help
dotnet run --project src/Sim.Headless -c Release -- bench --scenario s1k --ticks 2000 --out perf/results/s1k.json
dotnet run --project src/Sim.Headless -c Release -- compare --result perf/results/s1k.json --baseline perf/baseline.json

# Godot-проект (открывать в Godot 4.7.2 .NET)
dotnet build game/TrafficCity.csproj
```

## License

[PolyForm Noncommercial License 1.0.0](LICENSE). Использование в некоммерческих целях разрешено на условиях лицензии.

Required Notice: Copyright (c) 2026 Илья Мишин (github.com/EarlyGrave)
