using System.IO;
using Sim.Core.Api;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Worlds;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>Сценарии режима жителей для тестов Z1/Z2: s1k-сеть с <c>demand.model = residents</c> и дефолтами спеки (zoning-demand.md §16).</summary>
internal static class CityFixtures
{
    public const int MaxLots = 4_000;
    public const int MaxBuildings = 3_000;
    public const int MaxCityResidents = 20_000;

    /// <summary>s1k с жителями без пешеходов: автобусы как в s1k, OD-генератор выключен, ёмкости города с запасом на рост.</summary>
    public static ScenarioDesc S1kRes()
    {
        ScenarioDesc d = Step2.S1k(buses: true);
        d.Demand.Model = DemandModel.Residents;
        d.Town = CityDesc.Default();
        d.Lod.CellM = 1016f;
        SimCapacities c = d.Capacities;
        c.MaxLots = MaxLots;
        c.MaxBuildings = MaxBuildings;
        c.MaxCityResidents = MaxCityResidents;
        c.SkimObsCapacity = 4096;
        d.Capacities = c;
        return d;
    }

    /// <summary>Сценарий <c>scenarios/s1k-res.json</c> (жители города, <c>peds.source = city</c>, автобусы-агенты).</summary>
    public static ScenarioDesc S1kResPeds()
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res.json"));
        return sc.Desc;
    }
}
