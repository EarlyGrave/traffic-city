using System;
using System.Text;

namespace Sim.Scenarios.Mods;

/// <summary>Вид ассета в id (<c>vehicle | road | building | prop | service</c>).</summary>
public enum AssetKind
{
    Vehicle,
    Road,
    Building,
    Prop,
    Service,
}

/// <summary>Полный id ассета <c>&lt;пакет&gt;:&lt;вид&gt;/&lt;имя&gt;</c> (modding.md §2).</summary>
public readonly record struct AssetId(string Package, AssetKind Kind, string Name)
{
    public static string KindName(AssetKind kind) => kind switch
    {
        AssetKind.Vehicle => "vehicle",
        AssetKind.Road => "road",
        AssetKind.Building => "building",
        AssetKind.Service => "service",
        _ => "prop",
    };

    public static bool TryParseKind(string text, out AssetKind kind)
    {
        switch (text)
        {
            case "vehicle":
                kind = AssetKind.Vehicle;
                return true;
            case "road":
                kind = AssetKind.Road;
                return true;
            case "building":
                kind = AssetKind.Building;
                return true;
            case "prop":
                kind = AssetKind.Prop;
                return true;
            case "service":
                kind = AssetKind.Service;
                return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>Имя ассета: <c>[a-z0-9_]{1,64}</c>.</summary>
    public static bool IsValidName(string? name) =>
        name != null && name.Length is >= 1 and <= 64 && AllLowerAlnum(name);

    /// <summary>Id пакета: <c>[a-z][a-z0-9_]{1,31}</c>.</summary>
    public static bool IsValidPackageId(string? id) =>
        id != null && id.Length is >= 2 and <= 32 && id[0] is >= 'a' and <= 'z' && AllLowerAlnum(id);

    /// <summary>Разбор полного id; весь id ≤ 128 Б.</summary>
    public static bool TryParse(string? text, out AssetId id)
    {
        id = default;
        if (text == null || Encoding.UTF8.GetByteCount(text) > ModLimits.MaxAssetIdBytes)
        {
            return false;
        }

        int colon = text.IndexOf(':', StringComparison.Ordinal);
        int slash = text.IndexOf('/', StringComparison.Ordinal);
        if (colon < 0 || slash < colon)
        {
            return false;
        }

        string pkg = text[..colon];
        string kindText = text[(colon + 1)..slash];
        string name = text[(slash + 1)..];
        if (!IsValidPackageId(pkg) || !TryParseKind(kindText, out AssetKind kind) || !IsValidName(name))
        {
            return false;
        }

        id = new AssetId(pkg, kind, name);
        return true;
    }

    /// <summary>Сокращение без пакета <c>vehicle/car</c>.</summary>
    public static bool TryParseShort(string? text, out AssetKind kind, out string name)
    {
        kind = default;
        name = string.Empty;
        if (text == null)
        {
            return false;
        }

        int slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || text.Contains(':', StringComparison.Ordinal) || !TryParseKind(text[..slash], out kind))
        {
            return false;
        }

        name = text[(slash + 1)..];
        return IsValidName(name);
    }

    public override string ToString() => $"{Package}:{KindName(Kind)}/{Name}";

    private static bool AllLowerAlnum(string s)
    {
        foreach (char c in s)
        {
            if (!(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
