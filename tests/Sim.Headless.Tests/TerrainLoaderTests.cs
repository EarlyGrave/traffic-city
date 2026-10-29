using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>Загрузчик секции <c>terrain</c> (terrain.md §9.1-9.2, ADR-0036, задача TR2): хорошие случаи и каждый путь ошибки.</summary>
public class TerrainLoaderTests
{
    private const string Types = """
        {
          "format_version": 1,
          "types": [
            { "name": "car", "category": "car", "length_m": 4.5, "width_m": 1.8, "max_speed_kmh": 130, "speed_factor": 1.05,
              "idm": { "a": 1.0, "b": 1.5, "T": 1.2, "s0": 2.0, "delta": 4 },
              "mobil": { "politeness": 0.3, "threshold": 0.2, "b_safe": 4.0, "right_bias": 0.3, "duration_s": 2.0 } }
          ]
        }
        """;

    private const string Rest = """
        "city": { "enabled": false },
        "network": {
          "nodes": [ { "id": "n0", "x": 0, "z": 0, "control": "none" }, { "id": "n1", "x": 500, "z": 0, "control": "none" } ],
          "segments": [ { "a": "n0", "b": "n1", "lanes_ab": 1, "lanes_ba": 1, "speed_kmh": 36, "kind": "street" } ]
        },
        "capacities": { "max_vehicles": 64, "max_nodes": 8, "max_segments": 8, "max_lanes": 64, "max_lane_points": 512,
          "max_zones": 4, "max_bus_routes": 1, "max_bus_stops": 1, "max_routes": 64,
          "max_plan_steps": 2048, "max_pending_trips": 64, "max_signalized_nodes": 1 }
        """;

    private static readonly string _zeros2x2 = Convert.ToBase64String(new byte[8]);

    private static string Scenario(string terrainBody) =>
        "{ \"format_version\": 2, \"seed\": 7, \"duration_s\": 10, \"vehicles\": { \"count\": 5 }, " + Rest
        + (terrainBody.Length > 0 ? ", \"terrain\": { " + terrainBody + " }" : string.Empty) + " }";

    private static string Hm(string extra = "") =>
        "\"source\": \"heightmap\", \"heightmap\": { \"data_b64\": \"" + _zeros2x2 + "\", \"size\": [2, 2], \"cell_m\": 16" + extra + " }";

    private static LoadedScenario Load(string json) => ScenarioLoader.Parse(json, Types);

    private static ScenarioException Fails(string terrainBody) =>
        Assert.Throws<ScenarioException>(() => Load(Scenario(terrainBody)));

    private static string ScenariosDir()
    {
        DirectoryInfo? d = new(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "traffic-city.sln")))
            {
                return Path.Combine(d.FullName, "scenarios");
            }

            d = d.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }

    [Fact]
    public void NoSectionOrDisabled_MeansNoTerrain()
    {
        Assert.False(Load(Scenario(string.Empty)).Desc.Terrain.Enabled);
        LoadedScenario off = Load(Scenario("\"enabled\": false, \"cell_m\": 5, \"source\": \"noise\""));
        Assert.False(off.Desc.Terrain.Enabled);
        Assert.Empty(off.Warnings);
    }

    [Fact]
    public void EmptySection_IsFlatWithDefaults()
    {
        TerrainDesc t = Load(Scenario(string.Empty).Replace("\"vehicles\"", "\"terrain\": {}, \"vehicles\"", StringComparison.Ordinal)).Desc.Terrain;
        Assert.True(t.Enabled);
        Assert.Equal(TerrainSource.Flat, t.Source);
        Assert.Equal(8000, t.CellMm);
        Assert.False(t.HasExtent);
        Assert.Equal(0, t.MaxGradeStreetPm);   // 0 = дефолт ядра
    }

    [Fact]
    public void Heightmap_InlineBase64_IsDecodedLittleEndian()
    {
        byte[] raw = new byte[8];
        ushort[] v = [1, 258, 1000, 65535 / 2];
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(i * 2), v[i]);
        }

        string body = "\"cell_m\": 4, \"source\": \"heightmap\", \"heightmap\": { \"data_b64\": \"" + Convert.ToBase64String(raw)
            + "\", \"size\": [2, 2], \"cell_m\": 32, \"origin_m\": [-16, -48], \"base_m\": 120.5, \"unit_m\": 0.02 }";
        TerrainDesc t = Load(Scenario(body)).Desc.Terrain;
        Assert.Equal(TerrainSource.Heightmap, t.Source);
        Assert.Equal(v, t.SourceHeights);
        Assert.Equal((2, 2), (t.SourceSizeX, t.SourceSizeZ));
        Assert.Equal(32000, t.SourceCellMm);
        Assert.Equal(4000, t.CellMm);
        Assert.Equal((-16f, -48f), (t.SourceOriginX, t.SourceOriginZ));
        Assert.Equal(12050, t.BaseCm);
        Assert.Equal(20, t.SourceUnitMm);
    }

    [Fact]
    public void Heightmap_R16File_IsReadRelativeToScenario()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tc-terrain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "terrain"));
        try
        {
            byte[] raw = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(6), 777);
            File.WriteAllBytes(Path.Combine(dir, "terrain", "h.r16"), raw);
            File.WriteAllText(Path.Combine(dir, "vehicle-types.json"), Types);
            string body = "\"source\": \"heightmap\", \"heightmap\": { \"file\": \"terrain/h.r16\", \"size\": [2, 2], \"cell_m\": 8 }";
            File.WriteAllText(Path.Combine(dir, "s.json"), Scenario(body));
            LoadedScenario s = ScenarioLoader.LoadInfo(Path.Combine(dir, "s.json"), []);
            Assert.Equal((ushort)777, s.Desc.Terrain.SourceHeights[3]);

            File.WriteAllBytes(Path.Combine(dir, "terrain", "h.r16"), new byte[7]);
            ScenarioException e = Assert.Throws<ScenarioException>(() => ScenarioLoader.LoadInfo(Path.Combine(dir, "s.json"), []));
            Assert.Contains("$.terrain.heightmap.file: expected 8 bytes", e.Message, StringComparison.Ordinal);

            File.Delete(Path.Combine(dir, "terrain", "h.r16"));
            e = Assert.Throws<ScenarioException>(() => ScenarioLoader.LoadInfo(Path.Combine(dir, "s.json"), []));
            Assert.Contains("$.terrain.heightmap.file: cannot read", e.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Water_Rivers_Lakes_Sea_AreMapped()
    {
        string body = Hm(", \"base_m\": 100") + ", \"water\": { \"sea_level_m\": 100.5, "
            + "\"lakes\": [ { \"x\": 1, \"z\": 2, \"level_m\": 101 } ], "
            + "\"rivers\": [ { \"points\": [[0, 0], [10, 20]], \"width_m\": 12, \"depth_m\": 2, \"flow_m3s\": 3.5 }, { \"points\": [[0, 0], [5, 5]], \"width_m\": 4, \"depth_m\": 1 } ] }";
        LoadedScenario s = Load(Scenario(body));
        TerrainWaterDesc w = s.Desc.Terrain.Water;
        Assert.True(w.HasSeaLevel);
        Assert.Equal(100.5f, w.SeaLevelM);
        Assert.Equal(new TerrainLakeDesc { X = 1, Z = 2, LevelM = 101 }, w.Lakes[0]);
        Assert.Equal(2, w.Rivers.Length);
        Assert.Equal(12f, w.Rivers[0].WidthM);
        Assert.Equal(10f, w.Rivers[0].Points[1].X);
        Assert.Equal(3.5f, w.Rivers[0].FlowM3s);
        Assert.True(float.IsNaN(w.Rivers[1].FlowM3s));
        Assert.Contains(s.Warnings, x => x.StartsWith("terrain.water.rivers[0].flow_m3s", StringComparison.Ordinal));
        Assert.DoesNotContain(s.Warnings, x => x.Contains("rivers[1]", StringComparison.Ordinal));
    }

    [Fact]
    public void Water_NullSeaLevel_MeansNoSea()
    {
        Assert.False(Load(Scenario("\"water\": { \"sea_level_m\": null }")).Desc.Terrain.Water.HasSeaLevel);
    }

    [Fact]
    public void Water_Sources_AreValidatedPassedAndWarned()
    {
        string body = "\"extent\": { \"min_x\": -100, \"min_z\": -100, \"max_x\": 100, \"max_z\": 100 }, \"water\": { \"sources\": ["
            + "{ \"id\": 1, \"kind\": \"inflow\", \"x\": -100, \"z\": 0, \"flow_m3s\": 5.0, \"target_level_m\": null, \"radius_m\": 20 }, "
            + "{ \"id\": 2, \"kind\": \"sink\", \"x\": 50, \"z\": 50 } ] }";
        LoadedScenario s = Load(Scenario(body));
        TerrainWaterSourceDesc[] src = s.Desc.Terrain.Water.Sources;
        Assert.Equal(2, src.Length);
        Assert.Equal(TerrainWaterSourceKind.Inflow, src[0].Kind);
        Assert.Equal(5f, src[0].FlowM3s);
        Assert.Equal(20f, src[0].RadiusM);
        Assert.True(float.IsNaN(src[0].TargetLevelM));
        Assert.Equal(TerrainWaterSourceKind.Sink, src[1].Kind);
        Assert.True(float.IsNaN(src[1].FlowM3s));
        Assert.Contains("water.sources: течение не реализовано, источники игнорируются", s.Warnings);
    }

    [Fact]
    public void Stamps_AreParsedWithClosedAndGaps_AndWarned()
    {
        string body = "\"stamps\": [ { \"kind\": \"ridge\", \"closed\": true, \"points\": [[0, 0], [10, 0], [10, 10]], \"height_m\": 7, \"crest_m\": 2, \"slope\": 2.0, "
            + "\"gaps\": [ { \"x\": 5, \"z\": 0, \"width_m\": 20 } ] }, { \"kind\": \"pit\", \"points\": [[0, 0], [10, 10]], \"height_m\": 1 } ]";
        LoadedScenario s = Load(Scenario(body));
        Assert.Equal(2, s.TerrainStamps.Count);
        TerrainStampDesc ridge = s.TerrainStamps[0];
        Assert.Equal(TerrainStampKind.Ridge, ridge.Kind);
        Assert.True(ridge.Closed);
        Assert.Equal(3, ridge.Points.Length);
        Assert.Equal(7f, ridge.HeightM);
        Assert.Equal(new TerrainStampGap(5, 0, 20), ridge.Gaps.Single());
        Assert.Equal(TerrainStampKind.Pit, s.TerrainStamps[1].Kind);
        Assert.False(s.TerrainStamps[1].Closed);
        Assert.Equal(2f, s.TerrainStamps[1].Slope);   // дефолт 1:2
        Assert.Contains(s.Warnings, x => x.StartsWith("terrain.stamps:", StringComparison.Ordinal));
    }

    [Fact]
    public void Params_AreConvertedToCoreUnits()
    {
        string body = "\"cell_m\": 16, \"margin_m\": 500, \"extent\": { \"min_x\": -50, \"min_z\": -60, \"max_x\": 70, \"max_z\": 80 }, \"flat_m\": 12.5, "
            + "\"max_grade_pct\": { \"street\": 8, \"arterial\": 6 }, \"max_cut_m\": 10, \"max_fill_m\": 6, \"bank_slope\": 0.5, \"bridge_clearance_m\": 2, "
            + "\"max_bridge_m\": 300, \"auto_bridge\": false, \"max_brush_m\": 96, \"max_dab_m\": 2, \"max_samples_per_tick\": 1000, \"max_flood_samples\": 2000, \"grade_max_boost\": 1.1";
        TerrainDesc t = Load(Scenario(body)).Desc.Terrain;
        Assert.Equal(16000, t.CellMm);
        Assert.Equal(500f, t.MarginM);
        Assert.True(t.HasExtent);
        Assert.Equal((-50f, -60f, 70f, 80f), (t.ExtentMinX, t.ExtentMinZ, t.ExtentMaxX, t.ExtentMaxZ));
        Assert.Equal(12.5f, t.FlatM);
        Assert.Equal((80, 60), (t.MaxGradeStreetPm, t.MaxGradeArterialPm));
        Assert.Equal((1000, 600, 500, 200), (t.MaxCutCm, t.MaxFillCm, t.BankSlopePm, t.BridgeClearanceCm));
        Assert.Equal(300, t.MaxBridgeM);
        Assert.True(t.NoAutoBridge);
        Assert.Equal((96, 200, 1000, 2000, 1100), (t.MaxBrushM, t.MaxDabCm, t.MaxSamplesPerTick, t.MaxFloodSamples, t.GradeMaxBoostPm));
    }

    [Fact]
    public void TerrainDemo_LoadsAndCreates()
    {
        LoadedScenario s = ScenarioLoader.LoadInfo(Path.Combine(ScenariosDir(), "terrain-demo.json"));
        TerrainDesc t = s.Desc.Terrain;
        Assert.True(t.Enabled);
        Assert.Equal(25, t.SourceHeights.Length);
        Assert.Equal(10000, t.BaseCm);
        Assert.Single(s.TerrainStamps);
        Assert.True(s.TerrainStamps[0].Closed);
        Assert.Equal(2, s.Warnings.Count);
        ScenarioDesc desc = s.Desc;
        using Simulation sim = Simulation.Create(in desc);
        Assert.True(sim.HasTerrain);
        Assert.InRange(sim.Terrain.SamplesX, 76, 78);   // 600 м / 8 м + 1, сдвиг к мировой сетке
        Assert.Contains(sim.Terrain.Water.ToArray(), w => w > 0);
    }

    [Theory]
    [InlineData("\"cell_m\": 5", "$.terrain.cell_m: expected 4, 8 or 16")]
    [InlineData("\"cell_m\": \"8\"", "$.terrain.cell_m: expected integer")]
    [InlineData("\"margin_m\": -1", "$.terrain.margin_m: value -1 out of range")]
    [InlineData("\"extent\": { \"min_x\": 5, \"min_z\": 0, \"max_x\": 5, \"max_z\": 10 }", "$.terrain.extent: expected min_x < max_x")]
    [InlineData("\"extent\": { \"min_x\": 0, \"min_z\": 0, \"max_x\": 10 }", "$.terrain.extent.max_z: required key is missing")]
    [InlineData("\"cell_m\": 4, \"extent\": { \"min_x\": 0, \"min_z\": 0, \"max_x\": 20000, \"max_z\": 20000 }", "$.terrain.extent: about")]
    [InlineData("\"source\": \"tiles\"", "$.terrain.source: unknown source \"tiles\"")]
    [InlineData("\"source\": \"noise\"", "$.terrain.source: \"noise\" is not supported yet")]
    [InlineData("\"source\": \"heightmap\"", "$.terrain.heightmap: required key is missing")]
    [InlineData("\"heightmap\": {}", "$.terrain.heightmap: allowed only with source")]
    [InlineData("\"noise\": {}", "$.terrain.noise: allowed only with source")]
    [InlineData("\"flat_m\": 700", "$.terrain.flat_m: value 700 out of range")]
    [InlineData("\"unknown_key\": 1", "$.terrain.unknown_key: unknown key")]
    [InlineData("\"max_brush_m\": 97, \"cell_m\": 8", "$.terrain.max_brush_m: value 97 out of range")]
    [InlineData("\"max_grade_pct\": { \"street\": 0 }", "$.terrain.max_grade_pct.street: value 0 out of range")]
    [InlineData("\"max_grade_pct\": { \"hill\": 5 }", "$.terrain.max_grade_pct.hill: unknown key")]
    [InlineData("\"max_cut_m\": 0", "$.terrain.max_cut_m: value 0 out of range")]
    [InlineData("\"max_dab_m\": 700", "$.terrain.max_dab_m: value 700 out of range")]
    [InlineData("\"bank_slope\": 0", "$.terrain.bank_slope: value 0 out of range")]
    [InlineData("\"auto_bridge\": 1", "$.terrain.auto_bridge: expected boolean")]
    [InlineData("\"grade_max_boost\": 3", "$.terrain.grade_max_boost: value 3 out of range")]
    [InlineData("\"max_samples_per_tick\": 0", "$.terrain.max_samples_per_tick: value 0 out of range")]
    public void SectionErrors_CarryJsonPath(string body, string expected)
    {
        Assert.Contains(expected, Fails(body).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"size\": [2, 2], \"cell_m\": 16, \"data_b64\": \"AAAAAAAAAAA=\", \"file\": \"a.r16\"", "$.terrain.heightmap: exactly one of")]
    [InlineData("\"size\": [2, 2], \"cell_m\": 16", "$.terrain.heightmap: exactly one of")]
    [InlineData("\"data_b64\": \"AAAAAAAAAAA=\", \"cell_m\": 16", "$.terrain.heightmap.size: required key is missing")]
    [InlineData("\"data_b64\": \"AAAAAAAAAAA=\", \"size\": [1, 2], \"cell_m\": 16", "$.terrain.heightmap.size[0]: value 1 out of range")]
    [InlineData("\"data_b64\": \"AAAAAAAAAAA=\", \"size\": [2], \"cell_m\": 16", "$.terrain.heightmap.size: expected array of 2 integers")]
    [InlineData("\"data_b64\": \"AAAAAAAAAAA=\", \"size\": [2, 2]", "$.terrain.heightmap.cell_m: required key is missing")]
    [InlineData("\"data_b64\": \"AAAAAAAAAAA=\", \"size\": [2, 2], \"cell_m\": 12", "$.terrain.heightmap.cell_m: source cell 12 m must be a multiple of terrain.cell_m = 8 m")]
    [InlineData("\"data_b64\": \"AAAAAAAAAAA=\", \"size\": [2, 2], \"cell_m\": 16.0005", "$.terrain.heightmap.cell_m")]
    [InlineData("\"data_b64\": \"AAAAAAAAAAA=\", \"size\": [2, 2], \"cell_m\": 16, \"origin_m\": [1]", "$.terrain.heightmap.origin_m: expected array of 2 numbers")]
    [InlineData("\"data_b64\": \"AAAAAAAAAAA=\", \"size\": [2, 2], \"cell_m\": 16, \"unit_m\": 0.0005", "$.terrain.heightmap.unit_m")]
    [InlineData("\"data_b64\": \"AAAAAAAAAAA=\", \"size\": [2, 2], \"cell_m\": 16, \"base_m\": 1e9", "$.terrain.heightmap.base_m: value")]
    [InlineData("\"data_b64\": \"!!!!\", \"size\": [2, 2], \"cell_m\": 16", "$.terrain.heightmap.data_b64: invalid base64")]
    [InlineData("\"data_b64\": \"AAAA\", \"size\": [2, 2], \"cell_m\": 16", "$.terrain.heightmap.data_b64: expected 8 bytes (2 x 2 u16 little-endian), got 3")]
    [InlineData("\"data_b64\": \"AAAAAAAAAAA=\", \"size\": [2, 2], \"cell_m\": 16, \"extra\": 1", "$.terrain.heightmap.extra: unknown key")]
    [InlineData("\"file\": \"\", \"size\": [2, 2], \"cell_m\": 16", "$.terrain.heightmap.file: empty path")]
    [InlineData("\"file\": \"a.r16\", \"size\": [2, 2], \"cell_m\": 16", "$.terrain.heightmap.file: reading binary files is not available")]
    public void HeightmapErrors_CarryJsonPath(string heightmap, string expected)
    {
        ScenarioException e = Fails("\"source\": \"heightmap\", \"heightmap\": { " + heightmap + " }");
        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Heightmap_TooHigh_IsRejectedWithSampleIndex()
    {
        byte[] raw = new byte[8];
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(4), 40000);
        string body = "\"source\": \"heightmap\", \"heightmap\": { \"data_b64\": \"" + Convert.ToBase64String(raw) + "\", \"size\": [2, 2], \"cell_m\": 8, \"unit_m\": 0.02 }";
        Assert.Contains("$.terrain.heightmap.data_b64: sample [0, 1]", Fails(body).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"sea_level_m\": 1000", "$.terrain.water.sea_level_m: level 1000 m is outside [0, 655.35]")]
    [InlineData("\"sea_level_m\": \"high\"", "$.terrain.water.sea_level_m: expected number")]
    [InlineData("\"lakes\": [ { \"x\": 0, \"z\": 0 } ]", "$.terrain.water.lakes[0].level_m: required key is missing")]
    [InlineData("\"lakes\": [ { \"x\": 0, \"z\": 0, \"level_m\": -1 } ]", "$.terrain.water.lakes[0].level_m: level -1 m is outside")]
    [InlineData("\"lakes\": {}", "$.terrain.water.lakes: expected array")]
    [InlineData("\"rivers\": [ { \"points\": [[0, 0]], \"width_m\": 5, \"depth_m\": 1 } ]", "$.terrain.water.rivers[0].points: expected 2..4096 points, got 1")]
    [InlineData("\"rivers\": [ { \"points\": [[0, 0], [1]], \"width_m\": 5, \"depth_m\": 1 } ]", "$.terrain.water.rivers[0].points[1]: expected [x, z]")]
    [InlineData("\"rivers\": [ { \"points\": [[0, 0], [1, \"a\"]], \"width_m\": 5, \"depth_m\": 1 } ]", "$.terrain.water.rivers[0].points[1][1]: expected number")]
    [InlineData("\"rivers\": [ { \"points\": [[0, 0], [1, 1]], \"width_m\": 0, \"depth_m\": 1 } ]", "$.terrain.water.rivers[0].width_m: value 0 out of range")]
    [InlineData("\"rivers\": [ { \"points\": [[0, 0], [1, 1]], \"width_m\": 5 } ]", "$.terrain.water.rivers[0].depth_m: required key is missing")]
    [InlineData("\"rivers\": [ {}, { \"points\": [[0, 0], [1, 1]], \"width_m\": 5, \"depth_m\": 1, \"flow_m3s\": -2 } ]", "$.terrain.water.rivers[0].points: required key is missing")]
    [InlineData("\"rivers\": [ { \"points\": [[0, 0], [1, 1]], \"width_m\": 5, \"depth_m\": 1, \"flow_m3s\": -2 } ]", "$.terrain.water.rivers[0].flow_m3s: value -2 out of range")]
    [InlineData("\"rivers\": [ { \"points\": [[0, 0], [1, 1]], \"width_m\": 5, \"depth_m\": 1, \"flow_m3s\": \"x\" } ]", "$.terrain.water.rivers[0].flow_m3s: expected number")]
    [InlineData("\"sources\": [ { \"kind\": \"spring\", \"x\": 0, \"z\": 0 } ]", "$.terrain.water.sources[0].id: required key is missing")]
    [InlineData("\"sources\": [ { \"id\": 1, \"kind\": \"geyser\", \"x\": 0, \"z\": 0 } ]", "$.terrain.water.sources[0].kind: unknown kind \"geyser\"")]
    [InlineData("\"sources\": [ { \"id\": 1, \"kind\": \"sink\", \"x\": 0, \"z\": 0 }, { \"id\": 1, \"kind\": \"sink\", \"x\": 1, \"z\": 1 } ]", "$.terrain.water.sources[1].id: duplicate source id 1")]
    [InlineData("\"sources\": [ { \"id\": -1, \"kind\": \"sink\", \"x\": 0, \"z\": 0 } ]", "$.terrain.water.sources[0].id: value -1 out of range")]
    [InlineData("\"sources\": [ { \"id\": 1, \"kind\": \"spring\", \"x\": 0, \"z\": 0, \"flow_m3s\": -1 } ]", "$.terrain.water.sources[0].flow_m3s: value -1 out of range")]
    [InlineData("\"sources\": [ { \"id\": 1, \"kind\": \"spring\", \"x\": 0, \"z\": 0, \"radius_m\": -1 } ]", "$.terrain.water.sources[0].radius_m: value -1 out of range")]
    [InlineData("\"sources\": [ { \"id\": 1, \"kind\": \"spring\", \"x\": 0, \"z\": 0, \"target_level_m\": \"a\" } ]", "$.terrain.water.sources[0].target_level_m: expected number")]
    [InlineData("\"sources\": [ { \"id\": 1, \"kind\": \"spring\", \"x\": 0, \"z\": 0, \"depth\": 1 } ]", "$.terrain.water.sources[0].depth: unknown key")]
    [InlineData("\"ocean\": 1", "$.terrain.water.ocean: unknown key")]
    public void WaterErrors_CarryJsonPath(string water, string expected)
    {
        Assert.Contains(expected, Fails("\"water\": { " + water + " }").Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WaterSource_OutsideExtent_IsRejected()
    {
        string body = "\"extent\": { \"min_x\": 0, \"min_z\": 0, \"max_x\": 100, \"max_z\": 100 }, \"water\": { \"sources\": [ { \"id\": 1, \"kind\": \"spring\", \"x\": 101, \"z\": 5 } ] }";
        Assert.Contains("$.terrain.water.sources[0]: point (101, 5) is outside the terrain extent", Fails(body).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WaterLevel_IsCheckedAgainstHeightmapBase()
    {
        // base 100 м: уровень 100.5 допустим, 99 - ниже базы
        Assert.Contains("$.terrain.water.lakes[0].level_m", Fails(Hm(", \"base_m\": 100") + ", \"water\": { \"lakes\": [ { \"x\": 0, \"z\": 0, \"level_m\": 99 } ] }").Message, StringComparison.Ordinal);
        Load(Scenario(Hm(", \"base_m\": 100") + ", \"water\": { \"lakes\": [ { \"x\": 0, \"z\": 0, \"level_m\": 100.5 } ] }"));
    }

    [Theory]
    [InlineData("{ \"points\": [[0, 0], [1, 1]], \"height_m\": 1 }", "$.terrain.stamps[0].kind: required key is missing")]
    [InlineData("{ \"kind\": \"wall\", \"points\": [[0, 0], [1, 1]], \"height_m\": 1 }", "$.terrain.stamps[0].kind: unknown kind \"wall\"")]
    [InlineData("{ \"kind\": \"ridge\", \"points\": [[0, 0]], \"height_m\": 1 }", "$.terrain.stamps[0].points: expected 2..4096 points, got 1")]
    [InlineData("{ \"kind\": \"ridge\", \"closed\": true, \"points\": [[0, 0], [1, 1]], \"height_m\": 1 }", "$.terrain.stamps[0].points: expected 3..4096 points, got 2")]
    [InlineData("{ \"kind\": \"ridge\", \"points\": [[0, 0], [1, 1]] }", "$.terrain.stamps[0].height_m: required key is missing")]
    [InlineData("{ \"kind\": \"ridge\", \"points\": [[0, 0], [1, 1]], \"height_m\": 0 }", "$.terrain.stamps[0].height_m: value 0 out of range")]
    [InlineData("{ \"kind\": \"ridge\", \"points\": [[0, 0], [1, 1]], \"height_m\": 1, \"crest_m\": -1 }", "$.terrain.stamps[0].crest_m: value -1 out of range")]
    [InlineData("{ \"kind\": \"ridge\", \"points\": [[0, 0], [1, 1]], \"height_m\": 1, \"slope\": 0 }", "$.terrain.stamps[0].slope: value 0 out of range")]
    [InlineData("{ \"kind\": \"ridge\", \"closed\": 1, \"points\": [[0, 0], [1, 1]], \"height_m\": 1 }", "$.terrain.stamps[0].closed: expected boolean")]
    [InlineData("{ \"kind\": \"ridge\", \"points\": [[0, 0], [1, 1]], \"height_m\": 1, \"gaps\": [ { \"x\": 0, \"z\": 0 } ] }", "$.terrain.stamps[0].gaps[0].width_m: required key is missing")]
    [InlineData("{ \"kind\": \"ridge\", \"points\": [[0, 0], [1, 1]], \"height_m\": 1, \"gaps\": [ { \"x\": 0, \"z\": 0, \"width_m\": 0 } ] }", "$.terrain.stamps[0].gaps[0].width_m: value 0 out of range")]
    [InlineData("{ \"kind\": \"ridge\", \"points\": [[0, 0], [1, 1]], \"height_m\": 1, \"gap\": [] }", "$.terrain.stamps[0].gap: unknown key")]
    [InlineData("5", "$.terrain.stamps[0]: expected object")]
    public void StampErrors_CarryJsonPath(string stamp, string expected)
    {
        Assert.Contains(expected, Fails("\"stamps\": [ " + stamp + " ]").Message, StringComparison.Ordinal);
    }
}
