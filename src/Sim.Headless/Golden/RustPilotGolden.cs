using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Sim.Core.Api;
using Sim.Core.Metrics;
using Sim.Core.Rules;
using Sim.Core.Util;
using Sim.Scenarios;

namespace Sim.Headless.Golden;

/// <summary>
/// Генератор кросс-языкового golden Rust-пилота (rust-pilot.md §5): <c>util.json</c> и по файлу на сценарий.
/// Вывод побайтно детерминирован (LF, инвариантная культура, без времени). Файлы читают и C#-тест, и Rust.
/// </summary>
public static class RustPilotGolden
{
    public const int GoldenVersion = 1;
    public const int IdmCount = 10_000;
    public const int IdmPowCount = 1_000;
    public const string DemoGrid = "scenarios/demo-grid.json";
    public const string FixturesDir = "scenarios";

    private static readonly Regex _profileErrorRx = new(@"invalid road profile \((\w+)\)", RegexOptions.CultureInvariant);
    private static readonly ulong[] _pcgSeeds = [0UL, 1UL, 42UL, (1UL << 63) + 5UL];
    private static readonly ulong[] _pcgStreams = [0UL, 1UL, 6UL, 1UL << 62];
    private static readonly int[] _xxLens = [0, 1, 3, 4, 7, 8, 31, 32, 33, 63, 64, 100, 1000, 4097];
    private static readonly ulong[] _xxSeeds = [0UL, 0x9E3779B97F4A7C15UL];
    private static readonly int[] _xxChunks = [1, 7, 32];

    /// <summary>Сценарии golden: путь от корня репозитория (демо-сетка и фикстуры из <paramref name="goldenDir"/>/scenarios), в порядке имён.</summary>
    public static List<string> ScenarioPaths(string root, string goldenDir)
    {
        var list = new List<string> { DemoGrid };
        string fx = Path.Combine(goldenDir, FixturesDir);
        if (Directory.Exists(fx))
        {
            var names = new List<string>(Directory.GetFiles(fx, "*.json"));
            names.Sort(StringComparer.Ordinal);
            foreach (string n in names)
            {
                list.Add(Path.GetRelativePath(root, n).Replace('\\', '/'));
            }
        }

        return list;
    }

    /// <summary>Имя golden-файла сценария: имя файла сценария без расширения + <c>.json</c>.</summary>
    public static string FileNameFor(string scenarioPath) => Path.GetFileNameWithoutExtension(scenarioPath) + ".json";

    /// <summary>Содержимое golden-файла сценария (rust-pilot.md §5.1).</summary>
    public static string ScenarioJson(string root, string scenarioPath)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"golden_version\": ").Append(GoldenVersion.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("  \"scenario\": \"").Append(scenarioPath).Append("\",\n");

        ScenarioDesc desc;
        try
        {
            desc = ScenarioLoader.LoadInfo(Path.Combine(root, scenarioPath), null, new ScenarioOverrides()).Desc;
        }
        catch (ScenarioException e)
        {
            // Каталог профилей разобран, но не прошёл валидацию: вид ошибки (RoadProfileError) - часть golden, Rust сверяет его с C# (ADR-0030).
            Match m = _profileErrorRx.Match(e.Message);
            sb.Append("  \"load_ok\": false");
            if (m.Success)
            {
                sb.Append(",\n  \"profile_error\": \"").Append(m.Groups[1].Value).Append('"');
            }

            sb.Append("\n}\n");
            return sb.ToString();
        }

        CreateStatus st = Simulation.TryCreate(in desc, out Simulation? sim);
        sb.Append("  \"load_ok\": true,\n");
        sb.Append("  \"create_status\": ").Append(((int)st).ToString(CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("  \"desc_hash\": \"").Append(Hex(NetworkHash.Desc(in desc))).Append('"');
        if (st == CreateStatus.Ok && sim != null)
        {
            using (sim)
            {
                Sim.Core.World w = sim.World;
                Sim.Core.Network.NetworkStore n = w.Net;
                sb.Append(",\n  \"network_hash\": \"").Append(Hex(sim.ComputeNetworkHash())).Append("\",\n");
                sb.Append("  \"conflicts_hash\": \"").Append(Hex(NetworkHash.Conflicts(w))).Append("\",\n");
                sb.Append("  \"headings_hash\": \"").Append(Hex(NetworkHash.Headings(w))).Append("\",\n");
                sb.Append("  \"counts\": { ");
                sb.Append("\"nodes\": ").Append(N(n.NodeCount));
                sb.Append(", \"segments\": ").Append(N(n.SegmentCount));
                sb.Append(", \"lanes\": ").Append(N(n.LaneCount));
                sb.Append(", \"lane_points\": ").Append(N(n.LanePointCount));
                sb.Append(", \"segment_points\": ").Append(N(n.SegmentPointCount));
                sb.Append(", \"arms\": ").Append(N(n.NodeArmCount));
                sb.Append(", \"lane_next\": ").Append(N(n.LaneNextCount));
                sb.Append(", \"connectors\": ").Append(N(w.Connectors.Count));
                sb.Append(", \"conflicts\": ").Append(N(w.Connectors.ConflictsUsed));
                sb.Append(" }");
            }
        }

        sb.Append("\n}\n");
        return sb.ToString();
    }

    /// <summary>Содержимое <c>util.json</c>: PCG32, FNV-1a 64, XXH64, min/max, float-&gt;int, денормалы, векторы IDM (rust-pilot.md §5.1).</summary>
    public static string UtilJson(string root)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"golden_version\": ").Append(N(GoldenVersion)).Append(",\n");
        AppendPcg(sb);
        sb.Append(",\n");
        AppendFnv(sb);
        sb.Append(",\n");
        AppendXxh(sb);
        sb.Append(",\n");
        AppendMinMax(sb);
        sb.Append(",\n");
        AppendFloatInt(sb);
        sb.Append(",\n");
        AppendIdm(sb, root, IdmCount, IdmDeltaMix.Gated);
        sb.Append("\n}\n");
        return sb.ToString();
    }

    /// <summary>
    /// Содержимое <c>idm-pow.json</c>: наборы IDM с <c>delta</c> вне {2, 4} (<c>MathF.Pow</c> = системный libm). Это не гейт:
    /// между ОС (glibc/UCRT) биты расходятся, Rust считает счётчик расхождений (rust-pilot.md §6.3, R7).
    /// </summary>
    public static string IdmPowJson(string root)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"golden_version\": ").Append(N(GoldenVersion)).Append(",\n");
        AppendIdm(sb, root, IdmPowCount, IdmDeltaMix.Other);
        sb.Append("\n}\n");
        return sb.ToString();
    }

    private enum IdmDeltaMix
    {
        Gated,
        Other,
    }

    private static void AppendIdm(StringBuilder sb, string root, int count, IdmDeltaMix mix)
    {
        ScenarioDesc desc = ScenarioLoader.LoadInfo(Path.Combine(root, DemoGrid), null, new ScenarioOverrides()).Desc;
        // Вектора берут только первые два типа (car, bus): эталон Rust-пилота не зависит от числа типов в каталоге (VC1 добавил фургон, фуру и спецмашины).
        VehicleTypeDesc[] types = desc.VehicleTypes.Length > 2 ? desc.VehicleTypes[..2] : desc.VehicleTypes;
        bool gated = mix == IdmDeltaMix.Gated;
        var r = Pcg32.Create(gated ? 0x1D17UL : 0x1D18UL, (ulong)RngStream.Behaviour);
        float[] otherDeltas = [3f, 2.5f, 1.5f, 5f, 6f, 1f, 3.3f, 0.5f];
        sb.Append(gated ? "  \"idm\": {\n" : "  \"idm_other_delta\": {\n");
        sb.Append("    \"note\": \"rows are f32 bits [amax, b, T, s0, delta, max_speed, speed_factor, lane_limit, v, gap, dv, v0 = Idm.DesiredSpeed, a = Idm.Accel]; ")
            .Append(gated ? "delta in {4, 2}\",\n" : "delta not in {2, 4} (MathF.Pow, not a gate)\",\n");
        sb.Append("    \"rows\": [\n");
        for (int i = 0; i < count; i++)
        {
            VehicleTypeDesc t = types[(int)r.NextBelow((uint)types.Length)];
            if (r.NextBelow(2) == 1)
            {
                t.Accel = r.NextRange(0.3f, 3f);
                t.ComfortDecel = r.NextRange(0.5f, 4f);
                t.HeadwayTime = r.NextRange(0.3f, 3f);
                t.MinGap = r.NextRange(0.5f, 5f);
                t.MaxSpeed = r.NextRange(5f, 45f);
                t.SpeedFactor = r.NextRange(0.7f, 1.3f);
            }

            t.Delta = gated ? (r.NextBelow(2) == 0 ? 4f : 2f) : otherDeltas[(int)r.NextBelow((uint)otherDeltas.Length)];
            float limit = r.NextBelow(32) == 0 ? 0.01f : r.NextRange(2.78f, 36.1f);
            float v = r.NextBelow(16) == 0 ? 0f : r.NextRange(0f, 40f);
            uint gapKind = r.NextBelow(10);
            float gap = gapKind == 0 ? Idm.FreeGap : gapKind == 1 ? r.NextRange(0f, 0.02f) : r.NextRange(0.5f, 150f);
            float dv = r.NextRange(-15f, 25f);
            float v0 = Idm.DesiredSpeed(in t, limit);
            float a = Idm.Accel(in t, v, v0, gap, dv);
            sb.Append("      [")
                .Append(N(Bits(t.Accel))).Append(", ").Append(N(Bits(t.ComfortDecel))).Append(", ")
                .Append(N(Bits(t.HeadwayTime))).Append(", ").Append(N(Bits(t.MinGap))).Append(", ")
                .Append(N(Bits(t.Delta))).Append(", ").Append(N(Bits(t.MaxSpeed))).Append(", ")
                .Append(N(Bits(t.SpeedFactor))).Append(", ").Append(N(Bits(limit))).Append(", ")
                .Append(N(Bits(v))).Append(", ").Append(N(Bits(gap))).Append(", ").Append(N(Bits(dv))).Append(", ")
                .Append(N(Bits(v0))).Append(", ").Append(N(Bits(a))).Append(i + 1 < count ? "],\n" : "]\n");
        }

        sb.Append("    ]\n  }");
    }

    private static void AppendPcg(StringBuilder sb)
    {
        sb.Append("  \"pcg32\": [\n");
        bool first = true;
        foreach (ulong seed in _pcgSeeds)
        {
            foreach (ulong stream in _pcgStreams)
            {
                if (!first)
                {
                    sb.Append(",\n");
                }

                first = false;
                var r = Pcg32.Create(seed, stream);
                var first16 = new List<string>();
                for (int i = 0; i < 16; i++)
                {
                    first16.Add(N(r.NextUInt()));
                }

                for (int i = 16; i < 1000; i++)
                {
                    r.NextUInt();
                }

                ulong state = r.State;
                ulong inc = r.Inc;
                var f01 = new List<string>();
                var below7 = new List<string>();
                var range = new List<string>();
                var ints = new List<string>();
                r = Pcg32.Create(seed, stream);
                for (int i = 0; i < 16; i++)
                {
                    f01.Add(N(BitConverter.SingleToUInt32Bits(r.NextFloat01())));
                }

                r = Pcg32.Create(seed, stream);
                for (int i = 0; i < 16; i++)
                {
                    below7.Add(N(r.NextBelow(7)));
                }

                r = Pcg32.Create(seed, stream);
                for (int i = 0; i < 16; i++)
                {
                    range.Add(N(BitConverter.SingleToUInt32Bits(r.NextRange(-3.5f, 12.25f))));
                }

                r = Pcg32.Create(seed, stream);
                for (int i = 0; i < 16; i++)
                {
                    ints.Add(N(r.NextInt(-5, 100)));
                }

                sb.Append("    { \"seed\": \"").Append(Hex(seed)).Append("\", \"stream\": \"").Append(Hex(stream)).Append("\",\n");
                sb.Append("      \"first16\": ").Append(Arr(first16)).Append(",\n");
                sb.Append("      \"state_after_1000\": \"").Append(Hex(state)).Append("\", \"inc\": \"").Append(Hex(inc)).Append("\",\n");
                sb.Append("      \"float01_bits\": ").Append(Arr(f01)).Append(",\n");
                sb.Append("      \"below7\": ").Append(Arr(below7)).Append(",\n");
                sb.Append("      \"range_bits\": ").Append(Arr(range)).Append(",\n");
                sb.Append("      \"int_range\": ").Append(Arr(ints)).Append(" }");
            }
        }

        sb.Append("\n  ]");
    }

    private static void AppendFnv(StringBuilder sb)
    {
        byte[] bytes = [0, 1, 2, 127, 128, 255, 165, 90];
        int[] ints = [0, 1, -1, 255, 256, int.MinValue, int.MaxValue, 305419896];
        long[] longs = [0L, 1L, -1L, 4294967295L, 4294967296L, long.MinValue, long.MaxValue, 1311768467463790320L];
        uint[] floatBits = [0x0u, 0x80000000u, 0x3f800000u, 0xbfc00000u, 0xffc00000u, 0x7f800000u, 0xff800000u, 0x1u];

        var h = Fnv1a64.Create();
        ulong empty = h.Value;
        foreach (byte b in bytes)
        {
            h.Add(b);
        }

        var byEach = new List<string>();
        var byInt = new List<string>();
        var byUint = new List<string>();
        var byLong = new List<string>();
        var byUlong = new List<string>();
        var byFloat = new List<string>();
        foreach (byte b in bytes)
        {
            var x = Fnv1a64.Create();
            x.Add(b);
            byEach.Add(Q(Hex(x.Value)));
        }

        foreach (int v in ints)
        {
            var x = Fnv1a64.Create();
            x.Add(v);
            byInt.Add(Q(Hex(x.Value)));
            x = Fnv1a64.Create();
            x.Add(unchecked((uint)v));
            byUint.Add(Q(Hex(x.Value)));
        }

        foreach (long v in longs)
        {
            var x = Fnv1a64.Create();
            x.Add(v);
            byLong.Add(Q(Hex(x.Value)));
            x = Fnv1a64.Create();
            x.Add(unchecked((ulong)v));
            byUlong.Add(Q(Hex(x.Value)));
        }

        foreach (uint bits in floatBits)
        {
            var x = Fnv1a64.Create();
            x.Add(BitConverter.UInt32BitsToSingle(bits));
            byFloat.Add(Q(Hex(x.Value)));
        }

        sb.Append("  \"fnv1a64\": {\n");
        sb.Append("    \"empty\": \"").Append(Hex(empty)).Append("\",\n");
        sb.Append("    \"bytes\": ").Append(Arr(Strs(bytes))).Append(",\n");
        sb.Append("    \"bytes_hash\": \"").Append(Hex(h.Value)).Append("\",\n");
        sb.Append("    \"bytes_single_hashes\": ").Append(Arr(byEach)).Append(",\n");
        sb.Append("    \"ints\": ").Append(Arr(Strs(ints))).Append(",\n");
        sb.Append("    \"int_hashes\": ").Append(Arr(byInt)).Append(",\n");
        sb.Append("    \"uint_hashes\": ").Append(Arr(byUint)).Append(",\n");
        sb.Append("    \"longs\": ").Append(Arr(Strs(longs))).Append(",\n");
        sb.Append("    \"long_hashes\": ").Append(Arr(byLong)).Append(",\n");
        sb.Append("    \"ulong_hashes\": ").Append(Arr(byUlong)).Append(",\n");
        sb.Append("    \"float_bits\": ").Append(Arr(Strs(floatBits))).Append(",\n");
        sb.Append("    \"float_hashes\": ").Append(Arr(byFloat)).Append("\n  }");
    }

    private static byte[] XxData(int len)
    {
        var data = new byte[len];
        for (int i = 0; i < len; i++)
        {
            data[i] = (byte)((i * 31) + 7);
        }

        return data;
    }

    private static void AppendXxh(StringBuilder sb)
    {
        sb.Append("  \"xxh64\": {\n");
        sb.Append("    \"data\": \"b[i] = (i * 31 + 7) & 0xFF\",\n");
        sb.Append("    \"lens\": ").Append(Arr(Strs(_xxLens))).Append(",\n");
        sb.Append("    \"chunks\": ").Append(Arr(Strs(_xxChunks))).Append(",\n");
        sb.Append("    \"runs\": [\n");
        bool first = true;
        foreach (ulong seed in _xxSeeds)
        {
            if (!first)
            {
                sb.Append(",\n");
            }

            first = false;
            var whole = new List<string>();
            var chunked = new List<List<string>> { new(), new(), new() };
            foreach (int len in _xxLens)
            {
                byte[] data = XxData(len);
                ulong one = XxHash64.Hash(data, seed);
                whole.Add(Q(Hex(one)));
                for (int c = 0; c < _xxChunks.Length; c++)
                {
                    var s = XxHash64.Create(seed);
                    int step = _xxChunks[c];
                    for (int off = 0; off < len; off += step)
                    {
                        s.Append(new ReadOnlySpan<byte>(data, off, Math.Min(step, len - off)));
                    }

                    chunked[c].Add(Q(Hex(s.Value)));
                }
            }

            sb.Append("      { \"seed\": \"").Append(Hex(seed)).Append("\",\n");
            sb.Append("        \"hashes\": ").Append(Arr(whole)).Append(",\n");
            sb.Append("        \"chunked_hashes\": [").Append(string.Join(", ", chunked.ConvertAll(Arr))).Append("] }");
        }

        sb.Append("\n    ]\n  }");
    }

    private static void AppendMinMax(StringBuilder sb)
    {
        uint[] vals = [0x80000000u, 0x0u, 0x3f800000u, 0xbf800000u, 0xffc00000u, 0x7f800000u, 0xff800000u];
        sb.Append("  \"minmax\": {\n    \"note\": \"MathF.Max/MathF.Min; rows are [x_bits, y_bits, max_bits, min_bits]\",\n    \"rows\": [\n");
        bool first = true;
        foreach (uint xb in vals)
        {
            foreach (uint yb in vals)
            {
                if (!first)
                {
                    sb.Append(",\n");
                }

                first = false;
                float x = BitConverter.UInt32BitsToSingle(xb);
                float y = BitConverter.UInt32BitsToSingle(yb);
                sb.Append("      [").Append(N(xb)).Append(", ").Append(N(yb)).Append(", ")
                    .Append(N(BitConverter.SingleToUInt32Bits(MathF.Max(x, y)))).Append(", ")
                    .Append(N(BitConverter.SingleToUInt32Bits(MathF.Min(x, y)))).Append(']');
            }
        }

        sb.Append("\n    ]\n  }");
    }

    private static void AppendFloatInt(StringBuilder sb)
    {
        float[] vals =
        [
            0f, -0f, 0.4f, 0.5f, 0.6f, 1.5f, 2.5f, 3.5f, -0.4f, -0.5f, -0.6f, -1.5f, -2.5f, 123456.7f, -123456.7f,
            16777216f, 2147483520f, -2147483648f, 1e-3f, 0.1f + 0.2f,
        ];
        sb.Append("  \"float_int\": {\n    \"note\": \"rows are [x_bits, trunc (int)x, round_to_even, round_away, floor, ceil]; MathF.Round returns float, results cast to int\",\n    \"rows\": [\n");
        for (int i = 0; i < vals.Length; i++)
        {
            float x = vals[i];
            sb.Append("      [").Append(N(BitConverter.SingleToUInt32Bits(x))).Append(", ")
                .Append(N((int)x)).Append(", ")
                .Append(N((int)MathF.Round(x))).Append(", ")
                .Append(N((int)MathF.Round(x, MidpointRounding.AwayFromZero))).Append(", ")
                .Append(N((int)MathF.Floor(x))).Append(", ")
                .Append(N((int)MathF.Ceiling(x))).Append(i + 1 < vals.Length ? "],\n" : "]\n");
        }

        sb.Append("    ]\n  },\n");

        // Денормалы: значения и результаты арифметики побитово (ловит FTZ/DAZ в процессе-хосте).
        float tiny = BitConverter.UInt32BitsToSingle(1u);
        float sub = BitConverter.UInt32BitsToSingle(0x00400000u);
        float minNormal = BitConverter.UInt32BitsToSingle(0x00800000u);
        sb.Append("  \"denormal\": {\n    \"note\": \"f32 bits: inputs a, b and a+b, a*b, a*0.5, a/2, b-a, sqrt(a)\",\n    \"rows\": [\n");
        (float A, float B)[] pairs = [(tiny, tiny), (tiny, sub), (sub, sub), (minNormal, tiny), (minNormal, -sub), (sub, 0.5f)];
        for (int i = 0; i < pairs.Length; i++)
        {
            float a = pairs[i].A;
            float b = pairs[i].B;
            sb.Append("      [").Append(N(Bits(a))).Append(", ").Append(N(Bits(b))).Append(", ")
                .Append(N(Bits(a + b))).Append(", ")
                .Append(N(Bits(a * b))).Append(", ")
                .Append(N(Bits(a * 0.5f))).Append(", ")
                .Append(N(Bits(a / 2f))).Append(", ")
                .Append(N(Bits(b - a))).Append(", ")
                .Append(N(Bits(MathF.Sqrt(a)))).Append(i + 1 < pairs.Length ? "],\n" : "]\n");
        }

        sb.Append("    ]\n  }");
    }

    private static uint Bits(float f) => BitConverter.SingleToUInt32Bits(f);

    private static string Hex(ulong v) => v.ToString("x16", CultureInfo.InvariantCulture);

    private static string Q(string s) => "\"" + s + "\"";

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    private static string N(ulong v) => v.ToString(CultureInfo.InvariantCulture);

    private static string Arr(List<string> items) => "[" + string.Join(", ", items) + "]";

    private static List<string> Strs<T>(T[] values)
        where T : IFormattable
    {
        var l = new List<string>(values.Length);
        foreach (T v in values)
        {
            l.Add(v.ToString(null, CultureInfo.InvariantCulture));
        }

        return l;
    }
}
