using System;
using System.Collections.Generic;
using System.Text;
using Sim.Core.Api;
using CoreAssetKind = Sim.Core.Api.AssetKind;

namespace Sim.Scenarios.Mods;

/// <summary>Результат <see cref="SaveAssets.Load"/>: каталог установленных пакетов сейва и события хоста.</summary>
public sealed class SaveAssetsResult
{
    internal SaveAssetsResult(ContentCatalog? catalog, PackageSet? packages, IReadOnlyList<AssetNotice> notices)
    {
        Catalog = catalog;
        Packages = packages;
        Notices = notices;
    }

    /// <summary>Каталог из установленных пакетов сейва; <c>null</c>, если в манифесте нет ни пакетов, ни ассетов наследия.</summary>
    public ContentCatalog? Catalog { get; }

    public PackageSet? Packages { get; }

    /// <summary>Что не совпало с установленным: отсутствующие и изменённые ассеты, пакеты другой MAJOR (modding.md §6.1).</summary>
    public IReadOnlyList<AssetNotice> Notices { get; }

    public int Missing => Count(AssetNoticeState.Missing);

    public int Changed => Count(AssetNoticeState.Changed);

    public int Updated => Count(AssetNoticeState.Updated);

    private int Count(AssetNoticeState s)
    {
        int n = 0;
        foreach (AssetNotice x in Notices)
        {
            n += x.State == s ? 1 : 0;
        }

        return n;
    }
}

/// <summary>
/// Сверка манифеста сейва с установленными пакетами (modding.md §6.1): хост загружает пакеты, перечисленные в <c>MANI</c>
/// (вид 4), строит каталог «что есть сейчас» для <see cref="LoadOptions.AvailableAssets"/> и выдаёт <see cref="AssetNotice"/>.
/// Отсутствующий пакет или ассет - не ошибка: симуляция продолжается побитово, клиент рисует заглушку.
/// </summary>
public static class SaveAssets
{
    /// <summary>Префикс id наследия (<c>vehicle:&lt;имя&gt;</c>, сейвы до M3), который читается как <c>base:vehicle/&lt;имя&gt;</c> (modding.md §2).</summary>
    public const string LegacyVehiclePrefix = "vehicle:";

    /// <summary>
    /// Загрузить сейв и сверить ассеты. Первый проход читает манифест, второй передаёт ядру каталог (<c>AvailableAssets</c>), чтобы
    /// <c>LoadReport.AssetsMissing/AssetsChanged</c> и <c>Simulation.AssetStates</c> были заполнены. Манифест пуст - один проход, без событий.
    /// </summary>
    public static LoadStatus Load(
        byte[] bytes,
        in LoadOptions options,
        in SimRuntimeOptions runtime,
        IReadOnlyList<PackageSearchDir> searchDirs,
        out Simulation? sim,
        out LoadReport report,
        out SaveAssetsResult result)
    {
        result = new SaveAssetsResult(null, null, []);
        LoadStatus st = Simulation.Load(bytes, in options, in runtime, out sim, out report);
        if (st != LoadStatus.Ok || sim == null || sim.AssetManifest.Length == 0)
        {
            return st;
        }

        AssetRef[] saved = sim.AssetManifest.ToArray();
        byte[] savedIds = sim.AssetIds.ToArray();
        (ContentCatalog? cat, PackageSet? set) = LoadInstalled(saved, savedIds, searchDirs);
        BuildAvailable(saved, savedIds, cat, set, out AssetRef[] avail, out byte[] availIds);
        sim.Dispose();
        LoadOptions second = options;
        second.AvailableAssets = avail;
        second.AvailableIds = availIds;
        st = Simulation.Load(bytes, in second, in runtime, out sim, out report);
        if (st != LoadStatus.Ok || sim == null)
        {
            return st;
        }

        result = new SaveAssetsResult(cat, set, Notices(sim, saved, savedIds, avail, availIds));
        return st;
    }

    /// <summary>
    /// Пакеты из записей вида 4 (манифест без них, но с ассетами наследия <c>vehicle:...</c> - <c>base</c>). Каждый пакет сначала
    /// проверяется отдельно: недоступный (нет каталога, нет зависимости, ошибка пакета) пропускается и позже станет <c>Missing</c>.
    /// Оставшиеся грузятся одним набором; конфликт между ними - <see cref="ScenarioException"/>.
    /// </summary>
    private static (ContentCatalog?, PackageSet?) LoadInstalled(AssetRef[] saved, byte[] ids, IReadOnlyList<PackageSearchDir> dirs)
    {
        var wanted = new List<string>();
        bool legacy = false;
        foreach (AssetRef r in saved)
        {
            string id = Text(ids, r);
            if (r.Kind == (byte)CoreAssetKind.Package)
            {
                wanted.Add(id);
            }
            else if (r.Kind == (byte)CoreAssetKind.VehicleType && IsLegacy(id))
            {
                legacy = true;
            }
        }

        if (wanted.Count == 0 && legacy)
        {
            wanted.Add("base");
        }

        var ok = new List<string>();
        foreach (string id in wanted)
        {
            if (dirs.Count > 0 && PackageLoader.Load(dirs, [id]).Ok)
            {
                ok.Add(id);
            }
        }

        if (ok.Count == 0)
        {
            return (null, null);
        }

        PackageSet set = PackageLoader.Load(dirs, ok);
        set.ThrowIfFailed();
        var issues = new List<ModIssue>();
        ContentCatalog cat = ContentCatalog.Build(set, issues);
        if (issues.Count > 0)
        {
            throw new ScenarioException(issues[0].ToString(), issues[0].Code);
        }

        return (cat, set);
    }

    /// <summary>
    /// Каталог для <c>LoadOptions.AvailableAssets</c>: манифест установленных пакетов (<see cref="AssetManifestBuilder.ForCatalog"/>) плюс
    /// алиасы наследия - для каждой записи сейва <c>vehicle:&lt;имя&gt;</c>, у которой есть <c>base:vehicle/&lt;имя&gt;</c>, запись с id сейва и хэшем каталога.
    /// </summary>
    public static void BuildAvailable(
        AssetRef[] saved, byte[] savedIds, ContentCatalog? cat, PackageSet? set, out AssetRef[] avail, out byte[] availIds)
    {
        if (cat == null || set == null)
        {
            avail = [];
            availIds = [];
            return;
        }

        AssetManifestBuilder.ForCatalog(cat, set, out AssetRef[] refs, out byte[] ids);
        var list = new List<AssetRef>(refs);
        var blob = new List<byte>(ids);
        foreach (AssetRef r in saved)
        {
            string id = Text(savedIds, r);
            if (r.Kind != (byte)CoreAssetKind.VehicleType || !IsLegacy(id))
            {
                continue;
            }

            int idx = cat.FindVehicle(new AssetId("base", AssetKind.Vehicle, id[LegacyVehiclePrefix.Length..]));
            if (idx < 0)
            {
                continue;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(id);
            list.Add(new AssetRef
            {
                Kind = (byte)CoreAssetKind.VehicleType,
                SavedIndex = idx,
                IdOffset = (uint)blob.Count,
                IdLength = (uint)bytes.Length,
                Version = 1,
                ContentHash = AssetManifestBuilder.ParamsHash(cat.Vehicles[idx].Desc),
            });
            blob.AddRange(bytes);
        }

        avail = list.ToArray();
        availIds = blob.ToArray();
    }

    private static bool IsLegacy(string id) =>
        id.StartsWith(LegacyVehiclePrefix, StringComparison.Ordinal) && id.IndexOf('/', StringComparison.Ordinal) < 0;

    private static string Text(byte[] ids, in AssetRef r) => Encoding.UTF8.GetString(ids, (int)r.IdOffset, (int)r.IdLength);

    private static List<AssetNotice> Notices(Simulation sim, AssetRef[] saved, byte[] savedIds, AssetRef[] avail, byte[] availIds)
    {
        var notices = new List<AssetNotice>();
        ReadOnlySpan<byte> states = sim.AssetStates;
        int[]? perType = null;
        for (int i = 0; i < saved.Length && i < states.Length; i++)
        {
            AssetRef r = saved[i];
            string id = Text(savedIds, r);
            var state = (AssetState)states[i];
            if (state != AssetState.Ok)
            {
                int count = 0;
                if (r.Kind == (byte)CoreAssetKind.VehicleType)
                {
                    perType ??= CountByType(sim);
                    count = (uint)r.SavedIndex < (uint)perType.Length ? perType[r.SavedIndex] : 0;
                }

                notices.Add(new AssetNotice((CoreAssetKind)r.Kind, id, state == AssetState.Missing ? AssetNoticeState.Missing : AssetNoticeState.Changed, count));
            }

            if (r.Kind == (byte)CoreAssetKind.Package && state != AssetState.Missing)
            {
                uint now = FindVersion(avail, availIds, r, id);
                if (now != 0 && ContentFingerprint.UnpackVersion(now).Major != ContentFingerprint.UnpackVersion(r.Version).Major)
                {
                    notices.Add(new AssetNotice(CoreAssetKind.Package, id, AssetNoticeState.VersionChanged, 0));
                }
            }
        }

        return notices;
    }

    private static uint FindVersion(AssetRef[] avail, byte[] availIds, in AssetRef saved, string id)
    {
        foreach (AssetRef a in avail)
        {
            if (a.Kind == saved.Kind && Text(availIds, a) == id)
            {
                return a.Version;
            }
        }

        return 0;
    }

    private static int[] CountByType(Simulation sim)
    {
        int[] counts = new int[sim.VehicleTypes.Length];
        foreach (ref readonly VehicleRenderData v in sim.Vehicles)
        {
            if ((v.Flags & (byte)VehicleRenderFlags.Active) != 0 && v.Type < counts.Length)
            {
                counts[v.Type]++;
            }
        }

        return counts;
    }
}
