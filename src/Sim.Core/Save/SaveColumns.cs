using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Save;

/// <summary>Копирование элементов столбца <c>[first, first + count)</c> мира в <c>dst</c> (<c>count * ElemSize</c> байт).</summary>
internal delegate void ColumnCopy(World w, long first, int count, Span<byte> dst);

/// <summary>Приём столбца из файла: <c>src</c> - ровно <c>count * ElemSize</c> байт. Статус не <c>Ok</c> прерывает загрузку.</summary>
internal delegate LoadStatus ColumnStore(LoadContext c, long count, ReadOnlySpan<byte> src);

/// <summary>
/// Описание одного столбца блока сейва (save-format.md §3, приложение A): id (никогда не переиспользуется), имя, тип элемента, длина как функция состояния мира,
/// копирование в файл и приём из файла. Реестр столбцов - <see cref="SaveSchema"/>.
/// </summary>
internal sealed class ColumnDef
{
    public ColumnDef(uint id, string name, ColumnType type, int elemSize, string length, string defaultText, Func<World, long> count, ColumnCopy copy, ColumnStore store)
    {
        Id = id;
        Name = name;
        Type = type;
        ElemSize = elemSize;
        Length = length;
        Default = defaultText;
        Count = count;
        Copy = copy;
        Store = store;
    }

    public uint Id { get; }

    public string Name { get; }

    public ColumnType Type { get; }

    public int ElemSize { get; }

    /// <summary>Длина столбца словами (для приложения A).</summary>
    public string Length { get; }

    /// <summary>Значение для сейвов, где столбца нет (новые столбцы в будущих версиях мира), и канон мёртвых слотов.</summary>
    public string Default { get; }

    /// <summary>Длина столбца в мире (при записи) либо ожидаемая длина (при загрузке, мир уже содержит счётчики предыдущих столбцов); -1 - длина свободная.</summary>
    public Func<World, long> Count { get; }

    public ColumnCopy Copy { get; }

    public ColumnStore Store { get; }

    /// <summary>Столбцы одной группы (ненулевой номер) обязаны иметь одну длину; для блоков, где мир ещё не создан и длину задаёт файл.</summary>
    public int Group { get; init; }

    /// <summary>Столбца может не быть в файле (старые версии мира): значение остаётся по умолчанию.</summary>
    public bool Optional { get; init; }
}

/// <summary>Блок сейва: FourCC, версия схемы, условие присутствия и столбцы по возрастанию id.</summary>
internal sealed class BlockDef
{
    public BlockDef(string fourCc, string title, ColumnDef[] columns)
    {
        Id = SaveFormat.FourCc(System.Text.Encoding.ASCII.GetBytes(fourCc));
        Name = fourCc;
        Title = title;
        Columns = columns;
    }

    public uint Id { get; }

    public string Name { get; }

    public string Title { get; }

    public ushort Schema { get; init; } = 1;

    public ColumnDef[] Columns { get; }

    /// <summary>null - блок есть всегда.</summary>
    public Func<World, bool>? Present { get; init; }

    /// <summary>Текст условия присутствия для приложения A.</summary>
    public string PresentText { get; init; } = "всегда";

    /// <summary>Блок создаёт мир (PARM, DEMD, VTYP): его столбцы заполняют <see cref="LoadContext"/>, а не готовый мир.</summary>
    public bool PreWorld { get; init; }

    /// <summary>Вызывается после загрузки всех столбцов блока (производные счётчики структуры); null - нет.</summary>
    public Func<LoadContext, LoadStatus>? After { get; init; }

    /// <summary>Вызывается до загрузки столбцов блока, когда мир уже создан (выделить структуру, зависящую от предыдущих блоков); null - нет.</summary>
    public Func<LoadContext, LoadStatus>? Before { get; init; }
}

/// <summary>Фабрики столбцов: массивы, поля структур, скаляры. Один вызов - одна строка реестра.</summary>
internal static class Col
{
    private static readonly Func<World, long> _one = static _ => 1;

    public static ColumnType TypeOf(Type t)
    {
        if (t.IsEnum)
        {
            t = Enum.GetUnderlyingType(t);
        }

        if (t == typeof(byte) || t == typeof(bool)) { return ColumnType.U8; }
        if (t == typeof(sbyte)) { return ColumnType.I8; }
        if (t == typeof(ushort)) { return ColumnType.U16; }
        if (t == typeof(short)) { return ColumnType.I16; }
        if (t == typeof(uint)) { return ColumnType.U32; }
        if (t == typeof(int)) { return ColumnType.I32; }
        if (t == typeof(ulong)) { return ColumnType.U64; }
        if (t == typeof(long)) { return ColumnType.I64; }
        if (t == typeof(float)) { return ColumnType.F32; }
        if (t == typeof(double)) { return ColumnType.F64; }
        throw new ArgumentException("unsupported column type " + t, nameof(t));
    }

    private static string TypeName(ColumnType t) => t switch
    {
        ColumnType.U8 => "u8",
        ColumnType.I8 => "i8",
        ColumnType.U16 => "u16",
        ColumnType.I16 => "i16",
        ColumnType.U32 => "u32",
        ColumnType.I32 => "i32",
        ColumnType.U64 => "u64",
        ColumnType.I64 => "i64",
        ColumnType.F32 => "f32",
        ColumnType.F64 => "f64",
        _ => "raw",
    };

    public static string NameOf(ColumnType t, int size) => t == ColumnType.Raw ? "raw[" + size + "]" : TypeName(t);

    // ---- скаляры ----

    /// <summary>Скаляр: столбец длины 1. <paramref name="get"/> читает значение из мира, <paramref name="set"/> применяет к контексту загрузки.</summary>
    public static ColumnDef Scalar<T>(uint id, string name, Func<World, T> get, Func<LoadContext, T, LoadStatus> set, string def = "0")
        where T : unmanaged
    {
        ColumnType type = TypeOf(typeof(T));
        int size = Unsafe.SizeOf<T>();
        return new ColumnDef(
            id,
            name,
            type,
            size,
            "1",
            def,
            _one,
            (w, first, count, dst) =>
            {
                T v = get(w);
                MemoryMarshal.Write(dst, in v);
            },
            (c, n, src) => set(c, MemoryMarshal.Read<T>(src)));
    }

    /// <summary>Скаляр, присваиваемый без проверки.</summary>
    public static ColumnDef Scalar<T>(uint id, string name, Func<World, T> get, Action<LoadContext, T> set, string def = "0")
        where T : unmanaged =>
        Scalar(id, name, get, (c, v) => { set(c, v); return LoadStatus.Ok; }, def);

    /// <summary>Логический скаляр (u8 0/1).</summary>
    public static ColumnDef Flag(uint id, string name, Func<World, bool> get, Action<LoadContext, bool> set, string def = "0") =>
        Scalar<byte>(id, name, w => get(w) ? (byte)1 : (byte)0, (c, v) =>
        {
            if (v > 1)
            {
                return LoadStatus.InvalidValue;
            }

            set(c, v == 1);
            return LoadStatus.Ok;
        }, def);

    // ---- массивы ----

    /// <summary>
    /// Столбец - префикс <c>[0, count)</c> массива мира. <paramref name="dead"/> и <paramref name="deadValue"/>: для слотов пула, не занятых живой сущностью,
    /// пишется канонический <paramref name="deadValue"/> (мусор мёртвых слотов в файл не попадает).
    /// </summary>
    public static ColumnDef Array<T>(
        uint id,
        string name,
        Func<World, T[]> array,
        Func<World, int> count,
        string length,
        string def = "0",
        Func<World, HandlePool>? dead = null,
        T deadValue = default)
        where T : unmanaged
    {
        ColumnType type = TypeOf(typeof(T));
        int size = Unsafe.SizeOf<T>();
        return new ColumnDef(
            id,
            name,
            type,
            size,
            length,
            def,
            w => count(w),
            (w, first, n, dst) =>
            {
                T[] a = array(w);
                MemoryMarshal.AsBytes(new ReadOnlySpan<T>(a, (int)first, n)).CopyTo(dst);
                if (dead != null)
                {
                    PatchDead(dead(w), (int)first, n, dst, deadValue);
                }
            },
            (c, n, src) =>
            {
                T[] a = array(c.W);
                if (n > a.Length)
                {
                    return LoadStatus.CapacityExceeded;
                }

                src.CopyTo(MemoryMarshal.AsBytes(new Span<T>(a, 0, (int)n)));
                return LoadStatus.Ok;
            });
    }

    /// <summary>Мёртвые слоты (чётное поколение) в куске <c>[first, first + n)</c> получают канонический байтовый образ.</summary>
    private static void PatchDead<T>(HandlePool pool, int first, int n, Span<byte> dst, T deadValue)
        where T : unmanaged
    {
        if (pool.AliveCount >= pool.HighWater)
        {
            return;
        }

        int size = Unsafe.SizeOf<T>();
        for (int k = 0; k < n; k++)
        {
            if (!pool.IsAlive(first + k))
            {
                MemoryMarshal.Write(dst.Slice(k * size, size), in deadValue);
            }
        }
    }

    // ---- поля структур ----

    /// <summary>
    /// Столбец - одно поле структуры массива мира (формат не зависит от раскладки структуры в C#). <paramref name="field"/> - имя поля, <paramref name="sub"/> -
    /// смещение внутри поля (X/Z вектора), <paramref name="size"/> - размер элемента при <paramref name="sub"/> != null.
    /// </summary>
    public static ColumnDef Field<TS>(
        uint id,
        string name,
        Func<World, TS[]> array,
        Func<World, int> count,
        string length,
        string field,
        int sub = 0,
        ColumnType? asType = null,
        string def = "0")
        where TS : unmanaged
    {
        FieldInfo fi = typeof(TS).GetField(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new ArgumentException("no field " + field, nameof(field));
        int offset = (int)Marshal.OffsetOf<TS>(field) + sub;
        ColumnType type = asType ?? TypeOf(fi.FieldType);
        int size = SaveFormat.FixedElemSize(type);
        int stride = Unsafe.SizeOf<TS>();
        return new ColumnDef(
            id,
            name,
            type,
            size,
            length,
            def,
            w => count(w),
            (w, first, n, dst) =>
            {
                TS[] a = array(w);
                ReadOnlySpan<byte> src = MemoryMarshal.AsBytes(new ReadOnlySpan<TS>(a, (int)first, n));
                Gather(src, stride, offset, size, n, dst);
            },
            (c, n, src) =>
            {
                TS[] a = array(c.W);
                if (n > a.Length)
                {
                    return LoadStatus.CapacityExceeded;
                }

                Scatter(src, MemoryMarshal.AsBytes(new Span<TS>(a, 0, (int)n)), stride, offset, size, (int)n);
                return LoadStatus.Ok;
            });
    }

    /// <summary>Вынимает поле <c>size</c> байт со смещением <c>offset</c> у <c>n</c> структур шагом <c>stride</c> в плотный <c>dst</c>.</summary>
    internal static void Gather(ReadOnlySpan<byte> src, int stride, int offset, int size, int n, Span<byte> dst)
    {
        switch (size)
        {
            case 1:
                for (int k = 0; k < n; k++)
                {
                    dst[k] = src[(k * stride) + offset];
                }

                break;
            case 2:
                for (int k = 0; k < n; k++)
                {
                    MemoryMarshal.Write(dst.Slice(k * 2, 2), MemoryMarshal.Read<ushort>(src.Slice((k * stride) + offset, 2)));
                }

                break;
            case 4:
                for (int k = 0; k < n; k++)
                {
                    MemoryMarshal.Write(dst.Slice(k * 4, 4), MemoryMarshal.Read<uint>(src.Slice((k * stride) + offset, 4)));
                }

                break;
            case 8:
                for (int k = 0; k < n; k++)
                {
                    MemoryMarshal.Write(dst.Slice(k * 8, 8), MemoryMarshal.Read<ulong>(src.Slice((k * stride) + offset, 8)));
                }

                break;
            default:
                for (int k = 0; k < n; k++)
                {
                    src.Slice((k * stride) + offset, size).CopyTo(dst.Slice(k * size, size));
                }

                break;
        }
    }

    /// <summary>Обратное к <see cref="Gather"/>: кладёт плотный <c>src</c> в поля <c>n</c> структур.</summary>
    internal static void Scatter(ReadOnlySpan<byte> src, Span<byte> dst, int stride, int offset, int size, int n)
    {
        switch (size)
        {
            case 1:
                for (int k = 0; k < n; k++)
                {
                    dst[(k * stride) + offset] = src[k];
                }

                break;
            case 2:
                for (int k = 0; k < n; k++)
                {
                    MemoryMarshal.Write(dst.Slice((k * stride) + offset, 2), MemoryMarshal.Read<ushort>(src.Slice(k * 2, 2)));
                }

                break;
            case 4:
                for (int k = 0; k < n; k++)
                {
                    MemoryMarshal.Write(dst.Slice((k * stride) + offset, 4), MemoryMarshal.Read<uint>(src.Slice(k * 4, 4)));
                }

                break;
            case 8:
                for (int k = 0; k < n; k++)
                {
                    MemoryMarshal.Write(dst.Slice((k * stride) + offset, 8), MemoryMarshal.Read<ulong>(src.Slice(k * 8, 8)));
                }

                break;
            default:
                for (int k = 0; k < n; k++)
                {
                    src.Slice(k * size, size).CopyTo(dst.Slice((k * stride) + offset, size));
                }

                break;
        }
    }

    // ---- очереди (кольцевые буферы): порядок в файле - логический, голова при загрузке = 0 ----

    /// <summary>Поле структуры элементов очереди (в логическом порядке от головы).</summary>
    public static ColumnDef RingField<TS>(
        uint id,
        string name,
        Func<World, RingBuffer<TS>> ring,
        string length,
        string field,
        int rawSize = 0)
        where TS : unmanaged
    {
        int stride = Unsafe.SizeOf<TS>();
        int offset = 0;
        ColumnType type = ColumnType.Raw;
        int size = rawSize;
        if (rawSize == 0)
        {
            FieldInfo fi = typeof(TS).GetField(field, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new ArgumentException("no field " + field, nameof(field));
            offset = (int)Marshal.OffsetOf<TS>(field);
            type = TypeOf(fi.FieldType);
            size = SaveFormat.FixedElemSize(type);
        }

        int fieldSize = size;
        return new ColumnDef(
            id,
            name,
            type,
            size,
            length,
            "0",
            w => ring(w).Count,
            (w, first, n, dst) =>
            {
                RingBuffer<TS> r = ring(w);
                for (int k = 0; k < n; k++)
                {
                    ref readonly TS item = ref r.At((int)first + k);
                    ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<TS>(in item));
                    bytes.Slice(offset, fieldSize).CopyTo(dst.Slice(k * fieldSize, fieldSize));
                }
            },
            (c, n, src) =>
            {
                RingBuffer<TS> r = ring(c.W);
                if (n > r.Capacity)
                {
                    return LoadStatus.CapacityExceeded;
                }

                // Голова = 0: логический порядок совпадает с физическим. Размер очереди задал скаляр-счётчик блока.
                Span<byte> all = MemoryMarshal.AsBytes(new Span<TS>(r.Items, 0, (int)n));
                Scatter(src, all, stride, offset, fieldSize, (int)n);
                return LoadStatus.Ok;
            });
    }

    // ---- произвольные столбцы ----

    public static ColumnDef Custom(
        uint id,
        string name,
        ColumnType type,
        int elemSize,
        string length,
        string def,
        Func<World, long> count,
        ColumnCopy copy,
        ColumnStore store,
        int group = 0) =>
        new(id, name, type, elemSize, length, def, count, copy, store) { Group = group };
}
