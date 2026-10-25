using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sim.Core.Api;

namespace Sim.Core.Save;

// Блок MANI: манифест ассетов (save-format.md §5). Хост задаёт его после Create (SetAssetManifest); ядро хранит, пишет в сейв и отдаёт после Load.
internal static partial class SaveSchema
{
    private static BlockDef Manifest() => new("MANI", "манифест ассетов: что нужно состоянию мира (типы ТС, резерв: здания, пакеты)", new[]
    {
        ManiField(1, "Kind", nameof(AssetRef.Kind)),
        ManiField(2, "SavedIndex", nameof(AssetRef.SavedIndex)),
        ManiField(3, "IdOffset", nameof(AssetRef.IdOffset)),
        ManiField(4, "IdLength", nameof(AssetRef.IdLength)),
        ManiField(5, "Version", nameof(AssetRef.Version)),
        ManiField(6, "ContentHash", nameof(AssetRef.ContentHash)),
        Col.Custom(
            7,
            "IdBlob",
            ColumnType.U8,
            1,
            "IdBlobLength",
            "0",
            w => w.AssetIds.Length,
            (w, first, n, dst) => new ReadOnlySpan<byte>(w.AssetIds, (int)first, n).CopyTo(dst),
            (c, n, src) =>
            {
                c.ManiIds = src.ToArray();
                return LoadStatus.Ok;
            },
            group: 2),
    })
    {
        After = FinishManifest,
    };

    /// <summary>Столбец-поле записи манифеста; длина (группа 1) задаётся файлом, мир ещё не знает числа записей.</summary>
    private static ColumnDef ManiField(uint id, string name, string field)
    {
        FieldInfo fi = typeof(AssetRef).GetField(field) ?? throw new ArgumentException("no field " + field, nameof(field));
        int offset = (int)Marshal.OffsetOf<AssetRef>(field);
        ColumnType type = Col.TypeOf(fi.FieldType);
        int size = SaveFormat.FixedElemSize(type);
        int stride = Unsafe.SizeOf<AssetRef>();
        return Col.Custom(
            id,
            name,
            type,
            size,
            "ManifestCount",
            "0",
            w => w.AssetManifest.Length,
            (w, first, n, dst) => Col.Gather(MemoryMarshal.AsBytes(new ReadOnlySpan<AssetRef>(w.AssetManifest, (int)first, n)), stride, offset, size, n, dst),
            (c, n, src) =>
            {
                if (c.ManiRefs == null || c.ManiRefs.Length != n)
                {
                    c.ManiRefs = new AssetRef[n];
                }

                Col.Scatter(src, MemoryMarshal.AsBytes(c.ManiRefs.AsSpan()), stride, offset, size, (int)n);
                return LoadStatus.Ok;
            },
            group: 1);
    }

    /// <summary>Манифест в мир, проверка записей и сверка с каталогом хоста из <see cref="LoadOptions"/>.</summary>
    private static LoadStatus FinishManifest(LoadContext c)
    {
        AssetRef[] refs = c.ManiRefs ?? Array.Empty<AssetRef>();
        byte[] ids = c.ManiIds ?? Array.Empty<byte>();
        if (!AssetManifestRules.Validate(refs, ids, c.Types.Length, out int bad))
        {
            c.FailRow = bad;
            return LoadStatus.InvalidReference;
        }

        if (c.Options.AvailableAssets != null && !AssetManifestRules.Validate(c.Options.AvailableAssets, c.Options.AvailableIds ?? Array.Empty<byte>(), int.MaxValue, out _))
        {
            return LoadStatus.InvalidValue;   // ошибка хоста: каталог противоречив
        }

        c.W.AssetManifest = refs;
        c.W.AssetIds = ids;
        c.W.AssetStates = AssetManifestRules.Classify(refs, ids, c.Options.AvailableAssets, c.Options.AvailableIds, out c.AssetsMissing, out c.AssetsChanged);
        return LoadStatus.Ok;
    }
}
