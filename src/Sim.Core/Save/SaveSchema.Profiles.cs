using System;
using System.Runtime.InteropServices;
using System.Text;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Save;

// world_version 8 (ADR-0030, road-profiles.md §3.4): режим профилей дорог. PARM 188-190, блок RPRF (PreWorld, есть ⇔ PARM.RoadProfileMode = 1), NSEG.Profile, NLAN.Width, LOTS 11-15.
internal static partial class SaveSchema
{
    // ---- PARM, w8: режим профилей и начало мировой сетки 8 м; ids продолжают 187 ----
    private static ColumnDef[] RoadProfileParams() => new[]
    {
        Col.Scalar<int>(188, "RoadProfileMode", w => w.Net.Profiles != null ? 1 : 0, (c, v) => c.RoadProfileMode = v, "0"),
        Col.Scalar<float>(189, "WorldGridOriginX", w => w.WorldGridOriginX, (c, v) => c.Desc.WorldGridOriginX = v, "0"),
        Col.Scalar<float>(190, "WorldGridOriginZ", w => w.WorldGridOriginZ, (c, v) => c.Desc.WorldGridOriginZ = v, "0"),
    };

    private static readonly UTF8Encoding _strictUtf8 = new(false, true);

    private static RoadProfileStore Prof(World w) => w.Net.Profiles!;

    /// <summary>Идентификаторы профилей подряд (UTF-8, без разделителей; длины - столбец IdLength).</summary>
    private static byte[] IdBlob(World w)
    {
        RoadProfileStore p = Prof(w);
        var all = new System.Collections.Generic.List<byte>();
        for (int i = 0; i < p.Count; i++)
        {
            all.AddRange(Encoding.UTF8.GetBytes(p.Ids[i]));
        }

        return all.ToArray();
    }

    private static ColumnDef ProfCol<T>(uint id, string name, int group, string length, Func<RoadProfileStore, int> count, Func<RoadProfileStore, int, T> get, Action<LoadContext, T[]> assign)
        where T : unmanaged
    {
        ColumnType type = Col.TypeOf(typeof(T));
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<T>();
        return Col.Custom(
            id,
            name,
            type,
            size,
            length,
            "0",
            w => count(Prof(w)),
            (w, first, n, dst) =>
            {
                RoadProfileStore p = Prof(w);
                for (int k = 0; k < n; k++)
                {
                    T v = get(p, (int)first + k);
                    MemoryMarshal.Write(dst.Slice(k * size, size), in v);
                }
            },
            (c, n, src) =>
            {
                var a = new T[n];
                src.CopyTo(MemoryMarshal.AsBytes(a.AsSpan()));
                assign(c, a);
                return LoadStatus.Ok;
            },
            group);
    }

    // RPRF: каталог целиком. Группа 1 - профили, 2 - элементы, 3 - байты идентификаторов.
    private static BlockDef RoadProfilesBlock() => new("RPRF", "каталог профилей дорог: модуль, профили, элементы сечения, идентификаторы (road-profiles.md §3.4)", new[]
    {
        Col.Scalar<int>(1, "ModuleMm", w => Prof(w).ModuleMm, (c, v) => c.RprfModuleMm = v),
        ProfCol<byte>(2, "Kind", 1, "ProfileCount", p => p.Count, (p, i) => (byte)p.Kind[i], (c, a) => c.RprfKind = a),
        ProfCol<int>(3, "WidthMm", 1, "ProfileCount", p => p.Count, (p, i) => p.WidthMm[i], (c, a) => c.RprfWidthMm = a),
        ProfCol<byte>(4, "ElemCount", 1, "ProfileCount", p => p.Count, (p, i) => (byte)p.ElemCount[i], (c, a) => c.RprfElemCount = a),
        ProfCol<ushort>(5, "IdLength", 1, "ProfileCount", p => p.Count, (p, i) => (ushort)Encoding.UTF8.GetByteCount(p.Ids[i]), (c, a) => c.RprfIdLength = a),
        ProfCol<byte>(6, "ElemKind", 2, "sum(ElemCount)", p => p.ElemKind.Length, (p, i) => (byte)p.ElemKind[i], (c, a) => c.RprfElemKind = a),
        ProfCol<byte>(7, "ElemDir", 2, "sum(ElemCount)", p => p.ElemKind.Length, (p, i) => p.ElemDir[i], (c, a) => c.RprfElemDir = a),
        ProfCol<int>(8, "ElemWidthMm", 2, "sum(ElemCount)", p => p.ElemKind.Length, (p, i) => p.ElemWidthMm[i], (c, a) => c.RprfElemWidthMm = a),
        Col.Custom(
            9,
            "IdBlob",
            ColumnType.U8,
            1,
            "sum(IdLength)",
            "0",
            w => IdBlob(w).Length,
            (w, first, n, dst) => new ReadOnlySpan<byte>(IdBlob(w), (int)first, n).CopyTo(dst),
            (c, n, src) =>
            {
                c.RprfIdBlob = src.ToArray();
                return LoadStatus.Ok;
            },
            group: 3),
    })
    {
        PreWorld = true,
        PresentText = "PARM.RoadProfileMode = 1",
        Present = static w => w.Net.Profiles != null,
        After = FinishRoadProfiles,
    };

    /// <summary>Собирает <see cref="RoadProfilesDesc"/> из столбцов, проверяет согласованность групп и каталог тем же валидатором, что <c>Create</c>.</summary>
    private static LoadStatus FinishRoadProfiles(LoadContext c)
    {
        int count = c.RprfKind.Length;
        if (c.RprfWidthMm.Length != count || c.RprfElemCount.Length != count || c.RprfIdLength.Length != count ||
            c.RprfElemDir.Length != c.RprfElemKind.Length || c.RprfElemWidthMm.Length != c.RprfElemKind.Length)
        {
            return LoadStatus.InvalidValue;
        }

        long elems = 0;
        long idBytes = 0;
        for (int i = 0; i < count; i++)
        {
            elems += c.RprfElemCount[i];
            idBytes += c.RprfIdLength[i];
        }

        if (elems != c.RprfElemKind.Length || idBytes != c.RprfIdBlob.Length)
        {
            return LoadStatus.InvalidValue;
        }

        var profiles = new RoadProfileDesc[count];
        int e = 0;
        int b = 0;
        for (int i = 0; i < count; i++)
        {
            if (c.RprfKind[i] > (byte)RoadKind.Arterial)
            {
                return LoadStatus.InvalidValue;
            }

            string id;
            try
            {
                id = _strictUtf8.GetString(c.RprfIdBlob, b, c.RprfIdLength[i]);
            }
            catch (ArgumentException)
            {
                return LoadStatus.InvalidValue;
            }

            b += c.RprfIdLength[i];
            var el = new RoadElemDesc[c.RprfElemCount[i]];
            for (int k = 0; k < el.Length; k++, e++)
            {
                el[k] = new RoadElemDesc { Kind = (RoadElemKind)c.RprfElemKind[e], Dir = (RoadLaneDir)c.RprfElemDir[e], WidthMm = c.RprfElemWidthMm[e] };
            }

            profiles[i] = new RoadProfileDesc { Id = id, Kind = (RoadKind)c.RprfKind[i], WidthMm = c.RprfWidthMm[i], Elements = el };
        }

        var desc = new RoadProfilesDesc { ModuleMm = c.RprfModuleMm, Profiles = profiles };
        if (RoadProfileValidator.Validate(in desc, out int bad) != RoadProfileError.None)
        {
            c.FailRow = bad;
            return LoadStatus.InvalidValue;
        }

        c.Desc.RoadProfiles = desc;
        return LoadStatus.Ok;
    }

    // ---- NSEG 16 / NLAN 19 (w8): профиль сегмента и ширина полосы ----
    private static ColumnDef SegmentProfileColumn() =>
        Col.Field<SegmentData>(16, "Profile", w => w.Net.Segments, _segN, "SegmentCount", nameof(SegmentData.Profile), def: "-1");

    private static ColumnDef LaneWidthColumn() =>
        Col.Field<LaneData>(19, "Width", w => w.Net.Lanes, _laneN, "LaneCount", nameof(LaneData.Width));

    // ---- LOTS 11-15 (w8, road-profiles.md §4.4): участки на мировой сетке; объявлены сразу, чтобы RP5 не поднимал версию ----
    private static ColumnDef[] LotGridColumns() => new[]
    {
        Col.Array<int>(11, "CellX", w => w.City!.Lots.CellX, _lotN, "Count"),
        Col.Array<int>(12, "CellZ", w => w.City!.Lots.CellZ, _lotN, "Count"),
        Col.Array<byte>(13, "CellsF", w => w.City!.Lots.CellsF, _lotN, "Count"),
        Col.Array<byte>(14, "CellsD", w => w.City!.Lots.CellsD, _lotN, "Count"),
        Col.Array<byte>(15, "Orient", w => w.City!.Lots.Orient, _lotN, "Count", "255"),
    };
}
