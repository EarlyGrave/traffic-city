#pragma warning disable CA1861 // тестовые литералы-массивы вызываются один раз
using System;
using System.Buffers.Binary;
using Sim.Core.Api;
using Sim.Core.Save;
using Sim.Core.Util;

namespace Sim.Core.Tests.Save;

/// <summary>Образцы таблиц и вспомогательные операции над байтами контейнера для тестов.</summary>
internal static class ContainerTestData
{
    public static readonly uint Aaaa = SaveFormat.FourCc("AAAA"u8);
    public static readonly uint Bbbb = SaveFormat.FourCc("BBBB"u8);
    public static readonly uint Host = SaveFormat.FourCc("HOST"u8);
    public static readonly uint Mods = SaveFormat.FourCc("MODS"u8);

    public static SaveHeaderInfo Header() => new()
    {
        WorldVersion = 1,
        Tick = 4800,
        StateHash = 0x0123456789ABCDEFUL,
        HashVersion = 3,
        Writer = SaveFormat.WriterCSharp,
        WriterVersion = (1 << 20) | (2 << 10) | 3,
    };

    /// <summary>Блок AAAA со столбцами всех типов (в т.ч. пустой и raw), блок BBBB, блок хоста и блок мода.</summary>
    public static SaveTables Sample(int seed = 1, int bigCount = 0)
    {
        var rng = new Random(seed);
        var t = new SaveTables();
        t.AddBlock(Aaaa, 1, BlockFlags.Critical);
        t.AddColumn(1, new byte[] { 1, 2, 3, 4, 5 });
        t.AddColumn(2, new sbyte[] { -1, 0, 127, -128, 7, 9, 11 });
        t.AddColumn(3, new ushort[] { 0, 65535, 258 });
        t.AddColumn(4, new short[] { -32768, 32767, 5, -5 });
        t.AddColumn(5, new uint[] { uint.MaxValue, 0, 1, 2, 3 });
        t.AddColumn(6, new[] { int.MinValue, int.MaxValue, -1 });
        t.AddColumn(7, new[] { ulong.MaxValue, 0UL, 42UL });
        t.AddColumn(8, new[] { long.MinValue, long.MaxValue, -42L });
        // NaN с payload и -0 должны сохраниться битами.
        t.AddColumn(9, new[] { BitConverter.Int32BitsToSingle(0x7FC12345), -0.0f, 1.5f, float.NegativeInfinity });
        t.AddColumn(10, new[] { BitConverter.Int64BitsToDouble(0x7FF8000000ABCDEFL), -0.0, Math.PI });
        t.AddColumn(11, Array.Empty<byte>());
        byte[] raw = new byte[12 * 5];
        rng.NextBytes(raw);
        t.AddColumnBytes(12, ColumnType.Raw, 12, 5, raw);
        if (bigCount > 0)
        {
            ulong[] big = new ulong[bigCount];
            for (int i = 0; i < big.Length; i++)
            {
                big[i] = (ulong)rng.NextInt64();
            }

            t.AddColumn(13, big);
        }

        t.AddBlock(Bbbb, 2, BlockFlags.None);
        t.AddColumn(1, new[] { 10, 20, 30 });
        t.AddOpaqueBlock(Host, 1, BlockFlags.Host, new byte[] { 9, 8, 7, 6, 5, 4, 3 });
        t.AddOpaqueBlock(Mods, 4, BlockFlags.Kept, new byte[16]);
        return t;
    }

    public static byte[] Write(SaveTables t, int worldVersion = 1)
    {
        var h = Header();
        h.WorldVersion = worldVersion;
        Assert.Equal(SaveStatus.Ok, SaveWriter.Measure(ref t, out long len));
        byte[] buf = new byte[len];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, in h, buf, out long written));
        Assert.Equal(len, written);
        return buf;
    }

    public static (long Offset, long Length)[] TocRanges(byte[] file)
    {
        int n = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(36));
        var r = new (long, long)[n];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> e = file.AsSpan(64 + (i * 32), 32);
            r[i] = (BinaryPrimitives.ReadInt64LittleEndian(e.Slice(8)), BinaryPrimitives.ReadInt64LittleEndian(e.Slice(16)));
        }

        return r;
    }

    /// <summary>Пересчитать все контрольные суммы (блоки, заголовок+оглавление, подвал), не проверяя осмысленность полей.</summary>
    public static void Rehash(byte[] f)
    {
        int n = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(36)), (uint)((f.Length - 80) / 32));
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> e = f.AsSpan(64 + (i * 32), 32);
            ulong off = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(8));
            ulong len = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(16));
            if (off < (ulong)f.Length && len < (ulong)f.Length && off + len + 8 <= (ulong)f.Length)
            {
                ulong h = XxHash64.Hash(f.AsSpan((int)off, (int)len));
                BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan((int)(off + len)), h);
            }
        }

        var th = XxHash64.Create();
        th.Append(f.AsSpan(0, 56));
        th.Append(f.AsSpan(64, n * 32));
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(56), th.Value);
        int end = f.Length - 16;
        BinaryPrimitives.WriteUInt64LittleEndian(f.AsSpan(end + 8), XxHash64.Hash(f.AsSpan(0, end)));
    }

    public static LoadStatus Read(byte[] file, uint[]? known, out SaveTables? tables)
    {
        LoadStatus s = SaveReader.Read(file, known, out tables, out LoadReport r);
        Assert.Equal(s, r.Status == LoadStatus.Ok && s == LoadStatus.Ok ? LoadStatus.Ok : s);
        return s;
    }
}
