using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Host.Tests;

/// <summary>
/// <c>HostSaves</c> (меню сохранения клиента): снимок под эксклюзивным доступом не меняет состояние, загрузка даёт тот же тик и хэш, блок <c>meta</c>
/// возвращается как есть, мир-копия продолжается так же, как исходный; корпус сейвов загружается и продолжается.
/// </summary>
[Collection("HostThreads")]
public class HostSavesTests
{
    private static SimThreadHost Start(string name, int workers = 1)
    {
        ScenarioDesc d = ScenarioLoader.Load(ScenarioLocator.Resolve(name, null));
        return new SimThreadHost(Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = workers }));
    }

    private static void RunTo(SimThreadHost host, long tick)
    {
        host.SetTargetTick(tick);
        var sw = Stopwatch.StartNew();
        while (host.Tick < tick)
        {
            Assert.True(host.State == HostState.Running, "хост остановился: " + host.Fault);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(120), "таймаут");
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void SaveThenLoad_SameTickAndHash_MetaRoundTrips()
    {
        using SimThreadHost host = Start("s1k");
        RunTo(host, 300);

        SaveStatus ss = HostSaves.Save(host, _ => "{\"scenario\":\"s1k\"}", out byte[] bytes, out string metaOut, out long tick, out ulong hash);
        Assert.Equal(SaveStatus.Ok, ss);
        Assert.Equal(300, tick);
        Assert.Equal("{\"scenario\":\"s1k\"}", metaOut);
        Assert.NotEmpty(bytes);

        LoadStatus ls = HostSaves.Load(bytes, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? loaded, out string meta, out LoadReport rep);
        Assert.Equal(LoadStatus.Ok, ls);
        using (loaded)
        {
            Assert.Equal(1, rep.HashChecked);
            Assert.Equal(1, rep.HashMatched);
            Assert.Equal(300, loaded!.Tick);
            Assert.Equal(hash, loaded.ComputeStateHash());
            Assert.Equal("{\"scenario\":\"s1k\"}", meta);
        }

        // сохранение не меняет мир: хост шагает дальше как ни в чём не бывало
        RunTo(host, 340);
        Assert.Equal(340, host.Tick);
    }

    [Fact]
    public void LoadedWorld_UnderNewHost_ContinuesLikeOriginal()
    {
        using SimThreadHost a = Start("s1k");
        RunTo(a, 200);
        Assert.Equal(SaveStatus.Ok, HostSaves.Save(a, null, out byte[] bytes, out string meta, out _, out _));
        Assert.Equal(string.Empty, meta);
        RunTo(a, 400);
        ulong want;
        using (ExclusiveAccess ex = a.AcquireExclusive())
        {
            want = ex.Simulation!.ComputeStateHash();
        }

        Assert.Equal(LoadStatus.Ok, HostSaves.Load(bytes, new SimRuntimeOptions { WorkerThreads = 2 }, out Simulation? loaded, out _, out _));
        using var b = new SimThreadHost(loaded!);
        RunTo(b, 400);
        using ExclusiveAccess exb = b.AcquireExclusive();
        Assert.Equal(want, exb.Simulation!.ComputeStateHash());
    }

    [Fact]
    public void CollectEvents_GathersAllTicks_AndDoesNotChangeHash()
    {
        ulong plain;
        using (SimThreadHost a = Start("s1k"))
        {
            RunTo(a, 600);
            using ExclusiveAccess ex = a.AcquireExclusive();
            plain = ex.Simulation!.ComputeStateHash();
        }

        using SimThreadHost b = Start("s1k");
        b.CollectEvents = true;
        RunTo(b, 600);
        var got = new System.Collections.Generic.List<SimEvent>();
        b.DrainEvents(got);
        Assert.NotEmpty(got);
        Assert.True(got.Select(e => e.Tick).Distinct().Count() > 1, "события только одного тика: пачка теряет события");
        for (int i = 1; i < got.Count; i++)
        {
            Assert.True(got[i].Tick >= got[i - 1].Tick);
        }

        b.DrainEvents(got = new System.Collections.Generic.List<SimEvent>());
        Assert.Empty(got);
        using ExclusiveAccess exb = b.AcquireExclusive();
        Assert.Equal(plain, exb.Simulation!.ComputeStateHash());
    }

    [Fact]
    public void Save_AfterHostDisposed_Throws()
    {
        SimThreadHost host = Start("s1k");
        host.Dispose();
        Assert.Throws<ObjectDisposedException>(() => HostSaves.Save(host, null, out _, out _, out _, out _));
    }

    [Fact]
    public void Peek_ReadsTickFromHeader_AndCorruptBytesAreRefused()
    {
        using SimThreadHost host = Start("s1k");
        RunTo(host, 100);
        Assert.Equal(SaveStatus.Ok, HostSaves.Save(host, null, out byte[] bytes, out _, out _, out ulong hash));
        Assert.Equal(LoadStatus.Ok, HostSaves.Peek(bytes, out SaveHeaderInfo info));
        Assert.Equal(100, info.Tick);
        Assert.Equal(hash, info.StateHash);
        Assert.Equal(Simulation.CurrentWorldVersion, info.WorldVersion);

        byte[] bad = bytes.ToArray();
        bad[bad.Length / 2] ^= 0xFF;
        Assert.NotEqual(LoadStatus.Ok, HostSaves.Load(bad, new SimRuntimeOptions(), out Simulation? s, out _, out _));
        Assert.Null(s);
        Assert.NotEqual(LoadStatus.Ok, HostSaves.Load(new byte[10], new SimRuntimeOptions(), out s, out _, out _));
    }

    [Fact]
    public void Envelope_PackUnpack_RoundTrips_AndPeekReadsHeaderOfFile()
    {
        using SimThreadHost host = Start("s1k");
        RunTo(host, 120);
        Assert.Equal(SaveStatus.Ok, HostSaves.Save(host, _ => "{}", out byte[] raw, out _, out _, out ulong hash));
        byte[] file = SaveEnvelope.Pack(raw);
        Assert.True(file.Length < raw.Length);
        Assert.True(SaveEnvelope.TryUnpack(file, out byte[] back, out string err), err);
        Assert.True(back.AsSpan().SequenceEqual(raw));
        Assert.Equal(LoadStatus.Ok, HostSaves.Peek(file.AsSpan(0, 96), out SaveHeaderInfo info));
        Assert.Equal(120, info.Tick);
        Assert.Equal(hash, info.StateHash);
        Assert.Equal(LoadStatus.Ok, HostSaves.Peek(raw.AsSpan(0, 64), out info));
        Assert.Equal(120, info.Tick);
        file[200] ^= 0x55;
        Assert.False(SaveEnvelope.TryUnpack(file, out _, out _));
    }

    [Fact]
    public void CorpusFile_LoadsThroughHost()
    {
        string? dir = AppContext.BaseDirectory;
        string? file = null;
        for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
        {
            string c = Path.Combine(dir, "tests", "SaveCorpus", "w13-s1k-t2400.tcsave");
            if (File.Exists(c))
            {
                file = c;
                break;
            }
        }

        Assert.NotNull(file);
        byte[] bytes = SaveEnvelope.ReadContainer(file);
        Assert.Equal(LoadStatus.Ok, HostSaves.Load(bytes, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out string meta, out LoadReport rep));
        Assert.Equal(string.Empty, meta);
        Assert.Equal(1, rep.HashMatched);
        using var host = new SimThreadHost(sim!);
        Assert.Equal(2400, host.Tick);
        RunTo(host, 2600);
    }
}
