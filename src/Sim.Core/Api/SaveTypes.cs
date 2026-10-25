using System;
using System.Runtime.InteropServices;

namespace Sim.Core.Api;

// Публичные типы контейнера сейва (save-format.md §6). Ядро не знает про файлы: байты уходят в Span / SaveSink хоста.

/// <summary>Приёмник потока сейва (хост пишет в файл; через FFI - колбэк). Вызывается кусками не более 1 МБ.</summary>
public abstract class SaveSink
{
    public abstract void Write(ReadOnlySpan<byte> data);
}

/// <summary>Источник потока сейва. <see cref="Read"/> возвращает число прочитанных байт, 0 - конец потока.</summary>
public abstract class SaveSource
{
    public abstract int Read(Span<byte> buffer);
}

/// <summary>Блок хоста: срез <c>hostData</c> (Offset/Length) под своим FourCC.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct HostBlock
{
    public uint Id;
    public ushort Flags;
    public ushort Reserved;
    public int Offset;
    public int Length;
}

public enum SaveStatus : int
{
    Ok = 0,
    BufferTooSmall = 1,
    Disposed = 2,
    InvalidHostBlock = 3,

    /// <summary>Описание блоков/столбцов источника противоречиво (ошибка вызывающего кода, а не данных на диске).</summary>
    InvalidTables = 4,
}

public enum LoadStatus : int
{
    Ok = 0,
    NotASave = 1,
    Truncated = 2,
    ChecksumMismatch = 3,
    UnsupportedContainer = 4,
    TooNew = 5,
    UnknownCriticalBlock = 6,
    MissingBlock = 7,
    UnknownColumn = 8,

    /// <summary>Тип/длина/смещение столбца или каталога столбцов.</summary>
    BadColumn = 9,

    /// <summary>Оглавление: смещения, перекрытия, повтор id блока, лишние данные.</summary>
    InvalidReference = 10,

    /// <summary>NaN, вне диапазона, неизвестный флаг/писатель, ненулевой reserved.</summary>
    InvalidValue = 11,
    CapacityExceeded = 12,
    MigrationFailed = 13,
    HashMismatch = 14,
    NotSupported = 15,
}

/// <summary>Параметры загрузки (save-format.md §6).</summary>
public struct LoadOptions
{
    /// <summary>Ёмкости: 0 в поле = как в сейве, иначе не меньше занятого (продолжение уже не обязано совпасть с прогоном без сейва).</summary>
    public SimCapacities CapacityOverride;

    /// <summary>1 = расхождение хэша состояния с заголовком (при равных <c>hash_version</c>) - ошибка <see cref="LoadStatus.HashMismatch"/> (тесты, CI); 0 = только флаги в отчёте.</summary>
    public byte StrictHash;

    /// <summary>0 = параметры ассетов из сейва, 1 = новые параметры типов для новых спавнов (S3: манифест ассетов).</summary>
    public byte AssetParams;

    public VehicleTypeDesc[]? CurrentTypes;
}

/// <summary>Результат загрузки без строк: хост переводит в текст. Контейнер заполняет статус, версию и место поломки.</summary>
public struct LoadReport
{
    public LoadStatus Status;
    public int FileWorldVersion;
    public int MigrationsApplied;
    public int KeptBlocks;
    public uint FailedBlock;
    public uint FailedColumn;
    public long FailedRow;
    public byte HashChecked;
    public byte HashMatched;
}

/// <summary>Заголовок сейва. При записи <c>ContainerVersion</c>, <c>BlockCount</c> и <c>TotalLength</c> вычисляются писателем.</summary>
public struct SaveHeaderInfo
{
    public int ContainerVersion;
    public int WorldVersion;
    public int HashVersion;
    public int BlockCount;
    public int Writer;
    public int WriterVersion;
    public long Tick;
    public long TotalLength;
    public ulong StateHash;
}
