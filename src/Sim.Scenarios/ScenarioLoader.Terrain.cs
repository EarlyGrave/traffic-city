using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Scenarios;

/// <summary>Вид штампа рельефа (terrain.md §9.2).</summary>
public enum TerrainStampKind
{
    Ridge = 0,
    Pit = 1,
}

/// <summary>Проём штампа: отсчёты ближе <see cref="WidthM"/>/2 к точке (по оси штампа) не поднимаются.</summary>
public readonly record struct TerrainStampGap(float X, float Z, float WidthM);

/// <summary>
/// Штамп рельефа (<c>terrain.stamps[]</c>, terrain.md §9.2): вал или выемка вдоль полилинии. Загрузчик разбирает и проверяет, ядро MVP (TR1) штампы не исполняет:
/// в <see cref="TerrainDesc"/> поля для них нет (остаток для TR7/TR8).
/// </summary>
public sealed class TerrainStampDesc
{
    public TerrainStampKind Kind { get; init; }

    public bool Closed { get; init; }

    public Vec2[] Points { get; init; } = [];

    public float HeightM { get; init; }

    public float CrestM { get; init; }

    public float Slope { get; init; }

    public TerrainStampGap[] Gaps { get; init; } = [];
}

public static partial class ScenarioLoader
{
    private const string TerrainKey = "terrain";
    private const int MaxTerrainSamples = 1 << 24;       // = TerrainStore.MaxSamples
    private const double MaxTerrainCoord = 1e7;
    private const double MaxHeightM = 655.35;             // ushort в сантиметрах
    private const int MaxSourceCellMm = 256_000;          // = TerrainBuild.MaxSourceCellMm
    private const int MaxAbsBaseCm = 1_000_000;           // = TerrainBuild.MaxAbsBaseCm
    private const int MaxBrushPerCell = 12;               // = TerrainParams.MaxBrushPerCell

    /// <summary>
    /// Секция <c>terrain</c> (terrain.md §9.1, ADR-0036). Нет секции или <c>enabled: false</c> - рельефа нет (<c>default</c>). Все ошибки - с JSON-путём,
    /// чтобы <c>Simulation.Create</c> не возвращал безликий <c>InvalidNetwork</c>.
    /// </summary>
    private static TerrainDesc ParseTerrain(
        JsonObjectReader? r, ToolsDesc tools, Func<string, byte[]>? readBytes, List<string> warnings, out TerrainStampDesc[] stamps)
    {
        stamps = [];
        if (r == null)
        {
            return default;
        }

        if (!r.Bool("enabled", true))
        {
            foreach (string k in new List<string>(r.Keys))
            {
                r.Raw(k);
            }

            return default;
        }

        var d = new TerrainDesc { Enabled = true };
        int cellM = r.Int("cell_m", 8);
        if (cellM is not (4 or 8 or 16))
        {
            throw new ScenarioException($"{r.ChildPath("cell_m")}: expected 4, 8 or 16 (got {cellM})");
        }

        d.CellMm = cellM * 1000;
        d.MarginM = (float)r.Num("margin_m", 0, min: 0, max: MaxTerrainCoord);   // 0 = дефолт ядра (1024)
        JsonObjectReader? ext = r.OptObject("extent");
        bool hasBounds = false;
        double bMinX = 0, bMinZ = 0, bMaxX = 0, bMaxZ = 0;
        if (ext != null)
        {
            d.HasExtent = true;
            bMinX = ext.Num("min_x", min: -MaxTerrainCoord, max: MaxTerrainCoord);
            bMinZ = ext.Num("min_z", min: -MaxTerrainCoord, max: MaxTerrainCoord);
            bMaxX = ext.Num("max_x", min: -MaxTerrainCoord, max: MaxTerrainCoord);
            bMaxZ = ext.Num("max_z", min: -MaxTerrainCoord, max: MaxTerrainCoord);
            if (bMaxX <= bMinX || bMaxZ <= bMinZ)
            {
                throw new ScenarioException($"{ext.Path}: expected min_x < max_x and min_z < max_z");
            }

            ext.Finish();
            (d.ExtentMinX, d.ExtentMinZ, d.ExtentMaxX, d.ExtentMaxZ) = ((float)bMinX, (float)bMinZ, (float)bMaxX, (float)bMaxZ);
            hasBounds = true;
        }
        else if (tools.HasWorldBounds)
        {
            (bMinX, bMinZ, bMaxX, bMaxZ) = (tools.BoundsMinX, tools.BoundsMinZ, tools.BoundsMaxX, tools.BoundsMaxZ);
            hasBounds = true;
        }

        if (hasBounds)
        {
            long nx = (long)Math.Floor((bMaxX - bMinX) / cellM) + 1;
            long nz = (long)Math.Floor((bMaxZ - bMinZ) / cellM) + 1;
            if (nx * nz > MaxTerrainSamples)
            {
                string where = d.HasExtent ? r.ChildPath("extent") : "$.tools.world_bounds";
                throw new ScenarioException(
                    $"{where}: about {nx * nz} samples at cell_m = {cellM} exceed the limit {MaxTerrainSamples} (4096 x 4096); increase {r.ChildPath("cell_m")} or shrink the extent");
            }
        }

        string source = r.Str("source", "flat");
        switch (source)
        {
            case "flat":
                d.Source = TerrainSource.Flat;
                d.FlatM = (float)r.Num("flat_m", 0, min: 0, max: MaxHeightM);
                if (r.Has("heightmap"))
                {
                    throw new ScenarioException($"{r.ChildPath("heightmap")}: allowed only with source = \"heightmap\"");
                }

                break;
            case "heightmap":
                d.Source = TerrainSource.Heightmap;
                ParseHeightmap(r.ReqObject("heightmap"), ref d, readBytes);
                break;
            case "noise":
                // Шум - генератор TR7 (terrain.md §9.3); ядро отвечает NotSupported. Ключи блока не разбираем, чтобы не закрепить формат раньше задачи.
                throw new ScenarioException($"{r.ChildPath("source")}: \"noise\" is not supported yet (generator, task TR7); use \"flat\" or \"heightmap\"");
            default:
                throw new ScenarioException($"{r.ChildPath("source")}: unknown source \"{source}\" (expected \"flat\", \"heightmap\" or \"noise\")");
        }

        if (r.Has("noise"))
        {
            throw new ScenarioException($"{r.ChildPath("noise")}: allowed only with source = \"noise\" (not supported yet, task TR7)");
        }

        int baseCm = d.Source == TerrainSource.Heightmap ? d.BaseCm : 0;
        JsonObjectReader? water = r.OptObject("water");
        if (water != null)
        {
            d.Water = ParseTerrainWater(water, baseCm, hasBounds, (bMinX, bMinZ, bMaxX, bMaxZ), warnings);
        }

        JsonElement[]? stampArr = r.OptArray("stamps");
        if (stampArr != null)
        {
            stamps = ParseTerrainStamps(stampArr, r.ChildPath("stamps"));
            if (stamps.Length > 0)
            {
                warnings.Add("terrain.stamps: штампы пока не исполняются ядром, игнорируются");
            }
        }

        ParseTerrainParams(r, ref d, cellM);
        r.Finish();
        return d;
    }

    // ---------------------------------------------------------------- heightmap

    private static void ParseHeightmap(JsonObjectReader h, ref TerrainDesc d, Func<string, byte[]>? readBytes)
    {
        int[] size = ReadIntPair(h, "size", 2, 1 << 14);
        if ((long)size[0] * size[1] > MaxTerrainSamples)
        {
            throw new ScenarioException($"{h.ChildPath("size")}: {size[0]} x {size[1]} samples exceed the limit {MaxTerrainSamples}");
        }

        double srcCellM = h.Num("cell_m", min: 0.001, max: MaxSourceCellMm / 1000.0);
        int srcCellMm = checked((int)Math.Round(srcCellM * 1000.0));
        if (Math.Abs((srcCellM * 1000.0) - srcCellMm) > 1e-6)
        {
            throw new ScenarioException($"{h.ChildPath("cell_m")}: must be a whole number of millimetres");
        }

        if (srcCellMm % d.CellMm != 0)
        {
            throw new ScenarioException(
                $"{h.ChildPath("cell_m")}: source cell {Fmt(srcCellM)} m must be a multiple of terrain.cell_m = {Fmt(d.CellMm / 1000.0)} m");
        }

        double[] origin = h.Has("origin_m") ? ReadNumPair(h, "origin_m", -MaxTerrainCoord, MaxTerrainCoord) : [0, 0];
        double baseM = h.Num("base_m", 0, min: -MaxAbsBaseCm / 100.0, max: MaxAbsBaseCm / 100.0);
        double unitM = h.Num("unit_m", 0.01, min: 0.001, max: 1.0);
        int unitMm = checked((int)Math.Round(unitM * 1000.0));
        if (Math.Abs((unitM * 1000.0) - unitMm) > 1e-6)
        {
            throw new ScenarioException($"{h.ChildPath("unit_m")}: must be a whole number of millimetres (0.001..1)");
        }

        bool hasFile = h.Has("file");
        bool hasB64 = h.Has("data_b64");
        if (hasFile == hasB64)
        {
            throw new ScenarioException($"{h.Path}: exactly one of \"file\" or \"data_b64\" is required");
        }

        int need = checked(size[0] * size[1] * 2);
        byte[] bytes;
        string srcPath;
        if (hasFile)
        {
            string file = h.Str("file");
            srcPath = h.ChildPath("file");
            if (file.Length == 0)
            {
                throw new ScenarioException($"{srcPath}: empty path");
            }

            if (readBytes == null)
            {
                throw new ScenarioException($"{srcPath}: reading binary files is not available in this load mode (use data_b64)");
            }

            try
            {
                bytes = readBytes(file);
            }
            catch (ScenarioException e)
            {
                throw new ScenarioException($"{srcPath}: {e.Message}", e);
            }
        }
        else
        {
            srcPath = h.ChildPath("data_b64");
            string b64 = h.Str("data_b64");
            bytes = new byte[(b64.Length / 4 * 3) + 3];
            if (!Convert.TryFromBase64String(b64, bytes, out int written))
            {
                throw new ScenarioException($"{srcPath}: invalid base64");
            }

            Array.Resize(ref bytes, written);
        }

        if (bytes.Length != need)
        {
            throw new ScenarioException($"{srcPath}: expected {need} bytes ({size[0]} x {size[1]} u16 little-endian), got {bytes.Length}");
        }

        h.Finish();

        var heights = new ushort[size[0] * size[1]];
        ushort max = 0;
        for (int i = 0; i < heights.Length; i++)
        {
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(i * 2, 2));
            heights[i] = v;
            if (v > max)
            {
                max = v;
            }
        }

        // Ядро считает рельеф в целых сантиметрах над base (ushort): выше 655.35 м над base - InvalidNetwork.
        if ((long)max * unitMm / 10 > ushort.MaxValue)
        {
            int at = Array.IndexOf(heights, max);
            throw new ScenarioException(
                $"{srcPath}: sample [{at % size[0]}, {at / size[0]}] is {Fmt(max * unitM)} m above base_m, the limit is {Fmt(MaxHeightM)} m; raise unit_m or base_m");
        }

        d.SourceHeights = heights;
        d.SourceSizeX = size[0];
        d.SourceSizeZ = size[1];
        d.SourceCellMm = srcCellMm;
        d.SourceOriginX = (float)origin[0];
        d.SourceOriginZ = (float)origin[1];
        d.BaseCm = checked((int)Math.Round(baseM * 100.0));
        d.SourceUnitMm = unitMm;
    }

    // ---------------------------------------------------------------- water

    private static TerrainWaterDesc ParseTerrainWater(
        JsonObjectReader w, int baseCm, bool hasBounds, (double MinX, double MinZ, double MaxX, double MaxZ) bounds, List<string> warnings)
    {
        var d = new TerrainWaterDesc { Lakes = [], Rivers = [], Sources = [] };
        if (w.Has("sea_level_m") && w.Raw("sea_level_m").ValueKind != JsonValueKind.Null)
        {
            d.HasSeaLevel = true;
            d.SeaLevelM = CheckWaterLevel(w.Num("sea_level_m", min: -MaxTerrainCoord, max: MaxTerrainCoord), baseCm, w.ChildPath("sea_level_m"));
        }
        else if (w.Has("sea_level_m"))
        {
            w.Raw("sea_level_m");
        }

        JsonElement[]? lakes = w.OptArray("lakes");
        if (lakes != null)
        {
            d.Lakes = new TerrainLakeDesc[lakes.Length];
            for (int i = 0; i < lakes.Length; i++)
            {
                var l = new JsonObjectReader(lakes[i], JsonObjectReader.Index(w.ChildPath("lakes"), i));
                float x = (float)l.Num("x", min: -MaxTerrainCoord, max: MaxTerrainCoord);
                float z = (float)l.Num("z", min: -MaxTerrainCoord, max: MaxTerrainCoord);
                float level = CheckWaterLevel(l.Num("level_m", min: -MaxTerrainCoord, max: MaxTerrainCoord), baseCm, l.ChildPath("level_m"));
                l.Finish();
                d.Lakes[i] = new TerrainLakeDesc { X = x, Z = z, LevelM = level };
            }
        }

        JsonElement[]? rivers = w.OptArray("rivers");
        if (rivers != null)
        {
            d.Rivers = new TerrainRiverDesc[rivers.Length];
            for (int i = 0; i < rivers.Length; i++)
            {
                string rp = JsonObjectReader.Index(w.ChildPath("rivers"), i);
                var rv = new JsonObjectReader(rivers[i], rp);
                Vec2[] pts = ReadPoints(rv, "points", 2, 4096);
                float width = (float)rv.Num("width_m", min: 0.01, max: 10000);
                float depth = (float)rv.Num("depth_m", min: 0.01, max: MaxHeightM);
                float flow = ReadOptFlow(rv, "flow_m3s");
                if (!float.IsNaN(flow))
                {
                    warnings.Add($"terrain.water.rivers[{i}].flow_m3s: течение не реализовано, значение игнорируется");
                }

                rv.Finish();
                d.Rivers[i] = new TerrainRiverDesc { Points = pts, WidthM = width, DepthM = depth, FlowM3s = flow };
            }
        }

        JsonElement[]? sources = w.OptArray("sources");
        if (sources != null)
        {
            d.Sources = new TerrainWaterSourceDesc[sources.Length];
            var ids = new HashSet<int>();
            for (int i = 0; i < sources.Length; i++)
            {
                var s = new JsonObjectReader(sources[i], JsonObjectReader.Index(w.ChildPath("sources"), i));
                int id = s.Int("id", min: 0);
                if (!ids.Add(id))
                {
                    throw new ScenarioException($"{s.ChildPath("id")}: duplicate source id {id}");
                }

                string kindStr = s.Str("kind");
                TerrainWaterSourceKind kind = kindStr switch
                {
                    "spring" => TerrainWaterSourceKind.Spring,
                    "inflow" => TerrainWaterSourceKind.Inflow,
                    "sink" => TerrainWaterSourceKind.Sink,
                    _ => throw new ScenarioException($"{s.ChildPath("kind")}: unknown kind \"{kindStr}\" (expected \"spring\", \"inflow\" or \"sink\")"),
                };
                double x = s.Num("x", min: -MaxTerrainCoord, max: MaxTerrainCoord);
                double z = s.Num("z", min: -MaxTerrainCoord, max: MaxTerrainCoord);
                if (hasBounds && (x < bounds.MinX || x > bounds.MaxX || z < bounds.MinZ || z > bounds.MaxZ))
                {
                    throw new ScenarioException($"{s.Path}: point ({Fmt(x)}, {Fmt(z)}) is outside the terrain extent");
                }

                float flow = ReadOptFlow(s, "flow_m3s");
                float target = s.Has("target_level_m") && s.Raw("target_level_m").ValueKind != JsonValueKind.Null
                    ? (float)s.Num("target_level_m", min: -MaxTerrainCoord, max: MaxTerrainCoord)
                    : float.NaN;
                if (s.Has("target_level_m") && float.IsNaN(target))
                {
                    s.Raw("target_level_m");
                }

                float radius = s.Has("radius_m") && s.Raw("radius_m").ValueKind != JsonValueKind.Null
                    ? (float)s.Num("radius_m", min: 0, max: MaxTerrainCoord)
                    : float.NaN;
                s.Finish();
                d.Sources[i] = new TerrainWaterSourceDesc { Id = id, Kind = kind, X = (float)x, Z = (float)z, FlowM3s = flow, TargetLevelM = target, RadiusM = radius };
            }

            if (sources.Length > 0)
            {
                warnings.Add("water.sources: течение не реализовано, источники игнорируются");
            }
        }

        w.Finish();
        return d;
    }

    // Уровень воды абсолютный, м; ядро держит воду в сантиметрах над base в [0, 65535] (InvalidNetwork иначе).
    private static float CheckWaterLevel(double levelM, int baseCm, string path)
    {
        long rel = (long)Math.Round(levelM * 100.0) - baseCm;
        if (rel < 0 || rel > ushort.MaxValue)
        {
            throw new ScenarioException(
                $"{path}: level {Fmt(levelM)} m is outside [{Fmt(baseCm / 100.0)}, {Fmt((baseCm + ushort.MaxValue) / 100.0)}] m (base_m .. base_m + 655.35)");
        }

        return (float)levelM;
    }

    // null/нет ключа = NaN («не задано»); иначе число >= 0.
    private static float ReadOptFlow(JsonObjectReader r, string key)
    {
        if (!r.Has(key))
        {
            return float.NaN;
        }

        JsonElement e = r.Raw(key);
        return e.ValueKind == JsonValueKind.Null ? float.NaN : (float)JsonObjectReader.NumValue(e, r.ChildPath(key), min: 0, max: 1e9);
    }

    // ---------------------------------------------------------------- stamps

    private static TerrainStampDesc[] ParseTerrainStamps(JsonElement[] arr, string path)
    {
        var res = new TerrainStampDesc[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            var s = new JsonObjectReader(arr[i], JsonObjectReader.Index(path, i));
            string kindStr = s.Str("kind");
            TerrainStampKind kind = kindStr switch
            {
                "ridge" => TerrainStampKind.Ridge,
                "pit" => TerrainStampKind.Pit,
                _ => throw new ScenarioException($"{s.ChildPath("kind")}: unknown kind \"{kindStr}\" (expected \"ridge\" or \"pit\")"),
            };
            bool closed = s.Bool("closed", false);
            Vec2[] pts = ReadPoints(s, "points", closed ? 3 : 2, 4096);
            float height = (float)s.Num("height_m", min: 0.01, max: MaxHeightM);
            float crest = (float)s.Num("crest_m", 0, min: 0, max: 10000);
            float slope = (float)s.Num("slope", 2.0, min: 0.01, max: 100);
            var gaps = new List<TerrainStampGap>();
            JsonElement[]? gapArr = s.OptArray("gaps");
            if (gapArr != null)
            {
                for (int k = 0; k < gapArr.Length; k++)
                {
                    var g = new JsonObjectReader(gapArr[k], JsonObjectReader.Index(s.ChildPath("gaps"), k));
                    gaps.Add(new TerrainStampGap(
                        (float)g.Num("x", min: -MaxTerrainCoord, max: MaxTerrainCoord),
                        (float)g.Num("z", min: -MaxTerrainCoord, max: MaxTerrainCoord),
                        (float)g.Num("width_m", min: 0.01, max: 10000)));
                    g.Finish();
                }
            }

            s.Finish();
            res[i] = new TerrainStampDesc { Kind = kind, Closed = closed, Points = pts, HeightM = height, CrestM = crest, Slope = slope, Gaps = gaps.ToArray() };
        }

        return res;
    }

    // ---------------------------------------------------------------- params

    // terrain.md §9.1: единицы JSON (%, м, доли) -> целые единицы ядра (‰, см). Ноль в ядре = дефолт, поэтому явные значения обязаны быть > 0.
    private static void ParseTerrainParams(JsonObjectReader r, ref TerrainDesc d, int cellM)
    {
        JsonObjectReader? g = r.OptObject("max_grade_pct");
        if (g != null)
        {
            if (g.Has("street"))
            {
                d.MaxGradeStreetPm = (int)Math.Round(g.Num("street", min: 0.1, max: 100) * 10);
            }

            if (g.Has("arterial"))
            {
                d.MaxGradeArterialPm = (int)Math.Round(g.Num("arterial", min: 0.1, max: 100) * 10);
            }

            g.Finish();
        }

        d.MaxCutCm = OptCm(r, "max_cut_m", d.MaxCutCm);
        d.MaxFillCm = OptCm(r, "max_fill_m", d.MaxFillCm);
        if (r.Has("bank_slope"))
        {
            d.BankSlopePm = (int)Math.Round(r.Num("bank_slope", min: 0.001, max: 10) * 1000);
        }

        d.BridgeClearanceCm = OptCm(r, "bridge_clearance_m", d.BridgeClearanceCm);
        d.MaxBridgeM = r.Int("max_bridge_m", 0, min: 1, max: 100000);
        d.NoAutoBridge = !r.Bool("auto_bridge", true);
        d.MaxBrushM = r.Int("max_brush_m", 0, min: 1, max: MaxBrushPerCell * cellM);
        d.MaxDabCm = OptCm(r, "max_dab_m", d.MaxDabCm);
        d.MaxSamplesPerTick = r.Int("max_samples_per_tick", 0, min: 1, max: MaxTerrainSamples);
        d.MaxFloodSamples = r.Int("max_flood_samples", 0, min: 1, max: MaxTerrainSamples);
        if (r.Has("grade_max_boost"))
        {
            d.GradeMaxBoostPm = (int)Math.Round(r.Num("grade_max_boost", min: 1.0, max: 2.0) * 1000);
        }
    }

    private static int OptCm(JsonObjectReader r, string key, int current) =>
        r.Has(key) ? (int)Math.Round(r.Num(key, min: 0.01, max: MaxHeightM) * 100) : current;

    // ---------------------------------------------------------------- helpers

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static int[] ReadIntPair(JsonObjectReader r, string key, int min, int max)
    {
        JsonElement[] a = r.ReqArray(key);
        string path = r.ChildPath(key);
        if (a.Length != 2)
        {
            throw new ScenarioException($"{path}: expected array of 2 integers");
        }

        return
        [
            JsonObjectReader.IntValue(a[0], JsonObjectReader.Index(path, 0), min, max),
            JsonObjectReader.IntValue(a[1], JsonObjectReader.Index(path, 1), min, max),
        ];
    }

    private static double[] ReadNumPair(JsonObjectReader r, string key, double min, double max)
    {
        JsonElement[] a = r.ReqArray(key);
        string path = r.ChildPath(key);
        if (a.Length != 2)
        {
            throw new ScenarioException($"{path}: expected array of 2 numbers");
        }

        return
        [
            JsonObjectReader.NumValue(a[0], JsonObjectReader.Index(path, 0), min, max),
            JsonObjectReader.NumValue(a[1], JsonObjectReader.Index(path, 1), min, max),
        ];
    }

    private static Vec2[] ReadPoints(JsonObjectReader r, string key, int minCount, int maxCount)
    {
        JsonElement[] a = r.ReqArray(key);
        string path = r.ChildPath(key);
        if (a.Length < minCount || a.Length > maxCount)
        {
            throw new ScenarioException($"{path}: expected {minCount}..{maxCount} points, got {a.Length}");
        }

        var res = new Vec2[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            string pp = JsonObjectReader.Index(path, i);
            JsonElement[] xz = JsonObjectReader.ToArray(a[i], pp);
            if (xz.Length != 2)
            {
                throw new ScenarioException($"{pp}: expected [x, z]");
            }

            res[i] = new Vec2(
                (float)JsonObjectReader.NumValue(xz[0], JsonObjectReader.Index(pp, 0), -MaxTerrainCoord, MaxTerrainCoord),
                (float)JsonObjectReader.NumValue(xz[1], JsonObjectReader.Index(pp, 1), -MaxTerrainCoord, MaxTerrainCoord));
        }

        return res;
    }
}
