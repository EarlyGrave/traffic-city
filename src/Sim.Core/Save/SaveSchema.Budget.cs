using System;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Save;

// world_version 9 (city-economy.md §12, E1): казна города. PARM 191-218 (параметры §13), BLDG 21 (PayrollEwma), CNTR 154-156, LEDG 3-5 длиной 17 (потоки 13-16), блок CBGT (есть <=> economy.city_budget).
internal static partial class SaveSchema
{
    // ---- PARM, w9: секция economy (казна); ids продолжают 190 ----
    private static ColumnDef[] BudgetParams() => new[]
    {
        Col.Flag(191, "CityBudget", w => w.Town.Economy.CityBudget, (c, v) => c.Desc.Town.Economy.CityBudget = v),
        Col.Scalar<int>(192, "CityTaxRBp", w => w.Town.Economy.TaxRBp, (c, v) => c.Desc.Town.Economy.TaxRBp = v, "600"),
        Col.Scalar<int>(193, "CityTaxCProfitBp", w => w.Town.Economy.TaxCProfitBp, (c, v) => c.Desc.Town.Economy.TaxCProfitBp = v, "2000"),
        Col.Scalar<int>(194, "CityTaxIProfitBp", w => w.Town.Economy.TaxIProfitBp, (c, v) => c.Desc.Town.Economy.TaxIProfitBp = v, "2000"),
        Col.Scalar<int>(195, "CityLandTaxBpDay", w => w.Town.Economy.LandTaxBpDay, (c, v) => c.Desc.Town.Economy.LandTaxBpDay = v, "15"),
        Col.Scalar<int>(196, "CityBusFare", w => w.Town.Economy.BusFare, (c, v) => c.Desc.Town.Economy.BusFare = v, "2"),
        Col.Scalar<int>(197, "CityInitialCityBalancePerResident", w => w.Town.Economy.InitialCityBalancePerResident, (c, v) => c.Desc.Town.Economy.InitialCityBalancePerResident = v, "60"),
        Col.Scalar<int>(198, "CityInterestBpDay", w => w.Town.Economy.InterestBpDay, (c, v) => c.Desc.Town.Economy.InterestBpDay = v, "3"),
        Col.Scalar<int>(199, "CityCreditDays", w => w.Town.Economy.CreditDays, (c, v) => c.Desc.Town.Economy.CreditDays = v, "60"),
        Col.Scalar<int>(200, "CityMaintRoadStreetPerLaneKmDay", w => w.Town.Economy.MaintRoadStreetPerLaneKmDay, (c, v) => c.Desc.Town.Economy.MaintRoadStreetPerLaneKmDay = v, "0"),
        Col.Scalar<int>(201, "CityMaintRoadArterialPerLaneKmDay", w => w.Town.Economy.MaintRoadArterialPerLaneKmDay, (c, v) => c.Desc.Town.Economy.MaintRoadArterialPerLaneKmDay = v, "0"),
        Col.Scalar<int>(202, "CityMaintBusDay", w => w.Town.Economy.MaintBusDay, (c, v) => c.Desc.Town.Economy.MaintBusDay = v, "0"),
        Col.Scalar<int>(203, "CityMaintStopDay", w => w.Town.Economy.MaintStopDay, (c, v) => c.Desc.Town.Economy.MaintStopDay = v, "0"),
        Col.Scalar<int>(204, "CityLvRef", w => w.Town.Economy.LvRef, (c, v) => c.Desc.Town.Economy.LvRef = v, "200"),
        Col.Scalar<int>(205, "CityLvSlopePerMin", w => w.Town.Economy.LvSlopePerMin, (c, v) => c.Desc.Town.Economy.LvSlopePerMin = v, "20"),
        Col.Scalar<int>(206, "CityLvMin", w => w.Town.Economy.LvMin, (c, v) => c.Desc.Town.Economy.LvMin = v, "50"),
        Col.Scalar<int>(207, "CityLvMax", w => w.Town.Economy.LvMax, (c, v) => c.Desc.Town.Economy.LvMax = v, "1000"),
        Col.Scalar<int>(208, "CityLvShift", w => w.Town.Economy.LvShift, (c, v) => c.Desc.Town.Economy.LvShift = v, "3"),
        Col.Scalar<int>(209, "CityVotPct", w => w.Town.Economy.VotPct, (c, v) => c.Desc.Town.Economy.VotPct = v, "50"),
        Col.Scalar<int>(210, "CityResBufferDays", w => w.Town.Economy.ResBufferDays, (c, v) => c.Desc.Town.Economy.ResBufferDays = v, "5"),
        Col.Scalar<int>(211, "CityFirmBufferDays", w => w.Town.Economy.FirmBufferDays, (c, v) => c.Desc.Town.Economy.FirmBufferDays = v, "10"),
        Col.Scalar<int>(212, "CityPayoutShift", w => w.Town.Economy.PayoutShift, (c, v) => c.Desc.Town.Economy.PayoutShift = v, "3"),
        Col.Scalar<int>(213, "CityEvictDebtDays", w => w.Town.Economy.EvictDebtDays, (c, v) => c.Desc.Town.Economy.EvictDebtDays = v, "3"),
        Col.Scalar<int>(214, "CityBudgetEwmaShift", w => w.Town.Economy.BudgetEwmaShift, (c, v) => c.Desc.Town.Economy.BudgetEwmaShift = v, "3"),
        Col.Scalar<int>(215, "CityUndoRefundS", w => w.Town.Economy.UndoRefundS, (c, v) => c.Desc.Town.Economy.UndoRefundS = v, "10"),
        Col.Scalar<int>(216, "CityMaxTaxBp", w => w.Town.Economy.MaxTaxBp, (c, v) => c.Desc.Town.Economy.MaxTaxBp = v, "5000"),
        Col.Scalar<int>(217, "CityMaxLandBp", w => w.Town.Economy.MaxLandBp, (c, v) => c.Desc.Town.Economy.MaxLandBp = v, "100"),
        Col.Scalar<int>(218, "CityMaxFare", w => w.Town.Economy.MaxFare, (c, v) => c.Desc.Town.Economy.MaxFare = v, "1000"),
    };

    // ---- BLDG 21 (w9): сглаженный фонд оплаты фирмы ----
    private static ColumnDef PayrollEwmaColumn() =>
        Col.Array<long>(21, "PayrollEwma", w => w.City!.Buildings.PayrollEwma, _bldHw, "HighWater", "0", _bldPool);

    // ---- CNTR, w9: счётчики казны ----
    private static ColumnDef[] BudgetCounters() => new[]
    {
        Col.Scalar<long>(154, "FareEvaded", w => w.Counters.FareEvaded, (c, v) => c.W.Counters.FareEvaded = v),
        Col.Scalar<long>(155, "DebtEvictions", w => w.Counters.DebtEvictions, (c, v) => c.W.Counters.DebtEvictions = v),
        Col.Scalar<long>(156, "InsufficientFunds", w => w.Counters.InsufficientFunds, (c, v) => c.W.Counters.InsufficientFunds = v),
    };

    private static Func<World, bool> BudgetPresent => static w => w.City?.Budget != null;

    private static readonly Func<World, int> _cellN = static w => w.City!.Budget!.LandValue.Length;

    // ---- CBGT: казна города (есть <=> PARM.CityBudget = 1); производные (Maint*, VotDsQ8) не сохраняются ----
    private static BlockDef BudgetBlock() => new("CBGT", "казна города: политики, цена земли, сглаженные доход и расход, переносы остатков, долг, кредиты undo (city-economy.md §2.2, §12)", new[]
    {
        Col.Scalar<ushort>(1, "PolicyTaxRBp", w => w.City!.Budget!.Policy.TaxRBp, (c, v) => c.W.City!.Budget!.Policy.TaxRBp = v),
        Col.Scalar<ushort>(2, "PolicyTaxCBp", w => w.City!.Budget!.Policy.TaxCBp, (c, v) => c.W.City!.Budget!.Policy.TaxCBp = v),
        Col.Scalar<ushort>(3, "PolicyTaxIBp", w => w.City!.Budget!.Policy.TaxIBp, (c, v) => c.W.City!.Budget!.Policy.TaxIBp = v),
        Col.Scalar<ushort>(4, "PolicyLandTaxBpDay", w => w.City!.Budget!.Policy.LandTaxBpDay, (c, v) => c.W.City!.Budget!.Policy.LandTaxBpDay = v),
        Col.Scalar<ushort>(5, "PolicyFare", w => w.City!.Budget!.Policy.Fare, (c, v) => c.W.City!.Budget!.Policy.Fare = v),
        Col.Scalar<ushort>(6, "PolicyFlags", w => w.City!.Budget!.Policy.Flags, (c, v) => c.W.City!.Budget!.Policy.Flags = v),
        Col.Scalar<ushort>(7, "PolicyR0", w => w.City!.Budget!.Policy.R0, (c, v) => c.W.City!.Budget!.Policy.R0 = v),
        Col.Scalar<ushort>(8, "PolicyR1", w => w.City!.Budget!.Policy.R1, (c, v) => c.W.City!.Budget!.Policy.R1 = v),
        Col.Scalar<ushort>(9, "PendingTaxRBp", w => w.City!.Budget!.PendingPolicy.TaxRBp, (c, v) => c.W.City!.Budget!.PendingPolicy.TaxRBp = v),
        Col.Scalar<ushort>(10, "PendingTaxCBp", w => w.City!.Budget!.PendingPolicy.TaxCBp, (c, v) => c.W.City!.Budget!.PendingPolicy.TaxCBp = v),
        Col.Scalar<ushort>(11, "PendingTaxIBp", w => w.City!.Budget!.PendingPolicy.TaxIBp, (c, v) => c.W.City!.Budget!.PendingPolicy.TaxIBp = v),
        Col.Scalar<ushort>(12, "PendingLandTaxBpDay", w => w.City!.Budget!.PendingPolicy.LandTaxBpDay, (c, v) => c.W.City!.Budget!.PendingPolicy.LandTaxBpDay = v),
        Col.Scalar<ushort>(13, "PendingFare", w => w.City!.Budget!.PendingPolicy.Fare, (c, v) => c.W.City!.Budget!.PendingPolicy.Fare = v),
        Col.Scalar<ushort>(14, "PendingFlags", w => w.City!.Budget!.PendingPolicy.Flags, (c, v) => c.W.City!.Budget!.PendingPolicy.Flags = v),
        Col.Scalar<ushort>(15, "PendingR0", w => w.City!.Budget!.PendingPolicy.R0, (c, v) => c.W.City!.Budget!.PendingPolicy.R0 = v),
        Col.Scalar<ushort>(16, "PendingR1", w => w.City!.Budget!.PendingPolicy.R1, (c, v) => c.W.City!.Budget!.PendingPolicy.R1 = v),
        Col.Scalar<byte>(17, "HasPending", w => w.City!.Budget!.HasPending, (c, v) => c.W.City!.Budget!.HasPending = v),
        Col.Array<int>(18, "LandValue", w => w.City!.Budget!.LandValue, _cellN, "NC"),
        Col.Scalar<long>(19, "IncomeEwma", w => w.City!.Budget!.IncomeEwma, (c, v) => c.W.City!.Budget!.IncomeEwma = v),
        Col.Scalar<long>(20, "ExpenseEwma", w => w.City!.Budget!.ExpenseEwma, (c, v) => c.W.City!.Budget!.ExpenseEwma = v),
        Col.Array<long>(21, "Carry", w => w.City!.Budget!.Carry, static _ => Ledger.FlowCount, "23"),
        Col.Scalar<int>(22, "DebtDays", w => w.City!.Budget!.DebtDays, (c, v) => c.W.City!.Budget!.DebtDays = v),
        Col.Scalar<int>(23, "OverLimitDays", w => w.City!.Budget!.OverLimitDays, (c, v) => c.W.City!.Budget!.OverLimitDays = v),
        Col.Scalar<byte>(24, "Insolvent", w => w.City!.Budget!.Insolvent, (c, v) => c.W.City!.Budget!.Insolvent = v),
        Col.Field<UndoCredit>(25, "UndoTargetIndex", w => w.City!.Budget!.UndoCredits, static _ => CityBudget.UndoSlots, "64", nameof(UndoCredit.TargetIndex)),
        Col.Field<UndoCredit>(26, "UndoTargetGeneration", w => w.City!.Budget!.UndoCredits, static _ => CityBudget.UndoSlots, "64", nameof(UndoCredit.TargetGeneration)),
        Col.Field<UndoCredit>(27, "UndoAmount", w => w.City!.Budget!.UndoCredits, static _ => CityBudget.UndoSlots, "64", nameof(UndoCredit.Amount)),
        Col.Field<UndoCredit>(28, "UndoExpireTick", w => w.City!.Budget!.UndoCredits, static _ => CityBudget.UndoSlots, "64", nameof(UndoCredit.ExpireTick)),
        Col.Array<int>(29, "FareEvadedDay", w => w.City!.Budget!.FareEvadedDay, static _ => 2, "2"),
        Col.Array<int>(30, "DebtEvictionsDay", w => w.City!.Budget!.DebtEvictionsDay, static _ => 2, "2"),
    })
    {
        PresentText = "PARM.CityBudget = 1",
        Present = BudgetPresent,
    };
}
