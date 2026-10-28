using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 6 -> 7 (save-format.md §4, §12; ADR-0029, bus-bays.md §3): карман автобусных остановок. Чистая функция над <see cref="SaveTables"/>.
/// Добавляет «кармана нет»: <c>PARM</c> 186 <c>TransitBayModel = 0</c> (<c>curb</c>: остановки <c>Bay</c> ведут себя как раньше - автобус стоит на полосе), 187 <c>TransitBayYieldTicks = 60</c>;
/// <c>BDYN</c> 10-12 (<c>BayUsed</c>, <c>BayFlags</c> = 0, <c>BayExitFrom = -1</c> по остановкам) и <c>CNTR</c> 150-153 (счётчики кармана = 0). Новых блоков не создаёт.
/// Поведение и хэш не меняются (модель <c>curb</c>, раздел хэша «карманы» не пишется), поэтому мир из файла w1-w6 продолжается тем же хэшем.
/// </summary>
internal static class MigrationV7
{
    private static readonly int[] _model = { (int)BayModel.Curb };
    private static readonly int[] _yield = { 60 };

    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        int netw = t.FindBlock(SaveFormat.FourCc("NETW"u8));
        int bdyn = t.FindBlock(SaveFormat.FourCc("BDYN"u8));
        int cntr = t.FindBlock(SaveFormat.FourCc("CNTR"u8));
        if (parm < 0 || netw < 0 || bdyn < 0 || cntr < 0 ||
            !t.TryGetColumn(netw, 10, out System.ReadOnlySpan<int> stopCount) || stopCount.Length != 1 || stopCount[0] < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        int stops = stopCount[0];
        var exitFrom = new long[stops];
        System.Array.Fill(exitFrom, -1L);
        bool ok = true;
        ok &= t.SetColumn(parm, 186, _model);
        ok &= t.SetColumn(parm, 187, _yield);
        ok &= t.SetColumn(bdyn, 10, new byte[stops]);
        ok &= t.SetColumn(bdyn, 11, new byte[stops]);
        ok &= t.SetColumn(bdyn, 12, exitFrom);
        for (uint id = 150; id <= 153; id++)
        {
            ok &= t.SetColumn(cntr, id, new long[1]);
        }

        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }
}
