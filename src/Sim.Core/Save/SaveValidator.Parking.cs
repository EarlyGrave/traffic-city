using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Parking;

namespace Sim.Core.Save;

// Проверки парковки после загрузки (parking.md §13): объекты, ссылки жителей, машин и заявок живы с верным поколением, PK2, PK4, PK6 - Parking/ParkingCheck.cs.
internal static partial class SaveValidator
{
    /// <summary>Параметры режима парковки из PARM (до создания мира): пределы секции <c>parking</c> и ёмкости; режим только при residents.</summary>
    public static LoadStatus ValidateParkingParams(LoadContext c)
    {
        if (!c.Desc.Parking.Enabled)
        {
            return c.Desc.Parking.Equals(default(ParkingDesc)) ? LoadStatus.Ok : LoadStatus.InvalidValue;
        }

        if (c.Desc.Demand.Model != DemandModel.Residents || !c.Desc.Parking.IsValid() || c.Desc.Capacities.MaxParking < 0 || c.Desc.Capacities.MaxParking > CityBuild.MaxBigCount)
        {
            return LoadStatus.InvalidValue;
        }

        return LoadStatus.Ok;
    }

    /// <summary>Полигоны землепользования (блок LUSE): конечные вершины, вид Park или Water, флаги в пределах <see cref="LandUseMarks"/>.</summary>
    public static LoadStatus ValidateLandUse(LoadContext c)
    {
        LandUsePoly[] polys = c.W.LandUse;
        for (int i = 0; i < polys.Length; i++)
        {
            ref readonly LandUsePoly q = ref polys[i];
            if (!Fin(q.P0.X) || !Fin(q.P0.Z) || !Fin(q.P1.X) || !Fin(q.P1.Z) || !Fin(q.P2.X) || !Fin(q.P2.Z) || !Fin(q.P3.X) || !Fin(q.P3.Z) ||
                q.Kind is not ((byte)LandUseKind.Park or (byte)LandUseKind.Water) || q.Flags > (byte)(LandUseMarks.Pond | LandUseMarks.Coast))
            {
                return Bad(c, i, 10);
            }
        }

        return LoadStatus.Ok;
    }

    /// <summary>Состояние парковки (после <see cref="ValidateCity"/>): пул объектов, ссылки, PK2/PK4/PK6.</summary>
    public static LoadStatus ValidateParking(LoadContext c)
    {
        World w = c.W;
        ParkingStore? p = w.City?.Parking;
        if (p == null)
        {
            return LoadStatus.Ok;
        }

        LoadStatus st = ValidatePool(c, p.Pool);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        int code = ParkingCheck.Validate(w, out long row);
        return code == 0 ? LoadStatus.Ok : Bad(c, row, (uint)code);
    }
}
