using System.IO;
using Sim.Core.Api;
using Sim.Core.Tests.Gen;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>Сценарии режима жителей больших размеров (Z8, zoning-demand.md §11, §16): <c>s10k-res</c>, <c>s100k-res</c> разбираются загрузчиком и согласованы по ёмкостям. Создание мира не проверяется (минуты).</summary>
public sealed class ResScenariosTests
{
    [Theory]
    [InlineData("s10k-res", 16_000, true)]
    [InlineData("s100k-res", 150_000, false)]
    public void Scenario_Loads_AsResidentsCity(string name, int minLots, bool peds)
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", name + ".json"));
        ScenarioDesc d = sc.Desc;
        Assert.Equal(DemandModel.Residents, d.Demand.Model);
        Assert.True(d.Capacities.MaxLots >= minLots);
        Assert.True(d.Capacities.MaxBuildings >= d.Capacities.MaxLots * 2 / 3);
        Assert.True(d.Capacities.MaxCityResidents >= 40_000);
        Assert.Equal(peds, d.Peds.Source == PedSource.City);
    }
}
