using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Sim.Scenarios;

/// <summary>
/// Читатель JSON-объекта с отслеживанием пути и учётом прочитанных ключей:
/// <see cref="Finish"/> бросает ошибку на любой непрочитанный (неизвестный) ключ.
/// </summary>
internal sealed class JsonObjectReader
{
    private readonly Dictionary<string, JsonElement> _props = new(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new(StringComparer.Ordinal);

    public JsonObjectReader(JsonElement element, string path)
    {
        Path = path;
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ScenarioException($"{path}: expected object");
        }

        foreach (JsonProperty p in element.EnumerateObject())
        {
            if (!_props.TryAdd(p.Name, p.Value))
            {
                throw new ScenarioException($"{path}.{p.Name}: duplicate key");
            }
        }
    }

    public string Path { get; }

    public static string Index(string path, int i) => $"{path}[{i.ToString(CultureInfo.InvariantCulture)}]";

    public string ChildPath(string key) => $"{Path}.{key}";

    public bool Has(string key) => _props.ContainsKey(key);

    public IEnumerable<string> Keys => _props.Keys;

    /// <summary>Пометить ключ прочитанным (для объектов с произвольными ключами, напр. type_mix).</summary>
    public JsonElement Raw(string key)
    {
        _used.Add(key);
        return _props[key];
    }

    public void Finish()
    {
        foreach (string k in _props.Keys)
        {
            if (!_used.Contains(k))
            {
                throw new ScenarioException($"{ChildPath(k)}: unknown key");
            }
        }
    }

    public JsonObjectReader? OptObject(string key)
    {
        if (!TryGet(key, out JsonElement e))
        {
            return null;
        }

        return new JsonObjectReader(e, ChildPath(key));
    }

    public JsonObjectReader ReqObject(string key) =>
        OptObject(key) ?? throw Missing(key);

    public JsonElement[]? OptArray(string key)
    {
        if (!TryGet(key, out JsonElement e))
        {
            return null;
        }

        return ToArray(e, ChildPath(key));
    }

    public JsonElement[] ReqArray(string key) =>
        OptArray(key) ?? throw Missing(key);

    public static JsonElement[] ToArray(JsonElement e, string path)
    {
        if (e.ValueKind != JsonValueKind.Array)
        {
            throw new ScenarioException($"{path}: expected array");
        }

        var list = new JsonElement[e.GetArrayLength()];
        int i = 0;
        foreach (JsonElement x in e.EnumerateArray())
        {
            list[i++] = x;
        }

        return list;
    }

    public string Str(string key, string? def = null)
    {
        if (!TryGet(key, out JsonElement e))
        {
            return def ?? throw Missing(key);
        }

        if (e.ValueKind != JsonValueKind.String)
        {
            throw new ScenarioException($"{ChildPath(key)}: expected string");
        }

        return e.GetString()!;
    }

    public bool Bool(string key, bool? def = null)
    {
        if (!TryGet(key, out JsonElement e))
        {
            return def ?? throw Missing(key);
        }

        if (e.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ScenarioException($"{ChildPath(key)}: expected boolean");
        }

        return e.GetBoolean();
    }

    public double Num(string key, double? def = null, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
    {
        if (!TryGet(key, out JsonElement e))
        {
            return def ?? throw Missing(key);
        }

        return NumValue(e, ChildPath(key), min, max);
    }

    public int Int(string key, int? def = null, int min = int.MinValue, int max = int.MaxValue)
    {
        if (!TryGet(key, out JsonElement e))
        {
            return def ?? throw Missing(key);
        }

        return IntValue(e, ChildPath(key), min, max);
    }

    /// <summary>uint64 — числом или десятичной строкой.</summary>
    public ulong ULong(string key)
    {
        if (!TryGet(key, out JsonElement e))
        {
            throw Missing(key);
        }

        string path = ChildPath(key);
        if (e.ValueKind == JsonValueKind.String)
        {
            if (ulong.TryParse(e.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out ulong s))
            {
                return s;
            }

            throw new ScenarioException($"{path}: expected unsigned 64-bit integer (decimal string)");
        }

        if (e.ValueKind == JsonValueKind.Number && e.TryGetUInt64(out ulong v))
        {
            return v;
        }

        throw new ScenarioException($"{path}: expected unsigned 64-bit integer");
    }

    public static double NumValue(JsonElement e, string path, double min = double.NegativeInfinity, double max = double.PositiveInfinity)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out double v) || !double.IsFinite(v))
        {
            throw new ScenarioException($"{path}: expected number");
        }

        if (v < min || v > max)
        {
            throw new ScenarioException($"{path}: value {Fmt(v)} out of range [{Fmt(min)}, {Fmt(max)}]");
        }

        return v;
    }

    public static int IntValue(JsonElement e, string path, int min = int.MinValue, int max = int.MaxValue)
    {
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out int v))
        {
            throw new ScenarioException($"{path}: expected integer");
        }

        if (v < min || v > max)
        {
            throw new ScenarioException($"{path}: value {Fmt(v)} out of range [{Fmt(min)}, {Fmt(max)}]");
        }

        return v;
    }

    private static string Fmt(double d) => d.ToString(CultureInfo.InvariantCulture);

    private bool TryGet(string key, out JsonElement e)
    {
        if (_props.TryGetValue(key, out e))
        {
            _used.Add(key);
            return true;
        }

        return false;
    }

    private ScenarioException Missing(string key) =>
        new($"{ChildPath(key)}: required key is missing");
}
