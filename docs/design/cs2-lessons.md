# Уроки Cities: Skylines II — реестр рисков traffic-city

Статус: черновик архитектора, 2026-10-24. Обновлять при каждом новом ADR, закрывающем риск.

**Как читать.** В «Что было в CS2» — только публичные факты со ссылкой на источник (официальные анонсы
Paradox/CO, пресса, профильные разборы). Отзывы игроков (Steam) помечены **[игроки]** — это свидетельства,
а не подтверждённые баги. Колонка «Корневая причина» — **наша оценка** (кода CS2 мы не видели). Номера
ADR-0011–0015 — **предлагаемые**, ещё не написаны (номера зарезервированы). ADR-0009 зарезервирован под иерархическую маршрутизацию (roadmap 2.2).
ADR-0016 занят горячим путём s10k (roadmap 2.4b); следующие ADR вне этого реестра — с 0017.

Этапы: **1** — сделано (PR #1–#4), **2** — текущий (`roadmap.md`), **3+** — пешеходы/пассажиры-агенты,
зонирование и спрос, арт (порядок владельца). Статус: **закрыто** — есть механизм и автоматическая проверка;
**в работе** — механизм есть или в плане этапа 2, проверки нет/неполна; **открыто** — решения нет.

## 0. Контекст: хронология CS2 (факты)

| Дата | Событие | Источник |
|---|---|---|
| 2023-09 | Консольные версии перенесены с релиза на весну 2024 ради «quality targets» | [VGC](https://videogameschronicle.com/news/cities-skylines-2-has-been-delayed-on-consoles-until-spring-2024), [Gematsu](https://gematsu.com/2023/09/cities-skylines-ii-for-ps5-xbox-series-delayed-to-spring-2024) |
| 2023-10-24 | Релиз на PC; производительность — главный предмет критики; первый патч производительности 26.10 «не решает всё» | [TechRadar](https://www.techradar.com/gaming/pc-gaming/no-cities-skylines-2s-performance-issues-arent-because-of-the-citizens-teeth-developer-confirms) |
| 2023-10-27 | CO/Paradox: зубы не влияют на производительность, но **у персонажей нет LOD**, LOD планируется «для всех ассетов» | [Wargamer](https://www.wargamer.com/cities-skylines-2/teeth-rumors), [TechRadar](https://www.techradar.com/gaming/pc-gaming/no-cities-skylines-2s-performance-issues-arent-because-of-the-citizens-teeth-developer-confirms) |
| 2023-11 | Разбор кадра (RenderDoc, P. Huhtala): игра упирается в **GPU**, кадр ≈ 88 мс; избыточная геометрия, нет LOD у большинства объектов, слабый куллинг, тени от всех объектов без учёта размера/дистанции | [Windows Central](https://www.windowscentral.com/gaming/cities-skylines-ii-runs-horribly-a-deep-dive-into-unity-explains-why), [Gigazine](https://gigazine.net/gsc_news/en/20231106-why-cities-skylines-2-performs-poorly), [HotHardware](https://hothardware.com/news/cities-skylines-2-autopsy), оригинал blog.paavo.me/cities-skylines-2-performance |
| 2024-03-25 | Моддинг по частям: бета Paradox Mods (код-моды, карты), редактор ассетов «ещё в работе»; Steam Workshop не используется | [TechRaptor](https://techraptor.net/gaming/news/paradox-mods-for-cities-skylines-2-explained), [SI](https://videogames.si.com/news/cities-skylines-2-mods-release-delayed) |
| 2024-04 | DLC Beach Properties: отзывы 5.56% положительных (SteamDB), CEO: «выпустили поспешно»; DLC бесплатно + возврат денег; после снятия DLC у части игроков сохранения грузились с белыми «кубами» вместо зданий; консоль → октябрь 2024 | [TheSixthAxis](https://www.thesixthaxis.com/2024/04/18/we-let-you-down-cities-skylines-2-dlc-being-refunded-amidst-player-dissatisfaction/), [Insider Gaming](https://insider-gaming.com/paradox-apologizes-again-after-even-more-cities-skylines-2-problems/), [Shacknews](https://shacknews.com/article/139553/cities-skylines-2-state-of-game-console-delay) |
| 2024-06-24 | Патч 1.1.5f1 «Economy 2.0»: убраны субсидии, переделана аренда («virtual landlord» убран, содержание здания делят арендаторы); на старых сохранениях — временный рост смертности и отрицательный баланс; «no guarantees for modded saves» | [Paradox: economy patch](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/economy-patch-is-here), [Save game checklist](https://paradoxinteractive.com/games/cities-skylines-ii/news/save-game-checklist), [Dev diary economy 2](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/dev-diary-economy-part-two), [Dexerto](https://www.dexerto.com/gaming/cities-skylines-2-patch-notes-1-1-5f1-economy-changes-bug-fixes-more-2794481/) |
| 2024-07 | Консоль отложена **на неопределённый срок**: «не достигли целей по стабильности и производительности» | [TechRadar](https://www.techradar.com/gaming/consoles-pc/paradox-delays-the-console-release-of-cities-skylines-2-indefinitely-says-we-have-not-yet-met-the-stability-and-performance-targets-we-set), [VGC](https://www.videogameschronicle.com/news/cities-skylines-2s-console-version-has-been-delayed-again-this-time-indefinitely) |
| 2024-09 | 1.1.8: «массовая бездомность» — убрана случайная проверка, бездомные всегда ищут жильё; 1.1.10f1 — палатки в парках исчезают после ухода бездомных | [Automaton](https://automaton-media.com/articles/newsjp/cities-skylines-2-20240904-309144/), [Paradox 1.1.10f1](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/patch-1-1-10f1), [PCGamesN](https://www.pcgamesn.com/cities-skylines-2/latest-update) |
| 2025-11 | Paradox и CO расстаются; с начала 2026 разработку (патчи, редактор, консоль, DLC) ведёт **Iceflake** | [Paradox: an update](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/an-update-on-cities-skylines-ii), [heise](https://heise.de/-11083454), [CGMagazine](https://www.cgmagonline.com/news/cities-skylines-iceflake-studios/) |
| 2025-12-04 | 1.5.2f1: **первая** бета поддержки ассет-модов в редакторе — через 2 года после релиза; требует обновления базы и всех DLC под «новый способ обработки ассетов» | [Paradox: asset mods patch notes](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/asset-mods-patch-notes) |
| 2026 | Iceflake: поиск пути — одна из главных проблем CPU; оптимизация может потребовать больших изменений поведения симуляции. Разработчики (City Corner): LOD, вода, terrain дают слишком много полигонов; «nothing can be precomputed ahead of time or baked in» | [PCGH](https://www.pcgameshardware.de/Cities-Skylines-2-Spiel-74219/News/Update-Leistungsprobleme-Wegfindung-1552963/), [PCGamesN](https://www.pcgamesn.com/cities-skylines-2/performance-blog) |
| 2026-05-27 | 1.5.9f1 «Morning Dew»: меньше лишних разворотов (у разрешённого разворота была **нулевая** стоимость), полоса поворота выбирается раньше у съездов и перекрёстков | [PCGH](https://www.pcgameshardware.de/Cities-Skylines-2-Spiel-74219/News/Patch-1-5-9f1-Update-Bugfixes-1537221/), [TwistedVoxel](https://twistedvoxel.com/cities-skylines-2-patch-1-5-9f1-adds-traffic-ui-and-shadow-fixes/), [patched.gg](https://patched.gg/games/cities-skylines-ii/patch-159f1-morning-dew) |
| 2026-09-15 | 1.6.2f1 «Autumn Breeze»: жилой спрос **обрушивался в очень больших городах** — теперь учитывает размер города и пустые квартиры; решения жителей — по доходу, а не по накопленному богатству (оно «колеблется»); обновление UI-фреймворка может сломать часть модов | [jeu.video](https://jeu.video/en/article/cities-skylines-ii-patch-1-6-2f1-steam), [patched.gg](https://patched.gg/games/cities-skylines-ii/patch-162f1-autumn-breeze) |
| 2026-10 | Консольная версия — без даты (TBA) | [Wikipedia](https://en.wikipedia.org/wiki/Cities:_Skylines_II) |

## 1. Реестр: проблема → причина → наша защита

### 1.1 Производительность и масштаб

| # | Что было в CS2 (факт) | Корневая причина (оценка) | Как предотвращаем | Где проверяется | Этап | Статус |
|---|---|---|---|---|---|---|
| P1 | Симуляция тормозит в больших городах; поиск пути — главная CPU-проблема по словам Iceflake ([PCGH](https://www.pcgameshardware.de/Cities-Skylines-2-Spiel-74219/News/Update-Leistungsprobleme-Wegfindung-1552963/)); **[игроки]** скорость симуляции падает на 180k–350k жителей, CPU 90–100% ([Steam](https://steamcommunity.com/app/949230/discussions/0/4343240481094062255)) | Маршрутизация без иерархии/бюджета, стоимость растёт с размером сети и пробками; масштаб не мерили до релиза | Лестница s1k→s1m с первого дня; бюджет запросов маршрутизации на тик; иерархия (ADR-0009) | `bench` по лестнице, REPORT-stage1 §Шаг 5: A* = 57% CPU на s100k — **уже видим ту же ловушку**; критерий 2.2: RoutePhase ≤ 15% на s100k | 2 | в работе |
| P2 | «Ничего нельзя предрасчитать или запечь» ([PCGamesN](https://www.pcgamesn.com/cities-skylines-2/performance-blog)) | Модель, где всё зависит от всего каждый тик, без уровней детализации симуляции | Иерархия: кэш магистралей по парам зон (ADR-0005), матрица хабов/CH (ADR-0009), гибрид micro/meso — **ADR-0013** (`sim-lod.md`: meso на общих списках полос, уровень по ячейкам, фокус камеры командой) | Тест эквивалентности длины маршрута vs плоский A* (2.2); s1m ticks/s в отчёте | 2 | в работе |
| P3 | Производительность «потом»: патчи после релиза, консоль не вышла за 3 года ([TechRadar](https://www.techradar.com/gaming/consoles-pc/paradox-delays-the-console-release-of-cities-skylines-2-indefinitely-says-we-have-not-yet-met-the-stability-and-performance-targets-we-set), [Wikipedia](https://en.wikipedia.org/wiki/Cities:_Skylines_II)) | Бюджеты не были гейтом разработки | Бюджеты в CLAUDE.md; `compare` в CI и Stop-хуке: p95 +10% или любая аллокация → exit 1 | CI `ci.yml` (compare/budget), Stop-хук, `perf/baseline.json` (ADR-0007) | 1 | закрыто для s1k; s10k+ — в работе (нет бюджета в CI) |
| P4 | Лаги при автосохранении растут с городом **[игроки]** ([Steam](https://steamcommunity.com/app/949230/discussions/0/4343240481094062255)) | Синхронная сериализация большого мира в потоке симуляции | SoA + blittable: сохранение = копия плотных массивов (memcpy), запись — вне тика | Нет | 3+ | открыто (см. топ-5 №2) |
| P5 | Аллокации/GC-паузы — для CS2 публичных данных нет | — (превентивно) | 0 B/тик: BannedSymbols, тест `Step_Allocates_ZeroBytes_InSteadyState`, `compare` | Тест + CI; dotnet-counters: GC 0 за 30 000 тиков (REPORT-stage1) | 1 | закрыто |
| P6 | Шум измерений ведёт к ложным выводам — превентивно (у нас ±13% p95 при допуске 10%) | Общий облачный CPU | Медиана повторов, полная рампа, зонд `OnPhaseEnd` (roadmap 2.1) | Критерий 2.1: два прогона расходятся ≤ 5% | 2 | в работе |

### 1.2 Рендер и LOD

| # | Что было в CS2 (факт) | Корневая причина (оценка) | Как предотвращаем | Где проверяется | Этап | Статус |
|---|---|---|---|---|---|---|
| R1 | Нет LOD у персонажей и большинства моделей, GPU-узкое место, ~11 FPS на кадре в разборе ([Wargamer](https://www.wargamer.com/cities-skylines-2/teeth-rumors), [Windows Central](https://www.windowscentral.com/gaming/cities-skylines-ii-runs-horribly-a-deep-dive-into-unity-explains-why)); в 2026 LOD/вода/terrain всё ещё «слишком много полигонов» ([PCGamesN](https://www.pcgamesn.com/cities-skylines-2/performance-blog)) | Ассеты делались без бюджета треугольников; LOD как «доделаем потом» | Пока low-poly коробки (MultiMesh). **Нужно:** бюджет треугольников и draw calls на кадр, обязательные LOD-уровни у ассета (импорт без LOD отклоняется) | Нет (Godot в облаке нет; FPS проверяет владелец руками, REPORT-stage1 чек-лист п.4, 9) | 3+ (арт) | открыто (топ-5 №4) |
| R2 | Тени от всех объектов без учёта размера/дистанции, свой куллинг вместо движкового ([HotHardware](https://hothardware.com/news/cities-skylines-2-autopsy)) | Обход стандартного пайплайна движка (DOTS-интеграция) без замеров | Пользоваться штатным куллингом/LOD Godot (visibility range, HLOD), своё — только по ADR с замером | Нет | 3+ | открыто |
| R3 | Клиент обходит все слоты снапшота: при 1M — 32 МБ на буфер (наш факт, ADR-0002) | — | `HighWater`-длина спанов (ADR-0010); компактный список живых и агрегированный вид дальних машин — в планах (core-api §15 п.1) | Тест эквивалентности снапшота (2.3) | 2 / 3+ | в работе |

### 1.3 Трафик

| # | Что было в CS2 (факт) | Корневая причина (оценка) | Как предотвращаем | Где проверяется | Этап | Статус |
|---|---|---|---|---|---|---|
| T1 | Поздний выбор полосы, повороты из дальней полосы у съездов; исправлено только в 1.5.9f1 (май 2026): полоса поворота выбирается раньше ([PCGH](https://www.pcgameshardware.de/Cities-Skylines-2-Spiel-74219/News/Patch-1-5-9f1-Update-Bugfixes-1537221/)); **[игроки]** ([Steam](https://steamcommunity.com/app/949230/discussions/0/3951406749575538437)) | Выбор полосы — локальное решение у узла, а не часть маршрута | Целевая полоса — часть маршрута (`LayoutLanes`, routing.md §5); MOBIL с нарастающей срочностью за 150 м, запрет смены в 30 м до узла, промах → перепланирование, а не стоп (routing.md §6.4) | Счётчики `Reroutes`, `LaneChangesAborted`, `HardClamps`; stuck = 0 за 30 мин (`Step3GateReportTests`) | 1 | закрыто (метрика промахов без порога — добавить) |
| T2 | Лишние развороты: у разрешённого разворота была **нулевая стоимость** ([PCGH](https://www.pcgameshardware.de/Cities-Skylines-2-Spiel-74219/News/Patch-1-5-9f1-Update-Bugfixes-1537221/)) | Ошибка в весах, не покрытая тестом | Штраф манёвра 0/3/6/20 с, разворот только на тупиках (ADR-0004, ADR-0005) | Юнит-тесты весов маршрута (`tests/.../Routing`) | 1 | закрыто |
| T3 | **[игроки]** «все едут одним маршрутом», одна полоса из пяти ([Steam](https://steamcommunity.com/app/949230/discussions/0/6367585249991049578)); заявлено было обратное — заполнение всех полос ([Paradox: traffic AI](https://paradoxinteractive.com/games/cities-skylines-ii/features/traffic-ai)) | Вес маршрута слабо зависит от загрузки или обновляется рывками → стадное поведение | Вес по загрузке, пересчёт раз в 30 с, перемаршрутизация раз в 60 с (ADR-0006) | Тепловая карта (клиент F1); **нет** метрики распределения по полосам/альтернативам | 1 | в работе: нужна метрика «доля потока на лучшем маршруте» и дисперсия загрузки полос сегмента |
| T4 | CS1 «чинили» деспауном (TM:PE) — в CS2 мод его не дал **[игроки]** ([Steam](https://steamcommunity.com/app/949230/discussions/0/4406291330030559509)) | Деспаун маскирует тупики модели | Деспаун застрявших **запрещён** (brief-stage1, core-api фаза 8); stuck/gridlock — метрики и бюджет | stuck = 0, gridlock = 0 за 30 мин s1k (тест + bench) | 1 | закрыто для s1k; s10k — критерий этапа 2 |
| T5 | **[игроки]** машины кружат вокруг полной парковки мимо свободных мест ([Steam](https://steamcommunity.com/app/949230/discussions/0/3937895062993772316)) | Поиск парковки без резервирования/знания о свободных местах | Сейчас парковок нет (спавн/деспаун в зонах). **Нужно:** парковка как ресурс с резервированием при постройке маршрута, лимит кругов → метрика `parking_search_time` | Нет | 3+ | открыто |
| T6 | Пешеходы, пересадки, доступ к парковочным зданиям **[игроки]** (сим вышел, не может вернуться) ([Steam](https://steamcommunity.com/app/949230/discussions/0/5540051992990254747)) | Мультимодальный граф без проверки связности | Пешеходы — этап 3; правило: граф достижимости проверяется при постройке (как `RouteFailed` сейчас) | `RouteFailed` счётчик; проверки связности нет | 3+ | открыто |
| T7 | Автобусы «пачками» — для CS2 публичных данных не нашли; у нас факт: отклонение интервала 35.6 с | Нет удержания на остановке | Holding + `BusBay` (roadmap 2.5) | `bus_headway_deviation_s` в bench | 2 | в работе |

### 1.4 Экономика и жители

| # | Что было в CS2 (факт) | Корневая причина (оценка) | Как предотвращаем | Где проверяется | Этап | Статус |
|---|---|---|---|---|---|---|
| E1 | «High rent» повсюду → переделка аренды и земли (1.1.0f1, 1.1.5f1 Economy 2.0, «virtual landlord» убран) ([Dev diary 2](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/dev-diary-economy-part-two)); **[игроки]** иконка показывалась при >70% неплатёжеспособных ([Steam](https://steamcommunity.com/app/949230/discussions/0/4333106094374610672)) | Формулы не прогнали на долгих симуляциях до релиза; индикаторы, не отражающие модель | Экономика — отдельная спека + ADR до кода; headless-прогон «N лет игрового времени» с инвариантами | Нет | 3 | открыто (топ-5 №1) |
| E2 | Массовая бездомность, палатки в парках; фикс — убрать случайную проверку (1.1.8), потом палатки (1.1.10f1) ([Automaton](https://automaton-media.com/articles/newsjp/cities-skylines-2-20240904-309144/), [Paradox](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/patch-1-1-10f1)); **[игроки]** после фикса заселение «замерзало» ([Steam](https://steamcommunity.com/app/949230/discussions/0/4406292426773599646)) | Вероятностные переходы без гарантии прогресса; связанные подсистемы (жильё ↔ спрос ↔ парки) без контрактов | Как с stuck: метрика «застрявших» состояний жителя (бездомный > N дней, не переезжает), детерминизм, фиксированный порядок | Нет | 3 | открыто |
| E3 | Жилой спрос **обрушивался в очень больших городах** — исправлено в 1.6.2f1 (2026-09); решения по «богатству», которое колеблется → по доходу ([jeu.video](https://jeu.video/en/article/cities-skylines-ii-patch-1-6-2f1-steam)) | Модель не проверяли на масштабе; входы решений — шумные накопленные величины | Экономика проверяется на всей лестнице (s100k), а не на малом городе; входы решений — сглаженные потоки | Нет | 3 | открыто |
| E4 | Economy 2.0 на старых сохранениях: временный рост смертности, отрицательный баланс ([Paradox](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/economy-patch-is-here)) | Смена модели без миграции состояния | Миграции сохранений + тест «старый сейв → N дней → инварианты» (см. S1) | Нет | 3 | открыто |

### 1.5 Сохранения, моды, редактор, процесс

| # | Что было в CS2 (факт) | Корневая причина (оценка) | Как предотвращаем | Где проверяется | Этап | Статус |
|---|---|---|---|---|---|---|
| S1 | «No guarantees for modded saves» ([Paradox checklist](https://paradoxinteractive.com/games/cities-skylines-ii/news/save-game-checklist)); после снятия DLC — «белые кубы» вместо зданий ([TheSixthAxis](https://www.thesixthaxis.com/2024/04/18/we-let-you-down-cities-skylines-2-dlc-being-refunded-amidst-player-dissatisfaction/)); **[игроки]** порча сейвов от имён/модов ([Steam](https://steamcommunity.com/app/949230/discussions/0/601899800797917515)) | Сейв ссылается на ассеты/моды по идентификатору без деградации при отсутствии | Формат сохранения с версией схемы, миграциями и манифестом зависимостей; отсутствующий ассет → заглушка + событие, сейв не ломается | Нет; есть только `format_version: 1` у сценариев и хэш состояния | 2 (ADR) / 3 (код) | открыто (топ-5 №2) |
| S2 | Редактор ассетов — бета лишь в 12.2025, требует перевода базы и всех DLC на «новый способ обработки ассетов» ([Paradox](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/asset-mods-patch-notes)); моддинг по частям ([TechRaptor](https://techraptor.net/gaming/news/paradox-mods-for-cities-skylines-2-explained)) | Пайплайн ассетов не проектировался под моды; переделка задела весь контент | Ассеты — данные (JSON/glTF + манифест) с первого дня; свой контент грузится тем же путём, что и моды | Нет | 3+ | открыто (топ-5 №5) |
| S3 | Обновления ломают моды (UI-фреймворк в 1.6.2f1; Economy 2.0) ([jeu.video](https://jeu.video/en/article/cities-skylines-ii-patch-1-6-2f1-steam), [Paradox](https://paradoxinteractive.com/games/cities-skylines-ii/news/save-game-checklist)) | Моды патчат внутренности (код-моды) | Узкое версионируемое API ядра (ADR-0002): моды — данные + команды `Enqueue`, без доступа к внутренностям; ядро на Rust делает внутренний патчинг невозможным в принципе | Хук/анализатор: клиент не трогает внутренности; FFI-раскладки blittable | 1 (API) / 3+ (моды) | в работе |
| S4 | Платный контент без механики (Beach Properties) → возвраты, перенос планов ([TheSixthAxis](https://www.thesixthaxis.com/2024/04/18/we-let-you-down-cities-skylines-2-dlc-being-refunded-amidst-player-dissatisfaction/)) | Контент поверх нестабильной базы | DoD: фича = механика + метрика + тест, не только ассеты | DoD в CLAUDE.md | все | закрыто (процесс) |
| S5 | Консольный порт не вышел за 3 года: «не достигли целей по стабильности и производительности» ([TechRadar](https://www.techradar.com/gaming/consoles-pc/paradox-delays-the-console-release-of-cities-skylines-2-indefinitely-says-we-have-not-yet-met-the-stability-and-performance-targets-we-set)) | Бюджеты памяти/CPU под слабое железо не ставились заранее | Бюджеты ядра в мс и МБ по лестнице; peak MB уже в bench. **Нужно:** профиль «слабое железо» (4 ядра, 8–16 ГБ) как отдельный baseline | `bench` (peak MB), ADR-0007 (baseline на окружение) | 3+ | открыто |
| S6 | Смена разработчика CO → Iceflake (2025-11/2026-01) ([Paradox](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/an-update-on-cities-skylines-ii)); новая команда 2026: оптимизация «может потребовать больших изменений поведения симуляции» ([PCGH](https://www.pcgameshardware.de/Cities-Skylines-2-Spiel-74219/News/Update-Leistungsprobleme-Wegfindung-1552963/)) | Знание в головах, а не в спеках/тестах; нет страховочной сетки для редизайна | ADR на каждое решение, спеки `docs/design/`, `lessons.md`, тест хэша/golden hash, бенчи — новая команда/агент может переделать без страха | `GoldenHash_S1k`, детерминизм-тесты, compare | 1 | закрыто (поддерживать) |
| S7 | Детерминизм/воспроизводимость багов — публичных данных о CS2 нет; Paradox просит прикладывать сейв к баг-репорту ([Paradox](https://www.paradoxinteractive.com/games/cities-skylines-ii/news/patch-1-1-10f1)) | — (превентивно) | Детерминизм (dt, PCG32, порядок фаз), хэш FNV-1a; **нужно:** запись реплея (сейв + лог команд) | Детерминизм-тесты; реплея нет | 1 / 3 | в работе |

## 2. Топ-5 открытых рисков для нас

Принцип для всех пяти: **«переделка ломает всё» случается, когда контракт (формат, API, бюджет) появляется после
того, как на него опёрлись данные игроков или моды.** Карт-бланш владельца на редизайны действует, пока нет
релиза, — значит, контракты ниже нужно зафиксировать **до** появления первых сохранений/модов, а не после.

### №1. Экономика с жителями-агентами (E1–E4)
CS2 три года чинит аренду, бездомность и спрос — каждый раз с «переходным периодом» на сохранениях.
- **ADR-0011 «Модель жителя и экономики»** (opus, начало этапа 3, до кода): жители/домохозяйства — SoA,
  потоки денег — целые (детерминизм, как `DistanceTotalMm`), решения — по сглаженным доходам, без «виртуальных
  владельцев»; явный граф зависимостей подсистем.
- **Инварианты как stuck для трафика**: сохранение денег (Σ потоков = Δ балансов), «застрявшие жители»
  (бездомный > N дней, вакансия без найма > N дней) = 0, спрос не схлопывается при росте города.
- **Headless «10 лет игрового времени»** на s10k и s100k в отчётном прогоне (haiku-верификатор), метрики в JSON bench.
- Срок: ADR — первая задача этапа 3; инварианты — в DoD каждой экономической PR.

### №2. Формат сохранений и версионирование мира (S1, E4, P4)
Сейчас есть только `format_version: 1` сценариев и хэш состояния; сохранения нет вообще.
- **ADR-0012 «Формат сохранения»** (opus, **конец этапа 2**, пока мир маленький): бинарные блоки SoA по
  компонентам + заголовок (версия схемы на блок, манифест ассетов/модов, seed, тик, хэш); миграции
  `vN → vN+1` — чистые функции в ядре (переносимы на Rust); отсутствующий ассет → заглушка + событие.
- Тесты: round-trip «сохранить → загрузить → хэш равен»; «сейв vN → миграция → N тиков → инварианты»;
  корпус golden-сейвов в репо, CI грузит все. Сохранение — снимок между тиками, запись вне потока симуляции.
- Почему этап 2: каждый следующий редизайн структур ядра (2.2, 2.6) дешевле, пока формат не зафиксирован, но
  писать его надо до первых долгоживущих городов (этап 3).

### №3. Масштаб 100k+ жителей (P1, P2)
Мы уже повторяем ловушку CS2: A* = 57% CPU на s100k.
- **ADR-0009** (иерархия, в работе) + **ADR-0013 «Уровни детализации симуляции»** (предложено 2026-10-24: micro в фокусе/
  пробках / meso вдали, агрегат машин не нужен; агрегат — жители вне поездки, ADR-0011; лестница s1m = 1M жителей + stress-1m) — после 2.2, до пешеходов: пешеходы и пассажиры умножат число агентов в 3–10 раз.
- Бюджет в CI не только для s1k: добавить s10k (≤ 4 мс) в бюджетный режим после 2.4; s100k — отчётный прогон
  с порогом (≤ 15 мс, ориентир владельца) как критерий этапа 3.
- Метрика стоимости на агента (мкс/агент/тик) по фазам через `OnPhaseEnd` — ловит деградацию раньше, чем p95.

### №4. LOD и рендер (R1–R3)
CS2 спустя 3 года всё ещё режет полигоны LOD/воды/terrain.
- **ADR-0014 «Бюджет рендера и пайплайн ассетов»** (до начала low-poly арта, этап 3+): бюджет треугольников и
  draw calls на кадр для s100k на целевом GPU; обязательные 3 LOD + импостор; тени только для объектов выше
  порога размера/дистанции; штатный куллинг Godot.
- Проверка: импорт ассета без LOD/с превышением треугольников отклоняется скриптом в CI; сцена-бенч клиента
  (headless Godot на машине владельца) — FPS и время кадра в отчёт.
- Снапшот для 1M: компактный список видимых + агрегированные потоки вдали (core-api §15 п.1) — вместе с ADR-0013.

### №5. Моддинг (S2, S3)
CS2: ассет-моды через 2 года, обновления ломают код-моды.
- **ADR-0015 «Модель моддинга»** (этап 3, до редактора): моды = данные (JSON-сценарии, параметры ТС, ассеты с
  манифестом) + версионированный набор команд API ядра; без патчинга внутренностей (на Rust его и не будет).
- Свой контент грузится через тот же пайплайн, что и моды («dogfooding»), — пайплайн не переделывается под моды потом.
- Семантическая версия API ядра и формата данных; тест совместимости: мод-фикстура предыдущей версии грузится в CI.

## 3. Что сделать сейчас (этап 2)
1. Добавить в bench метрики: распределение потока по полосам сегмента и доля промахов полосы (`Reroutes`/поездку) — закрывает T1/T3.
2. Написать ADR-0012 (сохранения) в конце этапа 2; round-trip-тест хэша — в DoD шага.
3. После 2.4 — s10k в бюджетный режим CI.
4. Пересматривать этот реестр при каждом ADR (статус строки + ссылка на ADR).
