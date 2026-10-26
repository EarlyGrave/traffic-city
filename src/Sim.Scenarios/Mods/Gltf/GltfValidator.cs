using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Sim.Scenarios.Mods.Gltf;

/// <summary>
/// Проверка glTF/GLB без декодирования геометрии и без сторонних библиотек (modding.md §4.2, §4.3, §7).
/// Ничего не бросает наружу: ошибки и предупреждения — <see cref="ModIssue"/>. Файл читается целиком (≤ 128 МиБ,
/// размер проверяется до чтения), внешние буферы - только по длине файла, изображения - по заголовку.
/// </summary>
public static class GltfValidator
{
    /// <summary>Предел сообщений на один файл (защита от лавины однотипных ошибок).</summary>
    public const int MaxIssuesPerFile = 64;

    /// <summary>Расширения glTF, допустимые в <c>extensionsRequired</c>.</summary>
    public static readonly string[] SupportedExtensions =
    [
        "KHR_mesh_quantization",
        "KHR_texture_transform",
        "KHR_texture_basisu",
        "KHR_materials_emissive_strength",
    ];

    private const long MaxEmbeddedBytes = 16L * 1024 * 1024;
    private const int MaxNodes = 4096;
    private const int MaxPrimitives = 1024;

    /// <summary>Проверить файл <paramref name="file"/> пакета; <paramref name="kind"/> выбирает бюджет LOD.</summary>
    public static void Validate(
        LoadedPackage pkg, string file, AssetKind kind, RenderBudgets budgets, List<ModIssue> errors, List<ModIssue> warnings)
    {
        var c = new Ctx(pkg, file, errors, warnings);
        try
        {
            Run(c, kind, budgets);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            c.Error("$", ModError.GltfInvalid, e.GetType().Name);
        }
    }

    private static void Run(Ctx c, AssetKind kind, RenderBudgets budgets)
    {
        if (!TryOpen(c, out ReadOnlyMemory<byte> jsonBytes, out GlbChunks glb, out bool isGlb))
        {
            return;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonBytes, new JsonDocumentOptions { MaxDepth = ModLimits.MaxJsonDepth });
        }
        catch (JsonException e)
        {
            bool deep = e.Message.Contains("depth", StringComparison.OrdinalIgnoreCase);
            c.Error("$", deep ? ModError.TooDeep : ModError.GltfInvalid, deep ? "JSON too deep" : "invalid JSON");
            return;
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                c.Error("$", ModError.GltfInvalid, "root is not an object");
                return;
            }

            CheckAsset(c, root);
            CheckExtensions(c, root);
            List<Buf> buffers = CheckBuffers(c, root, isGlb, glb);
            List<ViewInfo> views = CheckViews(c, root, buffers);
            List<AccInfo> accessors = CheckAccessors(c, root, views);
            CheckImages(c, root, buffers, views);
            CheckTextures(c, root);
            List<long> meshTris = CheckMeshes(c, root, accessors);
            CheckExtras(c, root);
            List<NodeInfo> nodes = CheckNodes(c, root, meshTris.Count);
            if (c.ErrorCount == 0)
            {
                CheckLods(c, root, nodes, meshTris, kind, budgets);
            }
        }
    }

    // ------------------------------------------------------------------ открытие файла

    private static bool TryOpen(Ctx c, out ReadOnlyMemory<byte> json, out GlbChunks glb, out bool isGlb)
    {
        json = default;
        glb = default;
        string ext = Path.GetExtension(c.File).ToLowerInvariant();
        isGlb = ext == ".glb";
        if (!isGlb && ext != ".gltf")
        {
            c.Error("$", ModError.FileNotAllowed, "expected .gltf or .glb");
            return false;
        }

        string full = Path.Combine(c.Pkg.Directory, c.File);
        var info = new FileInfo(full);
        if (!info.Exists)
        {
            c.Error("$", ModError.UnknownAssetRef, "file not found");
            return false;
        }

        if (info.Length > ModLimits.MaxGltfBytes)
        {
            c.Error("$", ModError.TooLarge, $"{N(info.Length)} bytes, limit {N(ModLimits.MaxGltfBytes)}");
            return false;
        }

        byte[] bytes = File.ReadAllBytes(full);
        if (!isGlb)
        {
            json = bytes;
            return true;
        }

        if (!GlbReader.TryRead(bytes, out glb, out string err))
        {
            c.Error("$", ModError.GltfInvalid, err);
            return false;
        }

        json = glb.Json;
        return true;
    }

    // ------------------------------------------------------------------ asset, расширения

    private static void CheckAsset(Ctx c, JsonElement root)
    {
        if (!root.TryGetProperty("asset", out JsonElement asset) || asset.ValueKind != JsonValueKind.Object)
        {
            c.Error("$.asset", ModError.GltfInvalid, "missing asset");
            return;
        }

        if (!asset.TryGetProperty("version", out JsonElement v) || v.ValueKind != JsonValueKind.String || v.GetString() != "2.0")
        {
            c.Error("$.asset.version", ModError.GltfInvalid, "version must be \"2.0\"");
        }
    }

    private static void CheckExtensions(Ctx c, JsonElement root)
    {
        foreach ((string key, bool required) in new[] { ("extensionsRequired", true), ("extensionsUsed", false) })
        {
            if (!root.TryGetProperty(key, out JsonElement arr))
            {
                continue;
            }

            if (arr.ValueKind != JsonValueKind.Array)
            {
                c.Error("$." + key, ModError.GltfInvalid, "expected array");
                continue;
            }

            int i = 0;
            foreach (JsonElement e in arr.EnumerateArray())
            {
                string path = Idx("$." + key, i++);
                string? name = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (name == null)
                {
                    c.Error(path, ModError.GltfInvalid, "expected string");
                }
                else if (Array.IndexOf(SupportedExtensions, name) < 0)
                {
                    if (required)
                    {
                        c.Error(path, ModError.GltfExtension, name);
                    }
                    else
                    {
                        c.Warn(path, ModError.GltfExtension, name + " is not supported and will be ignored");
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ буферы

    private static List<Buf> CheckBuffers(Ctx c, JsonElement root, bool isGlb, GlbChunks glb)
    {
        var list = new List<Buf>();
        if (!TryArray(c, root, "buffers", out JsonElement[] arr))
        {
            return list;
        }

        for (int i = 0; i < arr.Length; i++)
        {
            string path = Idx("$.buffers", i);
            var buf = new Buf();
            list.Add(buf);
            JsonElement e = arr[i];
            if (e.ValueKind != JsonValueKind.Object || !GetLong(e, "byteLength", out long len) || len <= 0)
            {
                c.Error(path + ".byteLength", ModError.GltfInvalid, "byteLength must be a positive integer");
                continue;
            }

            if (len > ModLimits.MaxBinBytes)
            {
                c.Error(path + ".byteLength", ModError.TooLarge, N(len) + " bytes");
                continue;
            }

            if (!e.TryGetProperty("uri", out JsonElement uriEl))
            {
                if (isGlb && i == 0 && glb.HasBin)
                {
                    buf.Data = glb.Bin;
                    buf.Length = glb.Bin.Length;
                }
                else
                {
                    c.Error(path, ModError.GltfInvalid, "buffer without uri is allowed only as buffer 0 of a GLB with a BIN chunk");
                    continue;
                }
            }
            else if (uriEl.ValueKind != JsonValueKind.String)
            {
                c.Error(path + ".uri", ModError.GltfInvalid, "expected string");
                continue;
            }
            else if (!ResolveUri(c, uriEl.GetString()!, path + ".uri", out byte[]? data, out string? rel, out long fileLen))
            {
                continue;
            }
            else if (data != null)
            {
                buf.Data = data;
                buf.Length = data.Length;
            }
            else
            {
                buf.FileRel = rel;
                buf.Length = fileLen;
            }

            if (buf.Length < len)
            {
                c.Error(path + ".byteLength", ModError.GltfInvalid, $"declared {N(len)} bytes, actual {N(buf.Length)}");
            }
            else
            {
                buf.Declared = len;
            }
        }

        return list;
    }

    private static List<ViewInfo> CheckViews(Ctx c, JsonElement root, List<Buf> buffers)
    {
        var list = new List<ViewInfo>();
        if (!TryArray(c, root, "bufferViews", out JsonElement[] arr))
        {
            return list;
        }

        for (int i = 0; i < arr.Length; i++)
        {
            string path = Idx("$.bufferViews", i);
            JsonElement e = arr[i];
            var v = new ViewInfo(-1, 0, 0, 0);
            list.Add(v);
            if (e.ValueKind != JsonValueKind.Object || !GetLong(e, "buffer", out long b) || b < 0 || b >= buffers.Count)
            {
                c.Error(path + ".buffer", ModError.GltfInvalid, "invalid buffer index");
                continue;
            }

            long offset = 0;
            if (e.TryGetProperty("byteOffset", out _) && (!GetLong(e, "byteOffset", out offset) || offset < 0))
            {
                c.Error(path + ".byteOffset", ModError.GltfInvalid, "invalid byteOffset");
                continue;
            }

            if (!GetLong(e, "byteLength", out long len) || len <= 0)
            {
                c.Error(path + ".byteLength", ModError.GltfInvalid, "byteLength must be a positive integer");
                continue;
            }

            long stride = 0;
            if (e.TryGetProperty("byteStride", out _) && (!GetLong(e, "byteStride", out stride) || stride < 4 || stride > 252))
            {
                c.Error(path + ".byteStride", ModError.GltfInvalid, "byteStride must be in [4, 252]");
                continue;
            }

            Buf buf = buffers[(int)b];
            if (buf.Declared == 0)
            {
                continue; // ошибка буфера уже сообщена
            }

            if (offset + len > buf.Declared)
            {
                c.Error(path, ModError.GltfInvalid, $"range [{N(offset)}, {N(offset + len)}) outside buffer {N(b)} of {N(buf.Declared)} bytes");
                continue;
            }

            list[i] = new ViewInfo((int)b, offset, len, stride);
        }

        return list;
    }

    // ------------------------------------------------------------------ акцессоры

    private static List<AccInfo> CheckAccessors(Ctx c, JsonElement root, List<ViewInfo> views)
    {
        var list = new List<AccInfo>();
        if (!TryArray(c, root, "accessors", out JsonElement[] arr))
        {
            return list;
        }

        for (int i = 0; i < arr.Length; i++)
        {
            string path = Idx("$.accessors", i);
            JsonElement e = arr[i];
            list.Add(new AccInfo(false, 0, 0, 0));
            if (e.ValueKind != JsonValueKind.Object)
            {
                c.Error(path, ModError.GltfInvalid, "expected object");
                continue;
            }

            if (!GetLong(e, "componentType", out long ct) || ComponentSize(ct) == 0)
            {
                c.Error(path + ".componentType", ModError.GltfInvalid, "invalid componentType");
                continue;
            }

            string? type = e.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            int comps = TypeComponents(type);
            if (comps == 0)
            {
                c.Error(path + ".type", ModError.GltfInvalid, "invalid type");
                continue;
            }

            if (!GetLong(e, "count", out long count) || count <= 0)
            {
                c.Error(path + ".count", ModError.GltfInvalid, "count must be a positive integer");
                continue;
            }

            long elem = ComponentSize(ct) * comps;
            if (e.TryGetProperty("bufferView", out _))
            {
                if (!GetLong(e, "bufferView", out long bv) || bv < 0 || bv >= views.Count)
                {
                    c.Error(path + ".bufferView", ModError.GltfInvalid, "invalid bufferView index");
                    continue;
                }

                ViewInfo v = views[(int)bv];
                if (v.Buffer < 0)
                {
                    continue; // ошибка вью уже сообщена
                }

                long off = 0;
                if (e.TryGetProperty("byteOffset", out _) && (!GetLong(e, "byteOffset", out off) || off < 0))
                {
                    c.Error(path + ".byteOffset", ModError.GltfInvalid, "invalid byteOffset");
                    continue;
                }

                long stride = v.Stride != 0 ? v.Stride : elem;
                if (stride < elem)
                {
                    c.Error(path, ModError.GltfInvalid, $"byteStride {N(stride)} is smaller than the element size {N(elem)}");
                    continue;
                }

                long need = off + (stride * (count - 1)) + elem;
                if (need > v.Length)
                {
                    c.Error(path, ModError.GltfInvalid, $"accessor needs {N(need)} bytes, bufferView {N(bv)} has {N(v.Length)}");
                    continue;
                }
            }

            list[i] = new AccInfo(true, (int)ct, comps, count);
        }

        return list;
    }

    private static int ComponentSize(long componentType) => componentType switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => 0,
    };

    private static int TypeComponents(string? type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" or "MAT2" => 4,
        "MAT3" => 9,
        "MAT4" => 16,
        _ => 0,
    };

    // ------------------------------------------------------------------ изображения, текстуры

    private static void CheckImages(Ctx c, JsonElement root, List<Buf> buffers, List<ViewInfo> views)
    {
        if (!TryArray(c, root, "images", out JsonElement[] arr))
        {
            return;
        }

        for (int i = 0; i < arr.Length; i++)
        {
            string path = Idx("$.images", i);
            JsonElement e = arr[i];
            if (e.ValueKind != JsonValueKind.Object)
            {
                c.Error(path, ModError.GltfInvalid, "expected object");
                continue;
            }

            bool hasUri = e.TryGetProperty("uri", out JsonElement uriEl);
            bool hasView = e.TryGetProperty("bufferView", out _);
            if (hasUri == hasView)
            {
                c.Error(path, ModError.GltfInvalid, "image needs exactly one of uri and bufferView");
                continue;
            }

            Stream? stream = null;
            long size = 0;
            try
            {
                if (hasUri)
                {
                    if (uriEl.ValueKind != JsonValueKind.String)
                    {
                        c.Error(path + ".uri", ModError.GltfInvalid, "expected string");
                        continue;
                    }

                    if (!ResolveUri(c, uriEl.GetString()!, path + ".uri", out byte[]? data, out string? rel, out long fileLen))
                    {
                        continue;
                    }

                    if (data != null)
                    {
                        stream = new MemoryStream(data, false);
                        size = data.Length;
                    }
                    else
                    {
                        stream = new FileStream(Path.Combine(c.Pkg.Directory, rel!), FileMode.Open, FileAccess.Read, FileShare.Read);
                        size = fileLen;
                    }
                }
                else
                {
                    if (!GetLong(e, "bufferView", out long bv) || bv < 0 || bv >= views.Count || views[(int)bv].Buffer < 0)
                    {
                        c.Error(path + ".bufferView", ModError.GltfInvalid, "invalid bufferView index");
                        continue;
                    }

                    ViewInfo v = views[(int)bv];
                    ReadOnlyMemory<byte> bytes = BufferBytes(c, buffers[v.Buffer]);
                    if (bytes.Length < v.Offset + v.Length)
                    {
                        c.Error(path, ModError.GltfInvalid, "image bufferView outside buffer data");
                        continue;
                    }

                    stream = new MemoryStream(bytes.Slice((int)v.Offset, (int)v.Length).ToArray(), false);
                    size = v.Length;
                }

                if (size > ModLimits.MaxTextureBytes)
                {
                    c.Error(path, ModError.TooLarge, $"{N(size)} bytes, limit {N(ModLimits.MaxTextureBytes)}");
                    continue;
                }

                if (!ImageHeader.TryRead(stream, out ImageFormat fmt, out long w, out long h) || w <= 0 || h <= 0)
                {
                    c.Error(path, ModError.GltfInvalid, "unrecognized or truncated PNG/JPEG/KTX2 header");
                    continue;
                }

                if (w > ModLimits.MaxTextureSide || h > ModLimits.MaxTextureSide)
                {
                    c.Error(path, ModError.TooLarge, $"{fmt} {N(w)}x{N(h)}, limit {N(ModLimits.MaxTextureSide)}x{N(ModLimits.MaxTextureSide)}");
                }
            }
            finally
            {
                stream?.Dispose();
            }
        }
    }

    private static void CheckTextures(Ctx c, JsonElement root)
    {
        if (!TryArray(c, root, "textures", out JsonElement[] arr))
        {
            return;
        }

        int images = root.TryGetProperty("images", out JsonElement im) && im.ValueKind == JsonValueKind.Array ? im.GetArrayLength() : 0;
        for (int i = 0; i < arr.Length; i++)
        {
            string path = Idx("$.textures", i);
            JsonElement e = arr[i];
            if (e.ValueKind != JsonValueKind.Object)
            {
                c.Error(path, ModError.GltfInvalid, "expected object");
                continue;
            }

            if (e.TryGetProperty("source", out _) && (!GetLong(e, "source", out long s) || s < 0 || s >= images))
            {
                c.Error(path + ".source", ModError.GltfInvalid, "invalid image index");
            }
        }
    }

    // ------------------------------------------------------------------ меши

    private static List<long> CheckMeshes(Ctx c, JsonElement root, List<AccInfo> accessors)
    {
        var tris = new List<long>();
        if (!TryArray(c, root, "meshes", out JsonElement[] arr))
        {
            return tris;
        }

        int materials = root.TryGetProperty("materials", out JsonElement mt) && mt.ValueKind == JsonValueKind.Array ? mt.GetArrayLength() : 0;
        int primitiveTotal = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            string path = Idx("$.meshes", i);
            long meshTris = 0;
            tris.Add(0);
            JsonElement e = arr[i];
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("primitives", out JsonElement prims) || prims.ValueKind != JsonValueKind.Array || prims.GetArrayLength() == 0)
            {
                c.Error(path + ".primitives", ModError.GltfInvalid, "mesh needs a non-empty primitives array");
                continue;
            }

            int pi = 0;
            foreach (JsonElement p in prims.EnumerateArray())
            {
                string ppath = Idx(path + ".primitives", pi++);
                if (++primitiveTotal > MaxPrimitives)
                {
                    c.Error("$.meshes", ModError.TooLarge, $"more than {N(MaxPrimitives)} primitives");
                    return tris;
                }

                meshTris += CheckPrimitive(c, p, ppath, accessors, materials);
            }

            tris[i] = meshTris;
        }

        return tris;
    }

    private static long CheckPrimitive(Ctx c, JsonElement p, string path, List<AccInfo> accessors, int materials)
    {
        if (p.ValueKind != JsonValueKind.Object)
        {
            c.Error(path, ModError.GltfInvalid, "expected object");
            return 0;
        }

        long mode = 4;
        if (p.TryGetProperty("mode", out _) && !GetLong(p, "mode", out mode))
        {
            c.Error(path + ".mode", ModError.GltfInvalid, "expected integer");
            return 0;
        }

        if (mode != 4)
        {
            c.Error(path + ".mode", ModError.GltfFeature, $"only triangle lists (mode 4) are supported, got {N(mode)}");
            return 0;
        }

        if (p.TryGetProperty("targets", out JsonElement targets) && !(targets.ValueKind == JsonValueKind.Array && targets.GetArrayLength() == 0))
        {
            c.Error(path + ".targets", ModError.GltfFeature, "morph targets are not supported");
        }

        if (p.TryGetProperty("material", out _) && (!GetLong(p, "material", out long m) || m < 0 || m >= materials))
        {
            c.Error(path + ".material", ModError.GltfInvalid, "invalid material index");
        }

        if (!p.TryGetProperty("attributes", out JsonElement attrs) || attrs.ValueKind != JsonValueKind.Object)
        {
            c.Error(path + ".attributes", ModError.GltfInvalid, "missing attributes");
            return 0;
        }

        long vertexCount = -1;
        bool bad = false;
        foreach (JsonProperty a in attrs.EnumerateObject())
        {
            string apath = path + ".attributes." + a.Name;
            if (!GetLongValue(a.Value, out long ai) || ai < 0 || ai >= accessors.Count)
            {
                c.Error(apath, ModError.GltfInvalid, "invalid accessor index");
                bad = true;
                continue;
            }

            if (!accessors[(int)ai].Valid)
            {
                bad = true;
                continue;
            }

            if (a.Name == "POSITION")
            {
                AccInfo pos = accessors[(int)ai];
                if (pos.Components != 3 || pos.ComponentType == 5125)
                {
                    c.Error(apath, ModError.GltfInvalid, "POSITION must be VEC3");
                    bad = true;
                }

                vertexCount = pos.Count;
            }
        }

        if (vertexCount < 0 && !bad)
        {
            c.Error(path + ".attributes", ModError.GltfInvalid, "missing POSITION");
            bad = true;
        }

        long indexCount = -1;
        if (p.TryGetProperty("indices", out _))
        {
            string ipath = path + ".indices";
            if (!GetLong(p, "indices", out long ii) || ii < 0 || ii >= accessors.Count)
            {
                c.Error(ipath, ModError.GltfInvalid, "invalid accessor index");
                bad = true;
            }
            else if (accessors[(int)ii].Valid)
            {
                AccInfo ia = accessors[(int)ii];
                if (ia.Components != 1 || ia.ComponentType is not (5121 or 5123 or 5125))
                {
                    c.Error(ipath, ModError.GltfInvalid, "indices must be SCALAR of an unsigned integer type");
                    bad = true;
                }

                indexCount = ia.Count;
            }
            else
            {
                bad = true;
            }
        }

        if (bad)
        {
            return 0;
        }

        long n = indexCount >= 0 ? indexCount : vertexCount;
        if (n % 3 != 0)
        {
            c.Error(path, ModError.GltfInvalid, $"{N(n)} elements is not a multiple of 3");
            return 0;
        }

        return n / 3;
    }

    private static void CheckExtras(Ctx c, JsonElement root)
    {
        if (root.TryGetProperty("skins", out JsonElement skins) && skins.ValueKind == JsonValueKind.Array && skins.GetArrayLength() > 0)
        {
            c.Error("$.skins", ModError.GltfFeature, "skins are not supported");
        }

        if (root.TryGetProperty("animations", out JsonElement anims) && anims.ValueKind == JsonValueKind.Array && anims.GetArrayLength() > 0)
        {
            c.Warn("$.animations", ModError.GltfFeature, "animations are ignored");
        }
    }

    // ------------------------------------------------------------------ узлы

    private static List<NodeInfo> CheckNodes(Ctx c, JsonElement root, int meshCount)
    {
        var list = new List<NodeInfo>();
        if (!TryArray(c, root, "nodes", out JsonElement[] arr))
        {
            return list;
        }

        if (arr.Length > MaxNodes)
        {
            c.Error("$.nodes", ModError.TooLarge, $"{N(arr.Length)} nodes, limit {N(MaxNodes)}");
            return list;
        }

        for (int i = 0; i < arr.Length; i++)
        {
            string path = Idx("$.nodes", i);
            JsonElement e = arr[i];
            int mesh = -1;
            int[] children = [];
            string? name = null;
            if (e.ValueKind != JsonValueKind.Object)
            {
                c.Error(path, ModError.GltfInvalid, "expected object");
                list.Add(new NodeInfo(name, mesh, children));
                continue;
            }

            if (e.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String)
            {
                name = n.GetString();
            }

            if (e.TryGetProperty("skin", out _))
            {
                c.Error(path + ".skin", ModError.GltfFeature, "skins are not supported");
            }

            if (e.TryGetProperty("mesh", out _))
            {
                if (GetLong(e, "mesh", out long m) && m >= 0 && m < meshCount)
                {
                    mesh = (int)m;
                }
                else
                {
                    c.Error(path + ".mesh", ModError.GltfInvalid, "invalid mesh index");
                }
            }

            if (e.TryGetProperty("children", out JsonElement ch))
            {
                if (ch.ValueKind != JsonValueKind.Array)
                {
                    c.Error(path + ".children", ModError.GltfInvalid, "expected array");
                }
                else
                {
                    var ids = new List<int>();
                    int k = 0;
                    foreach (JsonElement x in ch.EnumerateArray())
                    {
                        if (GetLongValue(x, out long ci) && ci >= 0 && ci < arr.Length)
                        {
                            ids.Add((int)ci);
                        }
                        else
                        {
                            c.Error(Idx(path + ".children", k), ModError.GltfInvalid, "invalid node index");
                        }

                        k++;
                    }

                    children = [.. ids];
                }
            }

            list.Add(new NodeInfo(name, mesh, children));
        }

        return list;
    }

    // ------------------------------------------------------------------ LOD (§4.3)

    private static void CheckLods(Ctx c, JsonElement root, List<NodeInfo> nodes, List<long> meshTris, AssetKind kind, RenderBudgets budgets)
    {
        KindBudget budget = budgets.For(kind);
        int sceneIndex = 0;
        if (root.TryGetProperty("scene", out _))
        {
            if (!GetLong(root, "scene", out long sceneValue))
            {
                c.Error("$.scene", ModError.GltfInvalid, "expected integer");
                return;
            }

            sceneIndex = (int)Math.Clamp(sceneValue, -1, int.MaxValue);
        }

        if (!root.TryGetProperty("scenes", out JsonElement scenes) || scenes.ValueKind != JsonValueKind.Array || scenes.GetArrayLength() == 0)
        {
            c.Error("$.scenes", ModError.LodMissing, $"no scene, {N(budget.MinLods)} LOD levels (LOD0, LOD1, ...) required");
            return;
        }

        if (sceneIndex < 0 || sceneIndex >= scenes.GetArrayLength())
        {
            c.Error("$.scene", ModError.GltfInvalid, "invalid scene index");
            return;
        }

        JsonElement scene = scenes[sceneIndex];
        var levelNodes = new SortedDictionary<int, int>();
        if (scene.ValueKind == JsonValueKind.Object && scene.TryGetProperty("nodes", out JsonElement rootNodes) && rootNodes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement r in rootNodes.EnumerateArray())
            {
                if (!GetLongValue(r, out long ni) || ni < 0 || ni >= nodes.Count)
                {
                    c.Error(Idx($"$.scenes[{N(sceneIndex)}].nodes", 0), ModError.GltfInvalid, "invalid node index");
                    return;
                }

                if (TryParseLod(nodes[(int)ni].Name, out int level))
                {
                    if (!levelNodes.TryAdd(level, (int)ni))
                    {
                        c.Error("$.nodes", ModError.GltfInvalid, $"duplicate root node LOD{N(level)}");
                        return;
                    }
                }
            }
        }

        int count = levelNodes.Count;
        if (count == 0)
        {
            c.Error("$.scenes", ModError.LodMissing, $"no root nodes named LOD0..LODn, at least {N(budget.MinLods)} required");
            return;
        }

        int maxLevel = -1;
        foreach (int l in levelNodes.Keys)
        {
            maxLevel = Math.Max(maxLevel, l);
        }

        if (maxLevel + 1 != count)
        {
            c.Error("$.scenes", ModError.LodMissing, $"LOD levels must be contiguous from LOD0, found {N(count)} levels up to LOD{N(maxLevel)}");
            return;
        }

        if (count < budget.MinLods)
        {
            c.Error("$.scenes", ModError.LodMissing, $"{N(count)} LOD levels, at least {N(budget.MinLods)} required");
            return;
        }

        var state = new byte[nodes.Count];
        var memo = new long[nodes.Count];
        var tris = new long[count];
        foreach (KeyValuePair<int, int> kv in levelNodes)
        {
            long t = SubtreeTris(nodes, meshTris, kv.Value, state, memo, 0);
            if (t < 0)
            {
                c.Error("$.nodes", ModError.GltfInvalid, $"cycle in the node hierarchy under LOD{N(kv.Key)}");
                return;
            }

            tris[kv.Key] = t;
        }

        for (int i = 1; i < count; i++)
        {
            if (tris[i] > tris[i - 1])
            {
                c.Error("$.scenes", ModError.LodOrder, $"LOD{N(i)} has {N(tris[i])} triangles, more than LOD{N(i - 1)} ({N(tris[i - 1])})");
            }
        }

        for (int i = 0; i < count && i < budget.MaxTris.Count; i++)
        {
            int? max = budget.MaxTris[i];
            if (max is int limit && tris[i] > limit)
            {
                c.Error("$.scenes", ModError.LodOverBudget, $"LOD{N(i)} has {N(tris[i])} triangles, budget {N(limit)}");
            }
        }
    }

    // Число треугольников поддерева с мемоизацией (узел может входить в несколько поддеревьев). -1 - цикл.
    // state: 0 - не посещён, 1 - в обходе, 2 - готов. Сумма ограничена сверху (DAG с разветвлением растёт экспоненциально).
    private static long SubtreeTris(List<NodeInfo> nodes, List<long> meshTris, int index, byte[] state, long[] memo, int depth)
    {
        if (state[index] == 2)
        {
            return memo[index];
        }

        if (state[index] == 1 || depth > MaxNodes)
        {
            return -1;
        }

        state[index] = 1;
        NodeInfo n = nodes[index];
        long total = n.Mesh >= 0 ? meshTris[n.Mesh] : 0;
        foreach (int child in n.Children)
        {
            long t = SubtreeTris(nodes, meshTris, child, state, memo, depth + 1);
            if (t < 0)
            {
                return -1;
            }

            total = Math.Min(total + t, long.MaxValue / 4);
        }

        state[index] = 2;
        memo[index] = total;
        return total;
    }

    private static bool TryParseLod(string? name, out int level)
    {
        level = 0;
        if (name == null || name.Length < 4 || name.Length > 6 || !name.StartsWith("LOD", StringComparison.Ordinal))
        {
            return false;
        }

        for (int i = 3; i < name.Length; i++)
        {
            if (name[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out level);
    }

    // ------------------------------------------------------------------ URI, данные

    // data != null - декодированный data:-URI; иначе rel - путь файла внутри пакета и fileLen - его длина.
    private static bool ResolveUri(Ctx c, string uri, string path, out byte[]? data, out string? rel, out long fileLen)
    {
        data = null;
        rel = null;
        fileLen = 0;
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeDataUri(c, uri, path, out data);
        }

        if (uri.Length == 0 || uri[0] == '/' || uri[0] == '\\' || uri.Contains(':', StringComparison.Ordinal))
        {
            c.Error(path, ModError.PathEscape, "only package files and data: URIs are allowed");
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(uri);
        }
        catch (UriFormatException)
        {
            c.Error(path, ModError.GltfInvalid, "malformed URI");
            return false;
        }

        int slash = c.File.LastIndexOf('/');
        string full = slash >= 0 ? c.File[..slash] + "/" + decoded : decoded;
        if (!SafePath.IsSafeRelative(full))
        {
            c.Error(path, ModError.PathEscape, "path leaves the package or is unsafe");
            return false;
        }

        int idx = FindFile(c.Pkg.Files, full);
        if (idx < 0)
        {
            c.Error(path, ModError.UnknownAssetRef, "referenced file '" + full + "' is not in the package");
            return false;
        }

        if (!SafePath.IsInsideRoot(c.Pkg.Directory, full))
        {
            c.Error(path, ModError.PathEscape, "path leaves the package or goes through a symlink");
            return false;
        }

        rel = full;
        fileLen = c.Pkg.Files[idx].Length;
        return true;
    }

    private static bool DecodeDataUri(Ctx c, string uri, string path, out byte[]? data)
    {
        data = null;
        int comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !uri.AsSpan(0, comma).EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            c.Error(path, ModError.GltfInvalid, "data: URI must be base64");
            return false;
        }

        long estimate = (uri.Length - comma - 1) / 4L * 3;
        c.Embedded += estimate;
        if (c.Embedded > MaxEmbeddedBytes)
        {
            c.Error(path, ModError.TooLarge, $"data: URIs exceed {N(MaxEmbeddedBytes)} bytes in total");
            return false;
        }

        try
        {
            data = Convert.FromBase64String(uri[(comma + 1)..]);
            return true;
        }
        catch (FormatException)
        {
            c.Error(path, ModError.GltfInvalid, "invalid base64");
            return false;
        }
    }

    private static int FindFile(IReadOnlyList<PackageFile> files, string rel)
    {
        int lo = 0;
        int hi = files.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            int cmp = string.CompareOrdinal(files[mid].Path, rel);
            if (cmp == 0)
            {
                return mid;
            }

            if (cmp < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    private static ReadOnlyMemory<byte> BufferBytes(Ctx c, Buf b)
    {
        if (b.Data.Length == 0 && b.FileRel != null)
        {
            b.Data = File.ReadAllBytes(Path.Combine(c.Pkg.Directory, b.FileRel));
        }

        return b.Data;
    }

    // ------------------------------------------------------------------ JSON-помощники

    private static bool TryArray(Ctx c, JsonElement root, string key, out JsonElement[] items)
    {
        items = [];
        if (!root.TryGetProperty(key, out JsonElement e))
        {
            return false;
        }

        if (e.ValueKind != JsonValueKind.Array)
        {
            c.Error("$." + key, ModError.GltfInvalid, "expected array");
            return false;
        }

        items = new JsonElement[e.GetArrayLength()];
        int i = 0;
        foreach (JsonElement x in e.EnumerateArray())
        {
            items[i++] = x;
        }

        return true;
    }

    private static bool GetLong(JsonElement obj, string key, out long value)
    {
        value = 0;
        return obj.TryGetProperty(key, out JsonElement e) && GetLongValue(e, out value);
    }

    private static bool GetLongValue(JsonElement e, out long value)
    {
        value = 0;
        return e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out value);
    }

    private static string Idx(string path, int i) => path + "[" + N(i) + "]";

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ внутренние типы

    private sealed class Ctx(LoadedPackage pkg, string file, List<ModIssue> errors, List<ModIssue> warnings)
    {
        private int _count;

        public LoadedPackage Pkg { get; } = pkg;

        public string File { get; } = file;

        public long Embedded { get; set; }

        public int ErrorCount { get; private set; }

        public void Error(string jsonPath, ModError code, string detail)
        {
            ErrorCount++;
            if (_count++ < MaxIssuesPerFile)
            {
                errors.Add(new ModIssue(Pkg.Id, File, jsonPath, code, detail));
            }
        }

        public void Warn(string jsonPath, ModError code, string detail)
        {
            if (_count++ < MaxIssuesPerFile)
            {
                warnings.Add(new ModIssue(Pkg.Id, File, jsonPath, code, detail));
            }
        }
    }

    private sealed class Buf
    {
        public long Declared { get; set; }

        public long Length { get; set; }

        public ReadOnlyMemory<byte> Data { get; set; }

        public string? FileRel { get; set; }
    }

    private readonly record struct ViewInfo(int Buffer, long Offset, long Length, long Stride);

    private readonly record struct AccInfo(bool Valid, int ComponentType, int Components, long Count);

    private sealed record NodeInfo(string? Name, int Mesh, int[] Children);
}
