using System;
using Sim.Core.Api;
using Sim.Host;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// Данные панели «Город» (UI16, progression.md §2, §3) и замков инструментов (§4.2): раз в секунду под <c>AcquireExclusive</c>, как верхняя полоса (UI2).
/// Буферы выделены один раз (<see cref="CityPanelData"/>), в кадре - без аллокаций. Показатели читаются, только пока панель открыта или есть прогрессия (замки).
/// </summary>
public partial class SimHost
{
    private readonly CityPanelData _cityPanel = new();
    private double _cityPanelTimer;
    private long _cityPanelTick = -1;

    /// <summary>Снимок для панели «Город» и замков; <see cref="CityPanelData.Version"/> растёт при каждом обновлении.</summary>
    public CityPanelData CityPanel => _cityPanel;

    /// <summary>Панель «Город» открыта: читать показатели (без неё - только замки).</summary>
    public bool CityPanelWanted { get; set; }

    private void RefreshCityPanel(SimFrame f, double delta)
    {
        _cityPanelTimer -= delta;
        if (!_hasCity || _host == null || _cityPanelTimer > 0 || f.SnapshotTick == _cityPanelTick)
        {
            return;
        }

        bool first = _cityPanel.Version == 0;
        if (!first && !CityPanelWanted && !_cityPanel.HasProgression)
        {
            return; // нечего показывать: ни панели, ни замков
        }

        _cityPanelTimer = 1.0;
        _cityPanelTick = f.SnapshotTick;
        CityPanelData d = _cityPanel;
        using (ExclusiveAccess ex = _host.AcquireExclusive())
        {
            Simulation? sim = ex.Simulation;
            if (sim == null)
            {
                return;
            }

            d.HasKpi = sim.GetCityKpi(out d.Kpi);
            d.HourCount = sim.CopyKpiHours(d.Hours);
            d.HasCurrentHour = sim.GetKpiCurrentHour(out d.CurrentHour);
            d.DayCount = sim.CopyKpiDays(d.Days);
            d.HasProgression = sim.GetProgression(out d.Progress);
            d.MilestoneCount = 0;
            if (d.HasProgression)
            {
                int n = Math.Min(d.Progress.MilestoneCount, d.Milestones.Length);
                for (int i = 0; i < n; i++)
                {
                    if (sim.GetMilestone(i, out d.Milestones[i]))
                    {
                        d.MilestoneCount = i + 1;
                    }
                }

                for (int p = 0; p < d.ProfileOpen.Length; p++)
                {
                    d.ProfileOpen[p] = sim.IsProfileUnlocked(p);
                }
            }
        }

        d.Version++;
        if (d.HasProgression)
        {
            _roadsStrip?.ApplyLocks(d);
        }
    }
}
