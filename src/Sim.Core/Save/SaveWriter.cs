using System;
using System.Buffers.Binary;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Save;

/// <summary>
/// Писатель контейнера сейва (save-format.md §2): заголовок 64 Б, оглавление, блоки, подвал. Один код для записи в
/// <see cref="Span{T}"/> (0 аллокаций: хэши потоковые, заголовок собирается на стеке) и в <see cref="SaveSink"/> (один буфер
/// 1 МБ на вызов). Длины блоков известны из каталогов заранее, поэтому заголовок с xxh64 оглавления пишется первым.
/// Исключений не бросает (кроме исключений самого <see cref="SaveSink"/>).
/// </summary>
internal static class SaveWriter
{
    private const long MaxTotal = 1L << 60;

    /// <summary>Точная длина контейнера. Статус не <c>Ok</c> - источник описан неверно.</summary>
    public static SaveStatus Measure<TSrc>(ref TSrc src, out long length)
        where TSrc : ISaveSource
    {
        length = 0;
        SaveStatus st = Layout(ref src, out long total);
        if (st == SaveStatus.Ok)
        {
            length = total;
        }

        return st;
    }

    public static SaveStatus Write<TSrc>(ref TSrc src, in SaveHeaderInfo header, Span<byte> dest, out long written)
        where TSrc : ISaveSource
    {
        written = 0;
        SaveStatus st = Layout(ref src, out long total);
        if (st != SaveStatus.Ok)
        {
            return st;
        }

        st = CheckHeader(in header);
        if (st != SaveStatus.Ok)
        {
            return st;
        }

        if (total > dest.Length)
        {
            return SaveStatus.BufferTooSmall;
        }

        var o = new Output(dest, null);
        st = Emit(ref src, in header, total, ref o);
        if (st == SaveStatus.Ok)
        {
            written = o.Position;
        }

        return st;
    }

    public static SaveStatus Write<TSrc>(ref TSrc src, in SaveHeaderInfo header, SaveSink sink)
        where TSrc : ISaveSource
    {
        SaveStatus st = Layout(ref src, out long total);
        if (st != SaveStatus.Ok)
        {
            return st;
        }

        st = CheckHeader(in header);
        if (st != SaveStatus.Ok)
        {
            return st;
        }

        var o = new Output(default, sink);
        st = Emit(ref src, in header, total, ref o);
        o.Flush();
        return st;
    }

    private static SaveStatus CheckHeader(in SaveHeaderInfo h) =>
        BitConverter.IsLittleEndian && (h.Writer == SaveFormat.WriterCSharp || h.Writer == SaveFormat.WriterRust)
            ? SaveStatus.Ok
            : SaveStatus.InvalidTables;

    /// <summary>Проверка описания источника и вычисление полной длины файла.</summary>
    private static SaveStatus Layout<TSrc>(ref TSrc src, out long total)
        where TSrc : ISaveSource
    {
        total = 0;
        int n = src.BlockCount;
        if (n < 0 || n > (int.MaxValue - SaveFormat.HeaderSize) / SaveFormat.TocEntrySize)
        {
            return SaveStatus.InvalidTables;
        }

        long sum = SaveFormat.HeaderSize + (long)n * SaveFormat.TocEntrySize;
        for (int i = 0; i < n; i++)
        {
            SaveBlockDesc b = src.GetBlock(i);
            for (int j = 0; j < i; j++)
            {
                if (src.GetBlock(j).Id == b.Id)
                {
                    return SaveStatus.InvalidTables;
                }
            }

            SaveStatus st = PayloadLength(ref src, i, in b, out long len);
            if (st != SaveStatus.Ok)
            {
                return st;
            }

            sum += SaveFormat.BlockSpan(len);
            if (sum > MaxTotal)
            {
                return SaveStatus.InvalidTables;
            }
        }

        total = sum + SaveFormat.FooterSize;
        return SaveStatus.Ok;
    }

    private static SaveStatus PayloadLength<TSrc>(ref TSrc src, int block, in SaveBlockDesc b, out long length)
        where TSrc : ISaveSource
    {
        length = 0;
        if ((b.Flags & ~(ushort)BlockFlags.All) != 0)
        {
            return SaveStatus.InvalidTables;
        }

        if (SaveFormat.IsBlockOpaque(b.Flags))
        {
            if (b.RawLength < 0 || b.RawLength > MaxTotal)
            {
                return SaveStatus.InvalidHostBlock;
            }

            length = b.RawLength;
            return SaveStatus.Ok;
        }

        if (b.ColumnCount < 0 || b.ColumnCount > (int.MaxValue - SaveFormat.CatalogHeaderSize) / SaveFormat.ColumnEntrySize)
        {
            return SaveStatus.InvalidTables;
        }

        long len = SaveFormat.CatalogHeaderSize + (long)b.ColumnCount * SaveFormat.ColumnEntrySize;
        uint prev = 0;
        for (int c = 0; c < b.ColumnCount; c++)
        {
            SaveColumnDesc col = src.GetColumn(block, c);
            if (!SaveFormat.IsValidColumn(col.Type, col.ElemSize) || (c > 0 && col.Id <= prev) || col.Count < 0
                || col.Count > MaxTotal / col.ElemSize)
            {
                return SaveStatus.InvalidTables;
            }

            prev = col.Id;
            len += SaveFormat.Align8(col.Count * col.ElemSize);
            if (len > MaxTotal)
            {
                return SaveStatus.InvalidTables;
            }
        }

        length = len;
        return SaveStatus.Ok;
    }

    private static SaveStatus Emit<TSrc>(ref TSrc src, in SaveHeaderInfo h, long total, ref Output o)
        where TSrc : ISaveSource
    {
        int n = src.BlockCount;

        // Заголовок + оглавление: сначала хэш (нужен в заголовке), затем запись.
        Span<byte> hdr = stackalloc byte[SaveFormat.HeaderSize];
        hdr.Clear();
        SaveFormat.Magic.CopyTo(hdr);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(8), SaveFormat.ContainerVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(10), SaveFormat.HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(12), (uint)h.WorldVersion);
        BinaryPrimitives.WriteInt64LittleEndian(hdr.Slice(16), h.Tick);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(24), h.StateHash);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(32), (uint)h.HashVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(36), (uint)n);
        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(40), (ulong)total);
        BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(48), (ushort)h.Writer);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(52), (uint)h.WriterVersion);

        Span<byte> entry = stackalloc byte[SaveFormat.TocEntrySize];
        XxHash64 th = XxHash64.Create();
        th.Append(hdr.Slice(0, 56));
        long offset = SaveFormat.HeaderSize + (long)n * SaveFormat.TocEntrySize;
        for (int i = 0; i < n; i++)
        {
            SaveBlockDesc b = src.GetBlock(i);
            PayloadLength(ref src, i, in b, out long len);
            BuildEntry(entry, in b, offset, len);
            th.Append(entry);
            offset += SaveFormat.BlockSpan(len);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(hdr.Slice(56), th.Value);
        o.Put(hdr);

        offset = SaveFormat.HeaderSize + (long)n * SaveFormat.TocEntrySize;
        for (int i = 0; i < n; i++)
        {
            SaveBlockDesc b = src.GetBlock(i);
            PayloadLength(ref src, i, in b, out long len);
            BuildEntry(entry, in b, offset, len);
            o.Put(entry);
            offset += SaveFormat.BlockSpan(len);
        }

        for (int i = 0; i < n; i++)
        {
            SaveBlockDesc b = src.GetBlock(i);
            PayloadLength(ref src, i, in b, out long len);
            if (o.Position != OffsetOf(ref src, i, n))
            {
                return SaveStatus.InvalidTables;
            }

            o.BeginPayload();
            if (SaveFormat.IsBlockOpaque(b.Flags))
            {
                CopyRawChunks(ref src, i, len, ref o);
            }
            else
            {
                EmitCatalogBlock(ref src, i, in b, ref o);
            }

            o.EndPayload(len);
        }

        if (o.Position != total - SaveFormat.FooterSize)
        {
            return SaveStatus.InvalidTables;
        }

        o.PutFooter();
        return SaveStatus.Ok;
    }

    private static long OffsetOf<TSrc>(ref TSrc src, int block, int blockCount)
        where TSrc : ISaveSource
    {
        long offset = SaveFormat.HeaderSize + (long)blockCount * SaveFormat.TocEntrySize;
        for (int i = 0; i < block; i++)
        {
            SaveBlockDesc b = src.GetBlock(i);
            PayloadLength(ref src, i, in b, out long len);
            offset += SaveFormat.BlockSpan(len);
        }

        return offset;
    }

    private static void BuildEntry(Span<byte> e, in SaveBlockDesc b, long offset, long length)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(e, b.Id);
        BinaryPrimitives.WriteUInt16LittleEndian(e.Slice(4), b.Schema);
        BinaryPrimitives.WriteUInt16LittleEndian(e.Slice(6), b.Flags);
        BinaryPrimitives.WriteInt64LittleEndian(e.Slice(8), offset);
        BinaryPrimitives.WriteInt64LittleEndian(e.Slice(16), length);
        e.Slice(24, 8).Clear();
    }

    private static void EmitCatalogBlock<TSrc>(ref TSrc src, int block, in SaveBlockDesc b, ref Output o)
        where TSrc : ISaveSource
    {
        Span<byte> head = stackalloc byte[SaveFormat.CatalogHeaderSize];
        head.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(head, (uint)b.ColumnCount);
        o.Put(head);

        Span<byte> ce = stackalloc byte[SaveFormat.ColumnEntrySize];
        long dataOffset = SaveFormat.CatalogHeaderSize + (long)b.ColumnCount * SaveFormat.ColumnEntrySize;
        for (int c = 0; c < b.ColumnCount; c++)
        {
            SaveColumnDesc col = src.GetColumn(block, c);
            ce.Clear();
            BinaryPrimitives.WriteUInt32LittleEndian(ce, col.Id);
            ce[4] = (byte)col.Type;
            ce[5] = col.ElemSize;
            BinaryPrimitives.WriteInt64LittleEndian(ce.Slice(8), dataOffset);
            BinaryPrimitives.WriteInt64LittleEndian(ce.Slice(16), col.Count);
            o.Put(ce);
            dataOffset += SaveFormat.Align8(col.Count * col.ElemSize);
        }

        for (int c = 0; c < b.ColumnCount; c++)
        {
            SaveColumnDesc col = src.GetColumn(block, c);
            long bytes = col.Count * col.ElemSize;
            long done = 0;
            int maxN = o.MaxChunk / col.ElemSize * col.ElemSize;
            while (done < bytes)
            {
                int take = (int)Math.Min(bytes - done, maxN);
                Span<byte> dst = o.Reserve(take);
                src.CopyColumn(block, c, done, dst);
                o.Commit(take);
                done += take;
            }

            int pad = (int)(SaveFormat.Align8(bytes) - bytes);
            if (pad > 0)
            {
                Span<byte> z = o.Reserve(pad);
                z.Clear();
                o.Commit(pad);
            }
        }
    }

    private static void CopyRawChunks<TSrc>(ref TSrc src, int block, long length, ref Output o)
        where TSrc : ISaveSource
    {
        long done = 0;
        while (done < length)
        {
            int take = (int)Math.Min(length - done, o.MaxChunk);
            Span<byte> dst = o.Reserve(take);
            src.CopyRaw(block, done, dst);
            o.Commit(take);
            done += take;
        }
    }

    /// <summary>
    /// Выход: либо готовый <see cref="Span{T}"/> (куски пишутся прямо в него), либо буфер 1 МБ с выдачей в <see cref="SaveSink"/>.
    /// Ведёт xxh64 файла и текущего блока по мере записи.
    /// </summary>
    private ref struct Output
    {
        private readonly Span<byte> _dest;
        private readonly SaveSink? _sink;
        private readonly byte[]? _stage;
        private int _stagePos;
        private XxHash64 _file;
        private XxHash64 _block;
        private bool _inPayload;
        private int _reserved;

        public Output(Span<byte> dest, SaveSink? sink)
        {
            _dest = dest;
            _sink = sink;
            _stage = sink is null ? null : new byte[SaveFormat.SinkChunk];
            _stagePos = 0;
            Position = 0;
            _file = XxHash64.Create();
            _block = XxHash64.Create();
            _inPayload = false;
            _reserved = 0;
        }

        public long Position { get; private set; }

        public readonly int MaxChunk => _stage is null ? int.MaxValue / 2 : SaveFormat.SinkChunk;

        public Span<byte> Reserve(int n)
        {
            _reserved = n;
            if (_stage is null)
            {
                return _dest.Slice((int)Position, n);
            }

            if (_stagePos + n > _stage.Length)
            {
                Flush();
            }

            return _stage.AsSpan(_stagePos, n);
        }

        public void Commit(int n)
        {
            Span<byte> filled = _stage is null ? _dest.Slice((int)Position, n) : _stage.AsSpan(_stagePos, n);
            _file.Append(filled);
            if (_inPayload)
            {
                _block.Append(filled);
            }

            Position += n;
            _stagePos += _stage is null ? 0 : n;
        }

        public void Put(scoped ReadOnlySpan<byte> data)
        {
            data.CopyTo(Reserve(data.Length));
            Commit(data.Length);
        }

        public void BeginPayload()
        {
            _block = XxHash64.Create();
            _inPayload = true;
        }

        /// <summary>Хэш нагрузки и паддинг до 8 (хэш входит в xxh64 файла, но не в хэш блока).</summary>
        public void EndPayload(long payloadLength)
        {
            _inPayload = false;
            Span<byte> t = stackalloc byte[SaveFormat.HashSize];
            BinaryPrimitives.WriteUInt64LittleEndian(t, _block.Value);
            Put(t);
            int pad = (int)(SaveFormat.BlockSpan(payloadLength) - payloadLength - SaveFormat.HashSize);
            if (pad > 0)
            {
                Span<byte> z = Reserve(pad);
                z.Clear();
                Commit(pad);
            }
        }

        public void PutFooter()
        {
            Span<byte> f = stackalloc byte[SaveFormat.FooterSize];
            SaveFormat.FooterMagic.CopyTo(f);
            BinaryPrimitives.WriteUInt64LittleEndian(f.Slice(8), _file.Value);
            Put(f);
        }

        public void Flush()
        {
            if (_sink is not null && _stage is not null && _stagePos > 0)
            {
                _sink.Write(new ReadOnlySpan<byte>(_stage, 0, _stagePos));
                _stagePos = 0;
            }
        }
    }
}
