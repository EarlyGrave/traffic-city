using System.Runtime.InteropServices;
using Sim.Core.Network;

namespace Sim.Core.Api;

// Каталог профилей дорог (road-profiles.md §1, ADR-0030). Плоские данные, уже распарсенные хостом из JSON: ширины - целые мм, строки только в Id.
// null / пустой каталог = режим legacy (прежняя геометрия: ширина полосы одна на сеть, хэши и сейвы побитово прежние).

/// <summary>Тип элемента сечения (road-profiles.md §1). Порядок значений - часть формата (индекс в сейве RPRF, RP2).</summary>
public enum RoadElemKind : byte
{
    Sidewalk = 0,
    Verge = 1,
    Curb = 2,
    Lane = 3,
    Median = 4,
    Bike = 5,
    Parking = 6,
    Tram = 7,
    BusBay = 8,
}

/// <summary>Направление полосы: относительно направления рисования профиля (A→B).</summary>
public enum RoadLaneDir : byte
{
    Forward = 0,
    Backward = 1,
}

public struct RoadElemDesc
{
    public RoadElemKind Kind;
    public RoadLaneDir Dir;                // только у Lane; у остальных 0 (Forward) и не читается
    public int WidthMm;
}

public struct RoadProfileDesc
{
    public string Id;
    public RoadKind Kind;
    public int WidthMm;                    // заявленная ширина: Σ элементов = WidthMm = k * ModuleMm
    public RoadElemDesc[] Elements;        // слева направо по оси A→B
}

public struct RoadProfilesDesc
{
    public int ModuleMm;                   // module_m * 1000
    public RoadProfileDesc[] Profiles;     // null = режим legacy; индекс профиля = порядок в файле

    /// <summary>true - режим профилей (каталог задан).</summary>
    public readonly bool Enabled => Profiles != null;
}

/// <summary>Причина отказа валидации каталога (одинаково в C# и Rust, road-profiles.md §1 «Валидация»).</summary>
public enum RoadProfileError
{
    None = 0,
    NoProfiles,            // каталог пуст
    TooManyProfiles,       // > 64
    TooManyElements,       // > 16 в профиле (или 0)
    DuplicateId,           // id пуст или повторяется
    BadModule,             // module_m <= 0
    BadWidth,              // ширина профиля <= 0, не кратна module_m, k вне 1..8, либо Σ элементов != width
    ElementWidth,          // ширина элемента вне допуска своего типа
    NoLane,                // нет ни одной полосы
    TooManyLanes,          // > 8 полос в одну сторону
    LaneOrder,             // полосы одного направления не подряд или forward левее backward
    NoCurb,                // между полосой и тротуаром/газоном нет curb
    DoubleCurb,            // два curb подряд
    UnknownKind,           // неизвестный тип элемента / вида дороги / направления
}

/// <summary>Валидация каталога профилей (чистая функция над desc).</summary>
public static class RoadProfileValidator
{
    public const int MaxProfiles = 64;
    public const int MaxElements = 16;
    public const int MaxLanesPerDir = 8;
    public const int MaxModules = 8;

    /// <summary>Допуск ширины элемента, мм (road-profiles.md §1, таблица типов): <paramref name="min"/>..<paramref name="max"/> включительно.</summary>
    public static void WidthRange(RoadElemKind kind, out int min, out int max)
    {
        switch (kind)
        {
            case RoadElemKind.Sidewalk:
                min = 1500;
                max = 64000;
                break;
            case RoadElemKind.Curb:
                min = 0;
                max = 500;
                break;
            case RoadElemKind.Lane:
                min = 3000;
                max = 4000;
                break;
            case RoadElemKind.Bike:
            case RoadElemKind.Parking:
            case RoadElemKind.Tram:
            case RoadElemKind.BusBay:
                min = 1000;
                max = 4000;
                break;
            default:   // Verge, Median
                min = 0;
                max = 64000;
                break;
        }
    }

    /// <summary>Проверка каталога. <paramref name="profile"/> - индекс первого плохого профиля (-1 - ошибка уровня каталога).</summary>
    public static RoadProfileError Validate(in RoadProfilesDesc d, out int profile)
    {
        profile = -1;
        RoadProfileDesc[]? ps = d.Profiles;
        if (ps == null || ps.Length == 0)
        {
            return RoadProfileError.NoProfiles;
        }

        if (ps.Length > MaxProfiles)
        {
            return RoadProfileError.TooManyProfiles;
        }

        if (d.ModuleMm <= 0)
        {
            return RoadProfileError.BadModule;
        }

        for (int i = 0; i < ps.Length; i++)
        {
            profile = i;
            RoadProfileError e = ValidateOne(in ps[i], d.ModuleMm);
            if (e != RoadProfileError.None)
            {
                return e;
            }

            for (int j = 0; j < i; j++)
            {
                if (string.Equals(ps[j].Id, ps[i].Id, System.StringComparison.Ordinal))
                {
                    return RoadProfileError.DuplicateId;
                }
            }
        }

        profile = -1;
        return RoadProfileError.None;
    }

    private static RoadProfileError ValidateOne(in RoadProfileDesc p, int moduleMm)
    {
        if (string.IsNullOrEmpty(p.Id))
        {
            return RoadProfileError.DuplicateId;
        }

        RoadElemDesc[]? el = p.Elements;
        if (el == null || el.Length == 0 || el.Length > MaxElements)
        {
            return RoadProfileError.TooManyElements;
        }

        if (p.Kind is not (RoadKind.Street or RoadKind.Arterial))
        {
            return RoadProfileError.UnknownKind;
        }

        if (p.WidthMm <= 0 || p.WidthMm % moduleMm != 0 || p.WidthMm / moduleMm > MaxModules)
        {
            return RoadProfileError.BadWidth;
        }

        long sum = 0;
        int fwd = 0;
        int bwd = 0;
        int lastLane = -1;               // индекс последнего элемента-полосы
        int lastDir = -1;                // его направление
        bool sawForward = false;
        for (int i = 0; i < el.Length; i++)
        {
            ref readonly RoadElemDesc e = ref el[i];
            if (e.Kind > RoadElemKind.BusBay || (e.Kind == RoadElemKind.Lane && e.Dir > RoadLaneDir.Backward))
            {
                return RoadProfileError.UnknownKind;
            }

            WidthRange(e.Kind, out int min, out int max);
            if (e.WidthMm < min || e.WidthMm > max)
            {
                return RoadProfileError.ElementWidth;
            }

            sum += e.WidthMm;
            if (e.Kind == RoadElemKind.Curb && i > 0 && el[i - 1].Kind == RoadElemKind.Curb)
            {
                return RoadProfileError.DoubleCurb;
            }

            if (i > 0 && IsLaneAndGround(el[i - 1].Kind, e.Kind))
            {
                return RoadProfileError.NoCurb;
            }

            if (e.Kind != RoadElemKind.Lane)
            {
                continue;
            }

            int dir = (int)e.Dir;
            if (dir == 0)
            {
                fwd++;
                sawForward = true;
            }
            else
            {
                bwd++;
                if (sawForward)
                {
                    return RoadProfileError.LaneOrder;       // backward правее forward
                }
            }

            if (lastLane >= 0 && lastDir == dir && lastLane != i - 1)
            {
                return RoadProfileError.LaneOrder;           // полосы одного направления не подряд
            }

            lastLane = i;
            lastDir = dir;
        }

        if (sum != p.WidthMm)
        {
            return RoadProfileError.BadWidth;
        }

        if (fwd + bwd == 0)
        {
            return RoadProfileError.NoLane;
        }

        return fwd > MaxLanesPerDir || bwd > MaxLanesPerDir ? RoadProfileError.TooManyLanes : RoadProfileError.None;
    }

    private static bool IsGround(RoadElemKind k) => k is RoadElemKind.Sidewalk or RoadElemKind.Verge;

    private static bool IsLaneAndGround(RoadElemKind a, RoadElemKind b) =>
        (a == RoadElemKind.Lane && IsGround(b)) || (b == RoadElemKind.Lane && IsGround(a));
}

/// <summary>Профиль каталога для клиента (<see cref="NetworkView.RoadProfiles"/>, road-profiles.md §3.1): 12 байт, элементы - срез <see cref="NetworkView.ProfileElems"/>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
public struct RoadProfileData
{
    public int FirstElem;        // 0..3
    public int WidthMm;          // 4..7   Σ элементов = k * ModuleMm
    public byte ElemCount;       // 8
    public byte Kind;            // 9      RoadKind
    public byte LanesFwd;        // 10
    public byte LanesBwd;        // 11
}

/// <summary>Элемент сечения для клиента: слева направо по оси A→B (направление рисования профиля). 12 байт.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 12)]
public struct RoadElemData
{
    public byte Kind;            // 0      RoadElemKind
    public byte Dir;             // 1      RoadLaneDir (только у Lane)
    public ushort R0;            // 2..3
    public int OffsetMm;         // 4..7   левый край от оси, мм (+ вправо по A→B)
    public int WidthMm;          // 8..11
}
