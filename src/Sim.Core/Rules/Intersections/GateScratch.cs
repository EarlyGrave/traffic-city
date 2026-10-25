using System;

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
    public readonly long[] ConnWaitSum;   // [MaxConnectors] Σ WaitTicks при допуске на светофорном узле (2.5 S1: только копится; §6.9). Вне хэша до S2
    public readonly int[] ConnGrantCount; // [MaxConnectors] число таких допусков
    public int TouchedCount;
    public int OutTouchedCount;
    public int BusyTouchedCount;

    public GateScratch(int maxNodes, int maxVehicles, int maxLanes, int maxConnectors)
    {
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
        Array.Fill(NodeCandHead, -1);
    }
}
