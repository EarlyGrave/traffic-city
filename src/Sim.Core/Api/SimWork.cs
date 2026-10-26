namespace Sim.Core.Api;

/// <summary>
/// Счётчики работы горячего пути (ADR-0016, hotpath.md D1): сколько раз за всё время симуляции выполнены дорогие правила.
/// Детерминированы (одинаковы в двух прогонах одного сценария), НЕ входят в хэш состояния и не в <see cref="SimCounters"/>:
/// при оптимизациях они меняются намеренно. Мера прогресса без шума таймера. Растут монотонно; хост берёт разности.
/// В горячих циклах считаются в локальных int и добавляются в поле один раз за фазу/вызов.
/// </summary>
public struct SimWork
{
    /// <summary>Машин, обработанных циклом фазы 5 до поиска лидера (живые, на полосе, не на остановке).</summary>
    public long BehaviourVehicles;

    /// <summary>Переходов по цепочке полос в поиске лидера (фазы 5 и 6; MOBIL и фаза 7 не считаются).</summary>
    public long LeaderLookaheadHops;

    /// <summary>Вызовов <c>GateRules.ObstacleGap</c> (фазы 5 и 6).</summary>
    public long ObstacleGapCalls;

    /// <summary>Вызовов <c>GateRules.ConflictGap</c> из фазы 5.</summary>
    public long ConflictGapCallsBehaviour;

    /// <summary>Вызовов <c>GateRules.ConflictGap</c> из фазы 6.</summary>
    public long ConflictGapCallsIntegrate;

    /// <summary>Вызовов <c>ConflictGap</c>, отсечённых маской занятых connector'ов (появится с D3; до него 0).</summary>
    public long ConflictGapMaskRejects;

    /// <summary>Записей конфликтов, просмотренных циклом <c>ConflictGap</c> (включая пропущенные по <c>continue</c>).</summary>
    public long ConflictEntriesScanned;

    /// <summary>Машин-участников, просмотренных в <c>CrossBlocked</c>/<c>MergeGap</c> (хвост connector'а и допущенные на входной полосе).</summary>
    public long ConflictOccupantsScanned;

    /// <summary>Кандидатов ворот (машины, поставленные в список узла фазой 4).</summary>
    public long GateCandidates;

    /// <summary>Вызовов <c>TryGrant</c> (фаза 4).</summary>
    public long GateTryGrantCalls;

    /// <summary>Записей конфликтов, просмотренных в условии 4 <c>TryGrant</c>.</summary>
    public long GateTryGrantEntries;

    /// <summary>Проверок окна маршрута (<c>QueueRefill</c>) в <c>AssignConnectors</c> (фаза 7).</summary>
    public long RouteRefillChecks;

    /// <summary>Пересчётов <c>NextConn</c> в <c>AssignConnectors</c> (фаза 7).</summary>
    public long NextConnRecomputes;

    /// <summary>Слотов, просмотренных фазой 10 (HighWater).</summary>
    public long SnapshotSlots;

    /// <summary>Событий колеса таймеров пешеходов и жителей, обработанных фазой 4p (ped_events_per_tick = разность / тики).</summary>
    public long PedEvents;

    // ---- инструменты игрока (4.3, player-tools.md §13): вне хэша, как остальные счётчики работы ----

    /// <summary>Перестроек узла (<c>NodeEdit.Rebuild</c>).</summary>
    public long NodeRebuilds;

    /// <summary>Команд инструментов, принятых / отклонённых.</summary>
    public long ToolCommandsApplied, ToolCommandsRejected;

    /// <summary>Операций дренажа начато / закончено; событий «дренаж застрял»; наибольшая длительность законченной операции, тики.</summary>
    public long ToolOpsStarted, ToolOpsFinished, DrainStalls, DrainTicksMax;

    /// <summary>Маршрутов, освобождённых при закрытии элемента / переносе (перемаршрутизация).</summary>
    public long RouteReleasesOnClose;

    // Сырой счётчик вызовов ConflictGap: фазы 5 и 6 берут разность до/после цикла и пишут в именованные поля выше.
    internal long ConflictGapCallsRaw;

    /// <summary>Прибавляет все счётчики <paramref name="o"/> (сведение счётчиков воркеров параллельной фазы, ADR-0017; целые - порядок не важен). Новое поле - добавить и сюда.</summary>
    internal void Add(in SimWork o)
    {
        BehaviourVehicles += o.BehaviourVehicles;
        LeaderLookaheadHops += o.LeaderLookaheadHops;
        ObstacleGapCalls += o.ObstacleGapCalls;
        ConflictGapCallsBehaviour += o.ConflictGapCallsBehaviour;
        ConflictGapCallsIntegrate += o.ConflictGapCallsIntegrate;
        ConflictGapMaskRejects += o.ConflictGapMaskRejects;
        ConflictEntriesScanned += o.ConflictEntriesScanned;
        ConflictOccupantsScanned += o.ConflictOccupantsScanned;
        GateCandidates += o.GateCandidates;
        GateTryGrantCalls += o.GateTryGrantCalls;
        GateTryGrantEntries += o.GateTryGrantEntries;
        RouteRefillChecks += o.RouteRefillChecks;
        NextConnRecomputes += o.NextConnRecomputes;
        SnapshotSlots += o.SnapshotSlots;
        PedEvents += o.PedEvents;
        NodeRebuilds += o.NodeRebuilds;
        ToolCommandsApplied += o.ToolCommandsApplied;
        ToolCommandsRejected += o.ToolCommandsRejected;
        ToolOpsStarted += o.ToolOpsStarted;
        ToolOpsFinished += o.ToolOpsFinished;
        DrainStalls += o.DrainStalls;
        DrainTicksMax = System.Math.Max(DrainTicksMax, o.DrainTicksMax);
        RouteReleasesOnClose += o.RouteReleasesOnClose;
        ConflictGapCallsRaw += o.ConflictGapCallsRaw;
    }
}
