using System;
using Sim.Core.Api;

namespace TrafficCity.Ui;

/// <summary>
/// Снимок ядра для панели «Город» (UI16) и замков инструментов: заполняет <see cref="SimHost"/> раз в секунду, читает главный поток.
/// Массивы выделены один раз; поля-структуры - чтобы <c>out</c> писал прямо сюда. Числа - как отдало ядро (минуты ×10, доли ‰, −1 - нет данных).
/// </summary>
#pragma warning disable CA1051 // поля нужны: ядро пишет в них через out/Span без копий
public sealed class CityPanelData
{
    public const int MaxHours = 24;
    public const int MaxDays = 32;
    public const int MaxMilestones = 32;

    public int Version;
    public bool HasKpi;
    public CityKpiInfo Kpi;
    public readonly KpiHour[] Hours = new KpiHour[MaxHours];
    public int HourCount;
    public bool HasCurrentHour;
    public KpiHour CurrentHour;
    public readonly long[] Days = new long[MaxDays];
    public int DayCount;
    public bool HasProgression;
    public ProgressionInfo Progress;
    public readonly MilestoneInfo[] Milestones = new MilestoneInfo[MaxMilestones];
    public int MilestoneCount;
    public readonly bool[] ProfileOpen = new bool[RoadProfileValidator.MaxProfiles];

    /// <summary>Признак открыт (без прогрессии - всегда).</summary>
    public bool FeatureOpen(Feature f) => !HasProgression || (Progress.Features & (1UL << (int)f)) != 0;

    /// <summary>Номер вехи (0-based), которая откроет признак; −1 - ни одна не откроет.</summary>
    public int MilestoneFor(Feature f)
    {
        ulong bit = 1UL << (int)f;
        for (int i = 0; i < MilestoneCount; i++)
        {
            if (Milestones[i].ReachedTick < 0 && (Milestones[i].Desc.Features & bit) != 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Значение ряда для часа: минуты ×10 или ‰, −1 - данных нет (та же формула, что у ядра, <c>CityKpi.Value</c>).</summary>
    public static int HourValue(in KpiHour h, KpiKind kind, int dayTicks) => kind switch
    {
        KpiKind.TravelMin => h.LegsAll > 0 && dayTicks > 0 ? (int)Math.Min(int.MaxValue, (h.DurCarDs + h.DurBusDs + h.DurWalkDs) * 28_800 / ((long)dayTicks * h.LegsAll)) : -1,
        KpiKind.IdlePct => h.VehTicks > 0 ? (int)((h.VehTicks - h.MovingTicks) * 1000 / h.VehTicks) : -1,
        KpiKind.BusSharePct => h.LegsAll > 0 ? (int)((long)h.LegsBus * 1000 / h.LegsAll) : -1,
        KpiKind.SatisfactionPct => h.LegsAll > 0 ? 1000 - (int)Math.Min(1000L, (long)h.Incidents * 1000 / h.LegsAll) : -1,
        _ => -1,
    };
}
#pragma warning restore CA1051
