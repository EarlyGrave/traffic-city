#pragma warning disable CA1861 // тестовые литералы-массивы вызываются один раз
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Save;
using Sim.Core.Tests.Fixtures;

namespace Sim.Core.Tests.Save;

public class SaveContainerTests
{
    private static readonly uint[] _knownAaaaBbbb = { ContainerTestData.Aaaa, ContainerTestData.Bbbb };

    private sealed class ListSink : SaveSink
    {
        public readonly List<byte> Bytes = new();
        public int MaxPiece;
        public int Calls;

        public override void Write(ReadOnlySpan<byte> data)
        {
            Calls++;
            MaxPiece = Math.Max(MaxPiece, data.Length);
            Bytes.AddRange(data.ToArray());
        }
    }

    private sealed class DribbleSource : SaveSource
    {
        private readonly byte[] _data;
        private readonly int _piece;
        private int _pos;

        public DribbleSource(byte[] data, int piece)
        {
            _data = data;
            _piece = piece;
        }

        public override int Read(Span<byte> buffer)
        {
            int n = Math.Min(Math.Min(buffer.Length, _piece), _data.Length - _pos);
            _data.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
    }

    [Fact]
    public void RoundTrip_AllColumnTypes_BitExact()
    {
        SaveTables t = ContainerTestData.Sample();
        byte[] file = ContainerTestData.Write(t);

        Assert.Equal(LoadStatus.Ok, ContainerTestData.Read(file, _knownAaaaBbbb, out SaveTables? r));
        Assert.NotNull(r);
        Assert.Equal(4, r!.BlockCount);
        int a = r.FindBlock(ContainerTestData.Aaaa);
        Assert.True(r.TryGetColumn(a, 1, out ReadOnlySpan<byte> u8));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, u8.ToArray());
        Assert.True(r.TryGetColumn(a, 2, out ReadOnlySpan<sbyte> i8));
        Assert.Equal(new sbyte[] { -1, 0, 127, -128, 7, 9, 11 }, i8.ToArray());
        Assert.True(r.TryGetColumn(a, 3, out ReadOnlySpan<ushort> u16));
        Assert.Equal(new ushort[] { 0, 65535, 258 }, u16.ToArray());
        Assert.True(r.TryGetColumn(a, 4, out ReadOnlySpan<short> i16));
        Assert.Equal(new short[] { -32768, 32767, 5, -5 }, i16.ToArray());
        Assert.True(r.TryGetColumn(a, 5, out ReadOnlySpan<uint> u32));
        Assert.Equal(new uint[] { uint.MaxValue, 0, 1, 2, 3 }, u32.ToArray());
        Assert.True(r.TryGetColumn(a, 6, out ReadOnlySpan<int> i32));
        Assert.Equal(new[] { int.MinValue, int.MaxValue, -1 }, i32.ToArray());
        Assert.True(r.TryGetColumn(a, 7, out ReadOnlySpan<ulong> u64));
        Assert.Equal(new[] { ulong.MaxValue, 0UL, 42UL }, u64.ToArray());
        Assert.True(r.TryGetColumn(a, 8, out ReadOnlySpan<long> i64));
        Assert.Equal(new[] { long.MinValue, long.MaxValue, -42L }, i64.ToArray());
        Assert.True(r.TryGetColumn(a, 9, out ReadOnlySpan<float> f32));
        Assert.Equal(0x7FC12345, BitConverter.SingleToInt32Bits(f32[0]));
        Assert.Equal(BitConverter.SingleToInt32Bits(-0.0f), BitConverter.SingleToInt32Bits(f32[1]));
        Assert.Equal(1.5f, f32[2]);
        Assert.True(r.TryGetColumn(a, 10, out ReadOnlySpan<double> f64));
        Assert.Equal(0x7FF8000000ABCDEFL, BitConverter.DoubleToInt64Bits(f64[0]));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits(f64[1]));
        Assert.True(r.TryGetColumn(a, 11, out ReadOnlySpan<byte> empty));
        Assert.Equal(0, empty.Length);
        Assert.True(r.TryGetColumnBytes(a, 12, out ReadOnlySpan<byte> raw));
        Assert.Equal(60, raw.Length);

        int b = r.FindBlock(ContainerTestData.Bbbb);
        Assert.True(r.TryGetColumn(b, 1, out ReadOnlySpan<int> bi));
        Assert.Equal(new[] { 10, 20, 30 }, bi.ToArray());
        Assert.False(r.TryGetColumn(b, 1, out ReadOnlySpan<uint> _)); // тип не совпал
        Assert.False(r.TryGetColumn(b, 99, out ReadOnlySpan<int> _)); // столбца нет

        int h = r.FindBlock(ContainerTestData.Host);
        Assert.Equal(new byte[] { 9, 8, 7, 6, 5, 4, 3 }, r.OpaquePayload(h).ToArray());
        Assert.Equal(1, r.KeptBlocks); // MODS уже помечен kept

        Assert.Equal(1, r.Header.WorldVersion);
        Assert.Equal(4800, r.Header.Tick);
        Assert.Equal(0x0123456789ABCDEFUL, r.Header.StateHash);
        Assert.Equal(3, r.Header.HashVersion);
        Assert.Equal(file.Length, r.Header.TotalLength);
    }

    [Fact]
    public void Canonical_SameTables_SameBytes_RegardlessOfDestinationContents()
    {
        byte[] a = ContainerTestData.Write(ContainerTestData.Sample(5));
        byte[] b = ContainerTestData.Write(ContainerTestData.Sample(5));
        Assert.Equal(a, b);

        SaveTables t = ContainerTestData.Sample(5);
        byte[] dirty = new byte[a.Length + 100];
        Array.Fill(dirty, (byte)0xCD);
        var h = ContainerTestData.Header();
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, in h, dirty, out long written));
        Assert.Equal(a.Length, written);
        Assert.Equal(a, dirty.AsSpan(0, (int)written).ToArray());

        Assert.NotEqual(a, ContainerTestData.Write(ContainerTestData.Sample(6)));
    }

    [Fact]
    public void Resave_OfReadTables_IsByteIdentical()
    {
        byte[] file = ContainerTestData.Write(ContainerTestData.Sample(3, bigCount: 1000));
        Assert.Equal(LoadStatus.Ok, ContainerTestData.Read(file, _knownAaaaBbbb, out SaveTables? r));
        Assert.Equal(file, ContainerTestData.Write(r!));
    }

    [Fact]
    public void Layout_Is8ByteAligned_AndContiguous()
    {
        byte[] file = ContainerTestData.Write(ContainerTestData.Sample());
        Assert.Equal(0, file.Length % 8);
        long expected = 64 + (4 * 32);
        foreach ((long off, long len) in ContainerTestData.TocRanges(file))
        {
            Assert.Equal(expected, off);
            Assert.Equal(0, off % 8);
            expected = off + ((len + 8 + 7) & ~7L);
        }

        Assert.Equal(file.Length - 16, expected);
        Assert.True(file.AsSpan(0, 8).SequenceEqual("TCSAVE\r\n"u8));
        Assert.True(file.AsSpan(file.Length - 16, 8).SequenceEqual("TCEND\0\0\0"u8));
        Assert.Equal((ulong)file.Length, BinaryPrimitives.ReadUInt64LittleEndian(file.AsSpan(40)));
    }

    [Fact]
    public void Sink_ProducesSameBytes_InChunksUpTo1MB()
    {
        SaveTables t = ContainerTestData.Sample(2, bigCount: 400_000); // 3.2 МБ в одном столбце
        byte[] viaSpan = ContainerTestData.Write(t);
        var sink = new ListSink();
        var h = ContainerTestData.Header();
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, in h, sink));
        Assert.Equal(viaSpan, sink.Bytes.ToArray());
        Assert.InRange(sink.MaxPiece, 1, 1 << 20);
        Assert.True(sink.Calls >= 3);
    }

    [Fact]
    public void BufferTooSmall_WritesNothing_AndMeasureIsExact()
    {
        SaveTables t = ContainerTestData.Sample();
        var h = ContainerTestData.Header();
        Assert.Equal(SaveStatus.Ok, SaveWriter.Measure(ref t, out long len));
        byte[] small = new byte[len - 1];
        Assert.Equal(SaveStatus.BufferTooSmall, SaveWriter.Write(ref t, in h, small, out long written));
        Assert.Equal(0, written);
        Assert.All(small, x => Assert.Equal(0, x));
        byte[] exact = new byte[len];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, in h, exact, out written));
        Assert.Equal(len, written);
    }

    [Fact]
    public void InvalidSources_AreRejected_NotWritten()
    {
        var h = ContainerTestData.Header();
        byte[] buf = new byte[4096];

        var unsorted = new SaveTables();
        unsorted.AddBlock(ContainerTestData.Aaaa, 1, BlockFlags.None);
        unsorted.AddColumn(2, new[] { 1 });
        unsorted.AddColumn(1, new[] { 1 });
        Assert.Equal(SaveStatus.InvalidTables, SaveWriter.Write(ref unsorted, in h, buf, out _));

        var dup = new SaveTables();
        dup.AddBlock(ContainerTestData.Aaaa, 1, BlockFlags.None);
        dup.AddBlock(ContainerTestData.Aaaa, 1, BlockFlags.None);
        Assert.Equal(SaveStatus.InvalidTables, SaveWriter.Write(ref dup, in h, buf, out _));

        var badElem = new SaveTables();
        badElem.AddBlock(ContainerTestData.Aaaa, 1, BlockFlags.None);
        badElem.AddColumnBytes(1, ColumnType.I32, 8, 1, new byte[8]);
        Assert.Equal(SaveStatus.InvalidTables, SaveWriter.Write(ref badElem, in h, buf, out _));

        SaveTables ok = ContainerTestData.Sample();
        SaveHeaderInfo badWriter = h;
        badWriter.Writer = 7;
        Assert.Equal(SaveStatus.InvalidTables, SaveWriter.Write(ref ok, in badWriter, buf, out _));
    }

    [Fact]
    public void UnknownBlocks_KeptOrRejected_AndSurviveResave()
    {
        var t = new SaveTables();
        t.AddBlock(ContainerTestData.Aaaa, 1, BlockFlags.Critical);
        t.AddColumn(1, new[] { 1, 2 });
        uint future = SaveFormat.FourCc("FUTR"u8);
        t.AddBlock(future, 9, BlockFlags.None);
        t.AddColumn(5, new long[] { 77 });
        byte[] file = ContainerTestData.Write(t);

        // Для читателя, не знающего FUTR: блок без critical хранится как есть, переписывается побайтно с флагом kept.
        Assert.Equal(LoadStatus.Ok, ContainerTestData.Read(file, new[] { ContainerTestData.Aaaa }, out SaveTables? r));
        Assert.Equal(1, r!.KeptBlocks);
        int f = r.FindBlock(future);
        Assert.Equal(BlockFlags.Kept, r.BlockFlagsOf(f));
        byte[] again = ContainerTestData.Write(r);
        Assert.Equal(LoadStatus.Ok, ContainerTestData.Read(again, new[] { ContainerTestData.Aaaa }, out SaveTables? r2));
        Assert.Equal(again, ContainerTestData.Write(r2!));
        Assert.Equal(r.OpaquePayload(f).ToArray(), r2!.OpaquePayload(r2.FindBlock(future)).ToArray());

        // Тот же блок с critical - отказ.
        var t2 = new SaveTables();
        t2.AddBlock(future, 9, BlockFlags.Critical);
        byte[] crit = ContainerTestData.Write(t2);
        LoadStatus st = SaveReader.Read(crit, new[] { ContainerTestData.Aaaa }, out SaveTables? none, out LoadReport rep);
        Assert.Equal(LoadStatus.UnknownCriticalBlock, st);
        Assert.Null(none);
        Assert.Equal(future, rep.FailedBlock);
        Assert.Equal(LoadStatus.UnknownCriticalBlock, rep.Status);
    }

    [Fact]
    public void Inspect_ReadsHeader_FromFirst64Bytes()
    {
        byte[] file = ContainerTestData.Write(ContainerTestData.Sample());
        Assert.Equal(LoadStatus.Ok, SaveReader.Inspect(file.AsSpan(0, 64), out SaveHeaderInfo i));
        Assert.Equal(1, i.ContainerVersion);
        Assert.Equal(1, i.WorldVersion);
        Assert.Equal(3, i.HashVersion);
        Assert.Equal(4, i.BlockCount);
        Assert.Equal(1, i.Writer);
        Assert.Equal((1 << 20) | (2 << 10) | 3, i.WriterVersion);
        Assert.Equal(4800, i.Tick);
        Assert.Equal(file.Length, i.TotalLength);
        Assert.Equal(LoadStatus.Truncated, SaveReader.Inspect(file.AsSpan(0, 63), out _));
        Assert.Equal(LoadStatus.NotASave, SaveReader.Inspect("not a save at all, definitely not, nope nope nope nope nope nope nope!"u8, out _));
        Assert.Equal(LoadStatus.NotASave, SaveReader.Inspect("xyz"u8, out _));
    }

    [Fact]
    public void Source_Stream_ReadsSameAsSpan_ForAnyPieceSize()
    {
        byte[] file = ContainerTestData.Write(ContainerTestData.Sample(4, bigCount: 5000));
        foreach (int piece in new[] { 1, 7, 64, 1000, 1 << 20, int.MaxValue })
        {
            LoadStatus st = SaveReader.Read(new DribbleSource(file, piece), _knownAaaaBbbb, out SaveTables? r, out LoadReport rep);
            Assert.Equal(LoadStatus.Ok, st);
            Assert.Equal(file, ContainerTestData.Write(r!));
            Assert.Equal(1, rep.FileWorldVersion);
        }
    }

    [Fact]
    public void Source_Truncated_And_HugeTotalLength_DoNotAllocateByClaim()
    {
        byte[] file = ContainerTestData.Write(ContainerTestData.Sample());
        Assert.Equal(LoadStatus.Truncated, SaveReader.Read(new DribbleSource(file.AsSpan(0, file.Length - 1).ToArray(), 100), null, out _, out _));
        Assert.Equal(LoadStatus.Truncated, SaveReader.Read(new DribbleSource(file.AsSpan(0, 30).ToArray(), 100), null, out _, out _));
        Assert.Equal(LoadStatus.Truncated, SaveReader.Read(new DribbleSource(Array.Empty<byte>(), 100), null, out _, out _));

        byte[] huge = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(huge.AsSpan(40), 1UL << 40);
        Assert.Equal(LoadStatus.CapacityExceeded, SaveReader.Read(new DribbleSource(huge, 100), null, out _, out _));
        BinaryPrimitives.WriteUInt64LittleEndian(huge.AsSpan(40), 1UL << 62);
        Assert.Equal(LoadStatus.CapacityExceeded, SaveReader.Read(new DribbleSource(huge, 100), null, out _, out _));
        BinaryPrimitives.WriteUInt64LittleEndian(huge.AsSpan(40), (ulong)file.Length + 5000);
        Assert.Equal(LoadStatus.Truncated, SaveReader.Read(new DribbleSource(huge, 100), null, out _, out _));
    }

    [Fact]
    public void TrailingBytesAfterTotalLength_AreIgnored()
    {
        byte[] file = ContainerTestData.Write(ContainerTestData.Sample());
        byte[] padded = new byte[file.Length + 40];
        file.CopyTo(padded, 0);
        Array.Fill(padded, (byte)0xEE, file.Length, 40);
        Assert.Equal(LoadStatus.Ok, ContainerTestData.Read(padded, _knownAaaaBbbb, out _));
    }

    [Fact]
    public void BadHeaderFields_GiveSpecificCodes()
    {
        byte[] file = ContainerTestData.Write(ContainerTestData.Sample());

        byte[] f = (byte[])file.Clone();
        f[0] = (byte)'X';
        Assert.Equal(LoadStatus.NotASave, ContainerTestData.Read(f, null, out _));

        f = (byte[])file.Clone();
        f[8] = 2;
        Assert.Equal(LoadStatus.UnsupportedContainer, ContainerTestData.Read(f, null, out _));

        f = (byte[])file.Clone();
        f[10] = 96;
        Assert.Equal(LoadStatus.UnsupportedContainer, ContainerTestData.Read(f, null, out _));

        f = (byte[])file.Clone();
        f[50] = 1; // reserved
        Assert.Equal(LoadStatus.InvalidValue, ContainerTestData.Read(f, null, out _));

        f = (byte[])file.Clone();
        f[48] = 3; // неизвестный писатель
        Assert.Equal(LoadStatus.InvalidValue, ContainerTestData.Read(f, null, out _));

        f = (byte[])file.Clone();
        f[20] ^= 1; // tick: защищён хэшем заголовка
        Assert.Equal(LoadStatus.ChecksumMismatch, ContainerTestData.Read(f, null, out _));
    }

    [Fact]
    public void BadColumnCatalog_WithValidChecksums_GivesBadColumn()
    {
        byte[] file = ContainerTestData.Write(ContainerTestData.Sample());
        (long off, _) = ContainerTestData.TocRanges(file)[0];
        int catalog = (int)off + 8; // первая запись каталога блока AAAA

        void Mutate(Action<byte[]> edit, LoadStatus expected, uint expectedColumn)
        {
            byte[] f = (byte[])file.Clone();
            edit(f);
            ContainerTestData.Rehash(f);
            LoadStatus s = SaveReader.Read(f, _knownAaaaBbbb, out SaveTables? t, out LoadReport rep);
            Assert.Equal(expected, s);
            Assert.Null(t);
            Assert.Equal(ContainerTestData.Aaaa, rep.FailedBlock);
            Assert.Equal(expectedColumn, rep.FailedColumn);
        }

        // count = 2^60 (защита от гигантской аллокации)
        Mutate(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(catalog + 16), 1UL << 60), LoadStatus.BadColumn, 1);
        // смещение столбца не по канону
        Mutate(f => BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(catalog + 8), 8), LoadStatus.BadColumn, 1);
        // elem_size не соответствует типу
        Mutate(f => f[catalog + 5] = 3, LoadStatus.BadColumn, 1);
        // неизвестный тип
        Mutate(f => f[catalog + 4] = 99, LoadStatus.BadColumn, 1);
        // reserved столбца
        Mutate(f => f[catalog + 6] = 1, LoadStatus.BadColumn, 1);
        // id столбцов не по возрастанию
        Mutate(f => BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(catalog + 24), 1), LoadStatus.BadColumn, 1);

        // column_count больше, чем помещается в блок
        byte[] g = (byte[])file.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(g.AsSpan((int)off), uint.MaxValue);
        ContainerTestData.Rehash(g);
        Assert.Equal(LoadStatus.BadColumn, SaveReader.Read(g, _knownAaaaBbbb, out _, out LoadReport r2));
        Assert.Equal(ContainerTestData.Aaaa, r2.FailedBlock);
    }

    [Fact]
    public void BadToc_WithValidHeaderHash_GivesReferenceErrors()
    {
        byte[] file = ContainerTestData.Write(ContainerTestData.Sample());

        void MutateToc(Action<byte[]> edit, LoadStatus expected)
        {
            byte[] f = (byte[])file.Clone();
            edit(f);
            ContainerTestData.Rehash(f);
            Assert.Equal(expected, SaveReader.Read(f, _knownAaaaBbbb, out SaveTables? t, out _));
            Assert.Null(t);
        }

        // Entry 1 начинается на 8 дальше, чем конец блока 0 (дыра).
        MutateToc(f => BinaryPrimitives.WriteInt64LittleEndian(f.AsSpan(64 + 32 + 8), ContainerTestData.TocRanges(file)[1].Offset + 8), LoadStatus.InvalidReference);
        // Перекрытие: entry 1 начинается раньше.
        MutateToc(f => BinaryPrimitives.WriteInt64LittleEndian(f.AsSpan(64 + 32 + 8), ContainerTestData.TocRanges(file)[1].Offset - 8), LoadStatus.InvalidReference);
        // Смещение за концом файла.
        MutateToc(f => BinaryPrimitives.WriteInt64LittleEndian(f.AsSpan(64 + 32 + 8), 1L << 40), LoadStatus.Truncated);
        // Длина блока за концом файла.
        MutateToc(f => BinaryPrimitives.WriteInt64LittleEndian(f.AsSpan(64 + 16), 1L << 40), LoadStatus.Truncated);
        // Длина блока на 8 короче: следующий блок начнётся не там.
        MutateToc(f => BinaryPrimitives.WriteInt64LittleEndian(f.AsSpan(64 + 16), ContainerTestData.TocRanges(file)[0].Length - 8), LoadStatus.InvalidReference);
        // Повтор id блока.
        MutateToc(f => f.AsSpan(64, 4).CopyTo(f.AsSpan(64 + 32, 4)), LoadStatus.InvalidReference);
        // Неизвестный бит флагов.
        MutateToc(f => f[64 + 7] = 0x80, LoadStatus.InvalidValue);
    }

    [Fact]
    public void Write_ToSpan_Allocates_ZeroBytes()
    {
        SaveTables t = ContainerTestData.Sample(1, bigCount: 10_000);
        var h = ContainerTestData.Header();
        SaveWriter.Measure(ref t, out long len);
        byte[] dest = new byte[len];

        long bytes = AllocationProbe.Measure(() =>
        {
            for (int i = 0; i < 20; i++)
            {
                SaveWriter.Write(ref t, in h, dest, out _);
            }
        });

        Assert.Equal(0, bytes);
        Assert.Equal(LoadStatus.Ok, ContainerTestData.Read(dest, _knownAaaaBbbb, out _));
    }
}
