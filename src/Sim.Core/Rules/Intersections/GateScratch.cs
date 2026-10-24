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
    public int TouchedCount;
    public int OutTouchedCount;

    public GateScratch(int maxNodes, int maxVehicles, int maxLanes)
    {
        NodeCandHead = new int[Math.Max(0, maxNodes)];
        TouchedNodes = new int[Math.Max(0, maxNodes)];
        CandNext = new int[Math.Max(0, maxVehicles)];
        CandConn = new int[Math.Max(0, maxVehicles)];
        DenyReason = new byte[Math.Max(0, maxVehicles)];
        OutTouched = new int[Math.Max(0, maxLanes)];
        Array.Fill(NodeCandHead, -1);
    }
}
