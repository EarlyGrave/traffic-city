using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 7 -> 8 (save-format.md §4, §12; ADR-0030, road-profiles.md §3.4): профили дорог. Чистая функция над <see cref="SaveTables"/>.
/// Мир из файла v7 - режим <c>legacy</c>: <c>PARM</c> 188 <c>RoadProfileMode = 0</c> (блока <c>RPRF</c> нет), 189-190 <c>WorldGridOrigin = 0</c>;
/// <c>NSEG</c> 16 <c>Profile = -1</c> у всех слотов; <c>NLAN</c> 19 <c>Width = NetLaneWidth</c> у живых обычных полос (connector'ы и мёртвые слоты - 0, как при построении);
/// <c>LOTS</c> (если есть) 11-15: клеток нет, <c>Orient = 255</c> (прежняя геометрия вдоль дороги). Поведение и хэш не меняются
/// (профильный раздел хэша в режиме legacy не пишется, хэш участков эти поля пока не читает), поэтому мир из файла w1-w7 продолжается тем же хэшем.
/// </summary>
internal static class MigrationV8
{
    private static readonly int[] _mode = { 0 };
    private static readonly float[] _origin = { 0f };

    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        int netw = t.FindBlock(SaveFormat.FourCc("NETW"u8));
        int nseg = t.FindBlock(SaveFormat.FourCc("NSEG"u8));
        int nlan = t.FindBlock(SaveFormat.FourCc("NLAN"u8));
        if (parm < 0 || netw < 0 || nseg < 0 || nlan < 0 ||
            !t.TryGetColumn(parm, 74, out System.ReadOnlySpan<float> laneWidth) || laneWidth.Length != 1 ||
            !t.TryGetColumn(netw, 2, out System.ReadOnlySpan<int> segCount) || segCount.Length != 1 || segCount[0] < 0 ||
            !t.TryGetColumn(nlan, 1, out System.ReadOnlySpan<byte> kind) ||
            !t.TryGetColumn(nlan, 18, out System.ReadOnlySpan<int> generation) || generation.Length != kind.Length)
        {
            return LoadStatus.MigrationFailed;
        }

        var profile = new int[segCount[0]];
        System.Array.Fill(profile, -1);
        var width = new float[kind.Length];
        for (int i = 0; i < width.Length; i++)
        {
            if (NetworkAlive.IsAlive(generation[i]) && kind[i] == (byte)LaneKind.Normal)
            {
                width[i] = laneWidth[0];
            }
        }

        bool ok = true;
        ok &= t.SetColumn(parm, 188, _mode);
        ok &= t.SetColumn(parm, 189, _origin);
        ok &= t.SetColumn(parm, 190, _origin);
        ok &= t.SetColumn(nseg, 16, profile);
        ok &= t.SetColumn(nlan, 19, width);

        int lots = t.FindBlock(SaveFormat.FourCc("LOTS"u8));
        if (lots >= 0)
        {
            if (!t.TryGetColumn(lots, 1, out System.ReadOnlySpan<int> lotCount) || lotCount.Length != 1 || lotCount[0] < 0)
            {
                return LoadStatus.MigrationFailed;
            }

            int n = lotCount[0];
            var orient = new byte[n];
            System.Array.Fill(orient, (byte)255);
            ok &= t.SetColumn(lots, 11, new int[n]);
            ok &= t.SetColumn(lots, 12, new int[n]);
            ok &= t.SetColumn(lots, 13, new byte[n]);
            ok &= t.SetColumn(lots, 14, new byte[n]);
            ok &= t.SetColumn(lots, 15, orient);
        }

        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }
}
