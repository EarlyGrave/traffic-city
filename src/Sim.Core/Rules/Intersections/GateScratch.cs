using System;
using Sim.Core.Api;

namespace Sim.Core.Rules.Intersections;

/// <summary>
/// Рабочие буферы фазы 4 (intersections.md §4), выделены один раз по ёмкостям. Intrusive-списки кандидатов по узлам,
/// список затронутых узлов, список полос с ненулевым <c>OutReserved</c> (чтобы обнулять только их) и код последнего
/// отказа по машине (диагностика тестов; в хэш не входит).
/// </summary>
internal sealed class GateScratch
{
    public readonly int[] NodeCandHead;   // [MaxNodes], -1
    public readonly int[] CandNext;       // [MaxVehicles]
    public readonly int[] CandConn;       // [MaxVehicles] connector кандидата в этом тике
    public readonly int[] TouchedNodes;   // [MaxNodes]
    public readonly int[] OutTouched;     // [MaxLanes] полосы с OutReserved != 0
    public readonly byte[] DenyReason;    // [MaxVehicles] GateDeny
    public readonly ulong[] NodeBusy;     // [MaxNodes] биты LocalBit занятых connector'ов узла (ADR-0016 D3); строит фаза 4, читает фаза 5
    public readonly int[] BusyTouched;    // [MaxNodes] узлы с ненулевой NodeBusy (для обнуления в следующем тике)
    public readonly long[] ConnWaitSum;   // [MaxConnectors] Σ WaitTicks при допуске на узле Signal в режиме Adaptive за окно пересчёта весов (§6.9); в хэше
    public readonly int[] ConnGrantCount; // [MaxConnectors] число таких допусков за окно; в хэше
    public readonly float[] ConnDelay;    // [MaxConnectors] измеренная задержка connector'а, с (EWMA окон, §6.9; 0 - нет данных); в хэше
    public int TouchedCount;
    public int OutTouchedCount;
    public int BusyTouchedCount;

    // --- Активные множества фазы 4 (ADR-0019, A2). Всё производное: вне хэша и сейва. ---

    /// <summary>Статические маски полос (бит = номер полосы); пересобираются, когда меняется <c>NetworkStore.Version</c>.</summary>
    public readonly ulong[] GatedMask;        // Normal-полоса с воротами в конце (LaneGateNode >= 0)
    public readonly ulong[] ConnMask;         // любая connector-полоса
    public readonly ulong[] SignalMask;       // из GatedMask: узел Control == Signal (метка StoppedBeforeSignal)
    public int MaskVersion = -1;

    /// <summary>true: следующая фаза 4 делает полный проход по машинам (первый тик, смена сети/узлов); он же эталон Debug-сверки.</summary>
    public bool FullScan = true;

    /// <summary>Машины с <c>WaitTicks != 0</c> или <c>DenyReason != None</c> на конец прошлой фазы 4 (кандидаты прошлого тика + meso вне такта).</summary>
    public int[] PrevList;
    public readonly byte[] PrevKeep;          // [MaxVehicles] элемент PrevList остаётся в списке (результат GatePrevResetJob)
    public int PrevCount;
    public int[] NextList;
    public int NextCount;
    public readonly int[] CandStamp;          // [MaxVehicles] метка тика, в котором машина была кандидатом
    public int Stamp;

    // --- Параллельная фаза 4 по узлам (scale.md §4, S1). Всё производное: вне хэша и сейва. ---

    /// <summary>Узел полосы из CSR (Normal с воротами или connector), иначе -1; пересобирается с масками.</summary>
    public readonly int[] LaneNode;
    public readonly int[] NodeLaneStart;      // [MaxNodes + 1] CSR узел -> полосы (по возрастанию индекса полосы)
    public readonly int[] NodeLanes;          // [MaxLanes]
    public readonly int[] NodeFill;           // [MaxNodes] курсор заполнения CSR
    public readonly ulong[] NodeActiveMask;   // биты узлов, у которых занята хотя бы одна полоса CSR (на тик)
    public readonly int[] ActiveNodes;        // [MaxNodes] активные узлы по возрастанию
    public int ActiveCount;
    public GateWorker[] Workers;              // по числу потоков пула (1 без пула)
    private readonly int _maxNodes, _maxLanes;

    /// <summary>true: задания идут последовательно в обратном порядке узлов (тест: результат не зависит от порядка).</summary>
    internal bool ReverseOrder;

    /// <summary>Готовит буферы воркеров под <paramref name="threads"/> потоков (вызывается при создании мира и запуске пула).</summary>
    public void EnsureWorkers(int threads)
    {
        threads = Math.Max(1, threads);
        if (Workers.Length >= threads)
        {
            return;
        }

        var ws = new GateWorker[threads];
        Array.Copy(Workers, ws, Workers.Length);
        for (int k = Workers.Length; k < threads; k++)
        {
            ws[k] = new GateWorker(_maxNodes, _maxLanes);
        }

        Workers = ws;
    }

    public GateScratch(int maxNodes, int maxVehicles, int maxLanes, int maxConnectors)
    {
        _maxNodes = Math.Max(0, maxNodes);
        _maxLanes = Math.Max(0, maxLanes);
        LaneNode = new int[_maxLanes];
        NodeLaneStart = new int[_maxNodes + 1];
        NodeLanes = new int[_maxLanes];
        NodeFill = new int[_maxNodes];
        NodeActiveMask = new ulong[(_maxNodes + 63) >> 6];
        ActiveNodes = new int[_maxNodes];
        Workers = Array.Empty<GateWorker>();
        EnsureWorkers(1);
        NodeCandHead = new int[Math.Max(0, maxNodes)];
        TouchedNodes = new int[Math.Max(0, maxNodes)];
        CandNext = new int[Math.Max(0, maxVehicles)];
        CandConn = new int[Math.Max(0, maxVehicles)];
        DenyReason = new byte[Math.Max(0, maxVehicles)];
        NodeBusy = new ulong[Math.Max(0, maxNodes)];
        BusyTouched = new int[Math.Max(0, maxNodes)];
        OutTouched = new int[Math.Max(0, maxLanes)];
        ConnWaitSum = new long[Math.Max(0, maxConnectors)];
        ConnGrantCount = new int[Math.Max(0, maxConnectors)];
        ConnDelay = new float[Math.Max(0, maxConnectors)];
        Array.Fill(NodeCandHead, -1);
        int words = (Math.Max(0, maxLanes) + 63) >> 6;
        GatedMask = new ulong[words];
        ConnMask = new ulong[words];
        SignalMask = new ulong[words];
        PrevList = new int[Math.Max(0, maxVehicles)];
        PrevKeep = new byte[Math.Max(0, maxVehicles)];
        NextList = new int[Math.Max(0, maxVehicles)];
        CandStamp = new int[Math.Max(0, maxVehicles)];
#if DEBUG
        DbgWait0 = new ushort[Math.Max(0, maxVehicles)];
        DbgWait1 = new ushort[Math.Max(0, maxVehicles)];
        DbgDeny0 = new byte[Math.Max(0, maxVehicles)];
        DbgDeny1 = new byte[Math.Max(0, maxVehicles)];
        DbgGate0 = new byte[Math.Max(0, maxVehicles)];
        DbgGate1 = new byte[Math.Max(0, maxVehicles)];
        DbgGrant0 = new int[Math.Max(0, maxVehicles)];
        DbgGrant1 = new int[Math.Max(0, maxVehicles)];
        DbgOut1 = new float[Math.Max(0, maxLanes)];
        DbgBusy1 = new ulong[Math.Max(0, maxNodes)];
        DbgListed = new ulong[(Math.Max(0, maxVehicles) + 63) >> 6];
#endif
    }

#if DEBUG
    // Debug-сверка активного прохода с полным (GatePhase.DebugCompareWithFullScan): снимки входа (0) и результата активного прохода (1).
    internal readonly ushort[] DbgWait0, DbgWait1;
    internal readonly byte[] DbgDeny0, DbgDeny1, DbgGate0, DbgGate1;
    internal long DbgBox0;
    internal readonly int[] DbgGrant0, DbgGrant1;
    internal readonly float[] DbgOut1;
    internal readonly ulong[] DbgBusy1;
    internal readonly ulong[] DbgListed;
#endif
}

/// <summary>
/// Буфер воркера параллельной фазы 4 (ADR-0017): списки «тронутых» полос/узлов, кандидаты, допущенные (для отложенного <c>OnGranted</c> колеса Q),
/// целые накопители и метрики светофоров. Задание пишет только сюда и в данные своего узла; слияние - на главном потоке, результат от разбиения не зависит.
/// </summary>
internal sealed class GateWorker
{
    public readonly int[] OutTouched;
    public readonly int[] BusyTouched;
    public readonly int[] NextList;
    public readonly int[] TouchedNodes;
    public readonly int[] Granted;
    public int OutTouchedCount, BusyTouchedCount, NextCount, TouchedCount, GrantedCount;

    public int Candidates;
    public long TryGrantCalls, TryGrantEntries;
    public long Grants, DeniedExit, PriorityInversions, BoxWaitTicks, GateWaitTicksTotal, StarveDenials, CarCrossDelayTicks, OnCallRedGrants;
    public long LeftWaitMax, ArterialGrants, ArterialStops;
    public readonly SignalMetrics Signal = new();
    public bool SignalDirty;

    public GateWorker(int maxNodes, int maxLanes)
    {
        OutTouched = new int[maxLanes];
        BusyTouched = new int[maxNodes];
        NextList = new int[maxLanes];
        TouchedNodes = new int[maxNodes];
        Granted = new int[maxLanes];
    }

    /// <summary>Сводит накопители в мир и обнуляет воркера; списки сливаются отдельно (<c>GatePhase</c>).</summary>
    public void Collect(World w)
    {
        w.Work.GateCandidates += Candidates;
        w.Work.GateTryGrantCalls += TryGrantCalls;
        w.Work.GateTryGrantEntries += TryGrantEntries;
        w.Counters.Grants += Grants;
        w.Counters.GateDeniedExit += DeniedExit;
        w.Counters.PriorityInversions += PriorityInversions;
        w.Counters.OnCallRedGrants += OnCallRedGrants;
        w.Counters.BoxWaitTicks += BoxWaitTicks;
        w.Counters.GateWaitTicksTotal += GateWaitTicksTotal;
        w.Counters.StarveDenials += StarveDenials;
        w.Counters.CarCrossDelayTicks += CarCrossDelayTicks;
        w.Counters.ArterialSignalGrants += ArterialGrants;
        w.Counters.ArterialSignalStops += ArterialStops;
        if (LeftWaitMax > w.Counters.SignalLeftWaitMax)
        {
            w.Counters.SignalLeftWaitMax = LeftWaitMax;
        }

        if (SignalDirty)
        {
            SignalMetrics dst = w.SignalWait;
            for (int m = 0; m < SignalMetrics.Maneuvers; m++)
            {
                dst.WaitTicks[m] += Signal.WaitTicks[m];
                dst.Grants[m] += Signal.Grants[m];
            }

            for (int k = 0; k < Signal.Hist.Length; k++)
            {
                dst.Hist[k] += Signal.Hist[k];
            }

            Array.Clear(Signal.WaitTicks);
            Array.Clear(Signal.Grants);
            Array.Clear(Signal.Hist);
            SignalDirty = false;
        }

        Candidates = 0;
        TryGrantCalls = TryGrantEntries = 0;
        Grants = DeniedExit = PriorityInversions = BoxWaitTicks = GateWaitTicksTotal = StarveDenials = CarCrossDelayTicks = OnCallRedGrants = 0;
        LeftWaitMax = ArterialGrants = ArterialStops = 0;
    }
}
