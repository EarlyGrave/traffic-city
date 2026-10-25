using System;
using Sim.Core.Api;
using Sim.Core.Save;
using Sim.Core.Tests.Worlds;

namespace Sim.Core.Tests.Save;

/// <summary>Сценарии и помощники тестов сохранения мира (save-format.md §9).</summary>
internal static class WorldSaveFixtures
{
    /// <summary>
    /// Малый сценарий: город 1.2 x 1.2 км из генератора s1k (светофоры на магистралях, уступки, автобусы, оверлей), ~100 машин, быстрая рампа.
    /// Достаточно мал для сейва на каждом тике, достаточно богат для всех видов состояния.
    /// </summary>
    public static ScenarioDesc Small(bool buses = true)
    {
        ScenarioDesc d = Step2.S1k(buses);
        d.City.SizeX = 1200f;
        d.City.SizeZ = 1200f;
        d.City.ArterialEvery = 2;
        d.City.IrregularNodes = 4;
        d.Capacities.MaxVehicles = 192;
        d.Capacities.MaxRoutes = 512;
        d.Capacities.MaxPendingTrips = 256;
        d.Capacities.CommandQueueCapacity = 64;
        d.Capacities.MaxSignalizedNodes = 64;
        d.Demand.TargetVehicles = 100;
        d.Demand.SpawnRampTicks = 300;
        d.Demand.MinTripLength = 300f;
        d.Transit.Routes = buses ? 3 : 0;
        d.Transit.HeadwayTicks = 400;
        d.Routing.CostRecalcTicks = 200;
        d.Routing.CustomizeBudgetPerTick = 3000;
        return d;
    }

    public static ScenarioDesc S1k(bool buses = true) => Step2.S1k(buses);

    public static ScenarioDesc S10k()
    {
        ScenarioDesc d = CityWorldTests.Desc("s10k");
        d.Capacities.MaxVehicles = 12000;
        d.Capacities.MaxRoutes = 32000;
        d.Capacities.MaxPendingTrips = 16000;
        d.Demand.TargetVehicles = 10000;
        d.Demand.SpawnRampTicks = 2400;
        d.Demand.TypeMix = new[] { 1f, 0f };
        d.Routing = Step2.S1kRouting();
        return d;
    }

    public static byte[] SaveBytes(Simulation sim, ReadOnlySpan<HostBlock> host = default, ReadOnlySpan<byte> hostData = default)
    {
        long n = sim.MeasureSave(host);
        Assert.True(n > 0);
        byte[] buf = new byte[n];
        Assert.Equal(SaveStatus.Ok, sim.Save(buf, out long written, host, hostData));
        Assert.Equal(n, written);
        return buf;
    }

    public static Simulation Load(byte[] bytes, int threads = 1, bool strict = true)
    {
        var o = new LoadOptions { StrictHash = strict ? (byte)1 : (byte)0 };
        LoadStatus st = Simulation.Load(bytes, in o, new SimRuntimeOptions { WorkerThreads = threads }, out Simulation? sim, out LoadReport rep);
        Assert.True(st == LoadStatus.Ok, $"{st} block {FourCc(rep.FailedBlock)} column {rep.FailedColumn} row {rep.FailedRow}");
        return sim!;
    }

    /// <summary>Где в контейнере лежит байт: блок, столбец (id и имя из реестра) и номер элемента. Для сообщений об ошибке «байты различаются».</summary>
    public static string Locate(byte[] file, int offset)
    {
        int blocks = (int)BitConverter.ToUInt32(file, 36);
        for (int i = 0; i < blocks; i++)
        {
            int e = 64 + (i * 32);
            uint id = BitConverter.ToUInt32(file, e);
            long off = BitConverter.ToInt64(file, e + 8);
            long len = BitConverter.ToInt64(file, e + 16);
            if (offset < off || offset >= off + len)
            {
                continue;
            }

            string name = FourCc(id);
            BlockDef? def = Array.Find(SaveSchema.Blocks, b => b.Id == id);
            if (def == null || (BitConverter.ToUInt16(file, e + 6) & 6) != 0)
            {
                return $"{name} (opaque) +{offset - off}";
            }

            int cc = (int)BitConverter.ToUInt32(file, (int)off);
            for (int c = 0; c < cc; c++)
            {
                int ce = (int)off + 8 + (c * 24);
                uint cid = BitConverter.ToUInt32(file, ce);
                long coff = off + BitConverter.ToInt64(file, ce + 8);
                long cnt = BitConverter.ToInt64(file, ce + 16);
                int es = file[ce + 5];
                if (offset >= coff && offset < coff + (cnt * es) + 8)
                {
                    string cname = Array.Find(def.Columns, x => x.Id == cid)?.Name ?? "?";
                    return $"{name}.{cname} (col {cid}) element {(offset - coff) / es} of {cnt}";
                }
            }

            return $"{name} catalog +{offset - off}";
        }

        return "header/toc/footer";
    }

    public static string FourCc(uint id)
    {
        if (id == 0)
        {
            return "-";
        }

        return string.Concat((char)(id & 0xFF), (char)((id >> 8) & 0xFF), (char)((id >> 16) & 0xFF), (char)((id >> 24) & 0xFF));
    }

    public static string Hex(ulong h) => h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
}
