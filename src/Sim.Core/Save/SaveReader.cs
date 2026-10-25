using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Save;

/// <summary>
/// Читатель контейнера сейва (save-format.md §2). Любой вход (обрезанный, испорченный, чужой) даёт код <see cref="LoadStatus"/>
/// и место поломки в <see cref="LoadReport"/>; исключений наружу нет (<c>try/catch</c> как способ валидации не используется:
/// каждое смещение и длина сверяются до обращения, арифметика - в <c>long</c>/<c>ulong</c> после проверки пределов).
/// Аллокации по данным файла - только после сверки с реальной длиной входа.
/// </summary>
internal static class SaveReader
{
    // Практический предел одного массива .NET; контейнер больше читается только потоково (на стороне мира), здесь - CapacityExceeded.
    private const long MaxFile = 0x7FFFFFC7;

    /// <summary>Заголовок по первым 64 байтам (без проверки оглавления и хэшей): список сейвов, предпросмотр.</summary>
    public static LoadStatus Inspect(ReadOnlySpan<byte> head, out SaveHeaderInfo info) => ParseHeader(head, out info);

    /// <summary>
    /// Прочитать контейнер из памяти. <paramref name="knownBlocks"/> - FourCC блоков, которые знает версия мира: их каталог
    /// столбцов разбирается и проверяется; остальные без флага <c>critical</c> сохраняются как <c>kept</c>, с флагом - ошибка.
    /// <c>null</c> - все блоки без флагов <c>host</c>/<c>kept</c> считаются известными (проверка самого контейнера).
    /// </summary>
    public static LoadStatus Read(ReadOnlySpan<byte> src, uint[]? knownBlocks, out SaveTables? tables, out LoadReport report)
    {
        tables = null;
        report = default;
        LoadStatus st = ParseHeader(src, out SaveHeaderInfo h);
        report.FileWorldVersion = h.WorldVersion;
        if (st != LoadStatus.Ok)
        {
            return Fail(ref report, st);
        }

        if (h.TotalLength > src.Length)
        {
            return Fail(ref report, LoadStatus.Truncated);
        }

        if (h.TotalLength > MaxFile)
        {
            return Fail(ref report, LoadStatus.CapacityExceeded);
        }

        byte[] file = src.Slice(0, (int)h.TotalLength).ToArray();
        return Parse(file, in h, knownBlocks, out tables, ref report);
    }

    /// <summary>Прочитать контейнер из потока. Читает ровно <c>total_length</c> байт; буфер растёт по мере поступления данных.</summary>
    public static LoadStatus Read(SaveSource src, uint[]? knownBlocks, out SaveTables? tables, out LoadReport report)
    {
        tables = null;
        report = default;
        byte[] buf = new byte[SaveFormat.SinkChunk];
        int have = FillFrom(src, buf, 0, SaveFormat.HeaderSize);
        LoadStatus st = ParseHeader(buf.AsSpan(0, have), out SaveHeaderInfo h);
        report.FileWorldVersion = h.WorldVersion;
        if (st != LoadStatus.Ok)
        {
            return Fail(ref report, st);
        }

        if (h.TotalLength > MaxFile)
        {
            return Fail(ref report, LoadStatus.CapacityExceeded);
        }

        int total = (int)h.TotalLength;
        while (have < total)
        {
            if (have == buf.Length)
            {
                Array.Resize(ref buf, (int)Math.Min((long)buf.Length * 2, total));
            }

            int want = Math.Min(buf.Length, total) - have;
            int got = FillFrom(src, buf, have, want);
            if (got <= 0)
            {
                return Fail(ref report, LoadStatus.Truncated);
            }

            have += got;
        }

        if (buf.Length != total)
        {
            Array.Resize(ref buf, total);
        }

        return Parse(buf, in h, knownBlocks, out tables, ref report);
    }

    private static int FillFrom(SaveSource src, byte[] buf, int start, int count)
    {
        int have = 0;
        while (have < count)
        {
            int got = src.Read(buf.AsSpan(start + have, count - have));
            if (got <= 0)
            {
                break;
            }

            have += got;
        }

        return have;
    }

    private static LoadStatus ParseHeader(ReadOnlySpan<byte> h, out SaveHeaderInfo info)
    {
        info = default;
        int prefix = Math.Min(h.Length, SaveFormat.Magic.Length);
        if (!h.Slice(0, prefix).SequenceEqual(SaveFormat.Magic.Slice(0, prefix)))
        {
            return LoadStatus.NotASave;
        }

        if (h.Length < SaveFormat.HeaderSize)
        {
            return LoadStatus.Truncated;
        }

        if (!BitConverter.IsLittleEndian)
        {
            return LoadStatus.NotSupported;
        }

        info.ContainerVersion = BinaryPrimitives.ReadUInt16LittleEndian(h.Slice(8));
        int headerSize = BinaryPrimitives.ReadUInt16LittleEndian(h.Slice(10));
        info.WorldVersion = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(12));
        info.Tick = BinaryPrimitives.ReadInt64LittleEndian(h.Slice(16));
        info.StateHash = BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(24));
        info.HashVersion = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(32));
        uint blockCount = BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(36));
        ulong total = BinaryPrimitives.ReadUInt64LittleEndian(h.Slice(40));
        info.Writer = BinaryPrimitives.ReadUInt16LittleEndian(h.Slice(48));
        int reserved = BinaryPrimitives.ReadUInt16LittleEndian(h.Slice(50));
        info.WriterVersion = (int)BinaryPrimitives.ReadUInt32LittleEndian(h.Slice(52));

        if (info.ContainerVersion != SaveFormat.ContainerVersion || headerSize != SaveFormat.HeaderSize)
        {
            return LoadStatus.UnsupportedContainer;
        }

        if (reserved != 0 || (info.Writer != SaveFormat.WriterCSharp && info.Writer != SaveFormat.WriterRust))
        {
            return LoadStatus.InvalidValue;
        }

        // 64 + 32 * block_count + 16 <= total_length (в ulong без переполнения: block_count < 2^32).
        ulong minTotal = SaveFormat.HeaderSize + (ulong)blockCount * SaveFormat.TocEntrySize + SaveFormat.FooterSize;
        if (total < minTotal || total > long.MaxValue)
        {
            return LoadStatus.Truncated;
        }

        info.BlockCount = (int)Math.Min(blockCount, int.MaxValue);
        info.TotalLength = (long)total;
        return LoadStatus.Ok;
    }

    private static LoadStatus Parse(byte[] file, in SaveHeaderInfo h, uint[]? known, out SaveTables? tables, ref LoadReport report)
    {
        tables = null;
        ReadOnlySpan<byte> f = file;
        long total = file.Length;
        uint blockCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(f.Slice(36));
        int n = (int)blockCountRaw; // block_count * 32 <= total <= int.MaxValue, значит n безопасно помещается
        long tocEnd = SaveFormat.HeaderSize + (long)n * SaveFormat.TocEntrySize;

        // 1. Хэш заголовка и оглавления.
        XxHash64 th = XxHash64.Create();
        th.Append(f.Slice(0, 56));
        th.Append(f.Slice(SaveFormat.HeaderSize, n * SaveFormat.TocEntrySize));
        if (th.Value != BinaryPrimitives.ReadUInt64LittleEndian(f.Slice(56)))
        {
            return Fail(ref report, LoadStatus.ChecksumMismatch);
        }

        // 2. Оглавление: сплошная раскладка без дыр и перекрытий; хэши блоков.
        var result = new SaveTables { Header = h };
        var seen = new HashSet<uint>(n);
        long expected = tocEnd;
        long end = total - SaveFormat.FooterSize;
        var offsets = new long[n];
        var lengths = new long[n];
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> e = f.Slice(SaveFormat.HeaderSize + i * SaveFormat.TocEntrySize, SaveFormat.TocEntrySize);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(e);
            ushort schema = BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(4));
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(6));
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(8));
            ulong length = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(16));

            bool keptAndCritical = (flags & (ushort)BlockFlags.Kept) != 0 && (flags & (ushort)BlockFlags.Critical) != 0;
            if ((flags & ~(ushort)BlockFlags.All) != 0 || !IsZero(e.Slice(24, 8)) || keptAndCritical)
            {
                return Fail(ref report, LoadStatus.InvalidValue, id);
            }

            if (!seen.Add(id) || offset != (ulong)expected)
            {
                return Fail(ref report, offset > (ulong)end ? LoadStatus.Truncated : LoadStatus.InvalidReference, id);
            }

            if (length > (ulong)end || (long)offset + SaveFormat.BlockSpan((long)length) > end)
            {
                return Fail(ref report, LoadStatus.Truncated, id);
            }

            offsets[i] = (long)offset;
            lengths[i] = (long)length;
            expected = (long)offset + SaveFormat.BlockSpan((long)length);

            ReadOnlySpan<byte> payload = f.Slice((int)offset, (int)length);
            if (XxHash64.Hash(payload) != BinaryPrimitives.ReadUInt64LittleEndian(f.Slice((int)offset + (int)length)))
            {
                return Fail(ref report, LoadStatus.ChecksumMismatch, id);
            }

            ReadOnlySpan<byte> pad = f.Slice((int)offset + (int)length + SaveFormat.HashSize, (int)expected - (int)offset - (int)length - SaveFormat.HashSize);
            if (!IsZero(pad))
            {
                return Fail(ref report, LoadStatus.InvalidValue, id);
            }
        }

        if (expected != end)
        {
            return Fail(ref report, LoadStatus.InvalidReference);
        }

        // 3. Подвал: magic и xxh64 всего файла.
        if (!f.Slice((int)end, 8).SequenceEqual(SaveFormat.FooterMagic)
            || XxHash64.Hash(f.Slice(0, (int)end)) != BinaryPrimitives.ReadUInt64LittleEndian(f.Slice((int)end + 8)))
        {
            return Fail(ref report, LoadStatus.ChecksumMismatch);
        }

        // 4. Классификация блоков и каталоги столбцов.
        for (int i = 0; i < n; i++)
        {
            ReadOnlySpan<byte> e = f.Slice(SaveFormat.HeaderSize + i * SaveFormat.TocEntrySize, SaveFormat.TocEntrySize);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(e);
            ushort schema = BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(4));
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(6));
            var payload = new ReadOnlyMemory<byte>(file, (int)offsets[i], (int)lengths[i]);

            if (SaveFormat.IsBlockOpaque(flags))
            {
                if ((flags & (ushort)BlockFlags.Kept) != 0)
                {
                    result.KeptBlocks++;
                }

                result.AddOpaqueBlock(id, schema, (BlockFlags)flags, payload);
                continue;
            }

            if (known != null && Array.IndexOf(known, id) < 0)
            {
                if ((flags & (ushort)BlockFlags.Critical) != 0)
                {
                    return Fail(ref report, LoadStatus.UnknownCriticalBlock, id);
                }

                result.KeptBlocks++;
                result.AddOpaqueBlock(id, schema, (BlockFlags)flags | BlockFlags.Kept, payload);
                continue;
            }

            LoadStatus cs = ParseCatalog(file, offsets[i], lengths[i], id, schema, (BlockFlags)flags, result, ref report);
            if (cs != LoadStatus.Ok)
            {
                return cs;
            }
        }

        report.Status = LoadStatus.Ok;
        report.KeptBlocks = result.KeptBlocks;
        tables = result;
        return LoadStatus.Ok;
    }

    private static LoadStatus ParseCatalog(byte[] file, long blockOffset, long length, uint id, ushort schema, BlockFlags flags, SaveTables t, ref LoadReport report)
    {
        ReadOnlySpan<byte> p = file.AsSpan((int)blockOffset, (int)length);
        if (p.Length < SaveFormat.CatalogHeaderSize)
        {
            return Fail(ref report, LoadStatus.BadColumn, id);
        }

        uint cc = BinaryPrimitives.ReadUInt32LittleEndian(p);
        uint reserved = BinaryPrimitives.ReadUInt32LittleEndian(p.Slice(4));
        ulong catalogEnd = SaveFormat.CatalogHeaderSize + (ulong)cc * SaveFormat.ColumnEntrySize;
        if (reserved != 0 || catalogEnd > (ulong)p.Length)
        {
            return Fail(ref report, LoadStatus.BadColumn, id);
        }

        t.AddBlock(id, schema, flags);
        long expected = (long)catalogEnd;
        uint prev = 0;
        for (int c = 0; c < (int)cc; c++)
        {
            ReadOnlySpan<byte> ce = p.Slice(SaveFormat.CatalogHeaderSize + c * SaveFormat.ColumnEntrySize, SaveFormat.ColumnEntrySize);
            uint colId = BinaryPrimitives.ReadUInt32LittleEndian(ce);
            var type = (ColumnType)ce[4];
            int elem = ce[5];
            ushort res = BinaryPrimitives.ReadUInt16LittleEndian(ce.Slice(6));
            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(ce.Slice(8));
            ulong count = BinaryPrimitives.ReadUInt64LittleEndian(ce.Slice(16));

            if (res != 0 || !SaveFormat.IsValidColumn(type, elem) || (c > 0 && colId <= prev) || offset != (ulong)expected
                || count > (ulong)p.Length / (ulong)elem)
            {
                return Fail(ref report, LoadStatus.BadColumn, id, colId);
            }

            long bytes = (long)count * elem;
            long padded = SaveFormat.Align8(bytes);
            if (expected + padded > p.Length)
            {
                return Fail(ref report, LoadStatus.BadColumn, id, colId);
            }

            if (!IsZero(p.Slice((int)expected + (int)bytes, (int)(padded - bytes))))
            {
                return Fail(ref report, LoadStatus.BadColumn, id, colId);
            }

            t.AddColumnBytes(colId, type, elem, (long)count, new ReadOnlyMemory<byte>(file, (int)(blockOffset + expected), (int)bytes));
            prev = colId;
            expected += padded;
        }

        if (expected != p.Length)
        {
            return Fail(ref report, LoadStatus.BadColumn, id);
        }

        return LoadStatus.Ok;
    }

    private static bool IsZero(ReadOnlySpan<byte> s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static LoadStatus Fail(ref LoadReport r, LoadStatus s, uint block = 0, uint column = 0)
    {
        r.Status = s;
        r.FailedBlock = block;
        r.FailedColumn = column;
        return s;
    }
}
