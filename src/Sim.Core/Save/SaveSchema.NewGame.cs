using Sim.Core.Api;

namespace Sim.Core.Save;

// world_version 13 (new-game-scratch.md §2.2, задача Y4): параметры города для старта с нуля. PARM 256-258; без новых ключей сценария - дефолты (0, false, 0).
//   calendar.start_hour и zoning.grid_from_bounds в сейв не идут: первый только сдвигает Tick при Create (Tick лежит в заголовке), вторая нужна только при Create (сетка города лежит в PARM).
internal static partial class SaveSchema
{
    private static ColumnDef[] NewGameParams() => new[]
    {
        Col.Scalar<int>(256, "CityMinImmigrantsPerDay", w => w.Town.Residents.MinImmigrantsPerDay, (c, v) => c.Desc.Town.Residents.MinImmigrantsPerDay = v, "0"),
        Col.Flag(257, "CityGrowthHourly", w => w.Town.Zoning.GrowthStep != 0, (c, v) => c.Desc.Town.Zoning.GrowthStep = (byte)(v ? 1 : 0)),
        Col.Scalar<int>(258, "CityInitialCityBalance", w => w.Town.Economy.InitialCityBalance, (c, v) => c.Desc.Town.Economy.InitialCityBalance = v, "0"),
    };
}
