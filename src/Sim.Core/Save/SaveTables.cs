using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Таблицы блоков и столбцов сейва в памяти: результат <see cref="SaveReader"/> и, одновременно, простой источник для
/// <see cref="SaveWriter"/> (тесты, блоки хоста, <c>kept</c>). Данные столбцов - срезы чужой памяти (у прочитанных - буфер файла),
/// без копий. Блоки хранятся в порядке файла.
/// </summary>
internal sealed class SaveTables : ISaveSource
{
    internal struct BlockEntry
    {
        public uint Id;
        public ushort Schema;
        public ushort Flags;
        public int ColumnStart;
        public int ColumnCount;
        public ReadOnlyMemory<byte> Payload;
    }

    internal struct ColumnEntry
    {
        public uint Id;
        public ColumnType Type;
        public byte ElemSize;
        public long Count;
        public ReadOnlyMemory<byte> Data;
    }

    private readonly List<BlockEntry> _blocks = new();
    private readonly List<ColumnEntry> _columns = new();

    /// <summary>Заголовок прочитанного файла (для собранных вручную таблиц - нули).</summary>
    public SaveHeaderInfo Header;

    /// <summary>Сколько блоков читатель принял как неизвестные необязательные (<c>kept</c>).</summary>
    public int KeptBlocks;

    public int BlockCount => _blocks.Count;

    public SaveBlockDesc GetBlock(int block)
    {
        BlockEntry b = _blocks[block];
        return new SaveBlockDesc { Id = b.Id, Schema = b.Schema, Flags = b.Flags, ColumnCount = b.ColumnCount, RawLength = b.Payload.Length };
    }

    public SaveColumnDesc GetColumn(int block, int column)
    {
        ColumnEntry c = _columns[_blocks[block].ColumnStart + column];
        return new SaveColumnDesc { Id = c.Id, Type = c.Type, ElemSize = c.ElemSize, Count = c.Count };
    }

    public void CopyColumn(int block, int column, long byteOffset, Span<byte> dst) =>
        _columns[_blocks[block].ColumnStart + column].Data.Span.Slice((int)byteOffset, dst.Length).CopyTo(dst);

    public void CopyRaw(int block, long byteOffset, Span<byte> dst) =>
        _blocks[block].Payload.Span.Slice((int)byteOffset, dst.Length).CopyTo(dst);

    public BlockFlags BlockFlagsOf(int block) => (BlockFlags)_blocks[block].Flags;

    public uint BlockId(int block) => _blocks[block].Id;

    public int BlockSchema(int block) => _blocks[block].Schema;

    public int ColumnCountOf(int block) => _blocks[block].ColumnCount;

    /// <summary>Нагрузка непрозрачного блока (<c>Host</c>/<c>Kept</c>); для блоков с каталогом - пусто.</summary>
    public ReadOnlyMemory<byte> OpaquePayload(int block) => _blocks[block].Payload;

    /// <summary>Индекс блока с данным FourCC либо -1.</summary>
    public int FindBlock(uint id)
    {
        for (int i = 0; i < _blocks.Count; i++)
        {
            if (_blocks[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Индекс столбца внутри блока либо -1.</summary>
    public int FindColumn(int block, uint columnId)
    {
        BlockEntry b = _blocks[block];
        for (int i = 0; i < b.ColumnCount; i++)
        {
            if (_columns[b.ColumnStart + i].Id == columnId)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Типизированный просмотр столбца без копии; false - столбца нет или его тип не <typeparamref name="T"/>.</summary>
    public bool TryGetColumn<T>(int block, uint columnId, out ReadOnlySpan<T> values)
        where T : unmanaged
    {
        values = default;
        int c = FindColumn(block, columnId);
        if (c < 0)
        {
            return false;
        }

        ColumnEntry e = _columns[_blocks[block].ColumnStart + c];
        if (e.Type != TypeOf<T>() || e.ElemSize != System.Runtime.CompilerServices.Unsafe.SizeOf<T>())
        {
            return false;
        }

        values = MemoryMarshal.Cast<byte, T>(e.Data.Span);
        return true;
    }

    /// <summary>Сырые байты столбца (любой тип).</summary>
    public bool TryGetColumnBytes(int block, uint columnId, out ReadOnlySpan<byte> bytes)
    {
        bytes = default;
        int c = FindColumn(block, columnId);
        if (c < 0)
        {
            return false;
        }

        bytes = _columns[_blocks[block].ColumnStart + c].Data.Span;
        return true;
    }

    /// <summary>Начать блок с каталогом столбцов; столбцы добавляются в порядке возрастания id. Возвращает индекс блока.</summary>
    public int AddBlock(uint id, ushort schema, BlockFlags flags)
    {
        _blocks.Add(new BlockEntry { Id = id, Schema = schema, Flags = (ushort)flags, ColumnStart = _columns.Count });
        return _blocks.Count - 1;
    }

    /// <summary>Непрозрачный блок (хост, мод, <c>kept</c>): нагрузка пишется как есть. Флаг <c>Host</c> или <c>Kept</c> обязателен.</summary>
    public int AddOpaqueBlock(uint id, ushort schema, BlockFlags flags, ReadOnlyMemory<byte> payload)
    {
        if ((flags & (BlockFlags.Host | BlockFlags.Kept)) == 0)
        {
            throw new ArgumentException("opaque block needs Host or Kept flag", nameof(flags));
        }

        _blocks.Add(new BlockEntry { Id = id, Schema = schema, Flags = (ushort)flags, ColumnStart = _columns.Count, Payload = payload });
        return _blocks.Count - 1;
    }

    /// <summary>Добавить столбец примитивного типа к последнему блоку.</summary>
    public void AddColumn<T>(uint columnId, T[] values)
        where T : unmanaged
    {
        ColumnType type = TypeOf<T>();
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        byte[] bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        AddColumnBytes(columnId, type, size, values.Length, bytes);
    }

    /// <summary>Добавить столбец к последнему блоку: данные не копируются.</summary>
    public void AddColumnBytes(uint columnId, ColumnType type, int elemSize, long count, ReadOnlyMemory<byte> data)
    {
        if (_blocks.Count == 0)
        {
            throw new InvalidOperationException("AddBlock first");
        }

        _columns.Add(new ColumnEntry { Id = columnId, Type = type, ElemSize = (byte)elemSize, Count = count, Data = data });
        BlockEntry b = _blocks[^1];
        b.ColumnCount++;
        _blocks[^1] = b;
    }

    /// <summary>
    /// Шаг миграции: добавить столбец в уже существующий блок (на место по возрастанию id) либо заменить столбец с тем же id.
    /// Нужно только миграциям (<see cref="Migrations"/>); читатель и писатель контейнера им не пользуются.
    /// </summary>
    public bool SetColumn<T>(int block, uint columnId, T[] values)
        where T : unmanaged
    {
        if ((uint)block >= (uint)_blocks.Count)
        {
            return false;
        }

        byte[] bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        var entry = new ColumnEntry { Id = columnId, Type = TypeOf<T>(), ElemSize = (byte)System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), Count = values.Length, Data = bytes };
        BlockEntry b = _blocks[block];
        int pos = 0;
        while (pos < b.ColumnCount && _columns[b.ColumnStart + pos].Id < columnId)
        {
            pos++;
        }

        if (pos < b.ColumnCount && _columns[b.ColumnStart + pos].Id == columnId)
        {
            _columns[b.ColumnStart + pos] = entry;
            return true;
        }

        _columns.Insert(b.ColumnStart + pos, entry);
        b.ColumnCount++;
        _blocks[block] = b;
        ShiftColumnStarts(block + 1, 1);
        return true;
    }

    /// <summary>Шаг миграции: убрать столбец (выбывший id); false - столбца нет.</summary>
    public bool RemoveColumn(int block, uint columnId)
    {
        int c = (uint)block < (uint)_blocks.Count ? FindColumn(block, columnId) : -1;
        if (c < 0)
        {
            return false;
        }

        BlockEntry b = _blocks[block];
        _columns.RemoveAt(b.ColumnStart + c);
        b.ColumnCount--;
        _blocks[block] = b;
        ShiftColumnStarts(block + 1, -1);
        return true;
    }

    /// <summary>Шаг миграции: убрать блок целиком; false - блока нет.</summary>
    public bool RemoveBlock(uint blockId)
    {
        int i = FindBlock(blockId);
        if (i < 0)
        {
            return false;
        }

        BlockEntry b = _blocks[i];
        _columns.RemoveRange(b.ColumnStart, b.ColumnCount);
        _blocks.RemoveAt(i);
        ShiftColumnStarts(i, -b.ColumnCount);
        return true;
    }

    private void ShiftColumnStarts(int fromBlock, int delta)
    {
        for (int i = fromBlock; i < _blocks.Count; i++)
        {
            BlockEntry e = _blocks[i];
            e.ColumnStart += delta;
            _blocks[i] = e;
        }
    }

    internal static ColumnType TypeOf<T>()
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte)) { return ColumnType.U8; }
        if (typeof(T) == typeof(sbyte)) { return ColumnType.I8; }
        if (typeof(T) == typeof(ushort)) { return ColumnType.U16; }
        if (typeof(T) == typeof(short)) { return ColumnType.I16; }
        if (typeof(T) == typeof(uint)) { return ColumnType.U32; }
        if (typeof(T) == typeof(int)) { return ColumnType.I32; }
        if (typeof(T) == typeof(ulong)) { return ColumnType.U64; }
        if (typeof(T) == typeof(long)) { return ColumnType.I64; }
        if (typeof(T) == typeof(float)) { return ColumnType.F32; }
        if (typeof(T) == typeof(double)) { return ColumnType.F64; }
        return ColumnType.Raw;
    }
}
