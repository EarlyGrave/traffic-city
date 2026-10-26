#pragma warning disable CA1861 // тестовые литералы-массивы вызываются один раз
using System;
using System.Buffers.Binary;
using Sim.Core.Api;
using Sim.Core.Save;
using Sim.Core.Tests.Fixtures;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Save;

/// <summary>Фасад сохранения: размеры, потоки, блоки хоста и чужие блоки (T9), аллокации (T8), отказы кодами.</summary>
public class WorldSaveApiTests
{
    private readonly ITestOutputHelper _out;

    public WorldSaveApiTests(ITestOutputHelper output) => _out = output;

    private sealed class ListSink : SaveSink
    {
        public readonly System.Collections.Generic.List<byte> Bytes = new();
        public int Calls;
        public int MaxChunk;

        public override void Write(ReadOnlySpan<byte> data)
        {
            Calls++;
            MaxChunk = Math.Max(MaxChunk, data.Length);
            Bytes.AddRange(data.ToArray());
        }
    }

    private sealed class ChunkSource : SaveSource
    {
        private readonly byte[] _data;
        private int _pos;

        public ChunkSource(byte[] data) => _data = data;

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, 4093), _data.Length - _pos);   // нечётные куски
            _data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
    }

    private static Simulation Warm(int ticks = 600)
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        Simulation sim = Simulation.Create(in d);
        sim.Step(ticks);
        return sim;
    }

    [Fact]
    public void MeasureSave_IsExact_AndSmallBufferIsRefused()
    {
        using Simulation sim = Warm();
        long n = sim.MeasureSave();
        byte[] buf = new byte[n];
        Assert.Equal(SaveStatus.Ok, sim.Save(buf, out long written));
        Assert.Equal(n, written);
        Assert.Equal(SaveStatus.BufferTooSmall, sim.Save(new byte[n - 1], out long w2));
        Assert.Equal(0, w2);

        // Буфер больше нужного: пишется ровно n байт.
        byte[] big = new byte[n + 100];
        Assert.Equal(SaveStatus.Ok, sim.Save(big, out long w3));
        Assert.Equal(n, w3);
        Assert.True(big.AsSpan(0, (int)n).SequenceEqual(buf));
    }

    [Fact]
    public void SaveToSink_EqualsSaveToSpan_InChunksUpToOneMegabyte()
    {
        using Simulation sim = Warm();
        byte[] expected = WorldSaveFixtures.SaveBytes(sim);
        var sink = new ListSink();
        Assert.Equal(SaveStatus.Ok, sim.Save(sink));
        Assert.True(sink.Bytes.ToArray().AsSpan().SequenceEqual(expected));
        Assert.True(sink.MaxChunk <= 1 << 20);
        Assert.True(sink.Calls >= 1);
    }

    [Fact]
    public void LoadFromSource_EqualsLoadFromSpan()
    {
        using Simulation sim = Warm();
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        var o = new LoadOptions { StrictHash = 1 };
        Assert.Equal(LoadStatus.Ok, Simulation.Load(new ChunkSource(bytes), in o, default, out Simulation? b, out LoadReport rep));
        using (b)
        {
            Assert.Equal(sim.ComputeStateHash(), b!.ComputeStateHash());
            Assert.Equal(1, rep.HashChecked);
            Assert.Equal(1, rep.HashMatched);
        }
    }

    [Fact]
    public void InspectSave_ReadsHeader_WithoutLoading()
    {
        using Simulation sim = Warm();
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(bytes.AsSpan(0, 64), out SaveHeaderInfo info));
        Assert.Equal(sim.Tick, info.Tick);
        Assert.Equal(sim.ComputeStateHash(), info.StateHash);
        Assert.Equal(SaveSchema.WorldVersion, info.WorldVersion);
        Assert.Equal(bytes.Length, info.TotalLength);
        Assert.Equal(1, info.Writer);
        Assert.Equal(LoadStatus.Truncated, Simulation.InspectSave(bytes.AsSpan(0, 40), out _));
        Assert.Equal(LoadStatus.NotASave, Simulation.InspectSave(new byte[64], out _));
    }

    [Fact]
    public void Disposed_Simulation_ReturnsDisposed()
    {
        Simulation sim = Warm(10);
        sim.Dispose();
        Assert.Equal(SaveStatus.Disposed, sim.Save(new byte[16], out long w));
        Assert.Equal(0, w);
        Assert.Equal(-1, sim.MeasureSave());
        Assert.Equal(SaveStatus.Disposed, sim.Save(new ListSink()));
    }

    [Fact]
    public void Load_TooNew_AndForeignBlocks_AreReportedByCode()
    {
        using Simulation sim = Warm(50);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);

        byte[] newer = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(newer.AsSpan(12), (uint)SaveSchema.WorldVersion + 1);
        ContainerTestData.Rehash(newer);
        Assert.Equal(LoadStatus.TooNew, Simulation.Load(newer, default, default, out Simulation? s1, out LoadReport r1));
        Assert.Null(s1);
        Assert.Equal(SaveSchema.WorldVersion + 1, r1.FileWorldVersion);

        byte[] older = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(older.AsSpan(12), 0);
        ContainerTestData.Rehash(older);
        Assert.Equal(LoadStatus.MigrationFailed, Simulation.Load(older, default, default, out Simulation? s2, out _));
        Assert.Null(s2);
    }

    // ---------------------------------------------------------------- блоки хоста и чужие блоки (T9)

    private static readonly uint _meta = SaveFormat.FourCc("meta"u8);
    private static readonly uint _camr = SaveFormat.FourCc("camr"u8);

    [Fact]
    public void HostBlocks_RoundTrip_ThroughLoadedHostBlocks()
    {
        using Simulation sim = Warm(100);
        byte[] meta = { 1, 2, 3, 4, 5, 6, 7 };
        byte[] camr = new byte[100];
        new Random(5).NextBytes(camr);
        byte[] data = new byte[meta.Length + camr.Length];
        meta.CopyTo(data, 0);
        camr.CopyTo(data, meta.Length);
        HostBlock[] host =
        {
            new() { Id = _meta, Flags = 0, Offset = 0, Length = meta.Length },
            new() { Id = _camr, Flags = (ushort)BlockFlags.Critical, Offset = meta.Length, Length = camr.Length },
        };

        byte[] withHost = WorldSaveFixtures.SaveBytes(sim, host, data);
        byte[] without = WorldSaveFixtures.SaveBytes(sim);
        Assert.Equal(withHost.Length, sim.MeasureSave(host));

        using Simulation loaded = WorldSaveFixtures.Load(withHost);
        ReadOnlySpan<HostBlock> lb = loaded.LoadedHostBlocks;
        Assert.Equal(2, lb.Length);
        Assert.Equal(_meta, lb[0].Id);
        Assert.Equal(0, lb[0].Flags);
        Assert.Equal(_camr, lb[1].Id);
        Assert.Equal((ushort)BlockFlags.Critical, lb[1].Flags);
        Assert.True(loaded.LoadedHostData.Slice(lb[0].Offset, lb[0].Length).SequenceEqual(meta));
        Assert.True(loaded.LoadedHostData.Slice(lb[1].Offset, lb[1].Length).SequenceEqual(camr));

        // Хост возвращает блоки - сейв побайтно тот же; не возвращает - блоков нет.
        byte[] again = WorldSaveFixtures.SaveBytes(loaded, lb, loaded.LoadedHostData);
        Assert.True(again.AsSpan().SequenceEqual(withHost));
        Assert.True(WorldSaveFixtures.SaveBytes(loaded).AsSpan().SequenceEqual(without));
    }

    [Fact]
    public void HostBlocks_Invalid_AreRefused()
    {
        using Simulation sim = Warm(10);
        byte[] data = new byte[16];
        Span<byte> buf = new byte[sim.MeasureSave() + 100];
        HostBlock ok = new() { Id = _meta, Offset = 0, Length = 4 };
        Assert.Equal(SaveStatus.Ok, sim.Save(buf, out _, new[] { ok }, data));

        HostBlock upper = ok;
        upper.Id = SaveFormat.FourCc("META"u8);   // блоки ядра - с заглавной буквы
        HostBlock core = ok;
        core.Id = SaveFormat.FourCc("VEHC"u8);
        HostBlock beyond = ok;
        beyond.Length = 17;
        HostBlock negative = ok;
        negative.Offset = -1;
        HostBlock badFlags = ok;
        badFlags.Flags = 2;
        HostBlock reserved = ok;
        reserved.Reserved = 1;
        foreach (HostBlock bad in new[] { upper, core, beyond, negative, badFlags, reserved })
        {
            Assert.Equal(SaveStatus.InvalidHostBlock, sim.Save(buf, out _, new[] { bad }, data));
        }

        Assert.Equal(SaveStatus.InvalidHostBlock, sim.Save(buf, out _, new[] { ok, ok }, data));   // повтор id
    }

    private static byte[] Inject(byte[] file, Action<SaveTables> edit)
    {
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(file, null, out SaveTables? t, out _));
        edit(t!);
        SaveHeaderInfo h = t!.Header;
        h.StateHash = BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(24));
        Assert.Equal(SaveStatus.Ok, SaveWriter.Measure(ref t, out long len));
        byte[] outBytes = new byte[len];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, in h, outBytes, out _));
        return outBytes;
    }

    /// <summary>Копия таблиц файла; в блок <paramref name="blockId"/> после его столбцов дописывается <paramref name="addExtra"/>.</summary>
    private static byte[] CopyWith(byte[] file, uint blockId, Action<SaveTables> addExtra)
    {
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(file, SaveSchema.KnownBlockIds(), out SaveTables? t, out _));
        var copy = new SaveTables { Header = t!.Header };
        for (int i = 0; i < t.BlockCount; i++)
        {
            copy.AddBlock(t.BlockId(i), (ushort)t.BlockSchema(i), t.BlockFlagsOf(i));
            for (int c = 0; c < t.ColumnCountOf(i); c++)
            {
                SaveColumnDesc d = t.GetColumn(i, c);
                t.TryGetColumnBytes(i, d.Id, out ReadOnlySpan<byte> data);
                copy.AddColumnBytes(d.Id, d.Type, d.ElemSize, d.Count, data.ToArray());
            }

            if (t.BlockId(i) == blockId)
            {
                addExtra(copy);
            }
        }

        SaveHeaderInfo h = t.Header;
        Assert.Equal(SaveStatus.Ok, SaveWriter.Measure(ref copy, out long len));
        byte[] outBytes = new byte[len];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref copy, in h, outBytes, out _));
        return outBytes;
    }

    [Fact]
    public void UnknownOptionalBlock_IsKept_AndRewritten_UnknownCriticalBlock_And_UnknownColumn_AreErrors()
    {
        using Simulation sim = Warm(100);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        uint xtra = SaveFormat.FourCc("XTRA"u8);

        byte[] withExtra = Inject(bytes, t =>
        {
            t.AddBlock(xtra, 3, BlockFlags.None);
            t.AddColumn(1, new[] { 11, 22, 33 });
        });
        Assert.Equal(LoadStatus.Ok, Simulation.Load(withExtra, default, default, out Simulation? loaded, out LoadReport rep));
        using (loaded)
        {
            Assert.Equal(1, rep.KeptBlocks);
            Assert.Equal(sim.ComputeStateHash(), loaded!.ComputeStateHash());
            byte[] resaved = WorldSaveFixtures.SaveBytes(loaded);
            Assert.True(resaved.Length > bytes.Length);   // блок XTRA переписан
            using Simulation again = WorldSaveFixtures.Load(resaved);
            Assert.True(WorldSaveFixtures.SaveBytes(again).AsSpan().SequenceEqual(resaved));   // и дальше стабилен (флаг kept)
            Assert.Equal(LoadStatus.Ok, SaveReader.Read(resaved, SaveSchema.KnownBlockIds(), out SaveTables? tt, out _));
            int x = tt!.FindBlock(xtra);
            Assert.True(x >= 0);
            Assert.NotEqual(0, (int)(tt.BlockFlagsOf(x) & BlockFlags.Kept));
            Assert.Equal(3, tt.BlockSchema(x));
        }

        byte[] critical = Inject(bytes, t =>
        {
            t.AddBlock(xtra, 1, BlockFlags.Critical);
            t.AddColumn(1, new[] { 1 });
        });
        Assert.Equal(LoadStatus.UnknownCriticalBlock, Simulation.Load(critical, default, default, out Simulation? s2, out LoadReport r2));
        Assert.Null(s2);
        Assert.Equal(xtra, r2.FailedBlock);

        // Неизвестный столбец в известном блоке - порча; столбец другого типа или длины - BadColumn.
        byte[] unknownColumn = CopyWith(bytes, SaveFormat.FourCc("SMET"u8), t => t.AddColumn(999, new[] { 1 }));
        Assert.Equal(LoadStatus.UnknownColumn, Simulation.Load(unknownColumn, default, default, out Simulation? s3, out LoadReport r3));
        Assert.Null(s3);
        Assert.Equal(999u, r3.FailedColumn);
        Assert.Equal(SaveFormat.FourCc("SMET"u8), r3.FailedBlock);
    }

    [Fact]
    public void MissingBlockAndColumn_AreReported()
    {
        using Simulation sim = Warm(50);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(bytes, SaveSchema.KnownBlockIds(), out SaveTables? t, out _));

        // Таблицы без блока VEHC.
        var noVehicles = new SaveTables { Header = t!.Header };
        for (int i = 0; i < t.BlockCount; i++)
        {
            if (t.BlockId(i) == SaveFormat.FourCc("VEHC"u8))
            {
                continue;
            }

            noVehicles.AddBlock(t.BlockId(i), (ushort)t.BlockSchema(i), t.BlockFlagsOf(i));
            for (int c = 0; c < t.ColumnCountOf(i); c++)
            {
                SaveColumnDesc d = t.GetColumn(i, c);
                t.TryGetColumnBytes(i, d.Id, out ReadOnlySpan<byte> data);
                noVehicles.AddColumnBytes(d.Id, d.Type, d.ElemSize, d.Count, data.ToArray());
            }
        }

        byte[] missing = ContainerTestData.Write(noVehicles, SaveSchema.WorldVersion);
        Assert.Equal(LoadStatus.MissingBlock, Simulation.Load(missing, default, default, out Simulation? s, out LoadReport rep));
        Assert.Null(s);
        Assert.Equal(SaveFormat.FourCc("VEHC"u8), rep.FailedBlock);
    }

    // ---------------------------------------------------------------- аллокации (T8)

    [Fact]
    public void Save_IntoSpan_DoesNotAllocate_AndLoadedWorldStepsWithoutAllocations()
    {
        using Simulation sim = Warm(800);
        byte[] buf = new byte[sim.MeasureSave() + 4096];
        Assert.Equal(SaveStatus.Ok, sim.Save(buf, out _));   // первый вызов создаёт рабочие буферы
        Assert.Equal(0, AllocationProbe.Measure(() => sim.Save(buf, out _)));
        Assert.Equal(0, AllocationProbe.Measure(() => _ = sim.MeasureSave()));

        // С блоками хоста: после первого вызова буферы хоста уже есть.
        byte[] data = new byte[200];
        HostBlock[] host = { new() { Id = _meta, Length = 200 } };
        byte[] buf2 = new byte[sim.MeasureSave(host) + 64];
        Assert.Equal(SaveStatus.Ok, sim.Save(buf2, out _, host, data));
        Assert.Equal(0, AllocationProbe.Measure(() => sim.Save(buf2, out _, host, data)));

        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim));
        loaded.Step(600);   // прогрев: первый вход в редкие пути
        long bytes = AllocationProbe.Measure(() => loaded.Step(300));
        _out.WriteLine($"allocated after load, 300 ticks: {bytes} B");
        Assert.Equal(0, bytes);
    }
}
