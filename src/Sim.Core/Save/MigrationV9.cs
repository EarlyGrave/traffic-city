using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 8 -> 9 (save-format.md §4, §12; ADR-0025, city-economy.md §12): казна города. Чистая функция над <see cref="SaveTables"/>.
/// Мир из файла v8 - без бюджета: <c>PARM</c> 191-218 (секция <c>economy</c> казны) - дефолты §13 с <c>CityBudget = 0</c> у мира с жителями (есть блок <c>LEDG</c>) и нули у остальных
/// (как пишет <c>Create</c>); <c>LEDG</c> 3-5 (потоки) расширяются с 13 до 17 видов нулями; <c>BLDG</c> 21 <c>PayrollEwma</c> = 0 по слотам; <c>CNTR</c> 154-156 = 0.
/// Блок <c>CBGT</c> не создаётся. Поведение и хэш не меняются (раздел хэша казны пишется только при бюджете), поэтому мир из файла w1-w8 продолжается тем же хэшем.
/// </summary>
internal static class MigrationV9
{
    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        int cntr = t.FindBlock(SaveFormat.FourCc("CNTR"u8));
        if (parm < 0 || cntr < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        int ledg = t.FindBlock(SaveFormat.FourCc("LEDG"u8));
        bool city = ledg >= 0;
        bool ok = true;
        CityEconomyDesc e = city ? CityDesc.Default().Economy : default;
        int[] values =
        {
            0, e.TaxRBp, e.TaxCProfitBp, e.TaxIProfitBp, e.LandTaxBpDay, e.BusFare, e.InitialCityBalancePerResident, e.InterestBpDay, e.CreditDays,
            e.MaintRoadStreetPerLaneKmDay, e.MaintRoadArterialPerLaneKmDay, e.MaintBusDay, e.MaintStopDay, e.LvRef, e.LvSlopePerMin, e.LvMin, e.LvMax, e.LvShift, e.VotPct,
            e.ResBufferDays, e.FirmBufferDays, e.PayoutShift, e.EvictDebtDays, e.BudgetEwmaShift, e.UndoRefundS, e.MaxTaxBp, e.MaxLandBp, e.MaxFare,
        };
        ok &= t.SetColumn(parm, 191, new byte[1]);   // CityBudget = 0
        for (int k = 1; k < values.Length; k++)
        {
            ok &= t.SetColumn(parm, (uint)(191 + k), new[] { values[k] });
        }

        for (uint id = 154; id <= 156; id++)
        {
            ok &= t.SetColumn(cntr, id, new long[1]);
        }

        if (!city)
        {
            return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
        }

        // LEDG 3-5 (FlowDay, FlowYesterday, FlowTotal): 13 -> 17 видов, новые - нули.
        for (uint id = 3; id <= 5; id++)
        {
            if (!t.TryGetColumn(ledg, id, out System.ReadOnlySpan<long> old) || old.Length != Ledger13)
            {
                return LoadStatus.MigrationFailed;
            }

            var grown = new long[Ledger17];
            old.CopyTo(grown);
            ok &= t.SetColumn(ledg, id, grown);
        }

        // BLDG 21 PayrollEwma: нули по слотам (HighWater = BLDG 1).
        int bldg = t.FindBlock(SaveFormat.FourCc("BLDG"u8));
        if (bldg < 0 || !t.TryGetColumn(bldg, 1, out System.ReadOnlySpan<int> highWater) || highWater.Length != 1 || highWater[0] < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        ok &= t.SetColumn(bldg, 21, new long[highWater[0]]);
        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }

    private const int Ledger13 = 13;    // виды потоков до w9 (FlowKind.Endowment .. Subsidy)
    private const int Ledger17 = 17;    // с w9 (+ Interest, Dividend, Leakage, Refund)
}
