using System;

namespace Sim.Core.Save;

/// <summary>Тип элемента столбца (save-format.md §2).</summary>
internal enum ColumnType : byte
{
    U8 = 1,
    I8 = 2,
    U16 = 3,
    I16 = 4,
    U32 = 5,
    I32 = 6,
    U64 = 7,
    I64 = 8,
    F32 = 9,
    F64 = 10,

    /// <summary>Запись из <c>elem_size</c> байт (1..255).</summary>
    Raw = 11,
}

/// <summary>Биты <c>flags</c> блока в оглавлении.</summary>
[Flags]
internal enum BlockFlags : ushort
{
    None = 0,

    /// <summary>Читатель, не знающий блока, обязан отказать (<c>UnknownCriticalBlock</c>).</summary>
    Critical = 1,

    /// <summary>Блок хоста/мода: нагрузка произвольная, ядро её не разбирает.</summary>
    Host = 2,

    /// <summary>Переписан из чужого сейва без разбора.</summary>
    Kept = 4,

    All = Critical | Host | Kept,
}

/// <summary>Константы раскладки контейнера и арифметика выравнивания.</summary>
internal static class SaveFormat
{
    public const int ContainerVersion = 1;
    public const int HeaderSize = 64;
    public const int TocEntrySize = 32;
    public const int ColumnEntrySize = 24;
    public const int CatalogHeaderSize = 8;
    public const int FooterSize = 16;
    public const int HashSize = 8;
    public const int WriterCSharp = 1;
    public const int WriterRust = 2;

    /// <summary>Размер куска, которым пишется <c>SaveSink</c> (не более 1 МБ по спеке).</summary>
    public const int SinkChunk = 1 << 20;

    public static ReadOnlySpan<byte> Magic => "TCSAVE\r\n"u8;

    public static ReadOnlySpan<byte> FooterMagic => "TCEND\0\0\0"u8;

    /// <summary>FourCC: четыре ASCII-байта в порядке имени ('V','E','H','C' лежат в файле именно так).</summary>
    public static uint FourCc(ReadOnlySpan<byte> name) =>
        name.Length == 4 ? (uint)(name[0] | (name[1] << 8) | (name[2] << 16) | (name[3] << 24)) : 0;

    public static long Align8(long n) => (n + 7) & ~7L;

    public static bool IsBlockOpaque(ushort flags) => (flags & (ushort)(BlockFlags.Host | BlockFlags.Kept)) != 0;

    /// <summary>Размер элемента типа; 0 - тип неизвестен. Для <see cref="ColumnType.Raw"/> размер задаёт столбец.</summary>
    public static int FixedElemSize(ColumnType t) => t switch
    {
        ColumnType.U8 or ColumnType.I8 => 1,
        ColumnType.U16 or ColumnType.I16 => 2,
        ColumnType.U32 or ColumnType.I32 or ColumnType.F32 => 4,
        ColumnType.U64 or ColumnType.I64 or ColumnType.F64 => 8,
        _ => 0,
    };

    /// <summary>Описание столбца: допустимы тип 1..11, для 1..10 размер задан типом, для raw - 1..255.</summary>
    public static bool IsValidColumn(ColumnType t, int elemSize)
    {
        int fixedSize = FixedElemSize(t);
        if (fixedSize != 0)
        {
            return elemSize == fixedSize;
        }

        return t == ColumnType.Raw && elemSize is >= 1 and <= 255;
    }

    /// <summary>Размер блока ядра в файле: нагрузка + хэш + паддинг до 8.</summary>
    public static long BlockSpan(long payloadLength) => Align8(payloadLength + HashSize);
}

/// <summary>Блок для писателя: каталог столбцов (для блоков ядра) либо готовая нагрузка (<c>Host</c>/<c>Kept</c>).</summary>
internal struct SaveBlockDesc
{
    public uint Id;
    public ushort Schema;
    public ushort Flags;

    /// <summary>Число столбцов (блоки с каталогом).</summary>
    public int ColumnCount;

    /// <summary>Длина нагрузки (только непрозрачные блоки).</summary>
    public long RawLength;
}

internal struct SaveColumnDesc
{
    public uint Id;
    public ColumnType Type;
    public byte ElemSize;
    public long Count;
}

/// <summary>
/// Источник данных для писателя. Реализация - структура (generic-вызов без boxing и без аллокаций): для мира это обёртка над
/// массивами <c>World</c>, для тестов и блоков хоста - <see cref="SaveTables"/>.
/// </summary>
internal interface ISaveSource
{
    int BlockCount { get; }

    SaveBlockDesc GetBlock(int block);

    SaveColumnDesc GetColumn(int block, int column);

    /// <summary>
    /// Записать байты столбца <c>[byteOffset, byteOffset + dst.Length)</c>. <c>byteOffset</c> и <c>dst.Length</c> кратны размеру
    /// элемента; в синк-режиме столбец отдаётся кусками.
    /// </summary>
    void CopyColumn(int block, int column, long byteOffset, Span<byte> dst);

    /// <summary>То же для нагрузки непрозрачного блока.</summary>
    void CopyRaw(int block, long byteOffset, Span<byte> dst);
}
