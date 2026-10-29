using System;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Save;

// world_version 11 (progression.md §2.4, §4.4, ADR-0034; задачи PG1-PG2): прогрессия и показатели города.
//   PARM 240 (ProgressionEnabled), CNTR 173 (LockedRejects), CBGT 31 (WarnLevel, PG5); блок CKPI (есть <=> demand.model = residents) - кольцо часов KPI, текущий час, отметки счётчиков,
//   суточное кольцо баланса; блок PROG (есть <=> PARM.ProgressionEnabled = 1) - определения вех (POD без строк), индексы профилей, области, состояние открытого.
internal static partial class SaveSchema
{
    // ---- PARM 240 ----
    private static ColumnDef[] ProgressionParams() => new[]
    {
        Col.Flag(240, "ProgressionEnabled", w => w.Progression != null, (c, v) => c.Desc.Progression.Enabled = v),
    };

    // ---- CNTR 173 ----
    private static ColumnDef[] ProgressionCounters() => new[]
    {
        Col.Scalar<long>(173, "LockedRejects", w => w.Counters.LockedRejects, (c, v) => c.W.Counters.LockedRejects = v),
    };

    // ---- CBGT 31 ----
    private static ColumnDef[] ProgressionBudgetColumns() => new[]
    {
        Col.Scalar<byte>(31, "WarnLevel", w => w.City!.Budget!.WarnLevel, (c, v) => c.W.City!.Budget!.WarnLevel = v),
    };

    private static readonly Func<World, int> _kpiHours = static _ => CityKpi.Hours;
    private static readonly Func<World, int> _kpiOne = static _ => 1;

    // ---- CKPI: показатели города (есть <=> residents); производные (пороги случаев) не сохраняются ----
    private static BlockDef CityKpiBlock()
    {
        var cols = new ColumnDef[7 + (2 * KpiFields.Length)];
        cols[0] = Col.Scalar<int>(1, "Head", w => w.City!.Kpi.Head, (c, v) => c.W.City!.Kpi.Head = v);
        cols[1] = Col.Scalar<int>(2, "HoursFilled", w => w.City!.Kpi.HoursFilled, (c, v) => c.W.City!.Kpi.HoursFilled = v);
        cols[2] = Col.Scalar<long>(3, "VehAtHourStart", w => w.City!.Kpi.VehAtHourStart, (c, v) => c.W.City!.Kpi.VehAtHourStart = v);
        cols[3] = Col.Scalar<long>(4, "MovingAtHourStart", w => w.City!.Kpi.MovingAtHourStart, (c, v) => c.W.City!.Kpi.MovingAtHourStart = v);
        cols[4] = Col.Scalar<int>(5, "DayHead", w => w.City!.Kpi.DayHead, (c, v) => c.W.City!.Kpi.DayHead = v);
        cols[5] = Col.Scalar<int>(6, "DaysFilled", w => w.City!.Kpi.DaysFilled, (c, v) => c.W.City!.Kpi.DaysFilled = v);
        cols[6] = Col.Array<long>(7, "DayBalance", w => w.City!.Kpi.DayBalance, static _ => CityKpi.Days, "32");
        for (int i = 0; i < KpiFields.Length; i++)
        {
            cols[7 + i] = Col.Field<KpiHour>((uint)(8 + i), "Ring" + KpiFields[i], w => w.City!.Kpi.Ring, _kpiHours, "24", KpiFields[i]);
            cols[7 + KpiFields.Length + i] = Col.Field<KpiHour>((uint)(8 + KpiFields.Length + i), "Cur" + KpiFields[i], w => w.City!.Kpi.Cur, _kpiOne, "1", KpiFields[i]);
        }

        return new BlockDef("CKPI", "показатели города: кольцо 24 игровых часов (физический порядок), текущий час, отметки счётчиков движения, суточное кольцо баланса (progression.md §2.3)", cols)
        {
            After = c => SaveValidator.ValidateKpi(c),
            PresentText = "demand.model = residents",
            Present = static w => w.City != null,
        };
    }

    /// <summary>Поля <see cref="KpiHour"/> в сейве (порядок - id столбцов CKPI 8..22 кольца и 23..37 текущего часа); резерв <c>R</c> не сохраняется.</summary>
    internal static readonly string[] KpiFields =
    {
        nameof(KpiHour.LegsCar), nameof(KpiHour.LegsBus), nameof(KpiHour.LegsWalk), nameof(KpiHour.LegsWork),
        nameof(KpiHour.DurCarDs), nameof(KpiHour.DurBusDs), nameof(KpiHour.DurWalkDs), nameof(KpiHour.DurWorkDs),
        nameof(KpiHour.VehTicks), nameof(KpiHour.MovingTicks),
        nameof(KpiHour.IncLongWait), nameof(KpiHour.IncNoParking), nameof(KpiHour.IncLongCommute), nameof(KpiHour.IncTourCancelled), nameof(KpiHour.IncLongStopWait),
    };

    private static readonly Func<World, int> _progN = static w => w.Progression!.Defs.Length;
    private static readonly Func<World, int> _progIdxN = static w => w.Progression!.ProfileIdx.Length;
    private static readonly Func<World, int> _progAreaN = static w => w.Progression!.Areas.Length;
    private static readonly Func<World, int> _progWordsN = static w => w.Progression!.Profiles.Length;

    // ---- PROG: прогрессия (есть <=> PARM.ProgressionEnabled = 1) ----
    private static BlockDef ProgressionBlock() => new("PROG", "прогрессия: определения вех (порог, ворота, признаки, профили, грант, кредит, область), индексы профилей, области, открытое и взятые вехи (progression.md §4.4)", new[]
    {
        Col.Scalar<int>(1, "MilestoneCount", w => w.Progression!.Defs.Length, (c, v) => { c.ProgMilestones = v; return Range(v, 0, ProgressionState.MaxMilestones); }),
        Col.Scalar<int>(2, "ProfileIdxCount", w => w.Progression!.ProfileIdx.Length, (c, v) => { c.ProgProfileIdx = v; return Range(v, 0, 64 * ProgressionState.MaxMilestones); }),
        Col.Scalar<int>(3, "AreaCount", w => w.Progression!.Areas.Length, (c, v) => { c.ProgAreas = v; return Range(v, 0, ProgressionState.MaxAreas); }),
        Col.Scalar<int>(4, "ProfileCount", w => w.Progression!.ProfileCount, (c, v) =>
        {
            if (v != (c.W.Net.Profiles?.Count ?? 0))
            {
                return LoadStatus.InvalidValue;   // битсет профилей - по каталогу мира
            }

            c.W.Progression = new ProgressionState(c.ProgMilestones, c.ProgProfileIdx, c.ProgAreas, ProgressionState.WordsFor(v)) { ProfileCount = v };
            return LoadStatus.Ok;
        }),
        Col.Scalar<int>(5, "MinLegs", w => w.Progression!.MinLegs, (c, v) => c.W.Progression!.MinLegs = v),
        Col.Scalar<ulong>(6, "LockedFeatures", w => w.Progression!.LockedFeatures, (c, v) => c.W.Progression!.LockedFeatures = v),
        Col.Field<MilestoneDesc>(7, "Population", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.Population)),
        Col.Field<MilestoneDesc>(8, "Grant", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.Grant)),
        Col.Field<MilestoneDesc>(9, "CreditDays", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.CreditDays)),
        Col.Field<MilestoneDesc>(10, "Area", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.Area)),
        Col.Field<MilestoneDesc>(11, "Flags", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.Flags)),
        Col.Field<MilestoneDesc>(12, "G0Kpi", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.G0), 0, ColumnType.U8),
        Col.Field<MilestoneDesc>(13, "G0IsMin", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.G0), 1, ColumnType.U8),
        Col.Field<MilestoneDesc>(14, "G0Value", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.G0), 4, ColumnType.I32),
        Col.Field<MilestoneDesc>(15, "G1Kpi", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.G1), 0, ColumnType.U8),
        Col.Field<MilestoneDesc>(16, "G1IsMin", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.G1), 1, ColumnType.U8),
        Col.Field<MilestoneDesc>(17, "G1Value", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.G1), 4, ColumnType.I32),
        Col.Field<MilestoneDesc>(18, "Features", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.Features)),
        Col.Field<MilestoneDesc>(19, "ProfileFirst", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.ProfileFirst)),
        Col.Field<MilestoneDesc>(20, "ProfileCountOf", w => w.Progression!.Defs, _progN, "MilestoneCount", nameof(MilestoneDesc.ProfileCount)),
        Col.Array<long>(21, "ReachedTick", w => w.Progression!.ReachedTick, _progN, "MilestoneCount", "-1"),
        Col.Array<int>(22, "ProfileIdx", w => w.Progression!.ProfileIdx, _progIdxN, "ProfileIdxCount"),
        Col.Field<AreaRect>(23, "AreaMinX", w => w.Progression!.Areas, _progAreaN, "AreaCount", nameof(AreaRect.MinX)),
        Col.Field<AreaRect>(24, "AreaMinZ", w => w.Progression!.Areas, _progAreaN, "AreaCount", nameof(AreaRect.MinZ)),
        Col.Field<AreaRect>(25, "AreaMaxX", w => w.Progression!.Areas, _progAreaN, "AreaCount", nameof(AreaRect.MaxX)),
        Col.Field<AreaRect>(26, "AreaMaxZ", w => w.Progression!.Areas, _progAreaN, "AreaCount", nameof(AreaRect.MaxZ)),
        Col.Scalar<int>(27, "Reached", w => w.Progression!.Reached, (c, v) => c.W.Progression!.Reached = v),
        Col.Scalar<ulong>(28, "FeaturesOpen", w => w.Progression!.Features, (c, v) => c.W.Progression!.Features = v),
        Col.Array<ulong>(29, "ProfilesOpen", w => w.Progression!.Profiles, _progWordsN, "ceil(ProfileCount / 64), >= 1"),
        Col.Scalar<int>(30, "CreditDaysBonus", w => w.Progression!.CreditDaysBonus, (c, v) => c.W.Progression!.CreditDaysBonus = v),
        Col.Scalar<int>(31, "AreaStage", w => w.Progression!.AreaStage, (c, v) => c.W.Progression!.AreaStage = v),
        Col.Scalar<ulong>(32, "GatedNotified", w => w.Progression!.GatedNotified, (c, v) => c.W.Progression!.GatedNotified = v),
    })
    {
        After = c => SaveValidator.ValidateProgression(c),
        PresentText = "PARM.ProgressionEnabled = 1",
        Present = static w => w.Progression != null,
    };
}
