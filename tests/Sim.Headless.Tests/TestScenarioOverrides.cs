using System;
using System.IO;
using System.Runtime.CompilerServices;
using Sim.Scenarios;

namespace Sim.Headless.Tests;

/// <summary>
/// Тесты читают сценарии <c>*-res</c> с замороженным каталогом профилей (<c>tests/fixtures/road-profiles.fixture.json</c>), а не с игровым
/// <c>scenarios/road-profiles.json</c>: правка ширины в игровом каталоге не должна ронять golden (road-profiles.md, «Приёмка», RP8).
/// </summary>
internal static class TestScenarioOverrides
{
    [ModuleInitializer]
    internal static void Init()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "traffic-city.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir != null)
        {
            ScenarioLoader.DefaultOverrides = new ScenarioOverrides { Profiles = Path.Combine(dir, "tests", "fixtures", "road-profiles.fixture.json") };
        }
    }
}
