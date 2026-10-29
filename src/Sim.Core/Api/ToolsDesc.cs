namespace Sim.Core.Api;

// Параметры инструментов игрока (player-tools.md §11, scenario-format.md "tools"). Конфигурация хоста: в сейв и хэш не входит
// (после Load хост ставит её заново через Simulation.SetToolsConfig). Нулевая структура = всё бесплатно, границы по умолчанию.

/// <summary>Таблица стоимостей (<c>tools.costs</c>), целые единицы бюджета (<c>long</c>).</summary>
public struct ToolCostsDesc
{
    public long RoadPerMLaneStreet;        // за метр длины на полосу, улица
    public long RoadPerMLaneArterial;      // то же, магистраль
    public int CurveFactorPct;             // стоимость кривой, % от прямой (100 = без надбавки; 0 трактуется как 100)
    public long NodeSignal;                // светофор на узле
    public long ArmSign;                   // знак на рукаве
    public long TurnBan;                   // запрет манёвра
    public long BusStop;
    public long BusRoute;
    public int DemolishRefundPct;          // возврат при сносе, % стоимости постройки
    public int UpgradeRefundPct;           // возврат за заменяемое при апгрейде, %
    public long EarthworkPerM3;            // TR3: земляные работы под дорогой, за м³ (terrain.md §4.4); undo не возвращает
    public long BridgePerMLane;            // TR3: мост, за метр длины на полосу вместо road_per_m_lane (terrain.md §4.5)
}

/// <summary>Секция <c>tools</c> сценария.</summary>
public struct ToolsDesc
{
    /// <summary>Запас вокруг bounding box сети для границ по умолчанию, м.</summary>
    public const float DefaultBoundsMarginM = 2000f;

    public ToolCostsDesc Costs;

    /// <summary><c>tools.inverse_capacity</c>: команд в буфере обратных записей за <c>Step</c> (0 - 4096). Выделяется при создании мира.</summary>
    public int InverseCapacity;

    /// <summary><c>tools.world_bounds</c>: false - по умолчанию bounding box сети + <see cref="DefaultBoundsMarginM"/>.</summary>
    public bool HasWorldBounds;
    public float BoundsMinX, BoundsMinZ, BoundsMaxX, BoundsMaxZ;
}
