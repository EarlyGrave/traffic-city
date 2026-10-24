# Стек (зафиксирован 2026-10-24)

| Компонент | Версия | Источник проверки |
|-----------|--------|-------------------|
| Godot .NET | **4.7.2-stable** (последняя стабильная; 4.8 — dev/beta) | NuGet `Godot.NET.Sdk` (версии: …, 4.7.1, 4.7.2-rc.1, 4.7.2, 4.8.0-beta.1) |
| `Godot.NET.Sdk` / `GodotSharp` | 4.7.2, `GodotSharp` собран под `net8.0` | nuspec `godotsharp/4.7.2` → `targetFramework="net8.0"` |
| .NET SDK | **10.0.x** (LTS до ноября 2028), `global.json`: `10.0.100`, `rollForward: latestFeature` | в облаке стоит 10.0.113 из apt Ubuntu 24.04 |
| TFM `Sim.Core`, `game/` | `net8.0` | минимум, который гарантированно грузит Godot 4.7 |
| TFM `Sim.Headless`, тесты, бенчи | `net10.0` | замеры perf — на текущем LTS-рантайме |
| Тесты | xUnit | |
| Микробенчмарки | BenchmarkDotNet | |
| Анализаторы | Microsoft.CodeAnalysis.BannedApiAnalyzers (только Sim.Core, `PrivateAssets=all`) | ADR-0001 |
| CI | GitHub Actions, `ubuntu-latest`, `actions/setup-dotnet` по `global.json` | |

Подробности и причины — `docs/adr/0001-stack-and-target-frameworks.md`.

## Установка .NET в облачной сессии
Домены `dot.net` / `builds.dotnet.microsoft.com` в облачном прокси закрыты (403), поэтому
`dotnet-install.sh` там не скачивается. Работает apt Ubuntu: `apt-get install -y dotnet-sdk-10.0 dotnet-sdk-8.0`.
Рекомендуемый setup script окружения: `apt update && apt install -y dotnet-sdk-10.0 dotnet-sdk-8.0`
(8.0 нужен как рантайм для запуска net8.0-сборок, если понадобится).

## Десктоп (Windows)
Godot 4.7.2 .NET, .NET SDK 10 (x64) + .NET 8 Runtime (для Godot-проекта на net8.0), Git for Windows, Git LFS.
