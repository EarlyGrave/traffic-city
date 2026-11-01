# Грабли и как их обходить — индекс

Читай этот индекс в начале сессии. Тематический файл открывай, только когда работаешь в его области; искать по всем — `grep -rn "<слово>" docs/plan/lessons/`.
Формат записи: **симптом** → причина → что делать. Новый урок — в конец тематического файла; если правило важно почти для любой задачи — ещё одна строка в «Ключевые правила» ниже.

## Тематические файлы

| Файл | О чём |
|---|---|
| [env-and-tools.md](env-and-tools.md) | облако, Windows-локал, субагенты, worktree, хуки, мерж |
| [sim-traffic.md](sim-traffic.md) | перекрёстки, светофоры, маршрутизация, LOD/meso, воркеры, карманы, правка сети |
| [sim-city.md](sim-city.md) | жители, зоны, скимы, экономика и казна, агрегат, парковка, пешеходы, зонирование |
| [saves.md](saves.md) | сейвы: схема, корпус, миграции, валидатор загрузки, что считать состоянием |
| [tests-and-perf.md](tests-and-perf.md) | тесты 0 B/тик, JIT-флейки, валидатор и превью |
| [client-godot.md](client-godot.md) | Godot-клиент: рендер, шейдеры, LOD, стиль VS*, профили RP* |
| [rust.md](rust.md) | Rust-пилот: FFI, golden, конфликты |

## Ключевые правила

Ожидание и долгие команды
1. Ждать процесс только по PID (`while kill -0 <pid>`), не по `pgrep -f`/`pkill -f <шаблон>`: шаблон находит сам себя. [env](env-and-tools.md), [sim-city](sim-city.md)
2. Не ставить в фон циклы `until/sleep` повторно; ждать одной foreground-командой с `timeout`. [env](env-and-tools.md)
3. Даже docs-only коммит гоняет полный pre-commit (на нагруженном облаке 15–35 мин): копить правки в 1–2 коммита, коммит — отдельным фоновым процессом с большим `timeout`, вывод в файл. [sim-city](sim-city.md), [client-godot](client-godot.md)
4. Pre-commit тестирует рабочее дерево, не индекс: пока идёт хук, `src/` и `tests/` не править. [sim-city](sim-city.md)

Git, worktree, мерж
5. Мерж и коммит — разными вызовами; перед коммитом `git diff --name-only --diff-filter=U` и `grep -rn '^<<<<<<<' docs src tests` пусты. [env](env-and-tools.md)
6. Мерж и удаление ветки — разными командами; удалять только после `git branch --merged`; не `--ff-only`, если ветка ушла вперёд. [env](env-and-tools.md)
7. `--no-verify` запрещён всегда; для красных тестов на мерже — `git merge --no-commit`, починить, один коммит. [env](env-and-tools.md)
8. После squash-мержа не пересоздавать ветку force-ом: влить `origin/main` обычным merge; старые ветки — переносом diff (`git diff | git apply --3way`). [env](env-and-tools.md)
9. Worktree агента создаётся от `main`, а не от рабочей ветки: сначала закоммитить, в брифе — ff на локальную ветку; не `cd` в чужие worktree, не удалять worktree продолженного агента. [env](env-and-tools.md)

Хуки и guard Bash
10. guard-bash режет сложные составные команды, heredoc с python/длинным текстом, слово `hash`, `$переменные` в путях: файлы — через Write/Edit, скрипты — в scratchpad и запуск простой командой с литеральным путём. [env](env-and-tools.md), [sim-traffic](sim-traffic.md)

Windows и PowerShell
11. `bash` из PowerShell — это WSL; скрипты запускать через Git Bash (`C:\Program Files\Git\bin\bash.exe`). `python3` в Git Bash — заглушка Store, использовать `python`. [env](env-and-tools.md)
12. PowerShell 5.1 `Get-Content` без `-Encoding utf8` ломает кириллицу, Python пишет CRLF без `newline=''` (падает `dotnet format`): править через Edit/sed. [client-godot](client-godot.md)
13. Godot на Windows: запускать основной exe (не `_console.exe`), кавычить пути с пробелом вручную в `Start-Process -ArgumentList`. [env](env-and-tools.md), [client-godot](client-godot.md)

Godot и клиент
14. Окна Godot не должны всплывать поверх рабочего стола: `--headless` или `--position -3000,-3000` + `-WindowStyle Hidden`; `Minimized` даёт чёрный кадр. [env](env-and-tools.md), [client-godot](client-godot.md)
15. Перед каждым запуском Godot — `dotnet build game/...` (движок C# не собирает), в новом worktree — `--headless --import`. [client-godot](client-godot.md), [env](env-and-tools.md)
16. `gl_compatibility` считает освещение в sRGB, а `visibility_range` меряет до центра узла: сравнивать «до/после» в одном методе рендера, пороги LOD сдвигать на полудиагональ ячейки. [client-godot](client-godot.md)

Тесты, детерминизм, 0 B/тик
17. Тесты 0 B: `AllocationProbe` (повтор при GC), `ConcurrentGarbageCollection=false`, для воркеров — `MeasureSimThreads` и `DisableParallelization`; не `GC.GetTotalAllocatedBytes`. [tests-and-perf](tests-and-perf.md)
18. Любое «scratch», которое читает другая фаза раньше перезаписи, — состояние сейва; решает тест «сейв на каждом тике», не хэш. [saves](saves.md)
19. Новое поле/кэш «на сутки» или по версии обязан сбрасываться по версии входов; смена `State` здания — `Buildings.Version++`. [sim-city](sim-city.md)
20. Сменилось поведение или схема — эталоны перегенерировать отдельным коммитом (`TC_REGEN_CORPUS=1`, затем `dotnet build tests/Sim.Core.Tests`); бюджет корпуса растёт, это норма. [saves](saves.md), [sim-city](sim-city.md)
21. `float → int` в Sim.Core — только через `DetMath.ToIntSat/ToLongSat`; Golden Rust-пилота на Windows и Linux различаются (`headings_hash`). [sim-city](sim-city.md), [rust](rust.md)

Бенчи и замеры
22. p95 в облаке шумит ±25–40%, скорость VM плавает день ото дня: решает `scripts/bench-gate.sh` (до 3 попыток), параметры bench — только там; регрессию искать бисектом в одной серии. [env](env-and-tools.md), [client-godot](client-godot.md)
23. Цену на машину снимать на масштабе цели (s100k), а не умножать s10k; метрики stuck/ожиданий — на 3+ seed и окне после рампы. [sim-traffic](sim-traffic.md), [client-godot](client-godot.md)
24. Фаззер и Debug-сверки ловят то, что хэш не видит: гонять в Debug, несколькими seed, с `TC_LONG=1`. [saves](saves.md), [sim-traffic](sim-traffic.md)
