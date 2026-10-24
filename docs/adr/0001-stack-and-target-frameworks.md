# ADR-0001: Стек и целевые фреймворки

- Статус: принято, 2026-10-24

## Контекст
Godot 4.7.2 (последняя стабильная) поставляет `GodotSharp` под `net8.0`. .NET 8 LTS заканчивает поддержку
10 ноября 2026, .NET 10 — актуальный LTS. Ядро должно грузиться и в Godot, и в headless-раннере.
В Sim.Core нужен Microsoft.CodeAnalysis.BannedApiAnalyzers — это NuGet-пакет.

## Решение
- `global.json`: SDK 10.0.100, `rollForward: latestFeature`.
- `Sim.Core` и `game/` — `net8.0` (безопасный минимум для Godot 4.7). `Sim.Headless`, тесты, бенчи — `net10.0`.
- BannedApiAnalyzers в Sim.Core разрешён как исключение из правила «никаких NuGet в Sim.Core»: это анализатор
  времени компиляции (`PrivateAssets=all`), в рантайм-зависимости не попадает.

## Последствия
- Замеры perf в headless идут на рантайме .NET 10; в игре ядро крутится на рантайме проекта Godot (net8.0).
  Поднять `game/` на `net10.0` — отдельное решение после ручной проверки в редакторе Godot.
- В Sim.Core нельзя пользоваться API новее net8.0.
