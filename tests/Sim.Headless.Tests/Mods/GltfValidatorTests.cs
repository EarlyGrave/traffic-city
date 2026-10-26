#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Sim.Headless.Scenarios;
using Sim.Scenarios.Mods;
using Sim.Scenarios.Mods.Gltf;

namespace Sim.Headless.Tests.Mods;

/// <summary>Проверка glTF/GLB, бюджеты LOD и команда validate-package (modding.md §4.2, §4.3, §10 «glTF»; M6).</summary>
public sealed class GltfValidatorTests : IDisposable
{
    private const string PkgId = "gtest";
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "tc-gltf-" + Guid.NewGuid().ToString("N"));

    public GltfValidatorTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tmp, true);
        }
        catch (IOException)
        {
            // временный каталог - не критично
        }
    }

    private static string Root()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "traffic-city.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    // ------------------------------------------------------------------ сборка glTF

    private static JsonObject Doc(params int[] lodTris)
    {
        int maxTris = lodTris.Max();
        byte[] pos = new byte[3 * maxTris * 12];
        var nodes = new JsonArray();
        var meshes = new JsonArray();
        var accessors = new JsonArray();
        var sceneNodes = new JsonArray();
        for (int i = 0; i < lodTris.Length; i++)
        {
            nodes.Add(new JsonObject { ["name"] = "LOD" + i, ["mesh"] = i });
            meshes.Add(new JsonObject
            {
                ["primitives"] = new JsonArray(new JsonObject { ["attributes"] = new JsonObject { ["POSITION"] = i } }),
            });
            accessors.Add(new JsonObject
            {
                ["bufferView"] = 0,
                ["componentType"] = 5126,
                ["count"] = 3 * lodTris[i],
                ["type"] = "VEC3",
            });
            sceneNodes.Add(i);
        }

        return new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = sceneNodes }),
            ["nodes"] = nodes,
            ["meshes"] = meshes,
            ["accessors"] = accessors,
            ["bufferViews"] = new JsonArray(new JsonObject { ["buffer"] = 0, ["byteLength"] = pos.Length }),
            ["buffers"] = new JsonArray(new JsonObject
            {
                ["byteLength"] = pos.Length,
                ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(pos),
            }),
        };
    }

    private static byte[] Text(JsonObject doc) => Encoding.UTF8.GetBytes(doc.ToJsonString());

    private static byte[] Glb(JsonObject doc, byte[]? bin)
    {
        byte[] json = Encoding.UTF8.GetBytes(doc.ToJsonString());
        int jsonPad = (json.Length + 3) / 4 * 4;
        int binPad = bin == null ? 0 : (bin.Length + 3) / 4 * 4;
        int total = 12 + 8 + jsonPad + (bin == null ? 0 : 8 + binPad);
        byte[] r = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(r, 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(8), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(12), (uint)jsonPad);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(16), 0x4E4F534A);
        json.CopyTo(r, 20);
        for (int i = json.Length; i < jsonPad; i++)
        {
            r[20 + i] = (byte)' ';
        }

        if (bin != null)
        {
            int o = 20 + jsonPad;
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(o), (uint)binPad);
            BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(o + 4), 0x004E4942);
            bin.CopyTo(r, o + 8);
        }

        return r;
    }

    private static byte[] Png(uint w, uint h)
    {
        byte[] b = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), w);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), h);
        b[24] = 8;
        b[25] = 6;
        return b;
    }

    private static byte[] Jpeg(ushort w, ushort h) =>
    [
        0xFF, 0xD8,
        0xFF, 0xE0, 0x00, 0x04, 0x4A, 0x46, // APP0 (длина 4, 2 байта данных)
        0xFF, 0xC0, 0x00, 0x11, 0x08, (byte)(h >> 8), (byte)h, (byte)(w >> 8), (byte)w, 0x03,
    ];

    private static byte[] Ktx2(uint w, uint h)
    {
        byte[] b = new byte[28];
        new byte[] { 0xAB, 0x4B, 0x54, 0x58, 0x20, 0x32, 0x30, 0xBB, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(20), w);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), h);
        return b;
    }

    private static string DataUri(byte[] b) => "data:image/png;base64," + Convert.ToBase64String(b);

    // ------------------------------------------------------------------ запуск проверки

    private sealed record Outcome(List<ModIssue> Errors, List<ModIssue> Warnings);

    private Outcome Check(string modelRel, byte[] model, Dictionary<string, byte[]>? extra = null, RenderBudgets? budgets = null, bool road = false)
    {
        string dir = Path.Combine(_tmp, PkgId);
        Directory.CreateDirectory(dir);
        string key = road ? "profile" : "model";
        File.WriteAllText(Path.Combine(dir, "package.json"), $$$"""
            {"format_version":1,"id":"{{{PkgId}}}","version":"1.0.0","content_format":1,"title":"G",
             "content":{"vehicles":["vehicles.json"],"roads":["roads.json"]}}
            """);
        string car = """
            {"name":"car","category":"car","length_m":4.5,"width_m":1.8,"max_speed_kmh":130,"speed_factor":1.05,
             "idm":{"a":1.0,"b":1.5,"T":1.2,"s0":2.0,"delta":4},
             "mobil":{"politeness":0.3,"threshold":0.2,"b_safe":4.0,"right_bias":0.3,"duration_s":2.0}
             VISUAL}
            """;
        string visual = $",\"visual\":{{\"{key}\":\"{modelRel}\"}}";
        File.WriteAllText(
            Path.Combine(dir, "vehicles.json"),
            "{\"format_version\":2,\"types\":[" + car.Replace("VISUAL", road ? string.Empty : visual, StringComparison.Ordinal) + "]}");
        File.WriteAllText(
            Path.Combine(dir, "roads.json"),
            road
                ? $"{{\"format_version\":1,\"roads\":[{{\"name\":\"street\",\"kind\":\"street\",\"lanes_ab\":1,\"lanes_ba\":1,\"speed_kmh\":40{visual}}}]}}"
                : "{\"format_version\":1,\"roads\":[]}");
        Write(dir, modelRel, model);
        if (extra != null)
        {
            foreach (KeyValuePair<string, byte[]> kv in extra)
            {
                Write(dir, kv.Key, kv.Value);
            }
        }

        PackageSet set = PackageLoader.Load([new PackageSearchDir(_tmp, false)], [PkgId]);
        var errors = new List<ModIssue>(set.Errors);
        var warnings = new List<ModIssue>(set.Warnings);
        Assert.True(set.Ok, string.Join("; ", set.Errors));
        ContentCatalog cat = ContentCatalog.Build(set, errors);
        Assert.Empty(errors);
        VisualValidator.Validate(set, cat, budgets ?? RenderBudgets.Default, errors, warnings);
        return new Outcome(errors, warnings);
    }

    private static void Write(string dir, string rel, byte[] bytes)
    {
        string path = Path.Combine(dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private static void AssertError(Outcome o, ModError code, string? pathContains = null)
    {
        Assert.Contains(o.Errors, i => i.Code == code && (pathContains == null || i.JsonPath.Contains(pathContains, StringComparison.Ordinal)));
    }

    // ------------------------------------------------------------------ LOD (§4.3)

    [Fact]
    public void ThreeLods_Ok()
    {
        Outcome o = Check("models/car.gltf", Text(Doc(300, 150, 50)));
        Assert.Empty(o.Errors);
        Assert.Empty(o.Warnings);
    }

    [Fact]
    public void ThreeLods_EqualTriangles_Ok()
    {
        Assert.Empty(Check("models/car.gltf", Text(Doc(100, 100, 100))).Errors);
    }

    [Fact]
    public void TwoLods_LodMissing()
    {
        AssertError(Check("models/car.gltf", Text(Doc(300, 150))), ModError.LodMissing);
    }

    [Fact]
    public void NoLodNames_LodMissing()
    {
        JsonObject d = Doc(10, 10, 10);
        foreach (JsonNode? n in d["nodes"]!.AsArray())
        {
            n!["name"] = "body";
        }

        AssertError(Check("models/car.gltf", Text(d)), ModError.LodMissing);
    }

    [Fact]
    public void GapInLodNumbers_LodMissing()
    {
        JsonObject d = Doc(30, 20, 10);
        d["nodes"]![2]!["name"] = "LOD3";
        AssertError(Check("models/car.gltf", Text(d)), ModError.LodMissing);
    }

    [Fact]
    public void GrowingTriangles_LodOrder()
    {
        Outcome o = Check("models/car.gltf", Text(Doc(100, 200, 50)));
        AssertError(o, ModError.LodOrder);
        Assert.Contains("LOD1", o.Errors.First(i => i.Code == ModError.LodOrder).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Glb_ThreeLods_Ok()
    {
        JsonObject d = Doc(300, 150, 50);
        byte[] bin = new byte[3 * 300 * 12];
        d["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length });
        d["bufferViews"]![0]!["byteLength"] = bin.Length;
        Assert.Empty(Check("models/car.glb", Glb(d, bin)).Errors);
    }

    [Fact]
    public void Glb_BadLength_GltfInvalid()
    {
        JsonObject d = Doc(30, 20, 10);
        byte[] bin = new byte[3 * 30 * 12];
        d["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = bin.Length });
        d["bufferViews"]![0]!["byteLength"] = bin.Length;
        byte[] glb = Glb(d, bin);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(8), (uint)glb.Length + 8);
        AssertError(Check("models/car.glb", glb), ModError.GltfInvalid);
    }

    [Fact]
    public void Glb_ChunkLongerThanFile_GltfInvalid()
    {
        byte[] glb = Glb(Doc(30, 20, 10), null);
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(12), (uint)glb.Length);
        AssertError(Check("models/car.glb", glb), ModError.GltfInvalid);
    }

    [Fact]
    public void Glb_BadMagic_GltfInvalid()
    {
        byte[] glb = Glb(Doc(30, 20, 10), null);
        glb[0] = (byte)'X';
        AssertError(Check("models/car.glb", glb), ModError.GltfInvalid);
    }

    [Fact]
    public void Glb_BufferWithoutUriAndWithoutBin_GltfInvalid()
    {
        JsonObject d = Doc(30, 20, 10);
        ((JsonObject)d["buffers"]![0]!).Remove("uri");
        AssertError(Check("models/car.glb", Glb(d, null)), ModError.GltfInvalid, "buffers");
    }

    [Fact]
    public void RoadProfile_IsValidatedAsRoad()
    {
        Assert.Empty(Check("profiles/street.gltf", Text(Doc(30, 20, 10)), road: true).Errors);
        AssertError(Check("profiles/street.gltf", Text(Doc(30, 20)), road: true), ModError.LodMissing);
    }

    // ------------------------------------------------------------------ бюджеты

    [Fact]
    public void Budgets_FromFile_LodOverBudget()
    {
        string file = Path.Combine(_tmp, "b.json");
        File.WriteAllText(file, """
            {"format_version":1,"kinds":{"vehicle":{"min_lods":2,"max_tris":[400,null,50]}}}
            """);
        var issues = new List<ModIssue>();
        RenderBudgets b = RenderBudgets.Load(file, issues)!;
        Assert.Empty(issues);

        Assert.Empty(Check("m.gltf", Text(Doc(400, 100)), budgets: b).Errors);
        AssertError(Check("m.gltf", Text(Doc(401, 100)), budgets: b), ModError.LodOverBudget);
        Outcome o = Check("m.gltf", Text(Doc(400, 100, 60)), budgets: b);
        AssertError(o, ModError.LodOverBudget);
        Assert.Contains("LOD2", o.Errors.First(i => i.Code == ModError.LodOverBudget).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Budgets_RealFile_LoadsAndMatchesStub()
    {
        var issues = new List<ModIssue>();
        RenderBudgets b = RenderBudgets.Load(Path.Combine(Root(), "content", RenderBudgets.FileName), issues)!;
        Assert.Empty(issues);
        foreach (AssetKind k in new[] { AssetKind.Vehicle, AssetKind.Road, AssetKind.Building, AssetKind.Prop })
        {
            Assert.Equal(3, b.For(k).MinLods);
            Assert.All(b.For(k).MaxTris, t => Assert.Null(t));
        }

        Assert.Equal(3, RenderBudgets.Default.For(AssetKind.Vehicle).MinLods);
    }

    [Theory]
    [InlineData("""{"format_version":2,"kinds":{}}""", ModError.UnsupportedFormatVersion)]
    [InlineData("""{"format_version":1,"kinds":{"vehicle":{"min_lods":3,"extra":1}}}""", ModError.UnknownKey)]
    [InlineData("""{"format_version":1,"kinds":{"boat":{"min_lods":3}}}""", ModError.UnknownKey)]
    [InlineData("""{"format_version":1,"kinds":{"vehicle":{"min_lods":0}}}""", ModError.OutOfRange)]
    [InlineData("""{"format_version":1,"kinds":{"vehicle":{"max_tris":[1]}}}""", ModError.MissingKey)]
    [InlineData("""{"format_version":1,"kinds":{"vehicle":{"min_lods":3,"max_tris":[0]}}}""", ModError.OutOfRange)]
    [InlineData("""{"format_version":1}""", ModError.MissingKey)]
    [InlineData("not json", ModError.BadValue)]
    public void Budgets_Invalid(string json, ModError code)
    {
        string file = Path.Combine(_tmp, "bad.json");
        File.WriteAllText(file, json);
        var issues = new List<ModIssue>();
        Assert.Null(RenderBudgets.Load(file, issues));
        Assert.Contains(issues, i => i.Code == code);
    }

    // ------------------------------------------------------------------ URI

    [Theory]
    [InlineData("http://example.com/a.bin")]
    [InlineData("https://example.com/a.bin")]
    [InlineData("file:///etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/x.bin")]
    [InlineData("..%2Fsecret.bin")]
    [InlineData("../secret.bin")]
    [InlineData("a\\b.bin")]
    public void ExternalUri_PathEscape(string uri)
    {
        JsonObject d = Doc(30, 20, 10);
        d["buffers"]![0]!["uri"] = uri;
        AssertError(Check("models/car.gltf", Text(d)), ModError.PathEscape, "uri");
    }

    [Fact]
    public void PackageFileBuffer_RelativeToGltf_Ok()
    {
        JsonObject d = Doc(30, 20, 10);
        d["buffers"]![0]!["uri"] = "car%20data.bin";
        Outcome o = Check("models/car.gltf", Text(d), new() { ["models/car data.bin"] = new byte[3 * 30 * 12] });
        Assert.Empty(o.Errors);
    }

    [Fact]
    public void MissingBufferFile_UnknownAssetRef()
    {
        JsonObject d = Doc(30, 20, 10);
        d["buffers"]![0]!["uri"] = "nothere.bin";
        AssertError(Check("models/car.gltf", Text(d)), ModError.UnknownAssetRef);
    }

    [Fact]
    public void BufferFileShorterThanDeclared_GltfInvalid()
    {
        JsonObject d = Doc(30, 20, 10);
        d["buffers"]![0]!["uri"] = "car.bin";
        AssertError(Check("models/car.gltf", Text(d), new() { ["models/car.bin"] = new byte[10] }), ModError.GltfInvalid);
    }

    [Fact]
    public void DataUriNotBase64_GltfInvalid()
    {
        JsonObject d = Doc(30, 20, 10);
        d["buffers"]![0]!["uri"] = "data:text/plain,hello";
        AssertError(Check("models/car.gltf", Text(d)), ModError.GltfInvalid);
    }

    [Fact]
    public void DataUriTotalOver16MiB_TooLarge()
    {
        JsonObject d = Doc(30, 20, 10);
        var buffers = (JsonArray)d["buffers"]!;
        string big = "data:application/octet-stream;base64," + Convert.ToBase64String(new byte[9 * 1024 * 1024]);
        buffers.Add(new JsonObject { ["byteLength"] = 9 * 1024 * 1024, ["uri"] = big });
        buffers.Add(new JsonObject { ["byteLength"] = 9 * 1024 * 1024, ["uri"] = big });
        AssertError(Check("models/car.gltf", Text(d)), ModError.TooLarge);
    }

    // ------------------------------------------------------------------ возможности

    [Fact]
    public void Skin_GltfFeature()
    {
        JsonObject d = Doc(30, 20, 10);
        d["skins"] = new JsonArray(new JsonObject { ["joints"] = new JsonArray(0) });
        d["nodes"]![0]!["skin"] = 0;
        Outcome o = Check("models/car.gltf", Text(d));
        AssertError(o, ModError.GltfFeature, "skin");
    }

    [Fact]
    public void MorphTargets_GltfFeature()
    {
        JsonObject d = Doc(30, 20, 10);
        d["meshes"]![0]!["primitives"]![0]!["targets"] = new JsonArray(new JsonObject { ["POSITION"] = 0 });
        AssertError(Check("models/car.gltf", Text(d)), ModError.GltfFeature, "targets");
    }

    [Fact]
    public void NonTriangleMode_GltfFeature()
    {
        JsonObject d = Doc(30, 20, 10);
        d["meshes"]![0]!["primitives"]![0]!["mode"] = 1;
        AssertError(Check("models/car.gltf", Text(d)), ModError.GltfFeature, "mode");
    }

    [Fact]
    public void Animations_Warning()
    {
        JsonObject d = Doc(30, 20, 10);
        d["animations"] = new JsonArray(new JsonObject());
        Outcome o = Check("models/car.gltf", Text(d));
        Assert.Empty(o.Errors);
        Assert.Contains(o.Warnings, w => w.Code == ModError.GltfFeature);
    }

    [Fact]
    public void RequiredExtension_Whitelist()
    {
        JsonObject ok = Doc(30, 20, 10);
        ok["extensionsRequired"] = new JsonArray("KHR_mesh_quantization", "KHR_texture_transform");
        Assert.Empty(Check("models/car.gltf", Text(ok)).Errors);

        JsonObject bad = Doc(30, 20, 10);
        bad["extensionsRequired"] = new JsonArray("KHR_draco_mesh_compression");
        AssertError(Check("models/car.gltf", Text(bad)), ModError.GltfExtension);
    }

    [Fact]
    public void UnknownUsedExtension_Warning()
    {
        JsonObject d = Doc(30, 20, 10);
        d["extensionsUsed"] = new JsonArray("EXT_whatever");
        Outcome o = Check("models/car.gltf", Text(d));
        Assert.Empty(o.Errors);
        Assert.Contains(o.Warnings, w => w.Code == ModError.GltfExtension);
    }

    // ------------------------------------------------------------------ структура

    [Fact]
    public void WrongVersion_GltfInvalid()
    {
        JsonObject d = Doc(30, 20, 10);
        d["asset"]!["version"] = "1.0";
        AssertError(Check("models/car.gltf", Text(d)), ModError.GltfInvalid, "version");
    }

    [Fact]
    public void BrokenJson_GltfInvalid()
    {
        AssertError(Check("models/car.gltf", Encoding.UTF8.GetBytes("{ not json")), ModError.GltfInvalid);
    }

    [Fact]
    public void JsonDeeperThan32_TooDeep()
    {
        string deep = new string('[', 40) + new string(']', 40);
        string json = "{\"asset\":{\"version\":\"2.0\"},\"extras\":" + deep + "}";
        AssertError(Check("models/car.gltf", Encoding.UTF8.GetBytes(json)), ModError.TooDeep);
    }

    [Fact]
    public void AccessorBeyondBufferView_GltfInvalid()
    {
        JsonObject d = Doc(30, 20, 10);
        d["accessors"]![0]!["count"] = 3 * 30 + 3;
        AssertError(Check("models/car.gltf", Text(d)), ModError.GltfInvalid, "accessors[0]");
    }

    [Fact]
    public void BufferViewBeyondBuffer_GltfInvalid()
    {
        JsonObject d = Doc(30, 20, 10);
        d["bufferViews"]![0]!["byteOffset"] = 4;
        AssertError(Check("models/car.gltf", Text(d)), ModError.GltfInvalid, "bufferViews[0]");
    }

    [Fact]
    public void InvalidIndices_GltfInvalid()
    {
        JsonObject d = Doc(30, 20, 10);
        d["meshes"]![0]!["primitives"]![0]!["attributes"]!["POSITION"] = 99;
        d["nodes"]![1]!["mesh"] = 99;
        d["nodes"]![2]!["children"] = new JsonArray(77);
        Outcome o = Check("models/car.gltf", Text(d));
        AssertError(o, ModError.GltfInvalid, "POSITION");
        AssertError(o, ModError.GltfInvalid, "nodes[1].mesh");
        AssertError(o, ModError.GltfInvalid, "nodes[2].children");
    }

    [Fact]
    public void IndexedPrimitive_CountsIndices()
    {
        JsonObject d = Doc(30, 20, 10);
        // LOD0: 30 треугольников из 90 вершин, но индексы - всего 6 штук (2 треугольника), LOD1 = 20 > 2 -> порядок нарушен
        byte[] idx = new byte[12];
        var views = (JsonArray)d["bufferViews"]!;
        var buffers = (JsonArray)d["buffers"]!;
        buffers.Add(new JsonObject { ["byteLength"] = idx.Length, ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(idx) });
        views.Add(new JsonObject { ["buffer"] = 1, ["byteLength"] = idx.Length });
        var acc = (JsonArray)d["accessors"]!;
        acc.Add(new JsonObject { ["bufferView"] = 1, ["componentType"] = 5123, ["count"] = 6, ["type"] = "SCALAR" });
        d["meshes"]![0]!["primitives"]![0]!["indices"] = acc.Count - 1;
        Outcome o = Check("models/car.gltf", Text(d));
        AssertError(o, ModError.LodOrder);
        Assert.Contains("LOD0 (2)", o.Errors.First(i => i.Code == ModError.LodOrder).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeCycle_GltfInvalid()
    {
        JsonObject d = Doc(30, 20, 10);
        d["nodes"]![0]!["children"] = new JsonArray(0);
        AssertError(Check("models/car.gltf", Text(d)), ModError.GltfInvalid, "nodes");
    }

    [Fact]
    public void LodChildren_CountedInSubtree()
    {
        JsonObject d = Doc(30, 20, 10);
        // LOD0 - пустой узел, меш лежит в потомке: треугольники учитываются по поддереву
        var nodes = (JsonArray)d["nodes"]!;
        nodes.Add(new JsonObject { ["name"] = "body", ["mesh"] = 0 });
        nodes[0]!["mesh"] = null;
        ((JsonObject)nodes[0]!).Remove("mesh");
        nodes[0]!["children"] = new JsonArray(nodes.Count - 1);
        Assert.Empty(Check("models/car.gltf", Text(d)).Errors);
    }

    // ------------------------------------------------------------------ изображения

    private static JsonObject WithImage(string uri)
    {
        JsonObject d = Doc(30, 20, 10);
        d["images"] = new JsonArray(new JsonObject { ["uri"] = uri });
        d["textures"] = new JsonArray(new JsonObject { ["source"] = 0 });
        return d;
    }

    [Fact]
    public void Images_Headers_Ok()
    {
        Assert.Empty(Check("models/car.gltf", Text(WithImage(DataUri(Png(4096, 4096))))).Errors);
        Assert.Empty(Check("models/car.gltf", Text(WithImage(DataUri(Jpeg(512, 256))))).Errors);
        Assert.Empty(Check("models/car.gltf", Text(WithImage(DataUri(Ktx2(1024, 1024))))).Errors);
    }

    [Fact]
    public void Image_File_Ok()
    {
        Outcome o = Check("models/car.gltf", Text(WithImage("tex/body.png")), new() { ["models/tex/body.png"] = Png(64, 64) });
        Assert.Empty(o.Errors);
    }

    [Theory]
    [InlineData(4097u, 16u)]
    [InlineData(16u, 5000u)]
    public void Image_TooBig_TooLarge(uint w, uint h)
    {
        AssertError(Check("models/car.gltf", Text(WithImage(DataUri(Png(w, h))))), ModError.TooLarge, "images[0]");
        AssertError(Check("models/car.gltf", Text(WithImage(DataUri(Ktx2(w, h))))), ModError.TooLarge, "images[0]");
    }

    [Fact]
    public void Jpeg_TooBig_TooLarge()
    {
        AssertError(Check("models/car.gltf", Text(WithImage(DataUri(Jpeg(8000, 100))))), ModError.TooLarge, "images[0]");
    }

    [Fact]
    public void Image_BadHeader_GltfInvalid()
    {
        AssertError(Check("models/car.gltf", Text(WithImage(DataUri(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 })))), ModError.GltfInvalid, "images[0]");
        byte[] truncated = Png(16, 16)[..20];
        AssertError(Check("models/car.gltf", Text(WithImage(DataUri(truncated)))), ModError.GltfInvalid, "images[0]");
    }

    [Fact]
    public void Image_ExternalUri_PathEscape()
    {
        AssertError(Check("models/car.gltf", Text(WithImage("https://example.com/a.png"))), ModError.PathEscape, "images[0]");
    }

    [Fact]
    public void Image_InBufferView_Checked()
    {
        JsonObject d = Doc(30, 20, 10);
        byte[] png = Png(5000, 16);
        var buffers = (JsonArray)d["buffers"]!;
        buffers.Add(new JsonObject { ["byteLength"] = png.Length, ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(png) });
        ((JsonArray)d["bufferViews"]!).Add(new JsonObject { ["buffer"] = 1, ["byteLength"] = png.Length });
        d["images"] = new JsonArray(new JsonObject { ["bufferView"] = 1, ["mimeType"] = "image/png" });
        AssertError(Check("models/car.gltf", Text(d)), ModError.TooLarge, "images[0]");
    }

    [Fact]
    public void Texture_InvalidSource_GltfInvalid()
    {
        JsonObject d = Doc(30, 20, 10);
        d["textures"] = new JsonArray(new JsonObject { ["source"] = 3 });
        AssertError(Check("models/car.gltf", Text(d)), ModError.GltfInvalid, "textures[0]");
    }

    // ------------------------------------------------------------------ visual в каталоге

    [Fact]
    public void Visual_MissingFile_UnknownAssetRef()
    {
        // файл-заглушка нужен, чтобы пакет собрался, а ссылка указывает на другой
        string dir = Path.Combine(_tmp, PkgId);
        Outcome ok = Check("models/car.gltf", Text(Doc(30, 20, 10)));
        Assert.Empty(ok.Errors);
        File.Delete(Path.Combine(dir, "models", "car.gltf"));
        PackageSet set = PackageLoader.Load([new PackageSearchDir(_tmp, false)], [PkgId]);
        var errors = new List<ModIssue>();
        VisualValidator.Validate(set, ContentCatalog.Build(set, errors), RenderBudgets.Default, errors, []);
        AssertError(new Outcome(errors, []), ModError.UnknownAssetRef);
    }

    [Fact]
    public void Visual_NotGltfExtension_FileNotAllowed()
    {
        string json = Path.Combine(_tmp, PkgId, "models", "x.json");
        AssertError(Check("models/x.json", Encoding.UTF8.GetBytes("{}")), ModError.FileNotAllowed);
        Assert.True(File.Exists(json));
    }

    // ------------------------------------------------------------------ validate-package

    [Fact]
    public void ValidatePackage_Base_Ok()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = ValidatePackageCommand.Run(["validate-package", Path.Combine(Root(), "content", "base"), "--game"], o, e);
        Assert.True(code == 0, e.ToString());
        Assert.Contains("ok: base", o.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePackage_GoodAndBadModel_ExitCodes()
    {
        Check("models/car.gltf", Text(Doc(30, 20, 10)));
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(0, ValidatePackageCommand.Run(["validate-package", Path.Combine(_tmp, PkgId)], o, e));

        File.WriteAllBytes(Path.Combine(_tmp, PkgId, "models", "car.gltf"), Text(Doc(30, 20)));
        e = new StringWriter();
        Assert.Equal(1, ValidatePackageCommand.Run(["validate-package", Path.Combine(_tmp, PkgId)], o, e));
        Assert.Contains("LodMissing", e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePackage_Usage()
    {
        var o = new StringWriter();
        var e = new StringWriter();
        Assert.Equal(2, ValidatePackageCommand.Run(["validate-package"], o, e));
        Assert.Equal(2, ValidatePackageCommand.Run(["validate-package", Path.Combine(_tmp, "nope")], o, e));
        Assert.Equal(2, ValidatePackageCommand.Run(["validate-package", "x", "--wat"], o, e));
    }
}
