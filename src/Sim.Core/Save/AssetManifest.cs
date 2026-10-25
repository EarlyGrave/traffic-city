using System;
using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>Правила манифеста ассетов (save-format.md §5): проверка записей и сверка с каталогом хоста. Строки не разбираются - id сравниваются побайтно.</summary>
internal static class AssetManifestRules
{
    /// <summary>Вид 1..4, срез id внутри буфера, для типа ТС - индекс в таблице типов. <paramref name="badRow"/> - первая плохая запись.</summary>
    public static bool Validate(ReadOnlySpan<AssetRef> refs, ReadOnlySpan<byte> ids, int typeCount, out int badRow)
    {
        badRow = -1;
        for (int i = 0; i < refs.Length; i++)
        {
            AssetRef a = refs[i];
            bool ok = a.Kind is >= (byte)AssetKind.VehicleType and <= (byte)AssetKind.Package
                && (long)a.IdOffset + a.IdLength <= ids.Length
                && (a.Kind != (byte)AssetKind.VehicleType || (uint)a.SavedIndex < (uint)typeCount)
                && a.R0 == 0 && a.R1 == 0 && a.R2 == 0 && a.R3 == 0;
            if (!ok)
            {
                badRow = i;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Сверка манифеста с каталогом: запись без пары (вид + id) - <see cref="AssetState.Missing"/>, пара с другим <c>ContentHash</c> - <see cref="AssetState.Changed"/>.
    /// <paramref name="available"/> == null - каталог не задан, все записи <see cref="AssetState.Ok"/>.
    /// </summary>
    public static byte[] Classify(ReadOnlySpan<AssetRef> refs, ReadOnlySpan<byte> ids, AssetRef[]? available, byte[]? availableIds, out int missing, out int changed)
    {
        missing = 0;
        changed = 0;
        byte[] states = new byte[refs.Length];
        if (available == null)
        {
            return states;
        }

        ReadOnlySpan<byte> aIds = availableIds;
        for (int i = 0; i < refs.Length; i++)
        {
            AssetRef a = refs[i];
            ReadOnlySpan<byte> id = ids.Slice((int)a.IdOffset, (int)a.IdLength);
            AssetState st = AssetState.Missing;
            for (int j = 0; j < available.Length; j++)
            {
                AssetRef b = available[j];
                if (b.Kind != a.Kind || b.IdLength != a.IdLength || (long)b.IdOffset + b.IdLength > aIds.Length
                    || !aIds.Slice((int)b.IdOffset, (int)b.IdLength).SequenceEqual(id))
                {
                    continue;
                }

                st = b.ContentHash == a.ContentHash ? AssetState.Ok : AssetState.Changed;
                break;
            }

            states[i] = (byte)st;
            missing += st == AssetState.Missing ? 1 : 0;
            changed += st == AssetState.Changed ? 1 : 0;
        }

        return states;
    }
}
