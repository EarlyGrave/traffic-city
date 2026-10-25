using System;

namespace Sim.Core.Rules.Intersections;

/// <summary>
/// Измеренная задержка светофоров для маршрутизации (intersections.md 6.9, routing.md 1.1): раз в пересчёт весов (30 с, ADR-0006) среднее
/// <c>WaitTicks</c> при допуске по connector'у, накопленное фазой 4 на узлах <c>Adaptive</c>, входит в <c>ConnDelay</c> скользящим средним
/// (при &gt;= <see cref="MinGrants"/> допусков за окно); накопители обнуляются. Вызывается из <c>RoutePhase</c>, без аллокаций.
/// </summary>
internal static class SignalDelays
{
    public const int MinGrants = 5;

    public static void Update(World w)
    {
        GateScratch g = w.Gate;
        int n = Math.Min(w.Connectors.Count, g.ConnGrantCount.Length);
        for (int i = 0; i < n; i++)
        {
            int count = g.ConnGrantCount[i];
            if (count == 0)
            {
                continue;
            }

            if (count >= MinGrants)
            {
                float m = g.ConnWaitSum[i] / (float)count / 20f;
                g.ConnDelay[i] = g.ConnDelay[i] > 0f ? (0.5f * g.ConnDelay[i]) + (0.5f * m) : m;
            }

            g.ConnWaitSum[i] = 0;
            g.ConnGrantCount[i] = 0;
        }
    }
}
