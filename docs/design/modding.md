# Моддинг: пакеты данных, id, валидация, версии ассетов (спецификация, этап 4.5)

Статус: предложено (ADR-0015). Затрагивает `scenario-format.md` (новый `format_version: 3`, §3 типы ТС в пакете),
`save-format.md` §5 (`MANI`), §12 (журнал `world_version`), `core-api.md` §4 (`BuildRoadCmd.Asset`, `RejectReason.UnknownAsset`).
**Ядро про пакеты не знает**: всё в этом документе, кроме §6.2–§6.3, — хост (`Sim.Scenarios`, net8, IO разрешён).
Стык с ADR-0014 (бюджет рендера и LOD, ещё не написан) — §4.3. Стык с ADR-0023 (JSON-форма команд) — §3.5.

## 0. Инварианты

| # | Инвариант | Проверка |
|---|---|---|
| MD1 | В пакете нет исполняемого кода; файл вне белого списка — пакет отклонён | `PackageLoaderTests.Hostile*` |
| MD2 | Базовый контент (`content/base`) грузится тем же `PackageLoader`, что и мод; другого пути нет | M2: `ScenarioLoader` v3 без `vehicle_types_file` |
| MD3 | Сценарий v3 через `base` даёт тот же `ScenarioDesc`, что v2 через `vehicle-types.json` → golden-хэши s1k/s10k равны | `BasePackageGoldenTests` |
| MD4 | Порядок загрузки и индексы типов не зависят от файловой системы и порядка установки | тест с перемешанными каталогами |
| MD5 | Нет ассета → сейв грузится, продолжение побитовое, клиент рисует заглушку, хост выдаёт `AssetNotice` | `ModSaveTests.MissingPackage_*` |
| MD6 | Обновлённый ассет в режиме `UseCurrentForNewSpawns`: живые машины со старыми параметрами, новые спавны с новыми | `TypeRowTests` (M5) |
| MD7 | Ошибка в пакете → пакет не грузится целиком, список `(файл, путь JSON, код)`; без исключений наружу хоста | все тесты M1 |
| MD8 | Фикстура-мод прошлой версии формата грузится в CI | `ModCompatTests` (M8) |

## 1. Пакет

### 1.1 Расположение
- Поставляемые пакеты лежат в `content/<id>/`, где `content/base/` — содержимое игры. Пакеты игрока клиент берёт из
  `user://mods/<id>/`, headless — из `--packages-dir <dir>` (их может быть несколько, дефолт `content/`).
- Имя каталога равно `id` пакета, иначе ошибка `DirNameMismatch`. Каждый `id` установлен в одной версии. Два каталога
  с одним `id` дают ошибку `DuplicatePackage`.
- На этапе 4 пакет — это каталог. Архивы появятся позже, вместе с пределами распаковки (§7).

### 1.2 `package.json` (`format_version: 1`)
```jsonc
{
  "format_version": 1,
  "id": "transit_plus",                 // [a-z][a-z0-9_]{1,31}; "base" и префикс "tc_" зарезервированы за игрой
  "version": "1.2.0",                   // semver MAJOR.MINOR.PATCH (без pre-release на этапе 4)
  "content_format": 1,                  // версия набора форматов контента (§8); игра принимает [min, текущая]
  "title": "Transit Plus",              // ≤ 128 Б, только для UI
  "requires": [ { "id": "base", "version": "^1.0.0" } ],   // ^X.Y.Z — та же MAJOR и ≥ X.Y.Z; или точная "1.0.0"
  "content": {                          // всё опционально; порядок в массивах значим (§5.1)
    "vehicles": ["vehicles.json"],
    "roads": ["roads.json"],
    "buildings": [],                    // резерв до ADR-0011: непустой массив → UnsupportedContent
    "props": [],                        // резерв
    "scenarios": ["scenarios/rush_hour.json"]
  }
}
```
Неизвестный ключ — ошибка (`scenario-format.md` §1). `base` без `requires` — корень графа. Любой другой пакет без
`requires` тоже допустим, но тогда он видит только собственные ассеты.

## 2. Id, пространства имён, ревизии

- **Id ассета** — `<пакет>:<вид>/<имя>`: вид `vehicle | road | building | prop`, имя `[a-z0-9_]{1,64}`, весь id ≤ 128 Б.
  В файле контента пишется только имя, а пакет и вид загрузчик добавляет сам. Поэтому объявить ассет в чужом
  пространстве имён нельзя по построению. Повтор имени внутри пакета и вида даёт `DuplicateId`.
- **Ссылка** на ассет в JSON (сценарий, таймлайн, `requires`-зависимый пакет) — полный id. Сокращение без пакета
  (`vehicle/car`) ищется сначала в своём пакете, затем в `base`. Ссылка на пакет, которого нет в `requires`
  (транзитивно), даёт `UndeclaredDependency`.
- **Id стабилен навсегда**: переименование — это новый ассет, а старый автор оставляет или удаляет на свой риск
  (сейвы покажут заглушку, MD5).
- **Ревизия** `rev` (целое ≥ 1, дефолт 1) задаётся у каждого ассета, автор поднимает её при изменении. В сейве она
  попадает в `MANI.Version` и служит только для отчёта. Факт изменения определяет `ContentHash` (§5.3), а не `rev`.
- **Алиас наследия**: id `vehicle:<имя>` (сейвы до M3, `AssetManifestBuilder.ForVehicleTypes`) при сверке с каталогом
  читается как `base:vehicle/<имя>`. Ядро строки не разбирает, миграции сейва нет.

## 3. Виды контента

### 3.1 Типы ТС (`vehicles.json`, `format_version: 2`)
Формат `scenario-format.md` §3 плюс три ключа: имя `name` теперь локальное имя ассета, `rev` и `visual` (§4).
```jsonc
{ "format_version": 2,
  "types": [ { "name": "bus_long", "rev": 1, "category": "bus", "length_m": 18.0, "width_m": 2.55, "max_speed_kmh": 70,
               "speed_factor": 0.95, "idm": { ... }, "mobil": { ... },
               "visual": { "model": "models/bus_long.glb" } } ] }
```
- `category` — только существующие `car | bus`. Новая категория — это новое поведение, мод её добавить не может.
- Диапазоны: `length_m` 2–30, `width_m` 1–3.5, `max_speed_kmh` 5–200, `speed_factor` 0.5–1.5, IDM/MOBIL — диапазоны из
  `scenario-format.md` §3.1 (загрузчик уже проверяет их). Значение вне диапазона даёт `OutOfRange`.
- `vehicle-types.json` (`format_version: 1`, без `rev`/`visual`) остаётся для сценариев v2. Читатель v1 — это функция
  подъёма v1 → v2 (§8), а не отдельный парсер.

### 3.2 Типы дорог (`roads.json`, `format_version: 1`)
```jsonc
{ "format_version": 1,
  "roads": [ { "name": "avenue_4", "rev": 1, "kind": "arterial", "lanes_ab": 2, "lanes_ba": 2, "speed_kmh": 60,
               "visual": { "profile": "models/avenue_4.glb" } } ] }
```
- Симуляционная часть — это поля `BuildRoadCmd` (`kind`, `lanes_ab`, `lanes_ba` 0–4, причём хотя бы одно > 0; `speed_kmh` 10–130).
  Ширина полосы общая (`City.LaneWidth`). Тип дороги её не меняет: ширину по сегменту ядро не поддерживает,
  а тротуары и разметка относятся к визуалу.
- Ядру тип дороги не нужен. Хост разворачивает его в поля команды и ставит `BuildRoadCmd.Asset` = индекс записи
  в `MANI` (§6.2).
- `base` поставляет `street_2` (1+1, 40 км/ч, street) и `arterial_4` (2+2, 60 км/ч, arterial) — те, что строит генератор.
  Сверено при M2: скорость улицы 40 км/ч — дефолт `street_speed_kmh` сценариев (50 в первой редакции спеки была ошибкой).

### 3.3 Здания и пропы — резерв
`buildings` — после ADR-0011 (3.2 зонирование и спрос): симуляционные параметры (footprint, зона, вместимость, парковка
по 4.4) задаёт ADR-0011, а этот документ даёт только каркас: id `building/…`, `rev`, `visual`, `MANI` вид 2. Пропы —
только визуал, в симуляцию и сейв не попадают, пока ядро не размещает объекты. До этого непустые массивы дают
`UnsupportedContent`.

### 3.4 Сценарии `format_version: 3`
Отличия от v2 (`scenario-format.md` §2):
- `"packages": ["base", "transit_plus"]` — обязательный список, зависимости добавляются транзитивно. Вместо
  `vehicle_types_file` (в v3 этот ключ даёт ошибку).
- Ссылки на типы — полные id: `demand.type_mix: { "base:vehicle/car": 0.9, "transit_plus:vehicle/van": 0.1 }`,
  `transit.bus_type: "transit_plus:vehicle/bus_long"`.
- `city.road_assets: { "street": "base:road/street_2", "arterial": "base:road/arterial_4" }` (дефолт — эти же) — визуальная
  метка сегментов генератора (до M4 только проверяется, ядру не передаётся).
- `timeline` — §3.5.
- Сценарий из пакета ссылается на свой пакет неявно (`packages` дополняется им).
- Подъём v2 → v3 для тестов **не нужен**: v2 остаётся отдельным путём (`vehicle_types_file`), и `scenarios/s*.json` не трогаем.

### 3.5 Таймлайн команд (M9, после ADR-0023)
```jsonc
"timeline": [ { "tick": 1200, "cmd": { "kind": "set_target_vehicle_count", "count": 500 } },
              { "tick": 2400, "cmd": { "kind": "build_road", "a": [0, 0], "b": [200, 0], "road": "base:road/street_2" } } ]
```
- Поле `cmd` — JSON-форма `Command` из ADR-0023: одна на игру, её же используют лог реплея в тексте и инструменты.
  Здесь своей формы не вводим. Ссылки на ассеты (`road`, `vehicle_type`) хост переводит в индексы `ScenarioDesc` или `MANI`.
- `tick` ≥ 0, массив отсортирован по `tick` (не по порядку — `TimelineUnsorted`), команды с одним `tick` идут в порядке файла.
- Хост делит `Step` на границах таймлайна: команды тика `T` ставятся в очередь перед `Step`, первый тик которого — `T`
  (`core-api.md` §4.2). Результат совпадает с ручным `Enqueue` в тех же тиках побитово.
- Таймлайн требует `api_version` (поле сценария, semver `CoreInfo.ApiVersion`). Несовместимая MAJOR даёт ошибку
  `ApiVersionMismatch`, а не тихую потерю команд.

## 4. Визуал и glTF

### 4.1 Ключ `visual`
- `{ "model": "<путь .gltf|.glb>" }` для ТС, зданий и пропов, `{ "profile": "<путь>" }` для дороги (сечение,
  которое клиент тянет вдоль оси). Пути относительные, внутри пакета (§7).
- `visual` не задан — клиент рисует процедурный бокс по габаритам (как сейчас), а дорогу — профилем по умолчанию.
  Это **не** «ассет без LOD»: правило §4.3 относится только к присланным моделям.

### 4.2 Проверка glTF (`Sim.Scenarios/Mods/Gltf`, без NuGet)
- `.glb`: магия `glTF`, версия 2, чанки JSON + BIN, длины согласованы. `.gltf`: JSON, буферы — файлы пакета или `data:`.
- `asset.version == "2.0"`. Каждое расширение из `extensionsRequired` должно входить в белый список
  `KHR_mesh_quantization`, `KHR_texture_transform`, `KHR_texture_basisu`, `KHR_materials_emissive_strength`,
  иначе `GltfExtension`. Неизвестное в `extensionsUsed` даёт предупреждение.
- Все `accessor`/`bufferView`/индексы лежат в границах буферов. Примитивы только треугольники (`mode` 4 или по умолчанию).
  Треугольники = `indices.count / 3` (без индексов — `POSITION.count / 3`).
- `skins` и `morph targets` на этапе 4 дают ошибку `GltfFeature`: бюджеты для них задаст ADR-0014. `animations`
  игнорируются с предупреждением.
- Изображения: PNG (IHDR), JPEG (SOF), KTX2 (заголовок). Размер читается из заголовка без декодирования, предел — §7.

### 4.3 LOD — стык с ADR-0014
- **Ассет без LOD отклоняется** (`LodMissing`). Соглашение до ADR-0014: в сцене по умолчанию корневые узлы
  `LOD0 … LODn`, у каждого свой меш. `LOD0` — самый детальный. Треугольники не растут с номером уровня (`LodOrder`).
- Числа берутся из `content/render-budgets.json` (формат здесь, значения задаёт ADR-0014):
  `{ "format_version": 1, "kinds": { "vehicle": { "min_lods": 3, "max_tris": [null, null, null] }, "road": {…}, "building": {…} } }`.
  Заглушка до ADR-0014: `min_lods = 3` (`cs2-lessons.md` №4), `max_tris = null` (не проверяется, `TODO(ADR-0014)`),
  импостор не требуется. ADR-0014 меняет только этот файл и, при необходимости, соглашение об именах. Код валидатора
  читает бюджеты из файла.
- Превышение бюджета (после ADR-0014) даёт `LodOverBudget` с уровнем и числом треугольников.
- Дистанции переключения LOD в клиенте тоже задаёт ADR-0014. До него клиент берёт `visibility_range` 0/60/200 м (M7).

## 5. Загрузка и сборка `ScenarioDesc` (канонические правила; их же реализует Rust-хост)

### 5.1 Порядок
1. Найти пакеты в каталогах поиска (`--packages-dir` по порядку, затем `user://mods`). Порядок каталогов влияет только
   на поиск, а не на порядок загрузки.
2. Набор = `packages` сценария (или сейва, §6.1) плюс транзитивные `requires`. Отсутствует пакет — `MissingDependency`,
   версия не подходит — `VersionConflict`, цикл — `DependencyCycle`.
3. **Порядок загрузки**: топологическая сортировка по Кану. Из готовых пакетов первым берётся наименьший `id`
   (ординальное сравнение байт UTF-8).
4. Внутри пакета ассеты идут в порядке `content.<вид>` и в порядке элементов файла. Так получается **каталог** — упорядоченный
   список `(id, параметры, визуал)`.

### 5.2 Индексы
- `ScenarioDesc.VehicleTypes` = **все** типы ТС набора пакетов в порядке каталога, как v2 берёт весь `vehicle-types.json`.
  Неиспользуемый тип получает вес 0 и на поведение не влияет. `base` объявляет `car`, затем `bus`, поэтому любой
  сценарий на одном `base` получает `car = 0`, `bus = 1`, как сейчас (MD3). Тип 0 — дефолт ядра (`TypeMix = null`),
  поэтому первым типом каталога обязан быть `car`-категории (у `base` — `car`), иначе `BadValue`. Больше 256 типов
  в наборе — `CapacityExceeded` (`WorldBuilder.MaxVehicleTypes`).
- Числа из JSON переводятся в `float32` теми же функциями, что в v2 (`ScenarioLoader`): `kmh / 3.6f`, `round(s · 20)`.

### 5.3 Манифест и отпечаток
- `MANI` (`save-format.md` §5) хост задаёт после `Create` через `SetAssetManifest`. Сначала идут записи типов ТС (вид 1,
  `SavedIndex` = индекс в `VTYP`), затем дорог (вид 5, `SavedIndex` = −1, после M4), затем пакетов (вид 4, `SavedIndex` = −1)
  в порядке загрузки.
- `ContentHash` типа ТС — xxh64 по 15 полям `VehicleTypeDesc` в порядке столбцов `VTYP` 1–15, little-endian, 4 Б каждое
  (так уже работает `AssetManifestBuilder.ParamsHash`, раскладка совпадает со struct). `ContentHash` дороги — xxh64
  по `kind u8, lanes_ab u8, lanes_ba u8, 0 u8, speed f32` (м/с). Визуал в хэш не входит: смена модели не считается
  изменением симуляции.
- Запись пакета: `Version = MAJOR << 20 | MINOR << 10 | PATCH` (каждая часть < 1024, иначе `OutOfRange`), `ContentHash` =
  xxh64 по конкатенации `ContentHash` его ассетов в порядке каталога (u64 LE).
- **Отпечаток контента** `content_fingerprint` = xxh64 по последовательности записей пакетов `(id UTF-8, 0x00, Version u32 LE,
  ContentHash u64 LE)` в порядке `MANI`. Это функция манифеста, отдельно в сейве его не храним. Он печатается в
  результат bench (`"content_fingerprint": "hex"`), в `--json` команды `run` и в `RPLH`. Реплей и golden с другим
  отпечатком дают ошибку `FingerprintMismatch`, а не сравнение. Сценарии v2 получают отпечаток пустого списка.

## 6. Сейв и ассеты

### 6.1 Загрузка сейва (хост)
1. `InspectSave` → `MANI` (через `Load`: манифест отдаётся после загрузки). Набор пакетов сейва — записи вида 4.
2. Хост грузит установленные пакеты из этого набора. Отсутствующий пакет **не** ошибка, а `AssetNotice(Missing, пакет)`.
   Другая MAJOR — `AssetNotice(VersionChanged)`, пакет всё равно грузится, сверка идёт по `ContentHash`.
3. Каталог передаётся в `LoadOptions.AvailableAssets/AvailableIds` (алиасы §2 разворачивает хост). `LoadOptions.CurrentTypes`
   получает параметры типов каталога, `SavedIndex` записи каталога — индекс в `CurrentTypes`.
4. По `LoadReport` и `Simulation.AssetStates` хост выдаёт `AssetNotice` на каждую запись `Missing/Changed/Updated`
   с числом сущностей (машин данного типа, сегментов дороги). Клиент показывает список, headless печатает
   `assets: missing N, changed M, updated K`.
- `AssetNotice` — событие хоста (`Sim.Scenarios/Mods/AssetNotice.cs`: вид, id, состояние, число), а не `SimEvent` ядра:
  ядро не знает каталога. Ядро отдаёт только сверку (`AssetStates`).

### 6.2 Метка ассета у сегмента (M4, ядро)
- `SegmentData` (или столбец SoA рядом) получает поле `Asset u16`: индекс записи `MANI`, `0xFFFF` — без ассета. Ядро поле
  не трактует, оно идёт в сейв (столбец `SEGS.Asset`, дефолт `0xFFFF`) и в статическую геометрию для клиента
  (`core-api.md` §5.6).
- `BuildRoadCmd` получает `ushort Asset` (размер 24 → 28 Б, payload ≤ 56). Если запись не `0xFFFF` и это не запись
  вида 5 манифеста, команда отклоняется: `RejectReason.UnknownAsset = 6`.
- `CityGenDesc.StreetAsset/ArterialAsset u16` (дефолт `0xFFFF`) — метка сегментов генератора по `RoadKind`. Хост знает
  индексы до `Create`, потому что манифест строится детерминированно (§5.3).
- **`SetAssetManifest` только дописывает**: префикс текущего манифеста обязан совпасть побайтно, иначе `false`. Проверка:
  все `Asset` сегментов < `Count` и указывают на вид 5. Индексы `MANI` стабильны, пока жив мир.
- `AssetKind.RoadType = 5`. Хэш состояния не меняется: метка в `StateHash` не входит, golden-хэши прежние.

### 6.3 Строки типов только дописываются (M5, ядро)
Закрывает ADR-0012 п.8 «новые параметры для новых спавнов».
- `LoadOptions.AssetParams`: `Saved = 0` (как сейчас) или `UseCurrentForNewSpawns = 1`. В режиме 1, после проверок §6.1
  `save-format.md`, для каждой записи `MANI` вида 1 в порядке индекса, если она не `Retired` и её id есть в каталоге
  с другим `ContentHash`:
  1. Новая `Category` ≠ старой — обновление не применяется, `LoadReport.AssetsUpdateSkipped++`. Смена категории
     ломает транспорт.
  2. `Types.Length == 256` — так же `AssetsUpdateSkipped++`.
  3. Иначе в `World.Types` дописывается строка `n` = параметры из `CurrentTypes`, а в `MANI` — запись (тот же id, `Version`
     и `ContentHash` каталога, `SavedIndex = n`). У старой записи ставится `R0` бит 0 = `Retired`. Затем
     `TypeMix[n] = TypeMix[old]`, `TypeMix[old] = 0`, у маршрутов автобусов и `TransitCfg` тип `old → n`.
     `LoadReport.AssetsUpdated++`.
- Живые машины сохраняют свой `Type`, то есть старую строку и старую физику (длина копируется в `VehicleStore.Length`
  при спавне, остальное читается по `Type`). Новые спавны получают новую строку.
- Типы каталога, которых нет в `MANI` (мод поставили в существующий город), дописываются так же, с весом 0, без влияния
  на поведение. Выбрать их можно командой выбора долей типов (ADR-0023). `LoadReport.TypesAppended`.
- Хэш состояния сразу после `Load` в режиме 1 равен заголовку: `Types`, `TypeMix` и `BusRoutes.VehicleType` в
  `StateHash` не входят (`Metrics/StateHash.cs`), поэтому `StrictHash` совместим. Расходится только продолжение
  с первого нового спавна, так и задумано. Повторный сейв → загрузка `Saved` → побайтно равен (критерий (а) ADR-0012).
- Строки `Retired` без живых машин остаются (≈ 60 Б), чистку отложили: индексы `Type` пришлось бы перенумеровать.
- Режим 1 — дефолт клиента. Headless, тесты и корпус используют `Saved`.

### 6.4 Отсутствующее состояние мода
На этапе 4 у модов нет своего состояния: только параметры, а они в `VTYP`/`SEGS`. Блоки модов `critical = 1`
(`save-format.md` §5) не используются, пока нет скриптов (ADR-0015 п.3).

## 7. Безопасность и пределы (`Mods/ModLimits.cs`, константы; начальные значения, менять в этом документе)

| Что | Предел | Ошибка |
|---|---|---|
| Расширения файлов | `.json .gltf .glb .bin .png .jpg .jpeg .ktx2` (+ `README.md`, `LICENSE`, `.txt` — игнорируются) | `FileNotAllowed` |
| Пути | относительные, `/`, без `..`, `.` в начале сегмента, `\`, `:`, NUL; не симлинк; итог внутри каталога пакета | `PathEscape` |
| URI в glTF | только путь пакета или `data:`; `http(s)://`, `file://`, абсолютные — запрещены | `PathEscape` |
| Размер пакета | ≤ 512 МиБ, ≤ 4096 файлов | `TooLarge` |
| JSON-файл | ≤ 8 МиБ, глубина ≤ 32, строка ≤ 1024 Б, массив ≤ 65 536 элементов | `TooLarge` / `TooDeep` |
| glTF/GLB | ≤ 128 МиБ, `data:` в нём ≤ 16 МиБ суммарно, узлов ≤ 4096, примитивов ≤ 1024 | `TooLarge` |
| Текстура | ≤ 4096 × 4096, файл ≤ 64 МиБ | `TooLarge` |
| Ассетов в пакете | ≤ 4096; пакетов в наборе ≤ 256 | `TooLarge` |
| Таймлайн | ≤ 100 000 команд | `TooLarge` |
| Числа | конечные (`NaN/Inf` — ошибка), диапазоны §3 | `OutOfRange` |

- Файлы читаются с проверкой размера **до** чтения (`FileInfo.Length`), JSON — через `JsonDocumentOptions.MaxDepth`.
- Пакет не исполняет ничего: Godot-ресурсы (`.tscn/.tres/.res/.scn/.gd/.cs/.dll/.so`) запрещены именно потому, что
  Godot загружает в них скрипты. glTF клиент грузит через `GLTFDocument` (только меши и материалы).
- Строки из пакета (`title`, имена) — только данные UI, их не интерполируют в пути и команды.

## 8. Версии форматов и совместимость
- Каждый вид файла имеет `format_version`. Принимается диапазон `[min, текущая]`, старая версия поднимается чистой функцией
  `JsonNode vN → vN+1` (`Mods/FormatUpgrades.cs`) до разбора. Подъём только вверх, новее текущей — `UnsupportedFormatVersion`.
- `content_format` пакета — номер набора версий (таблица в этом документе). Игра принимает `[1, текущий]`.
- Таблица на сейчас: `content_format 1` = `package 1`, `vehicles 2`, `roads 1`, `scenario 3`, `render-budgets 1`.
- Удаление поля или смена смысла — новая `format_version` + подъём. Новый необязательный ключ с дефолтом версию
  не меняет (`scenario-format.md` §1).
- **Фикстура совместимости** `tests/ModFixtures/compat-c1/` (пакет на `content_format 1`) замораживается при M8 и
  больше не меняется. При подъёме формата добавляется `compat-c2`, старые фикстуры остаются, CI грузит все.
- Версия API команд (`CoreInfo.ApiVersion`, сейчас 0 → 1 при M9) проверяется только таймлайном (§3.5).

## 9. Коды ошибок (`ModError`, хост)
`UnknownKey, BadType, BadValue, OutOfRange, MissingKey, DuplicateId, DuplicatePackage, DirNameMismatch, UndeclaredDependency,
MissingDependency, VersionConflict, DependencyCycle, UnsupportedFormatVersion, UnsupportedContent, UnknownAssetRef,
CapacityExceeded, FileNotAllowed, PathEscape, TooLarge, TooDeep, GltfInvalid, GltfExtension, GltfFeature, LodMissing,
LodOrder, LodOverBudget, TimelineUnsorted, ApiVersionMismatch, FingerprintMismatch`. Отчёт: список `ModIssue
{ Package, File, JsonPath, Code, Detail }`, предупреждения отдельно. `ScenarioException` хоста оборачивает первый.

## 10. Тесты (DoD задач)
- **Загрузчик** (M1): каждая ошибка §9 — фикстура, которая её вызывает. Порядок (MD4) — те же пакеты в другом порядке
  каталогов и с другими mtime дают тот же каталог. Враждебные фикстуры: `..`, симлинк, `.gd`, глубина 33, JSON 9 МиБ
  (генерируется в тесте, не коммитится).
- **База** (M2): s1k/s10k v3 через `base` — `ScenarioDesc` поле за полем равен v2, `HotPathGoldenHashTests` проходят на v3-копии.
- **Манифест** (M3): `MANI` s1k = ожидаемый список; отпечаток стабилен (golden hex в тесте); алиас `vehicle:car`.
- **Сейв** (M3–M5): снятый пакет → `AssetsMissing > 0`, продолжение побитово (хэши на контрольных тиках как без снятия).
  Изменённый тип в `Saved` — побитово. В `UseCurrentForNewSpawns` у живых `Type` прежний, у новых спавнов новый,
  повтор детерминирован при 1/2 потоках, сейв → загрузка `Saved` побайтно равен.
- **glTF** (M6): минимальные `.gltf` с `data:`-буферами, собранные в тесте: 3 LOD ок, 2 LOD → `LodMissing`, рост
  треугольников → `LodOrder`, внешний URI → `PathEscape`, skin → `GltfFeature`, GLB с битой длиной → `GltfInvalid`.

## 11. Задачи

Порядок: **M1 → (M2 ∥ M6) → M3 → [после волны 3.1] M4 → M5**. M7 — после M3 и M6 (дороги — после M4). M8 — последней.
M9 — после ADR-0023 и M2. M4 и M5 трогают API ядра и сейв, поэтому строго последовательно. Файлы пешеходов
(`Peds/*`, `pedestrians.md`) не трогаем.

### M1 (sonnet) Загрузчик пакетов
- Файлы: `src/Sim.Scenarios/Mods/{PackageManifest,PackageLoader,PackageSet,AssetId,SemVer,ModError,ModIssue,ModLimits,SafePath}.cs`;
  `tests/Sim.Headless.Tests/Mods/PackageLoaderTests.cs`; `tests/ModFixtures/{ok_*,bad_*}/` (маленькие, текстовые; каталог = id пакета, а в id нет `-`, поэтому `_`).
- Делает: §1, §2 (без алиаса), §5.1, §7, §9. Только `package.json` и обход файлов (белый список, пределы),
  содержимое контента пока не разбирает.
- Приёмка: MD1, MD4, MD7. Каждое правило §7 и каждый код §9 этой задачи покрыт фикстурой. Без исключений наружу
  (ошибки — `ModIssue`).
- Проверки: `dotnet build src/Sim.Scenarios -warnaserror`, `dotnet test tests/Sim.Headless.Tests --filter FullyQualifiedName~Mods`, `dotnet format`.
- Реализация (решения M1): `PackageLoader.Load(IReadOnlyList<PackageSearchDir>, roots)` не бросает; при любой ошибке
  `PackageSet.Packages` пуст (набор целиком или никак). `PackageSearchDir.IsGame` — только из такого каталога допустимы
  `base` и `tc_*` (иначе `BadValue`). Файлы без расширения допустимы лишь `LICENSE`; `.md`/`.txt` игнорируются. Проверки
  JSON (размер/глубина/строка/массив) идут для **каждого** `.json` пакета. Сокращение ссылки: `PackageSet.TryExpandRef`.
  Вне M1: `DuplicateId`, `UnknownAssetRef`, `CapacityExceeded` (нужен разбор контента, M2), размер текстуры в пикселях
  и glTF-пределы (M6), таймлайн (M9).
- Зависимости: нет.

### M2 (sonnet) Пакет `base` и сценарии v3
- Файлы: `content/base/{package.json,vehicles.json,roads.json}`; `src/Sim.Scenarios/Mods/{ContentCatalog,VehicleContent,RoadContent}.cs`;
  `ScenarioLoader.cs` (ветка `format_version: 3`); `scenarios/s1k-v3.json` (копия s1k в v3 только для теста MD3);
  тесты `Mods/BasePackageGoldenTests.cs`. Правка `scenario-format.md`: §2.5 «v3», §3 — ссылка на §3.1 этой спеки.
- Делает: §3.1, §3.2 (без передачи в ядро), §3.4 (без `timeline`), §5.2, подъём `vehicle-types` v1 → v2 (§8).
- Приёмка: MD2, MD3: `ScenarioDesc` v3 побайтно по полям равен v2 для s1k и s10k, golden-хэши равны. `scenarios/s*.json`
  v2 не меняются.
- Зависимости: M1.

### M3 (sonnet) Манифест, отпечаток, `AssetNotice`
- Файлы: `src/Sim.Scenarios/AssetManifestBuilder.cs` (`ForCatalog`), `Mods/{ContentFingerprint,AssetNotice,SaveAssets}.cs`;
  `src/Sim.Headless` (`run/bench/load`: `--packages-dir`, `content_fingerprint` в JSON-результате, печать notice);
  проверить, что `compare` поле игнорирует или сверяет как `ramp_complete` (если отпечатки разные → exit 1).
- Делает: §2 алиас, §5.3, §6.1.
- Приёмка: тесты §10 «Манифест» и «Сейв: снятый пакет»; `perf/baseline.json` не трогаем (новое поле baseline
  отсутствует → compare не сверяет).
- Зависимости: M2.

### M4 (sonnet, ядро) Метка ассета у дороги
- Файлы: `src/Sim.Core/Api/{Commands,ScenarioDesc,SaveTypes,SimEvent}.cs`, `Network/*` (поле сегмента), `Save/SaveSchema*.cs`
  (`SEGS.Asset`, `world_version++`), `Api/Simulation.Save.cs` (`SetAssetManifest` только дописывает), генератор (метка);
  хост: `RoadContent` → `BuildRoadCmd`; доки `core-api.md` §4/§5.6, `save-format.md` §5/§12/прил. A.
- Делает: §6.2.
- Приёмка: golden-хэши s1k/s10k не меняются; `BuildRoad` с чужим индексом → `UnknownAsset`; сейв round-trip (а)(б)(в);
  корпус новой `world_version` (если волна не держит версию «открытой», см. `pedestrians.md` §11) + миграция по дефолту `0xFFFF`.
- Зависимости: M3, слитая волна 3.1 (w2). Последовательно с M5.

### M5 (sonnet, ядро) Версии параметров типов
- Файлы: `src/Sim.Core/Save/{WorldLoader,LoadContext}.cs`, `Api/SaveTypes.cs` (`AssetParamsMode`, `LoadReport.AssetsUpdated/
  AssetsUpdateSkipped/TypesAppended`), проверка `R0` в `SetAssetManifest`; тесты `tests/Sim.Core.Tests/Save/TypeRowTests.cs`;
  хост: клиент по умолчанию `UseCurrentForNewSpawns`. Доки: `save-format.md` §5 (убрать «не реализована»).
- Делает: §6.3.
- Приёмка: MD6; детерминизм при 1/2 потоках; хэш сразу после `Load` в режиме 1 равен заголовку; 0 B/тик после загрузки в режиме 1.
- Зависимости: M4.

### M6 (sonnet) Проверка glTF и бюджеты
- Файлы: `src/Sim.Scenarios/Mods/Gltf/{GltfValidator,GlbReader,ImageHeader,RenderBudgets}.cs`, `content/render-budgets.json`;
  `Sim.Headless validate-package <dir>` (печатает `ModIssue`, exit 1 при ошибках); шаг CI «validate content/*».
- Делает: §4.2, §4.3 (бюджеты из файла, `max_tris = null` до ADR-0014).
- Приёмка: тесты §10 «glTF»; `content/base` проходит (моделей пока нет — проверяется только `visual`-отсутствие).
- Зависимости: M1. Параллельно M2–M3 (другие файлы).

### M7 (sonnet) Клиент: пакеты, модели, заглушки
- Файлы: `game/Scripts/{SimHost,BoxBatch}.cs`, новый `game/Scripts/AssetLibrary.cs`.
- Делает: загрузка набора пакетов через `Sim.Scenarios`. Модели — `GLTFDocument.AppendFromFile` + `GenerateScene`,
  меши LOD → `MultiMesh` на тип и уровень, `visibility_range` по §4.3. Без `visual` — процедурный бокс. `Missing` —
  контрастный материал + метка «?» (бокс по габаритам из `Simulation.VehicleTypes`, дорога — профиль по умолчанию).
  Панель `AssetNotice`.
- Приёмка: `dotnet build game/TrafficCity.csproj -warnaserror`, `dotnet format game/...`. Визуально проверяет
  владелец (чек-лист в PR: тестовый мод с одной моделью, снятие мода → «?»).
- Зависимости: M3, M6 (дороги — M4).

### M8 (sonnet) Совместимость
- Файлы: `tests/ModFixtures/compat-c1/` (пакет с ТС, дорогой, сценарием), `tests/Sim.Headless.Tests/Mods/ModCompatTests.cs`,
  `Mods/FormatUpgrades.cs` (каркас реестра подъёмов).
- Приёмка: MD8; тест-правило «у каждого `content_format` ≤ текущего есть фикстура» (по образцу `Corpus_HasCurrentVersion`).
- Зависимости: M2–M6.

### M9 (sonnet) Таймлайн команд в сценарии
- Файлы: `src/Sim.Scenarios/Mods/Timeline.cs`, `ScenarioLoader.cs` (`timeline`, `api_version`), `Sim.Headless` run/bench
  (деление `Step`), `CoreInfo.ApiVersion = 1`.
- Делает: §3.5.
- Приёмка: таймлайн = ручной `Enqueue` в те же тики (хэши равны); несортированный → `TimelineUnsorted`; 0 B/тик (деление
  `Step` вне ядра).
- Зависимости: ADR-0023 (JSON-форма команд), M2.

### M10 (позже) Здания
После ADR-0011: формат `buildings.json`, `MANI` вид 2, таблица параметров зданий в ядре и сейве.

## 12. Открытые вопросы
1. (владелец) Переопределение базовых ассетов модом (`replaces`). Рекомендация: нет на этапе 4, только новые id.
   Трейдофф: мод «лучший автобус» требует, чтобы игрок выбрал тип в городе; зато нет конфликтов двух модов.
2. (владелец) Скрипты. Рекомендация: нет до конкретного запроса, затем WASM в хосте вне тика. Трейдофф: часть модов
   (правила, UI) невозможна.
3. (владелец) Обновлённый ассет у клиента: дефолт `UseCurrentForNewSpawns` (решение 2.6b) с уведомлением или вопрос
   игроку при загрузке? Рекомендация: дефолт + уведомление, переключатель в настройках. Трейдофф: город незаметно
   меняется со временем.
4. (владелец) Отсутствующий тип ТС в сейве продолжает спавниться (заглушка) или вес обнуляется? Рекомендация: спавнится
   (побитовое продолжение), игрок убирает вес инструментом. Трейдофф: «?»-коробки на улицах, пока игрок не вмешается.
5. Чистка строк `Retired` без машин (сжатие индексов `Type`) — по мере роста; 256 строк хватит на сотни обновлений.
6. Пакеты-архивы (zip) и распространение (мастерская) — после этапа 4, со своими пределами распаковки.
