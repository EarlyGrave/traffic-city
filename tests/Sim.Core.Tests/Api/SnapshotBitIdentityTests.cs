using System;
using Sim.Core.Api;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Api;

/// <summary>Побитовый эталон выхода снапшота (все поля VehicleRenderData, каждый тик): оптимизации фазы 10 не должны менять рендер.</summary>
public class SnapshotBitIdentityTests
{
    private const ulong Expected = 16429679446010778034UL;

    [Fact]
    public void Snapshot_RenderBuffersBitIdentical_S1k_3000Ticks()
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation sim = Simulation.Create(in d);
        ulong h = 14695981039346656037UL;
        for (int tick = 0; tick < 3000; tick++)
        {
            sim.Step(1);
            ReadOnlySpan<VehicleRenderData> cur = sim.Vehicles;
            for (int i = 0; i < cur.Length; i++)
            {
                VehicleRenderData r = cur[i];
                h = Mix(h, (uint)BitConverter.SingleToInt32Bits(r.X));
                h = Mix(h, (uint)BitConverter.SingleToInt32Bits(r.Y));
                h = Mix(h, (uint)BitConverter.SingleToInt32Bits(r.Z));
                h = Mix(h, (uint)BitConverter.SingleToInt32Bits(r.Speed));
                h = Mix(h, (uint)BitConverter.SingleToInt32Bits(r.LateralOffset));
                h = Mix(h, (uint)r.Index);
                h = Mix(h, (uint)r.Generation);
                h = Mix(h, (uint)r.Type);
                h = Mix(h, (uint)r.Flags);
            }
        }

        Assert.True(h == Expected, $"render hash = {h}UL");
    }

    [Fact]
    public void FastAtan2_MatchesMathAtan2_WithinFloatPrecision()
    {
        double worst = 0;
        var rng = new Random(12345);
        for (int k = 0; k < 2_000_000; k++)
        {
            double ang = (rng.NextDouble() * 2 * Math.PI) - Math.PI;
            float x = (float)Math.Cos(ang);
            float y = (float)Math.Sin(ang);
            double e = Math.Abs(SnapshotJob.FastAtan2(y, x) - Math.Atan2(y, x));
            worst = Math.Max(worst, e);
        }

        foreach ((float y, float x) in new[] { (0f, 1f), (0f, -1f), (1f, 0f), (-1f, 0f), (0f, 0f), (1f, 1f), (-1f, -1f) })
        {
            worst = Math.Max(worst, Math.Abs(SnapshotJob.FastAtan2(y, x) - Math.Atan2(y, x)));
        }

        Assert.True(worst < 5e-7, $"max error {worst}");
    }

    private static ulong Mix(ulong h, uint v) => (h ^ v) * 1099511628211UL;
}
