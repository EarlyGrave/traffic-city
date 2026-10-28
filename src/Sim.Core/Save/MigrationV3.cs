using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 2 -> 3 (save-format.md §4, §12; zoning-demand.md §15): вторая боевая. Чистая функция над <see cref="SaveTables"/> (без <c>World</c>, PRNG и времени).
/// Добавляет столбцы с дефолтами «города нет»: <c>PARM</c> 113-184 (источник пешеходов, секции жителей, ёмкости, сетка ячеек), <c>WRLD</c> 14-18 (поток <c>Demand</c>, сутки, режим
/// исполнения), <c>CNTR</c> 118-149 (счётчики города), <c>VEHC</c> 41 (<c>Resident</c> = -1 у всех машин). Новых блоков не создаёт: <c>BTYP</c>, <c>LOTS</c>, <c>BLDG</c>,
/// <c>RESD</c>, <c>SKIM</c>, <c>LEDG</c> обязательны только при <c>demand.model = residents</c>. Поведение без жителей не меняется, поэтому мир из файла w1/w2 продолжается тем же хэшем.
/// </summary>
internal static class MigrationV3
{
    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        int wrld = t.FindBlock(SaveFormat.FourCc("WRLD"u8));
        int cntr = t.FindBlock(SaveFormat.FourCc("CNTR"u8));
        int vehc = t.FindBlock(SaveFormat.FourCc("VEHC"u8));
        if (parm < 0 || wrld < 0 || cntr < 0 || vehc < 0 || !t.TryGetColumn(vehc, 1, out System.ReadOnlySpan<int> highWater) || highWater.Length != 1 || highWater[0] < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        int vehicles = highWater[0];
        bool ok = true;

        // TRIP: ResidentRef (11) = 0 у каждой отложенной поездки (поездок жителей в старых мирах нет).
        int trip = t.FindBlock(SaveFormat.FourCc("TRIP"u8));
        if (trip < 0 || !t.TryGetColumn(trip, 1, out System.ReadOnlySpan<int> tripCount) || tripCount.Length != 1 || tripCount[0] < 0)
        {
            return LoadStatus.MigrationFailed;
        }

        ok &= t.SetColumn(trip, 11, new int[tripCount[0]]);

        // PARM 113-184: флаги (118 Growth, 120 ZoningAuto) - u8, сетка (181, 182) - f32, остальное - i32; всё нули.
        for (uint id = 113; id <= 184; id++)
        {
            if (id is 118 or 120)
            {
                ok &= t.SetColumn(parm, id, new byte[1]);
            }
            else if (id is 181 or 182)
            {
                ok &= t.SetColumn(parm, id, new float[1]);
            }
            else
            {
                ok &= t.SetColumn(parm, id, new int[1]);
            }
        }

        // WRLD: DemandRngState (14), DemandRngInc (15) - u64; CityDay (16), CityExec (17), CityExecPending (18) - i32.
        ok &= t.SetColumn(wrld, 14, new ulong[1]);
        ok &= t.SetColumn(wrld, 15, new ulong[1]);
        ok &= t.SetColumn(wrld, 16, new int[1]);
        ok &= t.SetColumn(wrld, 17, new int[1]);
        ok &= t.SetColumn(wrld, 18, new int[1]);

        // CNTR 118-149: long, нули.
        for (uint id = 118; id <= 149; id++)
        {
            ok &= t.SetColumn(cntr, id, new long[1]);
        }

        // VEHC: Resident (41) = -1 у каждого слота.
        var resident = new int[vehicles];
        System.Array.Fill(resident, -1);
        ok &= t.SetColumn(vehc, 41, resident);
        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }
}
