using System;
using Sim.Core.Agents;
using Sim.Core.Rules.Intersections;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>Итог перестройки узла (4.3, T3).</summary>
internal struct RebuildInfo
{
    /// <summary>Connector'ов было / стало.</summary>
    public int OldConnectors, NewConnectors;

    /// <summary>Старых connector'ов, которые сохранились (та же пара полос) / исчезли.</summary>
    public int Survived, Removed;

    /// <summary>Рукавов было / стало.</summary>
    public int OldArms, NewArms;
}

/// <summary>Снимок старого узла и размеры нового плана между <see cref="NetworkBuilder.PlanRebuild"/> и <see cref="NetworkBuilder.CommitRebuild"/> (сам план connector'ов лежит в буферах построителя).</summary>
internal struct RebuildPlan
{
    public int OFirstArm, OArms, OConn, OConnN, ORow0, OConfFirst, OConfTotal, OOwn, OPoint;
    public NodeControl OControl;
    public ulong OBan, Ban;
    public int Arms, Plan, Survived;
}

internal sealed partial class NetworkBuilder
{
    private readonly int[] _oldArmSeg = new int[NetworkStore.MaxArms];
    private readonly byte[] _oldArmFlags = new byte[NetworkStore.MaxArms];
    private readonly int[] _oldIn = new int[MaxConnectorsPerNode];
    private readonly int[] _oldOut = new int[MaxConnectorsPerNode];
    private readonly int[] _newToOld = new int[MaxConnectorsPerNode];
    private readonly bool[] _oldKept = new bool[MaxConnectorsPerNode];

    /// <summary>Таблицы переноса последней перестройки узла (полосы и строки connector'ов); читает <see cref="Relocate"/> и тесты.</summary>
    internal readonly IndexRemap LaneRemap = new();

    internal readonly IndexRemap RowRemap = new();

    /// <summary>
    /// Планирование перестройки построенного узла (player-tools.md §5), без изменения хранилища: рукава заново по списку инцидентности, план connector'ов с запретами
    /// <c>TurnBan</c> (переносятся по сегментам рукавов), сопоставление со старыми connector'ами (выживший - та же пара входной/выходной полосы).
    /// Занятый машинами исчезающий connector - <see cref="NetworkStatus.Busy"/>; <paramref name="rejectTrap"/> - отказ <see cref="NetworkStatus.Trap"/>, если входная полоса, у которой
    /// были выезды, остаётся без них. План остаётся в буферах построителя до следующего вызова <c>GatherArms/PlanConnectors</c>.
    /// </summary>
    internal NetworkStatus PlanRebuild(World w, int node, bool rejectTrap, out RebuildPlan rp)
    {
        rp = default;
        NetworkStore s = _s;
        if (!s.NodeAlive(node) || s.Nodes[node].FirstArm < 0)
        {
            return NetworkStatus.InvalidArgument;
        }

        ref NodeData nd = ref s.Nodes[node];
        rp.OFirstArm = nd.FirstArm;
        rp.OArms = nd.ArmCount;
        rp.OConn = nd.FirstConnector;
        rp.OConnN = nd.ConnectorCount;
        rp.OControl = nd.Control;
        rp.OBan = nd.TurnBan;
        rp.ORow0 = -1;

        for (int a = 0; a < rp.OArms; a++)
        {
            _oldArmSeg[a] = s.NodeArms[rp.OFirstArm + a];
            _oldArmFlags[a] = s.ArmFlags[rp.OFirstArm + a];
        }

        for (int q = 0; q < rp.OConnN; q++)
        {
            ref readonly LaneData cl = ref s.Lanes[rp.OConn + q];
            _oldIn[q] = cl.FromLane;
            _oldOut[q] = cl.ToLane;
            _oldKept[q] = false;
            if (q == 0)
            {
                rp.ORow0 = cl.ConflictGroup;
                rp.OOwn = cl.FirstNext;
                rp.OPoint = cl.FirstPoint;
            }
        }

        if (rp.ORow0 >= 0)
        {
            ConnectorStore cs0 = w.Connectors;
            rp.OConfFirst = cs0.FirstConflict[rp.ORow0];
            for (int q = 0; q < rp.OConnN; q++)
            {
                rp.OConfTotal += cs0.ConflictCount[rp.ORow0 + q];
            }
        }

        int arms = GatherArms(node);
        if (arms < 0)
        {
            return NetworkStatus.TooManyArms;
        }

        // Запреты манёвров переносятся по сегментам рукавов (номера слотов меняются при вставке/удалении рукава).
        ulong ban = 0UL;
        if (rp.OBan != 0UL)
        {
            for (int oa = 0; oa < Math.Min(rp.OArms, 8); oa++)
            {
                int na = ArmSlotOfSegment(_oldArmSeg[oa], arms);
                for (int ob = 0; ob < Math.Min(rp.OArms, 8); ob++)
                {
                    if (((rp.OBan >> ((oa * 8) + ob)) & 1UL) == 0UL)
                    {
                        continue;
                    }

                    int nb = ArmSlotOfSegment(_oldArmSeg[ob], arms);
                    if (na >= 0 && nb >= 0 && na < 8 && nb < 8)
                    {
                        ban |= 1UL << ((na * 8) + nb);
                    }
                }
            }
        }

        _ban = ban;
        int plan = arms == 0 ? 0 : PlanConnectors(nd.Control, arms);
        if (plan < 0)
        {
            return NetworkStatus.TooManyConnectors;
        }

        for (int p = 0; p < plan; p++)
        {
            _newToOld[p] = -1;
            for (int q = 0; q < rp.OConnN; q++)
            {
                if (_oldIn[q] == _planIn[p] && _oldOut[q] == _planOut[p] && !_oldKept[q])
                {
                    _newToOld[p] = q;
                    _oldKept[q] = true;
                    break;
                }
            }
        }

        for (int q = 0; q < rp.OConnN; q++)
        {
            if (_oldKept[q])
            {
                rp.Survived++;
            }
        }

        rp.Arms = arms;
        rp.Plan = plan;
        rp.Ban = ban;
        if (rejectTrap && LeavesLaneWithoutExit(plan, rp.OConnN, arms))
        {
            return NetworkStatus.Trap;
        }

        // Исчезающий connector, на котором есть машины, - отказ: «дренаж узла» (ToolOp NodeRebuild) - T4.
        for (int q = 0; q < rp.OConnN; q++)
        {
            if (!_oldKept[q] && w.Occupancy.LaneCount[rp.OConn + q] > 0)
            {
                return NetworkStatus.Busy;
            }
        }

        return NetworkStatus.Ok;
    }

    /// <summary>
    /// Фиксация перестройки по плану <see cref="PlanRebuild"/> (буферы построителя не должны быть затронуты между вызовами): новый блок полос/точек/<c>LaneNext</c>/строк/конфликтов,
    /// перенос всего, что ссылается на выжившие connector'ы (<see cref="Relocate"/>), освобождение старого блока. Узел без рукавов (последний сегмент убран) становится «не построенным»
    /// (<c>FirstArm = -1</c>). Первый же вызов ставит <c>ToolsTouched = 1</c> (раскладка по лестнице). При нехватке места - статус ёмкости, мир не тронут.
    /// Не трогает светофор, сеть пешеходов, граф маршрутов и оверлей - это делает вызывающий (<c>NodeEdit</c>, <c>NetworkEdit.RefreshDerived</c>).
    /// </summary>
    internal NetworkStatus CommitRebuild(World w, int node, in RebuildPlan rp, out RebuildInfo info)
    {
        info = default;
        NetworkStore s = _s;
        ref NodeData nd = ref s.Nodes[node];
        int arms = rp.Arms;
        int plan = rp.Plan;
        int oConnN = rp.OConnN;
        int samples = _o.ConnectorSamples;

        // ---- ёмкость (до первого изменения) ----
        s.ToolsTouched = 1;
        if (!s.CanAllocBlock(s.ArmArena, arms))
        {
            return NetworkStatus.PointsFull;
        }

        if (!s.CanAllocBlock(s.LaneArena, plan))
        {
            return NetworkStatus.LanesFull;
        }

        if (!s.CanAllocBlock(s.LanePointArena, plan * samples) || !s.CanAllocBlock(s.LaneNextArena, 2 * plan))
        {
            return NetworkStatus.PointsFull;
        }

        if (plan > 0 && !s.CanAllocBlock(w.Connectors.RowArena, plan))
        {
            return NetworkStatus.ConnectorsFull;
        }

        // ---- фаза A: новые блоки; старое состояние нетронуто (кроме полей узла, которые при отказе возвращаются) ----
        int nFirstArm = s.AllocBlock(s.ArmArena, arms);
        for (int a = 0; a < arms; a++)
        {
            s.NodeArms[nFirstArm + a] = _armSeg[a];
            s.ArmDir[nFirstArm + a] = _armDir[a];
            s.ArmFlags[nFirstArm + a] = OverrideOf(_armSeg[a], rp.OArms);
        }

        int nConn = s.AllocLaneBlock(plan);
        int nPoint = s.AllocBlock(s.LanePointArena, plan * samples);
        int nIn = s.AllocBlock(s.LaneNextArena, 2 * plan);
        WriteConnectorBlock(node, plan, nConn, nPoint, nIn, linkInLanes: false);

        nd.FirstArm = arms == 0 ? -1 : nFirstArm;
        nd.ArmCount = arms;
        nd.FirstConnector = nConn;
        nd.ConnectorCount = plan;
        nd.TurnBan = rp.Ban;
        if (arms > 0)
        {
            RefreshArmFlags(node);
        }

        int nRow0 = -1;
        if (plan > 0)
        {
            NetworkStatus st = w.Connectors.BuildConflicts(s, node);
            if (st != NetworkStatus.Ok)
            {
                int g0 = s.Lanes[nConn].ConflictGroup;
                if (g0 >= 0)
                {
                    w.Connectors.FreeNodeRows(g0, plan, 0, 0);
                }

                nd.FirstArm = rp.OFirstArm;
                nd.ArmCount = rp.OArms;
                nd.FirstConnector = rp.OConn;
                nd.ConnectorCount = oConnN;
                nd.TurnBan = rp.OBan;
                nd.Control = rp.OControl;
                s.FreeLaneBlock(nConn, plan);
                s.FreePointBlock(nPoint, plan * samples);
                s.FreeLaneNextBlock(nIn, 2 * plan);
                s.FreeArmBlock(nFirstArm, arms);
                return st;
            }

            nRow0 = s.Lanes[nConn].ConflictGroup;
        }

        // ---- фаза B: фиксация (отказов нет) ----
        // Допуски на этом узле сбрасываются (intersections.md §5.7): новые конфликты и приоритеты - другая картина для уже допущенных. Пока старые connector'ы живы.
        GatePhase.ResetNode(w, node);

        LaneRemap.Clear();
        RowRemap.Clear();
        for (int q = 0; q < oConnN; q++)
        {
            int newLane = -1;
            int newRow = -1;
            for (int p = 0; p < plan; p++)
            {
                if (_newToOld[p] == q)
                {
                    newLane = nConn + p;
                    newRow = nRow0 + p;
                    break;
                }
            }

            LaneRemap.Add(rp.OConn + q, newLane);
            if (rp.ORow0 >= 0)
            {
                RowRemap.Add(rp.ORow0 + q, nRow0 >= 0 ? newRow : -1);
            }
        }

        Relocate.Apply(w, LaneRemap, RowRemap);
        InvalidateNextConn(w, rp.OArms, arms);

        // Входные полосы: старые связи сброшены, новые установлены (порядок в списке входной полосы - порядок плана).
        for (int q = 0; q < oConnN; q++)
        {
            ref LaneData li = ref s.Lanes[_oldIn[q]];
            li.FirstNext = 0;
            li.NextCount = 0;
            s.LaneGateNode[_oldIn[q]] = -1;
        }

        for (int p = 0; p < plan; p++)
        {
            ref LaneData li = ref s.Lanes[_planIn[p]];
            if (li.NextCount == 0)
            {
                li.FirstNext = nIn + p;
            }

            li.NextCount++;
        }

        // Старый блок: полосы (канонические мёртвые, производные массивы зануляются), точки, LaneNext, рукава, строки и конфликты.
        if (oConnN > 0)
        {
            s.FreeLaneBlock(rp.OConn, oConnN);
            s.FreePointBlock(rp.OPoint, oConnN * samples);
            s.FreeLaneNextBlock(rp.OOwn - oConnN, 2 * oConnN);
        }

        s.FreeArmBlock(rp.OFirstArm, rp.OArms);
        if (rp.ORow0 >= 0)
        {
            w.Connectors.FreeNodeRows(rp.ORow0, oConnN, rp.OConfFirst, rp.OConfTotal);
        }

        if (arms == 0)
        {
            nd.FirstArm = -1;
            nd.ArmCount = 0;
            nd.FirstConnector = 0;
            nd.ConnectorCount = 0;
        }
        else
        {
            s.RefreshLaneGateNode(node);
        }

        s.Version++;
        w.Gate.FullScan = true;
        w.Vehicles.MarkAllRouteDirty();
        info = new RebuildInfo
        {
            OldConnectors = oConnN,
            NewConnectors = plan,
            Survived = rp.Survived,
            Removed = oConnN - rp.Survived,
            OldArms = rp.OArms,
            NewArms = arms,
        };
        return NetworkStatus.Ok;
    }

    /// <summary>
    /// После <see cref="PlanRebuild"/> с отказом <see cref="NetworkStatus.Busy"/> (4.3, T4): все обречённые перестройкой connector'ы узла закрываются для въезда (<c>Closed</c>), занятые
    /// доезжают и ждут опустения. Возвращает число занятых машинами обречённых connector'ов.
    /// </summary>
    internal int CloseDoomedConnectors(World w, in RebuildPlan rp)
    {
        int busy = 0;
        bool changed = false;
        for (int q = 0; q < rp.OConnN; q++)
        {
            if (_oldKept[q])
            {
                continue;
            }

            int lane = rp.OConn + q;
            if ((_s.Lanes[lane].Flags & LaneFlags.Closed) == 0)
            {
                _s.Lanes[lane].Flags |= LaneFlags.Closed;
                changed = true;
            }

            if (w.Occupancy.LaneCount[lane] > 0)
            {
                busy++;
            }
        }

        if (changed)
        {
            _s.Version++;
            w.Gate.FullScan = true;
        }

        return busy;
    }

    // Кэш NextConn машин на подходах к узлу: выживший connector мог перестать быть «первым ходом» (порядок и состав connector'ов полосы другие). Пересчитает FirstHop/AssignConnectors.
    private void InvalidateNextConn(World w, int oldArms, int arms)
    {
        VehicleStore v = w.Vehicles;
        int hw = v.HighWater;
        for (int i = 0; i < hw; i++)
        {
            int nc = v.NextConn[i];
            int lane = v.Lane[i];
            if (nc < 0 || lane < 0 || !v.Pool.IsAlive(i))
            {
                continue;
            }

            // Подходы к узлу (полоса - рукав) и подходы к подходам: connector по умолчанию зависит от того, есть ли выезды у полосы, в которую он ведёт.
            int seg = _s.Lanes[lane].Segment;
            int toSeg = _s.Lanes[_s.Lanes[nc].ToLane].Segment;
            bool arm = false;
            for (int a = 0; a < arms && !arm; a++)
            {
                arm = _armSeg[a] == seg || _armSeg[a] == toSeg;
            }

            for (int a = 0; a < oldArms && !arm; a++)
            {
                arm = _oldArmSeg[a] == seg || _oldArmSeg[a] == toSeg;
            }

            if (arm)
            {
                v.NextConn[i] = -1;
            }
        }
    }

    private int ArmSlotOfSegment(int seg, int arms)
    {
        for (int a = 0; a < arms; a++)
        {
            if (_armSeg[a] == seg)
            {
                return a;
            }
        }

        return -1;
    }

    // Биты переопределения знаков рукава (SetArmControl) старого рукава того же сегмента.
    private byte OverrideOf(int seg, int oldArms)
    {
        for (int a = 0; a < oldArms; a++)
        {
            if (_oldArmSeg[a] == seg)
            {
                return (byte)(_oldArmFlags[a] & ArmOverrideMask);
            }
        }

        return 0;
    }

    // Входная полоса, у которой были connector'ы, а по новому плану их нет (после запрета манёвра): машины на ней не имели бы выезда.
    private bool LeavesLaneWithoutExit(int plan, int oldCount, int arms)
    {
        for (int q = 0; q < oldCount; q++)
        {
            int lane = _oldIn[q];
            bool kept = false;
            for (int p = 0; p < plan && !kept; p++)
            {
                kept = _planIn[p] == lane;
            }

            if (!kept && LaneStillExists(lane, arms))
            {
                return true;
            }
        }

        return false;
    }

    // Входная полоса остаётся входной (её сегмент по-прежнему рукав узла), иначе она исчезает вместе с рукавом и «выезд» ей не нужен.
    private bool LaneStillExists(int lane, int arms)
    {
        int seg = _s.Lanes[lane].Segment;
        for (int a = 0; a < arms; a++)
        {
            if (_armSeg[a] == seg)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Переопределение знака рукава (<c>SetArmControl</c>): 0 авто, 1 главный, 2 уступи, 3 стоп; пересчёт <c>ArmFlags</c>/<c>Control</c> узла. Версия сети растёт.</summary>
    internal void SetArmOverride(int node, int arm, int control)
    {
        ref NodeData nd = ref _s.Nodes[node];
        int at = nd.FirstArm + arm;
        byte f = (byte)(_s.ArmFlags[at] & ~ArmOverrideMask);
        switch (control)
        {
            case 1:
                f |= ArmForceMajorBit;
                break;
            case 2:
                f |= ArmForceMinorBit;
                break;
            case 3:
                f |= ArmForceMinorBit | ArmStopBit;
                break;
        }

        _s.ArmFlags[at] = f;
        RefreshArmFlags(node);
        _s.RefreshLaneGateNode(node);
        _s.Version++;
    }

    /// <summary>Текущее переопределение знака рукава в виде кода <c>SetArmControl</c> (0 авто, 1 главный, 2 уступи, 3 стоп).</summary>
    internal int ArmOverrideOf(int node, int arm)
    {
        byte f = _s.ArmFlags[_s.Nodes[node].FirstArm + arm];
        if ((f & ArmStopBit) != 0)
        {
            return 3;
        }

        return (f & ArmForceMinorBit) != 0 ? 2 : (f & ArmForceMajorBit) != 0 ? 1 : 0;
    }
}
