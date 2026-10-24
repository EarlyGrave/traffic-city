# Грабли и как их обходить

Сюда записываем каждую ошибку, о которую споткнулись, и как её решили — чтобы не наступать снова.
Формат: **симптом** → причина → что делать. Новые записи — сверху.

## Окружение (облако)
- **`dotnet-install.sh` / `dot.net` → 403.** Прокси закрывает microsoft-домены.
  → Ставить из apt Ubuntu: `apt-get install -y dotnet-sdk-10.0 dotnet-sdk-8.0`. NuGet (api.nuget.org) открыт.
- **polyformproject.org → 403.** → Официальный текст лицензии:
  `https://raw.githubusercontent.com/polyformproject/polyform-licenses/1.0.0/PolyForm-Noncommercial-1.0.0.md`
  (ветки `master` там нет — 404, нужен тег-путь `1.0.0`).
- **Релизы Godot на github.com → 403.** Версии Godot смотреть через NuGet: `api.nuget.org/v3-flatcontainer/godot.net.sdk/index.json`.

## Субагенты и worktree
- **Worktree агента создаётся от `main`, а не от рабочей ветки оркестратора** → агент не видит свежих
  файлов (брифа, csproj), post-build хук молчит. → Первый шаг каждого брифа:
  `git merge --ff-only claude/adoring-ritchie-cxzib8` (локальная ветка видна из worktree). Рабочую ветку
  перед раздачей задач — закоммитить.
- **`git add -A` в основном дереве подхватил `.claude/worktrees/*` как вложенные репозитории.**
  → `.claude/worktrees/` в `.gitignore` (и в `.git/info/exclude`). Перед коммитом смотреть `git status --short`.
- **`cd` в worktree агента из Bash сдвигает рабочий каталог сессии.** → Не `cd` в чужие worktree;
  использовать `git -C <путь>` или подкоманды в `( ... )`.
- Смерженные worktree удалять: `git worktree remove --force <путь> && git branch -D <ветка>`, затем `git worktree prune`.

## Хуки
- **guard-bash блокирует Bash-команду, если строка вида «git push --force» есть внутри heredoc/кавычек**
  (эвристика без полноценного парсера). → Тексты с такими примерами писать инструментом Write,
  а git-команды запускать отдельной Bash-командой.
- **Коммит с запрещённым трейлером (для демонстрации commit-msg) режет классификатор авто-режима.**
  → Проверять хук напрямую: `bash .githooks/commit-msg <файл-с-сообщением>`.
- Хуки из `.claude/settings.json` подхватываются живой сессией сразу после мержа — оркестратор
  после этого физически не может писать вне `docs/**` и `CLAUDE.md`.

## Git-хуки
- **pre-commit падает в worktree без `.sln`** (core.hooksPath общий для всех worktree). Причина — worktree
  от старого `main`. → Синхронизация по `agent-preamble.md` п.1; `--no-verify` не использовать.

## Мерж веток агентов
- **Два параллельных агента поправили одну и ту же строку спеки** (`scenario-format.md`, `irregular_nodes`) →
  конфликт при мерже. → В брифе явно указывать, какие разделы спеки агент может править; конфликты в docs
  разрешает оркестратор, объединяя смысл обеих правок.

## Визуализация в облаке
- **Godot 4 нельзя скачать** (github releases → 403), в apt только godot3. → Видео — через `Sim.Headless render`
  (растр из снапшотов ядра) + системный `ffmpeg` (есть в контейнере). matplotlib нет — рисуем сами на C#.
- **Агенты не видят свежих коммитов, если оркестратор не запушил ветку**, а некоторые делают ff на
  `origin/...`. → Перед раздачей: commit + push рабочей ветки; в брифе — ff на ЛОКАЛЬНУЮ ветку.
- **CA1861 на литералах-массивах в аргументах (тесты)** → выносить массив в локальную переменную/static readonly.
- **После squash-мержа PR ветки, начатые от старой (несквошенной) истории, дают add/add-конфликты почти во
  всех файлах.** → Не `git merge`, а перенос diff'а: `git diff <старый-head-PR> <ветка> | git apply --3way`
  (дерево старого head == дерево squash-коммита в main — проверить `git diff --quiet <head> origin/main`).
- **Оркестратор закоммитил merge с маркерами конфликта**: `git merge ... | grep conflict` в цепочке с
  `git add -A && git commit` — конфликт не остановил цепочку. → После каждого мержа отдельной командой
  `git diff --name-only --diff-filter=U` и `grep -rn '^<<<<<<<' docs src tests scenarios`; коммит — только при пустом выводе.

## Тесты 0 B и JIT
- **Редкий флейк `Step_Allocates_ZeroBytes_InSteadyState` (~1-2% прогонов `dotnet test traffic-city.sln`, только при параллельной
  загрузке CPU/классов): 3-8 КБ в окне «0 B», по фазам - в произвольной (IntegratePhase, SyncStructuralCounters).** Исходный код
  ядра не аллоцирует: это синхронная OSR/tier-up-компиляция метода с циклом в потоке теста (tier-0 патчпойнты; счётчики общие для
  всех потоков, поэтому момент зависит от соседних тестов), JIT/загрузчик типов кладут объекты в управляемую кучу.
  Прогрев не помогает - момент недетерминирован. → В `Sim.Core.Tests.csproj` `<TieredCompilation>false</TieredCompilation>`
  (полная оптимизация сразу, без тиринга/OSR). Для новых проектов с `GetAllocatedBytesForCurrentThread` - то же.
  Диагностика: временно разнести тик на фазы и мерить аллокации по каждой, гонять набор в 3 параллельных циклах.
## Godot-клиент
- **`dotnet build game/TrafficCity.csproj -warnaserror` работает без Godot-бинаря** (Godot.NET.Sdk 4.7.2 в nuget-кэше). Правила анализатора в game те же: CA1001 на классах с Godot-ресурсами в полях (подавлено pragma), IDE1006 требует `_camelCase` для private static readonly. Проверка стиля: `dotnet format game/TrafficCity.csproj --verify-no-changes` (game не в sln).
- **Семантический конфликт без текстового**: одна ветка перенесла тип в другой namespace, другая добавила файл со старым using → git сливает чисто, падает сборка в pre-commit. → После мержа параллельных веток всегда build до коммита.
