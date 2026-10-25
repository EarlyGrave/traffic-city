using System;
using System.Globalization;

namespace Sim.Scenarios.Mods;

/// <summary>Версия MAJOR.MINOR.PATCH без pre-release и build (modding.md §1.2).</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        if (text == null)
        {
            return false;
        }

        string[] parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        Span<int> n = stackalloc int[3];
        for (int i = 0; i < 3; i++)
        {
            string p = parts[i];
            bool digits = p.Length is >= 1 and <= 9;
            foreach (char ch in p)
            {
                digits &= ch is >= '0' and <= '9';
            }

            if (!digits || (p.Length > 1 && p[0] == '0'))
            {
                return false;
            }

            n[i] = int.Parse(p, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        version = new SemVer(n[0], n[1], n[2]);
        return true;
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;

    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;

    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;

    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public int CompareTo(SemVer other)
    {
        int c = Major.CompareTo(other.Major);
        if (c != 0)
        {
            return c;
        }

        c = Minor.CompareTo(other.Minor);
        return c != 0 ? c : Patch.CompareTo(other.Patch);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}

/// <summary>Ограничение версии зависимости: <c>^X.Y.Z</c> (та же MAJOR и не ниже) или точная <c>X.Y.Z</c>.</summary>
public readonly record struct VersionConstraint(SemVer Base, bool Caret)
{
    public static bool TryParse(string? text, out VersionConstraint constraint)
    {
        constraint = default;
        if (text == null)
        {
            return false;
        }

        bool caret = text.StartsWith('^');
        if (!SemVer.TryParse(caret ? text[1..] : text, out SemVer v))
        {
            return false;
        }

        constraint = new VersionConstraint(v, caret);
        return true;
    }

    public bool Satisfies(SemVer v) =>
        Caret ? v.Major == Base.Major && v.CompareTo(Base) >= 0 : v == Base;

    public override string ToString() => (Caret ? "^" : string.Empty) + Base;
}
