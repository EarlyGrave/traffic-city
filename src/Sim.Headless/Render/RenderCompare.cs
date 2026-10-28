using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using Sim.Headless.Bench;
using Sim.Scenarios.Mods;
using Sim.Scenarios.Mods.Gltf;

namespace Sim.Headless.Render;

/// <summary>
/// Гейт рендера клиента (docs/design/render.md §8.3, ADR-0014 п. 9). Вход — JSON <c>RenderStats</c> клиента
/// (<c>scripts/godot-shot.sh stats</c>): файл или каталог с <c>stats-*.json</c>. Гейтятся только детерминированные счётчики
/// (<c>deterministic</c>); <c>timing</c> (FPS, время) печатается справочно.
/// <c>render-compare --result dir [--budget content/render-budgets.json] [--baseline perf/render-baseline.json] [--tolerance 0.10]</c>:
/// exit 1 при превышении бюджета кадра пресета (<c>presets.*.frame</c>) или росте счётчика сверх допуска к baseline клиента;
/// 2 - ошибка ввода (нет результатов, разное окружение у baseline и результата).
/// <c>render-update-baseline --from dir --baseline file [--merge]</c> - единственный легальный способ менять baseline клиента (ADR-0007).
/// </summary>
public static class RenderCompare
{
    public const int ExitOk = 0;
    public const int ExitRegression = 1;
    public const int ExitUsage = 2;

    public const int BaselineFormat = 1;

    public const string Usage =
        "Usage: Sim.Headless render-compare --result <dir|stats.json> [--budget <render-budgets.json>] [--baseline <perf/render-baseline.json>] [--tolerance 0.10]\n"
        + "       (at least one of --budget / --baseline is required)\n";

    public const string UpdateUsage =
        "Usage: Sim.Headless render-update-baseline --from <dir|stats.json> --baseline <perf/render-baseline.json> [--merge]\n";

    /// <summary>Гейтируемые метрики: имя в baseline -> путь в <c>deterministic</c>.</summary>
    internal static readonly (string Name, string[] Path)[] Metrics =
    [
        ("main.draw_calls", ["main", "draw_calls"]),
        ("main.primitives", ["main", "primitives"]),
        ("main.objects", ["main", "objects"]),
        ("shadow.draw_calls", ["shadow", "draw_calls"]),
        ("shadow.primitives", ["shadow", "primitives"]),
        ("shadow.objects", ["shadow", "objects"]),
        ("memory.video_bytes", ["memory_bytes", "video"]),
        ("memory.texture_bytes", ["memory_bytes", "texture"]),
        ("memory.buffer_bytes", ["memory_bytes", "buffer"]),
        ("instances.total", ["instances", "total"]),
    ];

    private static readonly string[] _compareOptions = ["--result", "--budget", "--baseline", "--tolerance"];
    private static readonly string[] _updateOptions = ["--from", "--baseline"];

    /// <summary>Один замер: ключ <c>сценарий/поза/пресет</c>, окружение и числа.</summary>
    internal sealed record Entry(string Key, string Preset, string Env, SortedDictionary<string, long> Values, SortedDictionary<string, double> Timing);

    public static int RunCompare(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _compareOptions, out CliArgs a) || !a.TryGetDouble("--tolerance", 0, 10, out double tol, 0.10))
        {
            error.WriteLine($"render-compare: {a.Error}");
            error.Write(Usage);
            return ExitUsage;
        }

        string? resultPath = a.Get("--result");
        string? budgetPath = a.Get("--budget");
        string? basePath = a.Get("--baseline");
        if (resultPath == null || (budgetPath == null && basePath == null))
        {
            error.WriteLine("render-compare: --result and (--budget or --baseline) are required");
            error.Write(Usage);
            return ExitUsage;
        }

        if (!TryLoadResults(resultPath, out List<Entry> results, out string err))
        {
            error.WriteLine($"render-compare: {err}");
            return ExitUsage;
        }

        RenderBudgets? budgets = null;
        if (budgetPath != null)
        {
            var issues = new List<ModIssue>();
            budgets = File.Exists(budgetPath) ? RenderBudgets.Load(budgetPath, issues) : null;
            if (budgets == null)
            {
                error.WriteLine($"render-compare: cannot read budgets {budgetPath}{(issues.Count > 0 ? ": " + issues[0] : string.Empty)}");
                return ExitUsage;
            }
        }

        Dictionary<string, SortedDictionary<string, long>>? baseline = null;
        if (basePath != null)
        {
            if (!File.Exists(basePath))
            {
                error.WriteLine($"render-compare: baseline not found: {basePath}. Create it with: Sim.Headless render-update-baseline --from {resultPath} --baseline {basePath}");
                return ExitUsage;
            }

            if (!TryLoadBaseline(basePath, out baseline, out string baseEnv, out err))
            {
                error.WriteLine($"render-compare: {err}");
                return ExitUsage;
            }

            string? mismatch = results.Select(e => e.Env).Distinct().FirstOrDefault(e => e != baseEnv);
            if (mismatch != null)
            {
                error.WriteLine($"render-compare: refusing to compare different environments (baseline '{baseEnv}', result '{mismatch}')");
                return ExitUsage;
            }
        }

        CultureInfo ci = CultureInfo.InvariantCulture;
        bool fail = false;
        foreach (Entry e in results)
        {
            output.WriteLine(e.Key);
            if (budgets != null)
            {
                fail |= CheckBudget(e, budgets, output);
            }

            if (baseline != null)
            {
                if (!baseline.TryGetValue(e.Key, out SortedDictionary<string, long>? b))
                {
                    output.WriteLine("  warning: no baseline entry for this key (new scenario/pose/preset)");
                }
                else
                {
                    fail |= CheckBaseline(e, b, tol, output);
                }
            }

            if (e.Timing.Count > 0)
            {
                output.WriteLine(string.Create(ci, $"  info (not gated): {string.Join("  ", e.Timing.Select(t => $"{t.Key}={t.Value:0.###}"))}"));
            }
        }

        if (baseline != null)
        {
            foreach (string missing in baseline.Keys.Where(k => results.All(r => r.Key != k)))
            {
                output.WriteLine($"warning: baseline entry {missing} has no result in this run");
            }
        }

        output.WriteLine(fail ? "FAIL" : "OK");
        return fail ? ExitRegression : ExitOk;
    }

    public static int RunUpdate(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _updateOptions, ["--merge"], out CliArgs a))
        {
            error.WriteLine($"render-update-baseline: {a.Error}");
            error.Write(UpdateUsage);
            return ExitUsage;
        }

        string? from = a.Get("--from");
        string? target = a.Get("--baseline");
        if (from == null || target == null)
        {
            error.WriteLine("render-update-baseline: --from and --baseline are required");
            error.Write(UpdateUsage);
            return ExitUsage;
        }

        if (!TryLoadResults(from, out List<Entry> results, out string err))
        {
            error.WriteLine($"render-update-baseline: {err}");
            return ExitUsage;
        }

        string env = results[0].Env;
        string? other = results.Select(e => e.Env).FirstOrDefault(e => e != env);
        if (other != null)
        {
            error.WriteLine($"render-update-baseline: results come from different environments ('{env}' and '{other}')");
            return ExitUsage;
        }

        var entries = new SortedDictionary<string, SortedDictionary<string, long>>(StringComparer.Ordinal);
        if (a.Has("--merge") && File.Exists(target))
        {
            if (!TryLoadBaseline(target, out Dictionary<string, SortedDictionary<string, long>>? old, out string oldEnv, out err))
            {
                error.WriteLine($"render-update-baseline: {err}");
                return ExitUsage;
            }

            if (oldEnv != env)
            {
                error.WriteLine($"render-update-baseline: --merge refused, environment differs (baseline '{oldEnv}', results '{env}')");
                return ExitUsage;
            }

            foreach (KeyValuePair<string, SortedDictionary<string, long>> kv in old)
            {
                entries[kv.Key] = kv.Value;
            }
        }

        foreach (Entry e in results)
        {
            entries[e.Key] = e.Values;
        }

        string? dir = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using (var ms = new MemoryStream())
        {
            using (var j = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                j.WriteStartObject();
                j.WriteNumber("format", BaselineFormat);
                j.WriteString("environment", env);
                j.WriteStartObject("entries");
                foreach (KeyValuePair<string, SortedDictionary<string, long>> kv in entries)
                {
                    j.WriteStartObject(kv.Key);
                    foreach (KeyValuePair<string, long> m in kv.Value)
                    {
                        j.WriteNumber(m.Key, m.Value);
                    }

                    j.WriteEndObject();
                }

                j.WriteEndObject();
                j.WriteEndObject();
            }

            File.WriteAllBytes(target, ms.ToArray());
        }

        File.AppendAllText(target, "\n");
        output.WriteLine($"render baseline updated: {target} <- {from} ({results.Count} entries, environment {env})");
        return ExitOk;
    }

    private static bool CheckBudget(Entry e, RenderBudgets budgets, TextWriter output)
    {
        if (!budgets.Presets.TryGetValue(e.Preset, out PresetBudget? p) || p.Frame == null)
        {
            output.WriteLine($"  warning: no frame budget for preset '{e.Preset}' in budgets file");
            return false;
        }

        FrameBudget f = p.Frame;
        long V(string n) => e.Values[n];
        bool fail = false;
        (string Label, long Value, long? Limit)[] checks =
        [
            ("draw_calls (main+shadow)", V("main.draw_calls") + V("shadow.draw_calls"), f.DrawCalls),
            ("shadow.draw_calls", V("shadow.draw_calls"), f.ShadowDrawCalls),
            ("main.primitives", V("main.primitives"), f.Primitives),
            ("shadow.primitives", V("shadow.primitives"), f.ShadowPrimitives),
            ("main.objects", V("main.objects"), f.Objects),
            ("texture_mb", V("memory.texture_bytes") >> 20, f.TextureMb),
            ("video_mb", V("memory.video_bytes") >> 20, f.VideoMb),
        ];
        foreach ((string label, long value, long? limit) in checks)
        {
            if (limit is long lim && value > lim)
            {
                fail = true;
                output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  {label,-26} {value,12} > budget {e.Preset} {lim}  OVER BUDGET"));
            }
        }

        if (!fail)
        {
            output.WriteLine($"  budget '{e.Preset}': ok");
        }

        return fail;
    }

    private static bool CheckBaseline(Entry e, SortedDictionary<string, long> b, double tol, TextWriter output)
    {
        bool fail = false;
        CultureInfo ci = CultureInfo.InvariantCulture;
        foreach ((string name, _) in Metrics)
        {
            if (!b.TryGetValue(name, out long bv))
            {
                output.WriteLine($"  warning: baseline has no {name}");
                continue;
            }

            long rv = e.Values[name];
            bool regress = bv == 0 ? rv > 0 : rv > bv * (1 + tol);
            if (regress || rv != bv)
            {
                string delta = bv != 0 ? string.Create(ci, $"{((double)rv / bv - 1) * 100:+0.0;-0.0}%") : "n/a";
                output.WriteLine(string.Create(ci, $"  {name,-22} baseline {bv,12} result {rv,12} {delta,8}{(regress ? "  REGRESSION" : string.Empty)}"));
            }

            fail |= regress;
        }

        return fail;
    }

    private static bool TryLoadResults(string path, out List<Entry> entries, out string err)
    {
        entries = [];
        err = string.Empty;
        string[] files;
        if (File.Exists(path))
        {
            files = [path];
        }
        else if (Directory.Exists(path))
        {
            files = Directory.GetFiles(path, "stats-*.json");
            Array.Sort(files, StringComparer.Ordinal);
        }
        else
        {
            err = $"result not found: {path}";
            return false;
        }

        if (files.Length == 0)
        {
            err = $"no stats-*.json in {path}";
            return false;
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string f in files)
        {
            if (!TryParseStats(f, out Entry? e, out err))
            {
                return false;
            }

            if (!keys.Add(e!.Key))
            {
                err = $"{f}: duplicate key {e.Key}";
                return false;
            }

            entries.Add(e);
        }

        return true;
    }

    private static bool TryParseStats(string file, out Entry? entry, out string err)
    {
        entry = null;
        err = string.Empty;
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(file));
            JsonElement root = d.RootElement;
            string scenario = Str(root, "scenario");
            string pose = Str(root, "pose");
            if (scenario.Length == 0 || pose.Length == 0 || !root.TryGetProperty("deterministic", out JsonElement det))
            {
                err = $"{file}: not a RenderStats result (scenario/pose/deterministic missing)";
                return false;
            }

            string preset = det.TryGetProperty("vehicle_layer", out JsonElement vl) ? Str(vl, "preset") : string.Empty;
            if (preset.Length == 0)
            {
                preset = "unknown";
            }

            var values = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach ((string name, string[] path) in Metrics)
            {
                JsonElement cur = det;
                foreach (string seg in path)
                {
                    if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(seg, out cur))
                    {
                        err = $"{file}: missing deterministic.{string.Join('.', path)}";
                        return false;
                    }
                }

                if (!cur.TryGetInt64(out long v))
                {
                    err = $"{file}: deterministic.{string.Join('.', path)} is not an integer";
                    return false;
                }

                values[name] = v;
            }

            var timing = new SortedDictionary<string, double>(StringComparer.Ordinal);
            if (root.TryGetProperty("timing", out JsonElement t) && t.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty p in t.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out double dv))
                    {
                        timing[p.Name] = dv;
                    }
                }
            }

            string env = $"{Str(root, "driver")}/{Str(root, "method")}/{Str(root, "resolution")}";
            entry = new Entry($"{scenario}/{pose}/{preset}", preset, env, values, timing);
            return true;
        }
        catch (JsonException e)
        {
            err = $"{file}: invalid JSON ({e.Message})";
        }
        catch (IOException e)
        {
            err = $"{file}: {e.Message}";
        }

        return false;
    }

    private static bool TryLoadBaseline(string path, out Dictionary<string, SortedDictionary<string, long>> entries, out string env, out string err)
    {
        entries = new Dictionary<string, SortedDictionary<string, long>>(StringComparer.Ordinal);
        env = string.Empty;
        err = string.Empty;
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = d.RootElement;
            if (!root.TryGetProperty("format", out JsonElement fmt) || !fmt.TryGetInt32(out int fv) || fv != BaselineFormat
                || !root.TryGetProperty("entries", out JsonElement ents) || ents.ValueKind != JsonValueKind.Object)
            {
                err = $"{path}: not a render baseline (format {BaselineFormat} with entries expected)";
                return false;
            }

            env = Str(root, "environment");
            foreach (JsonProperty p in ents.EnumerateObject())
            {
                var m = new SortedDictionary<string, long>(StringComparer.Ordinal);
                foreach (JsonProperty q in p.Value.EnumerateObject())
                {
                    if (!q.Value.TryGetInt64(out long v))
                    {
                        err = $"{path}: {p.Name}.{q.Name} is not an integer";
                        return false;
                    }

                    m[q.Name] = v;
                }

                entries[p.Name] = m;
            }

            return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or IOException)
        {
            err = $"{path}: {e.Message}";
            return false;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;
}
