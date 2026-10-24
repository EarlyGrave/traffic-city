# Roadmap — этап 1

Цель этапа: 1 000 машин + 10 автобусных маршрутов на процедурном городе, замеры perf и качества трафика.
Каждый шаг = одна PR в `main` (squash), CI зелёный, before/after из bench в описании.

Статусы: `todo` / `wip` / `done` / `blocked`.

## Шаг 1. Каркас репо, правила, хуки, CI — `done` (PR #1, кроме 1.3 — спека API ещё в работе)
| # | Задача | Модель | Критерий готовности |
|---|--------|--------|---------------------|
| 1.1 | Скелет: sln, Directory.Build.props, global.json, .editorconfig, BannedSymbols.txt, проекты-заглушки (Core/Headless/Tests/Bench/game), .gitignore/.gitattributes, LICENSE, README, CI, .githooks, bootstrap | sonnet | `dotnet build -warnaserror` и `dotnet test` зелёные; banned API реально ловится |
| 1.2 | Хуки Claude Code: `.claude/settings.json`, `.claude/hooks/*.sh` | sonnet | каждый хук проверен скриптом-тестом на JSON-входах (allow/block) |
| 1.3 | Проектирование API ядра (handle, команды, снапшоты, ScenarioDesc, JSON-схема сценария) | fable | `docs/design/core-api.md` + ADR; без кода в src |
| 1.4 | Демонстрация хуков: намеренное нарушение → блок → откат | оркестратор + агент | лог блокировки в REPORT |

## Шаг 2. Sim.Core: граф дорог, генератор города, IDM без перекрёстков — `wip` (волны: AB → C,D,I → E,F,G → H; см. core-api.md §14)
- 2.1 (sonnet, по спеке 1.3) API-каркас: Simulation.Create/Step/Enqueue, handle-пулы, PRNG (PCG32), хэш состояния.
- 2.2 (fable) генератор города ~3×3 км, артерии 2+2, улицы 1+1, нерегулярные узлы, scale.
- 2.3 (sonnet) IDM + MOBIL по полосам, без перекрёстков; тест «нет проходов насквозь».
- 2.4 (sonnet) Sim.Bench: микробенчи IDM-шага и генератора.
- 2.5 (sonnet) `Sim.Headless render`: растровый 2D-вид сверху из снапшотов ядра → кадры → ffmpeg → mp4 (запрос владельца: видео прогресса; Godot 4 в облаке недоступен).
- Критерий: тест детерминизма (хэш), 0 B/тик в тесте, бенчи запускаются, первое видео.

## Шаг 3. Перекрёстки, светофоры, знаки, маршрутизация — `todo`
- 3.1 (fable) модель перекрёстка: connector-полосы, конфликтные пары, правила приоритета (светофор, yield/STOP, помеха справа, don't-block-the-box) — спека + ADR.
- 3.2 (sonnet) реализация перекрёстков по спеке.
- 3.3 (fable→sonnet) A* (вес = время), кэш по парам зон, бюджет запросов/тик.
- 3.4 (sonnet) поездки: зоны, OD, спавн на краю/парковке, исчезновение по прибытии, метрика stuck.
- Критерий: s1k 30 мин игрового времени — stuck = 0, gridlock = false.

## Шаг 4. Автобусы и остановки — `todo`
- маршруты как последовательность остановок, карман/обочина, стоянка от числа пассажиров, интервал, headway deviation.

## Шаг 5. Headless-бенч, baseline s1k, лестница s10k/s100k/s1m — `todo`
- bench/compare/update-baseline, полный JSON метрик; baseline s1k; цифры по лестнице в REPORT.
- Бюджет s1k: tick p95 ≤ 1 мс, 0 B/тик, stuck = 0 за 30 мин.

## Шаг 6. Godot-клиент — `todo`
- MultiMesh по типу ТС, ленты дорог, RTS-камера, интерполяция 20 Гц → 60+ FPS, оверлеи (heatmap, фазы, маршрут), HUD F3.
- Критерий: `dotnet build game/` без предупреждений; импорт Godot — если доступен бинарь.

## Шаг 7. Отчёт `docs/REPORT-stage1.md` — `todo`
- таблица метрик по лестнице, топ-3 узких места (dotnet-counters/dotnet-trace), чек-лист ручной проверки, предложения на этап 2.
