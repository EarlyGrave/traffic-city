using System;
using Sim.Core.Api;
using Sim.Core.Metrics;

namespace Sim.Core.Save;

/// <summary>Блок, не разобранный ядром и переписываемый в следующий сейв как есть (неизвестный необязательный блок загруженного файла).</summary>
internal readonly struct KeptBlock
{
    public KeptBlock(uint id, ushort schema, ushort flags, byte[] payload)
    {
        Id = id;
        Schema = schema;
        Flags = flags;
        Payload = payload;
    }

    public uint Id { get; }

    public ushort Schema { get; }

    public ushort Flags { get; }

    public byte[] Payload { get; }
}

/// <summary>Рабочие буферы сохранения: копия блоков хоста (спаны не хранятся в полях) и кэш набора блоков. Растут только при первом/большем вызове.</summary>
internal sealed class SaveScratch
{
    public BlockDef[]? CoreBlocks;
    public HostBlock[] Host = Array.Empty<HostBlock>();
    public byte[] HostData = Array.Empty<byte>();
}

/// <summary>Источник данных для <see cref="SaveWriter"/>: блоки ядра по реестру <see cref="SaveSchema"/>, затем блоки хоста, затем <c>kept</c>.</summary>
internal readonly struct WorldSaveSource : ISaveSource
{
    private readonly World _w;
    private readonly BlockDef[] _core;
    private readonly HostBlock[] _host;
    private readonly int _hostCount;
    private readonly byte[] _hostData;
    private readonly KeptBlock[] _kept;

    public WorldSaveSource(World w, BlockDef[] core, HostBlock[] host, int hostCount, byte[] hostData, KeptBlock[] kept)
    {
        _w = w;
        _core = core;
        _host = host;
        _hostCount = hostCount;
        _hostData = hostData;
        _kept = kept;
    }

    public int BlockCount => _core.Length + _hostCount + _kept.Length;

    public SaveBlockDesc GetBlock(int block)
    {
        if (block < _core.Length)
        {
            BlockDef b = _core[block];
            return new SaveBlockDesc { Id = b.Id, Schema = b.Schema, Flags = (ushort)BlockFlags.Critical, ColumnCount = b.Columns.Length };
        }

        block -= _core.Length;
        if (block < _hostCount)
        {
            HostBlock h = _host[block];
            return new SaveBlockDesc { Id = h.Id, Schema = 1, Flags = (ushort)((h.Flags & (ushort)BlockFlags.Critical) | (ushort)BlockFlags.Host), RawLength = h.Length };
        }

        KeptBlock k = _kept[block - _hostCount];
        return new SaveBlockDesc { Id = k.Id, Schema = k.Schema, Flags = k.Flags, RawLength = k.Payload.Length };
    }

    public SaveColumnDesc GetColumn(int block, int column)
    {
        ColumnDef c = _core[block].Columns[column];
        return new SaveColumnDesc { Id = c.Id, Type = c.Type, ElemSize = (byte)c.ElemSize, Count = c.Count(_w) };
    }

    public void CopyColumn(int block, int column, long byteOffset, Span<byte> dst)
    {
        ColumnDef c = _core[block].Columns[column];
        c.Copy(_w, byteOffset / c.ElemSize, dst.Length / c.ElemSize, dst);
    }

    public void CopyRaw(int block, long byteOffset, Span<byte> dst)
    {
        block -= _core.Length;
        if (block < _hostCount)
        {
            new ReadOnlySpan<byte>(_hostData, _host[block].Offset + (int)byteOffset, dst.Length).CopyTo(dst);
            return;
        }

        new ReadOnlySpan<byte>(_kept[block - _hostCount].Payload, (int)byteOffset, dst.Length).CopyTo(dst);
    }
}

/// <summary>Запись состояния мира в контейнер (save-format.md §6): сейв - снимок между <c>Step</c>, состояние не меняется.</summary>
internal static class WorldSaver
{
    public static SaveStatus Measure(World w, ReadOnlySpan<HostBlock> host, out long length)
    {
        length = 0;
        SaveStatus st = MakeSource(w, host, default, out WorldSaveSource src, checkData: false);
        return st != SaveStatus.Ok ? st : SaveWriter.Measure(ref src, out length);
    }

    public static SaveStatus Save(World w, Span<byte> dest, ReadOnlySpan<HostBlock> host, ReadOnlySpan<byte> hostData, out long written)
    {
        written = 0;
        SaveStatus st = MakeSource(w, host, hostData, out WorldSaveSource src);
        return st != SaveStatus.Ok ? st : SaveWriter.Write(ref src, Header(w), dest, out written);
    }

    public static SaveStatus Save(World w, SaveSink sink, ReadOnlySpan<HostBlock> host, ReadOnlySpan<byte> hostData)
    {
        SaveStatus st = MakeSource(w, host, hostData, out WorldSaveSource src);
        return st != SaveStatus.Ok ? st : SaveWriter.Write(ref src, Header(w), sink);
    }

    public static SaveHeaderInfo Header(World w) => new()
    {
        WorldVersion = SaveSchema.WorldVersion,
        HashVersion = SaveSchema.HashVersion,
        Tick = w.Tick,
        StateHash = StateHash.Compute(w),
        Writer = SaveFormat.WriterCSharp,
        WriterVersion = SaveSchema.WriterVersion,
    };

    private static SaveStatus MakeSource(World w, ReadOnlySpan<HostBlock> host, ReadOnlySpan<byte> hostData, out WorldSaveSource src, bool checkData = true)
    {
        src = default;
        SaveScratch sc = w.SaveScratch ??= new SaveScratch();
        BlockDef[] core = sc.CoreBlocks ??= SaveSchema.PresentBlocks(w).ToArray();

        // Блоки хоста: проверка и копия в рабочие буферы (спаны в полях структуры хранить нельзя; буферы растут только при необходимости).
        for (int i = 0; i < host.Length; i++)
        {
            HostBlock h = host[i];
            if (h.Offset < 0 || h.Length < 0 || (checkData && (long)h.Offset + h.Length > hostData.Length) || h.Reserved != 0 ||
                (h.Flags & ~(ushort)BlockFlags.Critical) != 0 || !IsHostId(h.Id))
            {
                return SaveStatus.InvalidHostBlock;
            }

            for (int j = 0; j < i; j++)
            {
                if (host[j].Id == h.Id)
                {
                    return SaveStatus.InvalidHostBlock;
                }
            }

            for (int j = 0; j < core.Length; j++)
            {
                if (core[j].Id == h.Id)
                {
                    return SaveStatus.InvalidHostBlock;
                }
            }
        }

        if (sc.Host.Length < host.Length)
        {
            sc.Host = new HostBlock[host.Length];
        }

        if (sc.HostData.Length < hostData.Length)
        {
            sc.HostData = new byte[hostData.Length];
        }

        host.CopyTo(sc.Host);
        hostData.CopyTo(sc.HostData);
        src = new WorldSaveSource(w, core, sc.Host, host.Length, sc.HostData, w.KeptBlocks);
        return SaveStatus.Ok;
    }

    /// <summary>Идентификатор блока хоста начинается со строчной ASCII-буквы (save-format.md §3); блоки ядра - с заглавной.</summary>
    private static bool IsHostId(uint id)
    {
        int first = (int)(id & 0xFF);
        return first >= 'a' && first <= 'z';
    }
}
