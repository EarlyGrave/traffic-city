using System;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using Sim.Core.Api;

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
                ContentHash = ParamsHash(in types[i]),
            };
            blob.Write(id, 0, id.Length);
        }

        ids = blob.ToArray();
    }

    /// <summary>xxh64 байтового образа параметров типа (все поля 4 байта, без паддинга).</summary>
    public static ulong ParamsHash(in VehicleTypeDesc t)
    {
        VehicleTypeDesc copy = t;
        return XxHash64.HashToUInt64(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1)));
    }
}
