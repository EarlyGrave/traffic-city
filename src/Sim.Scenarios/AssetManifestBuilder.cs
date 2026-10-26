using System;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Sim.Core.Api;
using Sim.Scenarios.Mods;
using AssetKind = Sim.Core.Api.AssetKind;

namespace Sim.Scenarios;

/// <summary>
/// Манифест ассетов хоста (save-format.md §5): по имени каждого типа ТС запись <c>vehicle:&lt;имя&gt;</c> с <c>ContentHash</c> = xxh64 его симуляционных параметров.
/// Хост кладёт результат в <c>Simulation.SetAssetManifest</c> после <c>Create</c> и в <c>LoadOptions.AvailableAssets</c> при загрузке (каталог «что есть сейчас»).
/// </summary>
public static class AssetManifestBuilder
{
    public static void ForVehicleTypes(string[] names, VehicleTypeDesc[] types, out AssetRef[] refs, out byte[] ids)
    {
        if (names.Length != types.Length)
        {
            throw new ArgumentException("names and types differ in length", nameof(names));
        }

        refs = new AssetRef[types.Length];
        using var blob = new System.IO.MemoryStream();
        for (int i = 0; i < types.Length; i++)
        {
            byte[] id = Encoding.UTF8.GetBytes("vehicle:" + names[i]);
            refs[i] = new AssetRef
            {
                Kind = (byte)AssetKind.VehicleType,
                SavedIndex = i,
                IdOffset = (uint)blob.Position,
                IdLength = (uint)id.Length,
                Version = 1,
                ContentHash = ParamsHash(types[i]),
            };
            blob.Write(id, 0, id.Length);
        }

        ids = blob.ToArray();
    }

    /// <summary>
    /// Манифест набора пакетов (modding.md §5.3): сначала типы ТС каталога (вид 1, <c>SavedIndex</c> = индекс типа, id <c>пакет:vehicle/имя</c>,
    /// <c>Version</c> = <c>rev</c>), затем пакеты в порядке загрузки (вид 4, <c>SavedIndex</c> = -1, <c>Version</c> = MAJOR&lt;&lt;20|MINOR&lt;&lt;10|PATCH,
    /// <c>ContentHash</c> = <see cref="ContentFingerprint.PackageHash"/>). Дороги (вид 5) добавит M4.
    /// </summary>
    public static void ForCatalog(ContentCatalog cat, PackageSet set, out AssetRef[] refs, out byte[] ids)
    {
        var list = new List<AssetRef>(cat.Vehicles.Count + set.Packages.Count);
        using var blob = new System.IO.MemoryStream();
        for (int i = 0; i < cat.Vehicles.Count; i++)
        {
            VehicleEntry e = cat.Vehicles[i];
            list.Add(Entry(blob, AssetKind.VehicleType, i, e.Id.ToString(), (uint)e.Rev, ParamsHash(e.Desc)));
        }

        foreach (LoadedPackage p in set.Packages)
        {
            uint version = ContentFingerprint.PackVersion(p.Manifest.Version, p.Id);
            list.Add(Entry(blob, AssetKind.Package, -1, p.Id, version, ContentFingerprint.PackageHash(p.Id, cat)));
        }

        refs = list.ToArray();
        ids = blob.ToArray();
    }

    private static AssetRef Entry(System.IO.MemoryStream blob, AssetKind kind, int savedIndex, string id, uint version, ulong hash)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(id);
        var r = new AssetRef
        {
            Kind = (byte)kind,
            SavedIndex = savedIndex,
            IdOffset = (uint)blob.Position,
            IdLength = (uint)bytes.Length,
            Version = version,
            ContentHash = hash,
        };
        blob.Write(bytes, 0, bytes.Length);
        return r;
    }

    /// <summary>xxh64 байтового образа параметров типа (все поля 4 байта, без паддинга).</summary>
    public static ulong ParamsHash(VehicleTypeDesc t)
    {
        VehicleTypeDesc copy = t;
        return XxHash64.HashToUInt64(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)));
    }
}
