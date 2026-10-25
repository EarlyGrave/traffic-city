using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Lod;

/// <summary>Слот фокуса (0..3) или закрепления (4..7) в ячейках сетки LOD (sim-lod.md §1). Kind = <see cref="LodFocusKind"/>.</summary>
internal struct LodFocusSlot
{
    public byte Kind;
    public int Cx0, Cz0, Cx1, Cz1;
    public int CenterCx, CenterCz;
}

/// <summary>Биты <c>VehicleStore.Lod</c>: бит0 - машина в meso-режиме, бит1 - <c>MesoFresh</c> (поведение считается в ближайшей фазе 5 независимо от цвета).</summary>
internal static class LodBits
{
    public const byte Meso = 1;
    public const byte Fresh = 2;

    // Q-meso (lod.meso_model = "queue", Lod/QueueMeso.cs; только при World.Queue != null):
    public const byte Queue = 4;    // машина очереди полосы: фазы 5-6 её не трогают, S/Speed - на момент въезда (или синтез головы)
    public const byte Head = 8;     // голова очереди, материализованная у стоп-линии (кандидат ворот в такт цвета)
    public const byte Parked = 16;  // событие «готова» наступило, но впереди недопущенная машина: ждёт освобождения
}

/// <summary>Смена уровня машины (sim-lod.md §2.5): общая для въезда на полосу и для смены уровня ячейки (L3).</summary>
internal static class LodTransitions
{
    /// <summary>
    /// Ставит уровень машины <paramref name="i"/>: micro -> meso: <c>MesoFresh = 1</c> (поведение считается в ближайшей фазе 5), <c>MesoAllow = 0</c>;
    /// meso -> micro: <c>Accel = 0</c>, <c>MesoAllow = 0</c>. Тот же уровень - без изменений. Только при выделенных массивах уровней.
    /// </summary>
    public static void SetLevel(Agents.VehicleStore v, int i, byte level)
    {
        bool meso = (v.Lod[i] & LodBits.Meso) != 0;
        if (level == LodCells.LevelMeso)
        {
            if (!meso)
            {
                v.Lod[i] = LodBits.Meso | LodBits.Fresh;
                v.MesoAllow[i] = 0f;
            }
        }
        else if (meso)
        {
            v.Lod[i] = 0;
            v.Accel[i] = 0f;
            v.MesoAllow[i] = 0f;
        }
    }
}

/// <summary>
/// LOD-ячейки (ADR-0013, sim-lod.md §1): квадратная сетка по bounding box узлов сети в <c>Create</c> (то же отображение с зажимом к краю,
/// что у ячеек уровня 1 оверлея маршрутизации), привязка узлов и полос к ячейкам и состояние уровней. Создаётся только при
/// <c>Lod.Mode != Off</c> (иначе <c>World.Lod == null</c> и ни один массив не выделен). L1 - каркас: уровни не читает ни одна фаза.
/// <para>
/// Привязка: узел - в ячейке своих координат; обычная полоса - в ячейке <b>конечного</b> узла; connector - в ячейке своего узла. Поэтому узел,
/// все его входные полосы и его connector'ы всегда в одной ячейке, и ворота никогда не стоят на границе уровней.
/// </para>
/// </summary>
internal sealed class LodCells
{
    /// <summary>Предел числа ячеек (защита от слишком мелкого <c>cell_m</c>); s1m - порядка тысяч.</summary>
    public const int MaxCells = 1 << 20;

    public const int FocusSlots = 4;
    public const int PinSlots = 4;
    public const int SlotCount = FocusSlots + PinSlots;

    public const byte LevelMicro = 0;
    public const byte LevelMeso = 1;

    public readonly float CellM;
    public readonly float OriginX, OriginZ;
    public readonly int CellsX, CellsZ;
    public readonly int Cells;                     // CellsX * CellsZ

    // ---- производные сети (вне хэша; пересчёт в Create и после BuildRoad) ----
    public readonly int[] NodeCell;                // [MaxNodes]
    public readonly int[] LaneCell;                // [MaxLanes]
    public readonly byte[] LaneColor;              // [MaxLanes] цвет ячейки полосы (0..3) = такт meso: решения машин и ворот узла в тик (Tick & 3) == цвет
    public readonly int[] CellLaneStart;           // [Cells + 1] CSR: полосы ячейки c - CellLaneList[Start[c] .. Start[c+1])
    public readonly int[] CellLaneList;            // [MaxLanes], внутри ячейки по возрастанию индекса полосы

    // ---- состояние уровней (в хэше при Mode != Off) ----
    public readonly byte[] CellLevel;              // 0 micro, 1 meso
    public readonly long[] CellHoldUntil;          // тик, до которого ячейка не понижается
    public readonly byte[] CellCongStreak;         // оценок подряд с признаком пробки (насыщение 255)
    public readonly LodFocusSlot[] Focus = new LodFocusSlot[SlotCount];
    public bool LodEvalRequested;
    public long LastLodEvalTick;

    // ---- scratch контроллера (L3), вне хэша ----
    public readonly int[] CellCount;
    public readonly int[] CellWaiting;
    public readonly int[] CellStuck;
    public readonly ulong[] CellKey;

    private readonly NetworkStore _net;
    private int _nodesMapped;
    private int _lanesMapped;

    private LodCells(NetworkStore net, float cellM, float originX, float originZ, int cellsX, int cellsZ)
    {
        _net = net;
        CellM = cellM;
        OriginX = originX;
        OriginZ = originZ;
        CellsX = cellsX;
        CellsZ = cellsZ;
        Cells = cellsX * cellsZ;
        NodeCell = new int[net.Nodes.Length];
        LaneCell = new int[net.Lanes.Length];
        LaneColor = new byte[net.Lanes.Length];
        CellLaneStart = new int[Cells + 1];
        CellLaneList = new int[net.Lanes.Length];
        CellLevel = new byte[Cells];
        CellHoldUntil = new long[Cells];
        CellCongStreak = new byte[Cells];
        CellCount = new int[Cells];
        CellWaiting = new int[Cells];
        CellStuck = new int[Cells];
        CellKey = new ulong[Cells];
    }

    /// <summary>
    /// Строит сетку по готовой сети, привязывает узлы и полосы, задаёт начальные уровни (<c>all_meso</c> - все meso) и слоты
    /// фокуса/закрепления из <paramref name="cfg"/>. Ячеек больше <see cref="MaxCells"/> - <c>InvalidCapacity</c>.
    /// </summary>
    public static CreateStatus TryCreate(NetworkStore net, in LodDesc cfg, out LodCells? cells)
    {
        cells = null;
        if (!float.IsFinite(cfg.CellM) || cfg.CellM <= 0f)
        {
            return CreateStatus.InvalidCapacity;
        }

        float minX = 0f, minZ = 0f, maxX = 0f, maxZ = 0f;
        for (int n = 0; n < net.NodeCount; n++)
        {
            Vec2 p = net.Nodes[n].Position;
            if (n == 0)
            {
                minX = maxX = p.X;
                minZ = maxZ = p.Z;
                continue;
            }

            minX = MathF.Min(minX, p.X);
            maxX = MathF.Max(maxX, p.X);
            minZ = MathF.Min(minZ, p.Z);
            maxZ = MathF.Max(maxZ, p.Z);
        }

        double nx = Math.Max(1.0, Math.Ceiling((maxX - minX) / cfg.CellM));
        double nz = Math.Max(1.0, Math.Ceiling((maxZ - minZ) / cfg.CellM));
        if (nx * nz > MaxCells)
        {
            return CreateStatus.InvalidCapacity;
        }

        var c = new LodCells(net, cfg.CellM, minX, minZ, (int)nx, (int)nz);
        c.OnNetworkGrown();
        if (cfg.Mode == LodMode.AllMeso)
        {
            Array.Fill(c.CellLevel, LevelMeso);
        }

        c.InitSlots(cfg.Focus, 0, LodFocusKind.Focus);
        c.InitSlots(cfg.Pinned, FocusSlots, LodFocusKind.Pin);
        cells = c;
        return CreateStatus.Ok;
    }

    /// <summary>Цвет ячейки (0..3) = такт meso: <c>(cx &amp; 1) | (cz &amp; 1) &lt;&lt; 1</c> (sim-lod.md §0).</summary>
    public int ColorOf(int cell)
    {
        int cx = cell % CellsX;
        int cz = cell / CellsX;
        return (cx & 1) | ((cz & 1) << 1);
    }

    public int CellXOf(float x) => ClampCell(MathF.Floor((x - OriginX) / CellM), CellsX);

    public int CellZOf(float z) => ClampCell(MathF.Floor((z - OriginZ) / CellM), CellsZ);

    public int CellOf(float x, float z) => (CellZOf(z) * CellsX) + CellXOf(x);

    /// <summary>
    /// После <c>BuildRoad</c> (и при создании): привязывает новые узлы и полосы к ячейкам (сетка не меняется, вне её - зажим к краю) и пересобирает
    /// CSR полос по ячейкам. Без аллокаций.
    /// </summary>
    public void OnNetworkGrown()
    {
        NetworkStore net = _net;
        for (int n = _nodesMapped; n < net.NodeCount; n++)
        {
            Vec2 p = net.Nodes[n].Position;
            NodeCell[n] = CellOf(p.X, p.Z);
        }

        _nodesMapped = net.NodeCount;

        for (int l = _lanesMapped; l < net.LaneCount; l++)
        {
            ref readonly LaneData ld = ref net.Lanes[l];
            int node = ld.Kind == LaneKind.Connector
                ? ld.Node
                : (ld.Direction == 0 ? net.Segments[ld.Segment].NodeB : net.Segments[ld.Segment].NodeA);
            LaneCell[l] = NodeCell[node];
            LaneColor[l] = (byte)ColorOf(LaneCell[l]);
        }

        _lanesMapped = net.LaneCount;
        RebuildLaneLists();
    }

    private void RebuildLaneLists()
    {
        int lanes = _lanesMapped;
        Array.Clear(CellLaneStart);
        for (int l = 0; l < lanes; l++)
        {
            CellLaneStart[LaneCell[l] + 1]++;
        }

        for (int c = 0; c < Cells; c++)
        {
            CellLaneStart[c + 1] += CellLaneStart[c];
        }

        // Заполнение курсорами в самом массиве начал: после прохода Start[c] = конец ячейки c (= начало c + 1).
        for (int l = 0; l < lanes; l++)
        {
            int c = LaneCell[l];
            CellLaneList[CellLaneStart[c]++] = l;
        }

        // Возвращаем начала сдвигом (Start[Cells] = всего полос не трогали).
        for (int c = Cells; c > 0; c--)
        {
            CellLaneStart[c] = CellLaneStart[c - 1];
        }

        CellLaneStart[0] = 0;
    }

    private void InitSlots(LodPlaceDesc[]? places, int firstSlot, LodFocusKind kind)
    {
        int n = Math.Min(places?.Length ?? 0, FocusSlots);
        for (int i = 0; i < n; i++)
        {
            LodPlaceDesc p = places![i];
            ref LodFocusSlot s = ref Focus[firstSlot + i];
            s.Kind = (byte)kind;
            s.Cx0 = CellXOf(p.X - p.RadiusM);
            s.Cx1 = CellXOf(p.X + p.RadiusM);
            s.Cz0 = CellZOf(p.Z - p.RadiusM);
            s.Cz1 = CellZOf(p.Z + p.RadiusM);
            s.CenterCx = CellXOf(p.X);
            s.CenterCz = CellZOf(p.Z);
        }
    }

    private static int ClampCell(float f, int n) => !(f > 0f) ? 0 : (f >= n ? n - 1 : (int)f);
}
