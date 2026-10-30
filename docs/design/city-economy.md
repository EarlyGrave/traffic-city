# Экономика города: казна, налоги, сборы, земля, долг (спецификация, этап 4.2, ADR-0025)

Статус: предложено (2026-10-26, агент #127). Источник истины для `src/Sim.Core/City/Budget*.cs` и денежных правок выбора, рынков,
инструментов и сейва. Опора: `zoning-demand.md` (3.2: §6.1 стоимость, §6.7 доступность, §7 деньги, §8 рынки, §9 агрегат, §11 бюджет,
§15 сейв, §16 сценарий, §20.1–20.3 замороженная поверхность); `player-tools.md` §6, §7, §8, §11; `cs2-lessons.md` E1–E4; ADR-0011,
0017, 0021, 0022, 0023. Результат Z4 (рынки, правила фирм, `res_stuck = 0`) — `zoning-demand.md` §20.4, когда появится: правила §7.4
и §8 3.2 здесь считаются реализованными как написано; отличия Z4 E1 учитывает при заморозке поверхности (§15).

## 0. Идея в трёх абзацах

1. **Казна — ещё один счёт.** `City` получает налоги, сборы и плату за проезд и платит за содержание сети, автобусов и стройку игрока.
   Всё — проводки `Ledger.Transfer`, поэтому Σ всех счетов = 0 в любой тик. Каждая проводка висит на событии, которое уже есть
   в 3.2 (зарплата, закрытие ноги, дневной проход фирмы, граница суток) и одинаково в micro и aggregate: «20 лет» агрегатом
   считают ровно ту экономику, которую игрок видит в micro.
2. **Земля дорожает от доступности, а не от спроса.** Цена места в ячейке — линейная функция logsum доступности к местам (3.2 §6.7),
   сглаженная за неделю. Обратной связи «дорого → пусто → дорого» нет по построению (урок E1). Земельный налог и плата за проезд
   входят в выбор как деньги через ценность времени. Балансы в выборе по-прежнему не участвуют (урок E3).
3. **Стационар замкнут учётными правилами, а у казны есть долг.** Остаток фирмы сверх буфера уходит дивидендами за город, остаток
   жителя сверх буфера — тратами вне города, должник выселяется по дедлайну. Поэтому балансы ограничены и сверху, и снизу. Казна
   может уйти в минус: проценты, кредитный лимит от сглаженного дохода, за лимитом — запрет платной стройки. Город не рушится.

## 1. Границы и инварианты

Режим: `economy.city_budget = true`, только при `demand.model = "residents"` (иначе загрузчик — `InvalidCity`).

| # | Инвариант | Проверка (§14) |
|---|---|---|
| EC0 | Без `city_budget`: состояние, хэш, сейв (кроме столбцов-дефолтов новой версии), выбор побитово прежние | golden s1k/s10k/`*-peds`/`*-res`, корпус w3 |
| EC1 | Σ балансов всех счетов = 0 в каждый тик (CZ2), включая `City`; Σ потоков за день по видам = Δ балансов | debug каждые 100 тиков, тест-фаззер, JSON по дням |
| EC2 | Нет схлопывания за 20 лет (§14.2) | `run --years 20` |
| EC3 | Нет разгона: балансы жителей и фирм на душу ограничены, казна растёт не быстрее линейного сальдо (§14.2) | `run --years 20` |
| EC4 | `res_stuck = 0`, CZ3, невыполненные визиты ≤ 5%, выселения за долг ≤ 0.5% населения в день в среднем | тест, прогон |
| EC5 | Политика по умолчанию: `City ≥ 0` все 20 лет, годовое сальдо ∈ [0, +25%] дохода казны | прогон |
| EC6 | Долг без краха: фикстура «налоги 0» уходит за лимит, стройка → `InsufficientFunds`, 5 лет без падений и переполнений, EC1–EC4; возврат политики → `City ≥ 0` за ≤ 3 года | тест `Long` |
| EC7 | Деньги не зависят от исполнителя ноги: потоки по видам на жителя в день, micro против aggregate за 3 дня — ±10% (`Fare` ±15%) | тест `Long` |
| EC8 | Включение на старом сейве (урок E4): w3 `s1k-res` → `Enable` → 30 дней: EC1, падение населения ≤ 5%, выселения за долг ≤ 2% суммарно, `City ≥ 0` | тест |
| EC9 | 0 B/тик, хэш не зависит от числа потоков (1/2/3), без float в деньгах и решениях (CZ5–CZ7) | тесты, ревью |

Вне объёма 4.2: услуги (школы, больницы, полиция), парки, рыночные цены и зарплаты (фиксированы параметрами), топливо и платные
дороги, налоговые льготы по районам, займы как отдельные сущности (только овердрафт), деградация сервисов при банкротстве.

## 2. Данные

### 2.1 Счёт и виды потоков (`Api/CityInfo.cs`, `City/Ledger.cs`)
`FlowKind` дописывается: `Interest = 13`, `Dividend = 14`, `Leakage = 15`, `Refund = 16`; `Ledger.FlowCount = 17`.
Смысл зарезервированных: `Tax` — налоги R/C/I; `Rent` — земельный налог (частной аренды нет, ADR-0025 альт.); `Fare` — плата за
проезд; `Maintenance` — содержание дорог и автобусов; `Subsidy` — резерв (не используется в 4.2); `Construction` — стройка игрока
(в 3.2 — стройка зданий, остаётся на `External`, см. §17 т. 3).

**Режим `city.lifecycle`** (`building-lifecycle.md` §2.10, BL3–BL4) дописывает счёт `Utility` (фонд ЖКХ, рядом с `City`; `SumAll` и CZ2 его включают) и потоки
`UtilityFee = 17` (житель/фирма → `Utility`), `Repair = 18` (`Utility` → `External`), `CrewWages = 19` (`Utility` → `External`), `Restoration = 20` (`City` → `External`, капремонт),
`UtilitySubsidy = 21` (`City` → `Utility`, флаг политики); `FlowKindCount = 22`. Снос игроком и очистка рассыпавшегося идут потоком `Construction` (из `City`).

**Перенос остатков (`Ledger.TransferPct`).** Пропорциональный сбор `amount = base · rate / D` считается с глобальным переносом
по виду потока: `Carry[kind] += base · rate; amount = Carry[kind] / D; Carry[kind] -= amount · D`. `D` = 10 000 (базисные пункты).
`Carry` — `long[17]`, состояние (сейв, хэш). Сумма за день точна до одного `D`, плательщики чередуют округление в фиксированном
порядке событий. Отрицательных `base` нет (налог на прибыль — только с прибыли > 0).

### 2.2 Состояние бюджета (`City/CityBudget.cs`, `CityWorld.Budget`, `null` без режима)
| Поле | Тип | Смысл |
|---|---|---|
| `Policy`, `PendingPolicy` | `BudgetPolicy` | действующая и ожидающая границы суток (`HasPending` — byte) |
| `LandValue[NC]` | int | цена места в ячейке, деньги (EWMA, §4) |
| `IncomeEwma`, `ExpenseEwma` | long | сглаженные доход и расход казны за сутки (`>> budget_ewma_shift`, 3) |
| `PayrollEwma[b]` | long | сглаженный фонд оплаты фирмы (для буфера §7); живёт в `BuildingStore` как новый столбец |
| `Carry[17]` | long | переносы остатков §2.1 |
| `DebtDays`, `OverLimitDays` | int | дней подряд с `City < 0` / за лимитом |
| `Insolvent` | byte | за кредитным лимитом сейчас |
| `UndoCredits[64]` | `{EntityRef Target; long Amount; long ExpireTick}` | кредиты undo (§8.3), кольцо, мёртвые — нули |
| `MaintRoadDay`, `MaintBusDay` | long | производные: итоги содержания (пересчёт при `NetDirty` и правке маршрутов; не в сейве) |
| `VotDsQ8` | int | производное: децисекунд на единицу денег, Q8 (§5) |

`BudgetPolicy` (POD, 16 Б): `ushort TaxRBp, TaxCBp, TaxIBp, LandTaxBpDay, Fare, Flags, R0, R1` (ставки — базисные пункты; `Fare` —
деньги за ногу; `Flags` бит0 `Enable`).

### 2.3 Параметры (секция `economy` сценария, §13)
Ставки и цены — дефолты политики; коэффициенты — константы режима. Все целые после загрузчика (правило ADR-0022, одно место —
`ScenarioLoader.City.cs`).

## 3. Потоки 4.2

| Поток | Когда (фаза) | Откуда → куда | Сумма |
|---|---|---|---|
| `Tax` R | событие `Wage` (2r/9r, то же место, что зарплата 3.2) | житель → `City` | `TransferPct(wage, TaxRBp)` сразу после `Wage`; доход жителя для `IncomeEwma` — после налога |
| `Tax` C/I | дневной проход фирмы (§7.4 3.2), до правил найма | фирма → `City` | `TransferPct(max(0, вчерашняя прибыль), TaxC/IBp)`; `ProfitEwma` — по прибыли после налога |
| `Rent` жителя | дневной проход жителя (курсор 3.2) | житель → `City` | `TransferPct(LV[cell дома], LandTaxBpDay)` — за одно место |
| `Rent` фирмы | дневной проход фирмы | фирма → `City` | `TransferPct(LV[cell] · Capacity, LandTaxBpDay)`; входит во вчерашнюю прибыль следующего дня |
| `Fare` | закрытие ноги со способом `Bus` (`TripLegs.CloseLeg`: micro 9r и aggregate 2r) | житель → `City` | `Policy.Fare`, если `Account ≥ Fare`; иначе 0 и счётчик `fare_evaded` (поездка уже совершена — не отменяем) |
| `Maintenance` | граница суток (9r) | `City` → `External` | `MaintRoadDay + MaintBusDay` (§6.1) |
| `Interest` | граница суток, `City < 0` | `City` → `External` | `TransferPct(−City, interest_bp_day)` |
| `Dividend` | дневной проход фирмы, после налога | фирма → `External` | `(Account − firm_buffer_days · PayrollEwma) >> payout_shift`, если > 0 |
| `Leakage` | дневной проход жителя | житель → `External` | `(Account − res_buffer_days · IncomeEwma) >> payout_shift`, если > 0 |
| `Construction` | применение команды инструмента (фаза 1) | `City` → `External` | цена `tools.costs` (§8) |
| `Refund` | снос, понижение, undo (фаза 1) | `External` → `City` | §8 |
| `UtilityFee` | дневной проход жителя и фирмы (только `city.lifecycle`) | житель/фирма → `Utility` | `TransferPct(R0 · места, 100 б.п.)`: житель — 1 место, фирма — `Capacity`; `R0` = тариф, стартово 30 (сотые доли в сутки) |
| `Repair`, `CrewWages` | окончание ремонта; граница суток | `Utility` → `External` | `(Ceil − Cond) · Capacity · repair_per_unit / 10 000` (10); `crew_day_cost` (60) на бригаду |
| `Restoration`, `UtilitySubsidy` | команда `RestoreBuilding`; выезд при пустом фонде и флаге политики | `City` → `External`; `City` → `Utility` | `Capacity · restore_per_unit · (2 − Cond/10 000)`; дефицит фонда |
| `Endowment` | `Create` или `Enable` | `External` → `City` | `initial_city_balance_per_resident · Alive` |

Порядок внутри дневного прохода фирмы (по возрастанию индекса здания, 3.2): прибыль вчера → `Tax` → `Rent` (в счёт сегодняшнего дня)
→ `ProfitEwma` → правила найма/сокращения/закрытия 3.2 → `Dividend`. Жителя: `Rent` → проверка долга (§7.2) → `Leakage`.

## 4. Цена земли (`City/BudgetLand.cs`)

- `LS_land[c] = min(LsJobs[c], min_m LsC[c, m])` — лучший logsum к местам из ячейки (децисекунды, 3.2 §6.7). `NoLs` → `lv_min`.
- Цель: `T[c] = clamp(lv_ref + (lv_slope_per_min · (LsRef − LS_land[c])) / 600, lv_min, lv_max)`. `LsRef` — сохранённый `LS_ref` 3.2.
- Граница суток, после `Accessibility` дня: `LV[c] += (T[c] − LV[c]) >> lv_shift` (3, ≈ неделя). При `Enable` и `Create` — `LV = T`.
- Земля не зависит от заселённости, налога, заброшенности и цены соседей: одна сторона цикла E1 вырезана. Игрок поднимает цену
  только транспортом (дорогой, маршрутом), и это видно на heatmap (`GetLandValue`) — тот же массив, что берёт налог (урок E1).
- Цена O(NC) в сутки (s100k: 225 ячеек).

## 5. Деньги в выборе (`Demand/Choice.cs`, `Demand/Accessibility.cs`, `City/Market.cs`)

- Ценность времени: `VotDsQ8 = (shift_ds · 100 · 256) / (wage_per_shift · vot_pct)`, где `shift_ds` — длительность эталонной
  смены (96 слотов) в децисекундах симуляции. Считается в `Create`/`Load`/при смене политики. Денежный член: `(money · VotDsQ8) >> 8`.
- **Способ и доступность.** В единственное место — `AscOf(ch, m)` 3.2 (переименовать в `ModeConstDs(w, m)`) — для `Bus` добавить
  `Fare` в децисекундах (на ногу; тур — две ноги, как ASC). Через него же плата попадает в `LsC`/`LsJobs` и в рабочий тур. Деньги не
  наблюдаются скимами: смена платы действует со следующего плана дня, без EWMA (это политика, не шум).
- **Переезд (§8.2 3.2).** К стоимости варианта `C_commute + C_disc` добавляется `Rent` места в день в децисекундах:
  `(LV[cell] · LandTaxBpDay / 10000 · VotDsQ8) >> 8` (округление вниз, без переноса — решение, не проводка).
- **Рост и найм** — без изменений: выбор участка по доступности, найм по `ProfitEwma` (уже после налогов и земли).
- Без режима: `Fare = 0`, `Rent = 0` — выражения дают 0, ветвления на горячем пути нет; EC0 проверяется golden.

## 6. Казна: расходы, долг, лимит (`City/BudgetFlows.cs`)

### 6.1 Содержание
- Дороги: `MaintRoadDay = Σ_seg lanes · len_m · rate[RoadKind] / 1000` (`maint_road_per_lane_km_day {street, arterial}`).
  Пересчёт при `NetDirty` (фаза 1c, один проход по живым сегментам; s100k — микросекунды на правку).
- Автобусы: для каждого живого маршрута `buses = ceil(cycle_s / headway_s)`, где `cycle_s = 2 · len_m / v_bus + stops · dwell_ref_s`
  (`v_bus` — свободная скорость типа ТС маршрута); `MaintBusDay = Σ buses · maint_bus_day + stops · maint_stop_day`. Пересчёт при
  правке маршрутов. Не зависит от живых автобусов: в aggregate их нет (ADR-0025 п. 4).

### 6.2 Граница суток (9r, порядок фиксирован)
`Accessibility` дня (3.2) → `LV` (§4) → `Maintenance` → `Interest` → `IncomeEwma/ExpenseEwma` по вчерашним `FlowYesterday`
(доход = `Tax + Rent + Fare`, расход = `Maintenance + Interest + Construction − Refund`) → лимит и `Insolvent` (§6.3) → применение
`PendingPolicy` → `Ledger.RollDay`.

### 6.3 Долг и лимит
- `CreditLimit = credit_days · IncomeEwma` (поток, а не баланс; урок E3). `Insolvent = City < −CreditLimit`.
- `Insolvent`: команды инструментов с ценой > 0 — `InsufficientFunds` (проверка `City − cost ≥ −CreditLimit` в фазе 1 и в превью),
  снос/undo с возвратом — разрешены. События `BudgetLimitReached` / `BudgetRecovered` на смене флага, `BudgetNegative` на первом дне `City < 0`.
- Казна за лимитом продолжает платить содержание (долг растёт), налоги собираются, жители живут. Деградация сервисов — нет в 4.2
  (§17 т. 6). Процент — только на долг; на положительный остаток — нет.

### 6.4 Переполнение
Деньги `long`. Худший случай фикстуры EC6 (s100k, 400 тыс. жителей, расход ≈ 4/жителя/день, процент 0.03%/день, 20 лет):
долг < 400 000 · 4 · 7 300 · e^(0.0003·7 300) ≈ 1.1·10^11 — далеко от 9.2·10^18. Произведения `base · rate` ≤ 10^11 · 10^4 = 10^15.
Без проверок в горячем пути; `Debug.Assert` на `|City| < 2^56`.

## 7. Замыкание стационара

### 7.1 Таблица потоков на жителя в день (символы — `zoning-demand.md` §7.3; проверка параметров, а не закон)
Пусть `t_R, t_C, t_I` — ставки, `F` — плата, `b` — автобусных ног на жителя в день, `ρ` — земельный налог на место в день,
`e·N`, `J_C`, `J_I` — работники, жильё ≈ `N`. Тогда (на жителя в день):
- чистый приток извне: `Π = X·J_I/N − m·g·S`, где `S` — траты жителей; казна и фирмы делят ровно его (дивиденды и утечка — его же часть);
- траты: `S = e·W·(1 − t_R) − b·F − ρ` (если жители тратят весь располагаемый доход; иначе остаток — `Leakage`);
- прибыль C: `S·(1 − g) − J_C·(W + ρ)/N`; прибыль I: `(1 − m)·g·S + X·J_I/N − J_I·(W + ρ)/N`;
- доход казны: `t_R·e·W + b·F + ρ·(1 + (J_C + J_I)/N) + t_C·max(0, прибыль C) + t_I·max(0, прибыль I)`.
С дефолтами 3.2 (`W` 60, `B` 30, `e` 0.6, `g` 0.4, `m` 0.5, `X` 42, `J = 0.3N`) и §13 (`t_R` 6%, `t_C = t_I` 20%, `F` 2, `b ≈ 0.45`,
`ρ` = `LV_ref` 200 · 15 б.п. = 0.3): `S ≈ 32.6`, прибыль C ≈ 1.5, I ≈ 1.0, доход казны ≈ 4.0, после налогов у фирм ≈ 2.0,
`Π ≈ 6.1` (= 4.0 + 2.0 + округления). Содержание по умолчанию калибруется на ≈ 90% дохода казны (§13, E7).
**Вывод:** казна живёт на чистом притоке извне `Π`; налоги выше `Π` не собрать — они срежут траты и прибыль. Это и показывать игроку.

### 7.2 Правила учёта (в дневных проходах 3.2, по возрастанию индекса)
- Фирма: `PayrollEwma += (вчерашний фонд − PayrollEwma) >> ewma_shift`; `Dividend` §3. Буфер не даёт закрыть фирму из-за выплат.
- Житель: `Leakage` §3; **долг**: `Account < −evict_debt_days · max(IncomeEwma, basket_price)` → флаг `Evicted` (переезд с приоритетом,
  дедлайн `evict_max_days` 3.2, затем отъезд с балансом на `External`). Порог по сглаженному доходу; новых состояний нет (урок E2).
- Безработный платит `Rent` и уходит в минус; его выводит дедлайн `seek_max_days` (3.2) раньше порога долга при дефолтах.

## 8. Стоимость инструментов (`Tools/*`, после T7/T8; `player-tools.md` §11)

### 8.1 Списание
Только при `city_budget`. Цена команды — тот же расчёт `tools.costs`, что в превью (T7). Фаза 1, при применении (после валидации,
до мутации): `City − cost < −CreditLimit` → отказ `InsufficientFunds` (`RejectReason`, `IssueCode` 12); иначе
`Transfer(City, External, cost, Construction)`. Отрицательная цена (снос, понижение) — `Transfer(External, City, −cost, Refund)`
с `demolish_refund_pct` / `upgrade_refund_pct`. `PaintZone`, `SetBudgetPolicy` и не-инструменты — бесплатны.

### 8.2 Цена возврата
Цена сноса считается из текущей геометрии и таблицы (`tools.costs`), а не из сохранённой цены постройки: состояние не растёт,
реплей детерминирован. Смена таблицы цен между стройкой и сносом — допустима (возврат по новой цене).

### 8.3 Undo (ответ на вопрос 5 `player-tools.md` §16)
- `InverseBuilder` ставит в обратных командах бит `Command.Flags.Inverse` (журнал и реплей видят его как обычные данные).
- Применённая платная прямая команда пишет в `UndoCredits` запись `{Target, Amount = cost, ExpireTick = tick + undo_refund_s · 20}`;
  `Target` — handle, который команда создала или изменила (новый сегмент; для сноса — снесённый, по нему матчится `RestoreOf`).
  Прямая команда со сносом/понижением — запись с `Amount = −refund` (undo сноса вернёт то, что дали за снос).
- Команда с битом `Inverse`, цель (`Target` или `RestoreOf`) которой есть в таблице и `tick ≤ ExpireTick`: цена 0, проводка
  `Refund` на `Amount` (или `Construction` на `−Amount`, если запись — возврат), запись гасится. Иначе — обычная цена.
- Таблица — кольцо на 64 записи; переполнение вытесняет старейшую (это только раньше закрывает окно). Прямая команда новой группы
  (не `Inverse`, другой тик) очищает таблицу: возврат 100% — только за последнее действие.
- Redo = прямые команды: платные заново. Подделка бита даёт не больше честного undo последнего действия.

## 9. Порядок тика и бюджет

| # | Фаза | Добавляется |
|---|---|---|
| 1 | Команды | `SetBudgetPolicy` → `PendingPolicy`; списания/возвраты инструментов (§8); после 1c при `NetDirty` — `MaintRoadDay` |
| 2r | `CityPhase.Events` | `Tax` R на `Wage`; `Fare` на закрытии агрегатной ноги |
| 9r | `CityPhase.Periodic` | `Fare` на закрытии micro-ноги (`ArrivalRing`); курсоры: житель (`Rent`, долг, `Leakage`), фирма (`Tax`, `Rent`, `Dividend`); граница суток §6.2 |

Без новых фаз, без PRNG, однопоточно. **Бюджет:** слой экономики ≤ **0.01 мс p95** сверх 3.2 (фазы 2r + 9r ≤ 0.11 мс на
`s10k-res-econ`, `bench --phases --warmup-days 1 --window am_peak`, 2 потока); весь тик `s10k-res-econ` ≤ 4 мс p95 как `s10k-res`;
0 B/тик. Оценка (не замер): +1 проводка на ≈ 3 события за тик (≈ 10 нс), курсор жителя +1 запись за тик (≈ 50 нс), граница суток
O(NC + 1) раз в 72 000 тиков; правка сети — +1 проход сегментов в 1c (только при `NetDirty`). s100k-res-econ — справочно.

## 10. API и клиент (правка `core-api.md` §3, §5)
- `CityBudgetInfo GetCityBudget()` — POD: `Balance`, `IncomeEwma`, `ExpenseEwma`, `CreditLimit`, `Insolvent`, `DebtDays`,
  `MaintRoadDay`, `MaintBusDay`, `Policy`, `PendingPolicy`, `HasPending`, `FareEvadedYesterday`, `DebtEvictionsYesterday`.
  Потоки по видам за вчера — уже есть (`CopyFlowTotals`, 17 видов).
- `GetLandValue(Span<int> perCell)` — `LV` (heatmap; тот же массив, что берёт налог).
- События: `BudgetNegative`, `BudgetLimitReached`, `BudgetRecovered`, `PolicyApplied`.
- `CityStats` (3.2) без изменений раскладки; новые числа — только в `CityBudgetInfo`.

## 11. Команды
- `SetBudgetPolicy` (`CommandKind 36`: 25-35 заняты инструментами T1; закреплено в E1): payload `BudgetPolicy` (§2.2).
  Проверки: ставки ≤ `max_tax_bp` (5 000), земля ≤ `max_land_bp_day` (100), `Fare ≤ max_fare` (1 000); `Enable` без режима
  `residents` — `NotSupported`; иначе `RejectReason.BudgetRule`. Применение — на границе суток (последняя команда суток побеждает).
- `Enable` на городе без бюджета (урок E4): создаётся `CityBudget`, `Endowment` казне, `LV = T`, `PayrollEwma` = вчерашний фонд,
  `IncomeEwma` = оценка по §7.1 из вчерашних потоков (чтобы лимит не был 0 в первый день). Выключить бюджет нельзя (`BudgetRule`).
- Обратная (если T8 слит): `SetBudgetPolicy(прежняя)`; для `Enable` — `NoInverse`.

## 12. Сейв и хэш
- Блок `CBGT` (только при бюджете): `Policy`, `PendingPolicy`, `HasPending`, `LandValue[NC]`, `IncomeEwma`, `ExpenseEwma`, `Carry[17]`,
  `DebtDays`, `OverLimitDays`, `Insolvent`, `UndoCredits[64]`. Столбцы: `LEDG` — потоки 13–16 (сегодня/вчера/всего),
  `BLDG` — `PayrollEwma`, `PARM` — параметры §13, `CNTR` — `fare_evaded`, `debt_evictions`, `insufficient_funds`.
- Производные (не в сейве): `MaintRoadDay`, `MaintBusDay`, `VotDsQ8` — пересчёт в `CityWorld.RestoreDerived`.
- `world_version` = 9 (E1; запись в `save-format.md` §12). Окно версии открыто до E8 (E2–E5 вправе дописывать столбцы с дефолтом в миграции и перегенерацией корпуса текущей версии). Миграция: столбцы-дефолты (потоки 13–16 = 0,
  `PayrollEwma` = 0, параметры = дефолты §13 с `city_budget = false`), блок не создаётся — бюджет выключен, продолжение побитово.
- Хэш: секция бюджета только при бюджете; `hash_version` 3 -> 4 (E1; виды потоков 13–16 вне бюджета в хэш не входят). `ValidateCity`: Σ счетов = 0 с `City`, `LV ∈ [lv_min, lv_max]`,
  записи `UndoCredits` с `ExpireTick ≥ Tick` или нули, `Insolvent` согласован с `City` и лимитом.
- Корпус: `wN-s1k-res-econ-d3` (день 3) и `wN-s1k-res` (прежний город в новой версии).

## 13. Сценарий (`scenario-format.md`, секция `economy`, ключи разрешены только при `residents`)
```json
"economy": { "city_budget": true,
  "tax_r_pct": 6, "tax_c_profit_pct": 20, "tax_i_profit_pct": 20, "land_tax_bp_day": 15, "bus_fare": 2,
  "initial_city_balance_per_resident": 60, "interest_bp_day": 3, "credit_days": 60,
  "maint_road_per_lane_km_day": { "street": 0, "arterial": 0 }, "maint_bus_day": 0, "maint_stop_day": 0,
  "lv_ref": 200, "lv_slope_per_min": 20, "lv_min": 50, "lv_max": 1000, "lv_shift": 3,
  "vot_pct": 50, "res_buffer_days": 5, "firm_buffer_days": 10, "payout_shift": 3, "evict_debt_days": 3,
  "budget_ewma_shift": 3, "undo_refund_s": 10, "max_tax_bp": 5000, "max_land_bp_day": 100, "max_fare": 1000 }
```
Нули в `maint_*` — заглушки до E7: E7 подбирает их на `s10k-res-econ` так, чтобы содержание ≈ 90% дохода казны (дороги ≈ 75%,
автобусы ≈ 25%), и записывает числа в дефолты и отчёт. Сценарии: `s1k-res-econ`, `s10k-res-econ`, `s100k-res-econ` = `*-res` +
`city_budget`. `tools.costs` — секция T7, в 4.2 её значения ставит E7 по §17 т. 4.

## 14. Headless «20 лет» и приёмка

### 14.1 Прогон
`Sim.Headless run --scenario s10k-res-econ --years 20 --exec aggregate --calibrate-days 1 --micro-sample-every-days 365
--save-every-days 365 --out perf/results/econ-20y-s10k.json` (команда Z5). Блок `city.budget` JSON (ряды по 30 дней): `City`,
доход и расход по видам, `IncomeEwma`, `CreditLimit`, `Insolvent`-дни, средние балансы на душу (жители, фирмы), `LV` (среднее,
p10, p90), дивиденды и утечка на душу, доля невыполненных визитов, выселения за долг, `fare_evaded`. Итог — `econ_checks`
с вердиктом EC1–EC5 (код проверок — `Sim.Headless/EconChecks.cs`, общий с тестами `Long`).

### 14.2 Определения (технические пороги; меняет оркестратор)
- **Схлопывание** (EC2): население года `y ≥ 2` < 0.85 · max по прошлым годам; или занятые/рабочая сила < 0.85; или доля `Active`
  среди зданий < 0.85; или туров на жителя в день вне коридора DM1.
- **Разгон** (EC3): средний баланс на душу (жители, фирмы отдельно) в годы 5–20 > 1.25 · максимума за годы 3–5; или прирост
  `City` за годы 11–20 > 1.2 · прироста за годы 1–10 (ускорение вместо линейного сальдо); или `|City|` > 10 лет дохода.
- **Масштаб** (урок E3): EC2–EC5 на `s10k-res-econ` — гейт; на `s100k-res-econ` — отчёт (20 лет, если прогон ≤ 3 ч, иначе 10 лет и
  цифра времени), провал — вопрос владельцу.

## 15. Тест-план (`tests/Sim.Core.Tests/City/Budget*`, `Sim.Headless.Tests`)
- **Поверхность (E1):** EC0 (golden без изменений), `Save → Load → Save` побайтно с бюджетом, миграция w3 → wN побитово,
  проверки команды `SetBudgetPolicy`, `TransferPct` (сумма за 10 000 проводок равна точной до `D`, Σ = 0).
- **Потоки (E2):** фаззер проводок (EC1 после каждой); налог R на зарплату; налог на прибыль не делает прибыль отрицательной;
  `Fare` в micro и aggregate по одному пути; содержание = формула §6.1 на фикстуре; `Interest` и `Insolvent`; буферы ограничивают
  балансы (житель и фирма с большим остатком за 30 дней сходятся к буферу); `Enable` на идущем городе.
- **Решения (E3):** монотонность: плата ↑ → доля автобуса ↓ (`s1k-res-econ`, 5 дней, A/B одинаковый seed); `LV` монотонна по доступности;
  переезд предпочитает дешёвую землю при равном `C_commute`; выселение за долг ведёт к переезду/отъезду в дедлайн; DM1/DM2 с бюджетом.
- **Инструменты (E4):** списание и `InsufficientFunds` (применение и превью совпадают); возврат сноса; undo ≤ 10 с — 100%, после — по
  таблице; undo не последнего действия — по таблице; подделанный `Inverse` без записи — обычная цена; реплей журнала = тот же хэш.
- **Приёмка (E5, `Long`):** `Budget_SteadySheet` (формулы §7.1 против aggregate `s1k-res-econ` 20 дней, ±15%); EC6 фикстура долга;
  EC7 micro/aggregate; EC8 старый сейв; `s1k-res-econ` 2 года aggregate с `EconChecks` (EC1–EC4).

## 16. Задачи (sonnet; E8 — верификатор haiku)

Старт — **после мержа Z4** (E1 трогает `Ledger`, `Economy`, `Market`, `BuildingStore`); E5 — после Z5 (`run --years`, агрегат);
E4 — после T7 и T8 (цены и обратные команды инструментов). Порядок: **E1 → (E2 ∥ E3 ∥ E6) → E4 → E5 → E7 → E8**; E4 может идти
∥ E2/E3, если T7/T8 уже слиты (общих файлов нет: E4 — `Tools/`, фаза 1). E2 ∥ E3 — только при заморозке E1: `CityBudget` и все
поля §2, `BudgetPolicy`, `CityBudgetInfo`, `FlowKind` 13–16, `TransferPct`, столбцы сейва, сигнатуры `BudgetFlows.*`/`BudgetLand.*`
с заглушками. Общее DoD: build своего csproj `-warnaserror`, тесты своей области (`--filter FullyQualifiedName~City|Demand|Tools`),
`dotnet format`, EC0 (golden без изменений), правка своего раздела `core-api.md`/`save-format.md`/`scenario-format.md` отдельным
коммитом docs, результат — подраздел §18 этой спеки. Отчётные прогоны, бюджеты и 20 лет — только E8.

| ID | Задача | Файлы | Приёмка / проверки | Зависит |
|---|---|---|---|---|
| E1 | **Поверхность**: параметры §13 в `CityEconomyDesc` и загрузчике (проверки, `city_budget` только при `residents`), `FlowKind` 13–16, `Ledger.FlowCount = 17`, `TransferPct` с `Carry`, `CityBudget` (§2.2, `CityWorld.Budget`), `BudgetPolicy`, `CityBudgetInfo`, `GetCityBudget`, `GetLandValue`, события §10, `SetBudgetPolicy` (25) с проверками и применением на границе суток (`Enable` — заглушка), `RejectReason.BudgetRule`/`InsufficientFunds`, сейв `CBGT` + столбцы §12, миграция, хэш, `ValidateCity`, корпус | `Api/{CityDesc,CityInfo,Command,CommandDispatcher,Simulation.City}.cs`, `City/{Ledger,CityBudget,CityWorld,CityHash}.cs`, `Save/*`, `ScenarioLoader.City.cs`, `tests/…/City/BudgetSurfaceTests.cs`, корпус | §15 «Поверхность»; EC0; заморожена поверхность (список в §18) | Z4 |
| E2 | **Потоки и казна**: §3 целиком (кроме `Construction/Refund`), `PayrollEwma`, `LV` (§4), содержание (§6.1, пересчёт в 1c и на правке маршрутов), граница суток §6.2, долг/лимит/события (§6.3), правила учёта §7.2 (кроме выселения), `Enable` (§11); сценарий `s1k-res-econ.json` | `City/{BudgetFlows,BudgetLand,CityPhase,TripLegs,Economy}.cs`, `scenarios/s1k-res-econ.json`, тесты `City/BudgetFlowsTests.cs` | §15 «Потоки»; EC1 фаззер; 0 B/тик с `s1k-res-econ`; хэш 1/2/3 потока | E1 |
| E3 | **Деньги в решениях**: `VotDsQ8`, `ModeConstDs` (плата в способе, `LsC`, `LsJobs`, рабочем туре), земля в переезде, выселение за долг (§7.2) | `Demand/{Choice,Accessibility}.cs`, `City/Market.cs`, тесты `Demand/MoneyChoiceTests.cs` | §15 «Решения»; без бюджета выбор побитово прежний | E1 (∥ E2) |
| E4 | **Цены инструментов**: списание/возврат (§8.1–8.2), `InsufficientFunds` в применении и превью, бит `Inverse` в `InverseBuilder`, `UndoCredits` (§8.3); правка `player-tools.md` §11, §16 в. 5 | `Tools/{ToolValidator,InverseBuilder,ToolCosts}.cs`, `Api/Simulation.Tools.cs`, тесты `Tools/BudgetToolTests.cs` | §15 «Инструменты» | E1, T7, T8 (T4 — для сноса) |
| E5 | **Headless и приёмка**: блок `city.budget` и `econ_checks` (§14), `EconChecks.cs`, сценарии `s10k-res-econ`, `s100k-res-econ`, фикстура долга (EC6), тесты EC6–EC8 и `Budget_SteadySheet`, `s1k-res-econ` 2 года (`Long`) | `src/Sim.Headless/{EconChecks,RunCommand}.cs`, `scenarios/*-res-econ.json`, `tests/Sim.Headless.Tests/EconTests.cs`, `tests/…/City/BudgetAcceptanceTests.cs` | §15 «Приёмка»; EC1–EC4 на `s1k-res-econ` 2 года | E2, E3, Z5 |
| E6 | **Клиент**: панель бюджета (`GetCityBudget`, потоки по видам за вчера, лимит, `Insolvent`), ползунки ставок и платы → `SetBudgetPolicy` (с подписью «с завтрашнего дня»), heatmap земли (`GetLandValue`, общий шейдер V4), цена и `InsufficientFunds` в превью инструмента | `game/…` | ручной прогон `s1k-res-econ`, скриншот в отчёт; `dotnet build game/TrafficCity.csproj` | E1 (числа — после E2) |
| E7 | **Калибровка**: `maint_*` на `s10k-res-econ` (≤ 2 итерации, §13), `tools.costs` из §17 т. 4, проверка таблицы §7.1 против прогона; числа в дефолты, отчёт и §18 | `Api/CityDesc.cs` (дефолты), `scenarios/*-econ.json`, `scenario-format.md` | `Budget_SteadySheet` зелёный, EC5 на 2 годах aggregate `s10k-res-econ` | E5 |
| E8 | (верификатор) полный `dotnet test` (с `Long`), `bench-gate.sh` s1k, `bench s10k-res-econ` и `s10k-res` `--threads 2 --view focus --phases --warmup-days 1 --window am_peak` (разница = цена экономики, §9), `run --years 20` на `s10k-res-econ` и `s100k-res-econ` (§14), `econ_checks`, закрыть окно `world_version` | — | реальные числа в отчёт; EC1–EC5 на s10k — гейт | E7 |

## 17. Открытые вопросы

### Геймдизайн (владелец; дефолты стоят в §13, решение меняет только числа)
| № | Вопрос | Рекомендация (дефолт) | Трейдофф |
|---|---|---|---|
| 1 | Ставки налогов по зонам и их база | R — 6% зарплаты; C и I — 20% прибыли | Налог на выручку проще объяснить, но убивает фирмы с маржой 3.2 (+10%); ставки выше ≈ `Π` (§7.1) не собрать |
| 2 | Плата за проезд в автобусе | 2 за ногу (≈ 3% дневной зарплаты, ≈ 80 с времени при `vot_pct` 50) | Выше — меньше доля автобуса и больше машин; 0 — автобус как социальная услуга за счёт казны |
| 3 | Стройка зданий (рост) платит городу (разрешение) или внешним строителям | Внешним (как в 3.2) | Плата городу — ещё один доход, растущий с ростом; двойное налогообложение роста |
| 4 | Цены инструментов (`tools.costs`) и возвраты: дорога, светофор, остановка, маршрут; снос — `demolish_refund_pct` | 1 полосо-км улицы ≈ 10 дней дохода казны на 1 000 жителей (≈ 40 000), артерия ×1.5, светофор = 50 м улицы, остановка = 20 м, маршрут — 0; снос 25%, понижение 50% | Дешевле — песочница, дороже — экономика решает, где строить |
| 5 | Undo: 100% только за последнее действие в 10 с (как рекомендовано в `player-tools.md` §16 в. 5) | Да, окно 10 с игрового времени | Длиннее — эксплойт «построил, посмотрел трафик, отменил»; короче — undo теряет смысл |
| 6 | Последствия долга за лимитом | Только запрет платной стройки + проценты + индикатор; без отключения автобусов и роста налогов | Мягко: игрок может «жить в долг» годами; жёстче (урезание маршрутов, авто-налог) — честнее, но рискует «крахом» |
| 7 | Процент по долгу и лимит | 0.03% в день (≈ 11.6% в год), лимит 60 дней сглаженного дохода; на остаток процентов нет | Выше — долг быстро становится ловушкой; проценты на остаток поощряют копить |
| 8 | Цена земли: диапазон и крутизна | `lv_ref` 200, +20 за минуту выигрыша доступности, [50, 1 000], налог 0.15% в день | Круче — сильнее сортировка (центр дорогой, окраины дешёвые), больше выселений при новом метро/дороге |
| 9 | Ценность времени (`vot_pct`) | 50% зарплаты за час симуляции эталонной смены | Сжатые сутки (×24) делают «время» условным; выше — деньги почти не влияют на выбор |
| 10 | Стартовая казна | 60 на жителя (≈ 15 дней дохода) | Больше — песочница в начале; меньше — сразу долг при стройке |
| 11 | Буферы сбережений и вывод за город | Житель 5 дней дохода, фирма 10 дней фонда, вывод 1/8 остатка в день | Без вывода — разгон балансов за 20 лет; больше буфер — медленнее реакция на налоги |
| 12 | Плата за проезд машин (топливо, платные дороги, парковка) | Не в 4.2 (парковка — 4.4) | Машина «бесплатна» против автобуса за 2 — доля автобуса занижена |
| 13 | Видимость денег игроку | Панель бюджета + heatmap земли + потоки по видам; балансы жителей и фирм — только в инспекторе | Больше чисел — больше вопросов «почему», зато индикаторы = модель (урок E1) |

### Технические (оркестратор)
| № | Вопрос | Решение архитектора | Трейдофф |
|---|---|---|---|
| 1 | Номер `world_version` | Следующий свободный при мерже E1, окно открыто до E8 | Если T2/M4 сольются раньше — номер сдвинется, правка одной строки |
| 2 | `CommandKind` для политики | 25 (свободны 25+), закрепить при мерже E1 | Конфликт с волной 4.3 — решает оркестратор по порядку мержа |
| 3 | Дробные сборы | Глобальный перенос остатков по виду потока (`Carry[17]`), а не округление каждой проводки | Плательщик платит 4 или 5 по очереди — незаметно, зато суммы точны |
| 4 | Налог на прибыль только с прибыли > 0, без переноса убытков | Да | Фирма с переменной прибылью платит чуть больше, чем с годовой базы |
| 5 | Буфер и вывод (`Dividend`, `Leakage`) по балансу | Да: это учётное правило, а не решение (выбор балансов не видит) | Отступление от «баланс не участвует» только в учёте, записано в ADR-0025 п. 7 |
| 6 | Выселение за долг по балансу и сглаженному доходу | Да, через существующий `Evicted` и его дедлайн | Без него должник копит минус бесконечно (разгон вниз) |
| 7 | Плата на закрытии ноги, а не на посадке | Да (одинаково в micro и aggregate) | Безбилетник «уже проехал»: `fare_evaded`, поездка не отменяется |
| 8 | Содержание автобусов по интервалу маршрута | Да | Не видит реальную загрузку и сходы с графика |
| 9 | Цена сноса — по текущей таблице, не по цене постройки | Да | Смена таблицы влияет на возврат старых дорог |
| 10 | Выключить бюджет на идущем городе | Нельзя (`BudgetRule`) | Нет обратного перехода — нечего мигрировать назад |
| 11 | Бюджет слоя экономики | ≤ 0.01 мс p95 к 2r + 9r на `s10k-res-econ` | Оценка с запасом ×10 — замер E8 |
| 12 | Гейт 20 лет | `s10k-res-econ` — гейт, `s100k-res-econ` — отчёт | На s100k 20 лет агрегатом — часы (оценка Z5 ×2) |

## 18. Результаты задач
(заполняют E1–E7: отличия от спеки, замороженная поверхность, числа калибровки)

### 18.1 E1 — поверхность (агент L21, ветка `local/e1-budget-surface`)

**Замороженная поверхность** (E2, E3, E6 не меняют, только заполняют; изменение — отдельным решением оркестратора):
- `Api/CityDesc.cs`: поля казны в `CityEconomyDesc` (`CityBudget`, `TaxRBp`, `TaxCProfitBp`, `TaxIProfitBp`, `LandTaxBpDay`, `BusFare`, `InitialCityBalancePerResident`, `InterestBpDay`, `CreditDays`,
  `MaintRoadStreetPerLaneKmDay`, `MaintRoadArterialPerLaneKmDay`, `MaintBusDay`, `MaintStopDay`, `LvRef`, `LvSlopePerMin`, `LvMin`, `LvMax`, `LvShift`, `VotPct`, `ResBufferDays`, `FirmBufferDays`, `PayoutShift`,
  `EvictDebtDays`, `BudgetEwmaShift`, `UndoRefundS`, `MaxTaxBp`, `MaxLandBp`, `MaxFare`), дефолты §13. Параметры хранятся и сохраняются всегда (при `city_budget = false` — дефолтами); пределы проверяет `CityBudget.IsValid` (из `CityBuild.IsValid`).
- `Api/CityInfo.cs`: `FlowKind` 13-16, `BudgetPolicy` (16 Б, `Pack = 2`), `BudgetPolicyFlags.Enable`, `CityBudgetInfo`. `Api/Command.cs`: `CommandKind.SetBudgetPolicy = 36`, `SetBudgetPolicyCmd`, `Command.MakeSetBudgetPolicy`.
  `Api/SimEvent.cs`: `BudgetNegative` 55, `BudgetLimitReached` 56, `BudgetRecovered` 57, `PolicyApplied` 58; `RejectReason.BudgetRule` = 16.
- `Api/Simulation.City.cs`: `GetCityBudget`, `GetLandValue`, `Simulation.FlowKindCount` (= 17; `CopyFlowTotals` требует не меньше). `Api/SimCounters.cs`: `FareEvaded`, `DebtEvictions`, `InsufficientFunds`.
- `City/Ledger.cs`: `FlowCount` 17, `LegacyFlowCount` 13, `Bp` 10 000, `Carry` (ссылка на `CityBudget.Carry`), `TransferPct`. `City/CityBudget.cs`: состояние §2.2 (+ суточные пары `FareEvadedDay`, `DebtEvictionsDay`),
  `UndoCredit`, `DefaultPolicy`, `CheckPolicy`, `RecomputeDerived` (считает `VotDsQ8`), `CreditLimit`. `City/CityWorld.cs`: `Budget` (создаётся в конструкторе при `city_budget`). `City/BuildingStore.cs`: `PayrollEwma`.
- Заглушки E2 с зафиксированными сигнатурами: `BudgetFlows.OnDayBoundary(World, CityWorld)` (вызов в `CityPhase.Periodic` после `Day++`, до `Ledger.RollDay`; E1 применяет `PendingPolicy`),
  `BudgetFlows.RecomputeMaintenance(World, CityWorld)`, `BudgetFlows.Enable(World, CityWorld)` -> `RejectReason`, `BudgetLand.Update(World, CityWorld, bool reset)`.
- Сейв: `world_version` 9, блок `CBGT`, `PARM` 191-218, `BLDG` 21, `CNTR` 154-156, `LEDG` 3-5 по 17 (`save-format.md` §12), `MigrationV9`, `hash_version` 4, `SaveValidator.ValidateBudget`, корпус `w9-*` (+ `w9-s1k-res-econ-d3-t216500`).

**Отличия от спеки (минимальные решения):**
1. `SetBudgetPolicy` = **36**, а не 25: 25-35 заняты инструментами игрока (T1). `RejectReason.InsufficientFunds` = 12 уже существует (T1) — новый код E1 только `BudgetRule` = 16.
2. Проверки команды: неизвестные биты `Flags` и ненулевой резерв — `InvalidArgument`; ставки выше `max_*` и политика без бита `Enable` на городе с бюджетом (выключить бюджет нельзя) — `BudgetRule`; нет города — `NotSupported`;
   город без бюджета: `Enable` — `NotSupported` (заглушка до E2), политика без `Enable` — `BudgetRule`. Политика, равная действующей, **снимает** ожидающую (последняя команда суток побеждает, как `SetTripExecution`).
3. `FareEvadedYesterday`/`DebtEvictionsYesterday` в `CityBudgetInfo` требуют состояния «вчера» — добавлены пары `FareEvadedDay[2]`, `DebtEvictionsDay[2]` (индекс `day & 1`) в `CityBudget` и `CBGT` 29-30 (в §12 их не было).
   E2 обязана обнулять элемент нового дня на границе суток (как `ToursDay`) и увеличивать их вместе со счётчиками `FareEvaded`/`DebtEvictions`.
4. `LV` при создании = `lv_ref` (а не цель `T`): `T` считает `BudgetLand.Update(reset: true)` (E2) при `Create`/`Enable`. `Endowment` казне E1 не делает (поток E2): казна при создании = 0.
5. `Ledger.Carry` — ссылка на тот же массив, что `CityBudget.Carry`; без бюджета `Carry = null` и `TransferPct` округляет вниз без переноса.
6. Хэш вне бюджета прежний: виды потоков 13-16 в общую часть хэша не входят (`LegacyFlowCount = 13`), раздел казны (`CityHash.AddBudget`) пишется только при бюджете. `hash_version` поднят до 4 по спеке (на хэши прежних сценариев не влияет).
7. `ValidateBudget`: `Insolvent = 1` допустим только при `DebtDays >= 1`; запись `UndoCredits` — нули либо `TargetGeneration > 0`, `TargetIndex >= 0`, `ExpireTick >= Tick` (E4 обязана гасить истёкшие записи до сохранения мира или менять правило).
8. `VotDsQ8` считается в `CityBudget.RecomputeDerived` по формуле §5: `96 · SlotTicks / 2 · 100 · 256 / (wage_per_shift · vot_pct)` (децисекунды эталонной смены; тик = 0.5 дс). E3 берёт готовое значение.
9. Загрузчик: проценты `tax_*_pct` -> базисные пункты `round(float(v) · 100)`; кросс-проверки (ставка не выше `max_*`, `lv_min <= lv_ref <= lv_max`) дают `ScenarioException`; те же пределы на стороне ядра — `CityBuild.IsValid` (`InvalidCity`); `city_budget` вне `residents` — `InvalidCity`.
10. `Sim.Headless/Bench/CityRunCommand.cs`: буфер `Flows` берёт `Simulation.FlowKindCount` (иначе `CopyFlowTotals` вернул бы `false`); отчёт по-прежнему печатает 13 видов (расширяет E5).

### 18.2 E2 — потоки и казна (агент L24, ветка `local/e2-budget-flows`)

**Сделано.** Все потоки §3 кроме `Construction/Refund` (E4); `BudgetFlows.OnDayBoundary` в порядке §6.2; `Enable` (EC8); `BudgetLand.Update`; `RecomputeMaintenance`; `Endowment` и `LV = T` при создании;
`Insolvent` с `DebtDays >= 1` и события `BudgetNegative/LimitReached/Recovered`; `PayrollEwma`; `FareEvadedDay`/`DebtEvictionsDay` (обнуление элемента нового дня на границе, рост вместе со счётчиками).
Точки подключения: `Economy.PayWage` (налог R), `Economy.BuildingDay` (налог C/I, земля, дивиденд), `TripLegs.CompleteLeg` (плата за проезд - один путь micro и aggregate), `CityPhase.DayPass`
(`BudgetFlows.ResidentDay`: земля и утечка перед `Market.ResidentDay`), `CityInit.Run` (`BudgetFlows.InitCreated`). Сценарий `scenarios/s1k-res-econ.json` = `s1k-res` + `economy` (`city_budget`,
`maint_road_per_lane_km_day {15, 30}`, `maint_bus_day 40`, `maint_stop_day 3`: на s1k это ≈ 7.4 тыс. в сутки против дохода ≈ 8.7 тыс.; калибровку на `s10k-res-econ` делает E7).

**Отличия от спеки.**
1. `World.Town` и `CityWorld.Cfg` перестали быть `readonly`: `Enable` на идущем городе обязан выставить `economy.city_budget = true` в параметрах мира (иначе сейв не содержит `CBGT`).
2. `PayrollEwma` считается по номинальному фонду `Occupants * wage_per_shift` (измеренного фонда за вчера нет: добавлять столбец в `BLDG` не стали), `EwmaShift` 3.2.
3. Дивиденд: буфер `max(firm_buffer_days * PayrollEwma, initial_firm_balance)`. По формуле §7.2 у новой фирмы с малым фондом выплаты первых дней съедали стартовый капитал (120 тыс. в первые сутки при фонде зарплат 45 тыс.),
   фирмы уходили в минус и закрывались к 14-м суткам; пол по стартовому капиталу снимает это.
4. Содержание пересчитывается на каждой границе суток (O(сегментов + остановок)), а также при создании, `Enable` и загрузке; не в фазе 1c: правка сети учитывается со следующей границы. Длина маршрута - сумма расстояний
   по прямой между соседними остановками (пути маршрута ядро не хранит), `v_bus` - `MaxSpeed` типа ТС, `dwell_ref` - `transit.dwell_base`. Остановки считаются по сумме `StopCount` маршрутов.
5. `Enable`: ставки в самой команде игнорируются (действуют дефолты секции `economy`), потом политику меняют обычной командой. `IncomeEwma` при `Create` и `Enable` - оценка: налог R с вчерашних зарплат + земля + плата
   за вчерашние автобусные ноги (налог на прибыль не оценивается). `Endowment` казне идёт тем же `FlowKind.Endowment`, что начальные балансы жителей и фирм.
6. Рента жителя - за дом `Home`, пока жилья нет (`Home < 0`) - не берётся. Налог R с зарплаты, плата за проезд, налог C/I - на `Policy` действующих суток (ожидающая политика вступает на границе).
7. Выселение за долг (§7.2) - E3; счётчики `DebtEvictions` пока нули.

**Тесты** (`tests/Sim.Core.Tests/City/BudgetFlowsTests.cs`): фаззер проводок (EC1), дельта казны = сумма потоков за каждые сутки, налог R точен до одного `D`, налог C/I не берёт убыток, `Fare` (micro и aggregate, в сценарии
`s1k-res` автобусом никто не ездит, туры принудительно переведены в `Bus`), уклонение от платы, `LV = T` и границы, содержание = формула, проценты/`Insolvent`/события/возврат в лимит, буферы жителя и фирмы, `Enable`
(30 суток, сейв после включения), хэш при 1/2/3 потоках и 0 байт на тик на `s1k-res-econ`, 30 суток без разгона.

**30 суток `s1k-res-econ`** (агрегат, `Day` = 72 000 тиков; ряд печатает тест `Econ30Days_ClosedLoop_NoRunaway_AndPrintsTreasurySeries`): казна 86 991 -> 136 398 на 29-е сутки (+≈ 1.7 тыс. в сутки: доход ≈ 8.7 тыс., содержание 7 424),
кредитный лимит 212 тыс. -> 526 тыс., проценты 0, `Insolvent` не наступал. Баланс жителя на душу сходится к ≈ 200 (буфер 5 суток дохода), фирмы на душу 2990 -> ≈ 2070. **Осцилляция 3.2:** выручка фирм (экспорт + покупки) примерно
равна фонду зарплат, и сектор живёт циклом «наём - сокращение»: зарплаты 57 тыс. на 25-е сутки -> 32 тыс. на 29-е, 4 закрытия. Тот же цикл есть без бюджета (60 суток: 63 -> 41 -> 64 -> 54 тыс.), бюджет сдвигает фазу и углубляет провал
(налог C/I и земля ≈ 5 тыс. в сутки у фирм); без налога C/I и земли провал на 30-х сутках исчезает, на 40-х остаётся. Разбор и подбор ставок - E5/E7, не поведение потоков.

### 18.3 E4 — цены инструментов (агент 178, ночь N1, ветка `claude/cool-newton-91p1tr-n1`)
**Сделано** (`Tools/ToolCosts.cs`, `Command.Flags`). Всё — только при `city_budget`; без него цены бесплатны и поведение прежнее (в превью `Cost` по-прежнему показывает таблицу).
1. **Одно место для превью и применения.** `ToolValidator.Check` в конце зовёт `ToolCosts.Resolve`: эффективная цена (`ToolCheck.Cost`) с учётом undo-кредита и проверка `City − cost < −CreditLimit` → `InsufficientFunds` (12). Превью отдаёт тот же вердикт и ту же `Cost`; `Counters.InsufficientFunds++` — при отказе в применении (не в превью).
2. **Проводка** — `ToolCosts.Settle` в точке «применена» (`AppliedPaid` в обработчиках): `Transfer(City→External, Construction)` либо `External→City, Refund`; отказ после валидатора (например, `Capacity` при перестройке) денег не списывает. Цели записей: новый сегмент (`BuildRoad`), `Seg` (`RemoveRoad`, `UpgradeRoad`), узел (`SetNodeControl`, `SetSignalPlan`, `SetArmControl`, `SetTurnBan`).
3. **`Inverse`.** Под бит выделено поле `Command.Flags` (`ushort` со смещения 2; `CommandKind` стал `ushort` — байты и размер команды прежние, у прямых команд флаги 0). `InverseBuilder.Add` ставит `CommandFlags.Inverse` каждой обратной команде.
4. **`UndoCredits`.** Прямая платная команда пишет запись `{Target, Amount = cost, ExpireTick = tick + undo_refund_s·20}` (снос: `Amount = −refund`). Обратная со своей живой записью (`tick ≤ ExpireTick`) стоит `−Amount` (возврат 100% / возврат полученного за снос), запись гасится. Новая прямая платная команда в другом тике очищает таблицу; при переполнении (одна группа) вытесняется первая запись. Просроченные записи гасятся в конце фазы 1 (`ToolCosts.Purge`), поэтому правило валидатора сейва 7 выполняется без изменений схемы.
5. **Отмена сноса** (`BuildRoad(RestoreOf)` на закрывающемся сегменте) без кредита стоит как новая дорога той же геометрии (раньше валидатор давал 0 — при бюджете это дыра: снос вернул долю цены). Без бюджета по-прежнему 0.

**Отступления.** (а) Обратные команды без собственной записи (настройки узлов, которые `InverseBuilder` добавляет вместе с `BuildRoad`/`RemoveRoad`) стоят по таблице, если меняют платное состояние. (б) Прямая бесплатная команда таблицу не очищает. (в) `UpgradeRoad` с `Speed`-режимом бесплатен, запись не пишется.
**Тесты** — `tests/Sim.Core.Tests/Tools/BudgetToolTests.cs`.
