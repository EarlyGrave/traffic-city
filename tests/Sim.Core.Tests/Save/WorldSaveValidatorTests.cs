using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Save;

namespace Sim.Core.Tests.Save;

/// <summary>
/// Целевые случаи валидатора (save-format.md §6.1): ссылки и связи, которые читают производные перестройки и Debug-сверки ядра,
/// проверяются до них; ответ - код ошибки, не исключение.
/// </summary>
public class WorldSaveValidatorTests
{
    private static byte[] SmallSave()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        d.Capacities.MaxVehicles = 128;
        using Simulation sim = Simulation.Create(in d);
        sim.Step(700);
        return WorldSaveFixtures.SaveBytes(sim);
    }

    /// <summary>Правит int-столбец блока (после чтения таблиц) и записывает файл заново с верными контрольными суммами.</summary>
    private static byte[] Patch(byte[] file, string block, uint columnId, Action<SaveTables, Span<int>> edit)
    {
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(file, null, out SaveTables? t, out _));
        int b = t!.FindBlock(SaveFormat.FourCc(System.Text.Encoding.ASCII.GetBytes(block)));
        Assert.True(b >= 0);
        Assert.True(t.TryGetColumn(b, columnId, out ReadOnlySpan<int> col));
        edit(t, MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(col), col.Length));
        SaveHeaderInfo h = t.Header;
        Assert.Equal(SaveStatus.Ok, SaveWriter.Measure(ref t, out long len));
        byte[] buf = new byte[len];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, in h, buf, out _));
        return buf;
    }

    private static void AssertRejected(byte[] bad)
    {
        LoadStatus st = LoadStatus.Ok;
        Exception? ex = Record.Exception(() => st = Simulation.Load(bad, default, default, out _, out _));
        Assert.Null(ex);
        Assert.Equal(LoadStatus.InvalidValue, st);
    }

    [Fact]
    public void Control_RewriteWithoutCorruption_Loads()
    {
        byte[] same = Patch(SmallSave(), "NNOD", 8, (_, _) => { });
        Assert.Equal(LoadStatus.Ok, Simulation.Load(same, default, default, out Simulation? s, out _));
        s!.Dispose();
    }

    [Fact]
    public void NodeConnectorRange_PointingAtNonConnectorLanes_IsRejectedWithCode()
    {
        // FirstConnector узла -> полоса 0 (обычная): её FromLane = -1 раньше давал IndexOutOfRangeException в RefreshLaneGateNode.
        AssertRejected(Patch(SmallSave(), "NNOD", 8, (_, first) => first[1] = 0));
    }

    [Fact]
    public void ConnectorFromLane_NotMatchingLaneNextOfThatLane_IsRejectedWithCode()
    {
        // FromLane connector'а -> другая обычная полоса: NextConn / ворота расходились с полным расчётом (Debug.Assert в FirstHop на шаге).
        byte[] bad = Patch(SmallSave(), "NLAN", 7, (t, from) =>
        {
            int b = t.FindBlock(SaveFormat.FourCc("NLAN"u8));
            Assert.True(t.TryGetColumnBytes(b, 1, out ReadOnlySpan<byte> kind));
            int conn = kind.IndexOf((byte)LaneKind.Connector);
            Assert.True(conn >= 0);
            int other = kind.IndexOf((byte)LaneKind.Normal);
            Assert.True(other >= 0);
            from[conn] = from[conn] == other ? other + 1 : other;
            Assert.Equal((byte)LaneKind.Normal, kind[from[conn]]);
        });
        AssertRejected(bad);
    }

    [Fact]
    public void RouteSharedByTwoVehicles_IsRejectedWithCode()
    {
        byte[] bad = Patch(SmallSave(), "VEHC", 20, (_, route) =>
        {
            int first = -1;
            for (int i = 0; i < route.Length; i++)
            {
                if (route[i] < 0)
                {
                    continue;
                }

                if (first < 0)
                {
                    first = i;
                }
                else
                {
                    route[i] = route[first];
                    return;
                }
            }

            Assert.Fail("в сейве меньше двух машин с маршрутом");
        });
        AssertRejected(bad);
    }
}
