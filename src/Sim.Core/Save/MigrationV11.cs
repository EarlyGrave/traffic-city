using System;
using Sim.Core.Api;

namespace Sim.Core.Save;

/// <summary>
/// Миграция <c>world_version</c> 10 -> 11 (save-format.md §4, §12; ADR-0034, progression.md §2.4, §4.4, задача PG1): прогрессия и показатели города. Чистая функция над <see cref="SaveTables"/>.
/// Мир из файла v10 - без прогрессии: <c>PARM</c> 240 = 0 (блока <c>PROG</c> нет), <c>CNTR</c> 173 = 0, <c>CBGT</c> 31 = 0 (если казна есть). В режиме жителей создаётся пустой блок
/// <c>CKPI</c> («собираем данные»: кольцо часов и суточное кольцо пусты), отметки начала часа = текущие <c>CNTR.VehicleTicksTotal/MovingVehicleTicksTotal</c>, чтобы первый час не взял
/// всю историю. Поведение и хэш не меняются (раздел хэша прогрессии пишется только при прогрессии), поэтому мир из файла w1-w10 продолжается тем же хэшем.
/// </summary>
internal static class MigrationV11
{
    public static LoadStatus Run(SaveTables t)
    {
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        int cntr = t.FindBlock(SaveFormat.FourCc("CNTR"u8));
        if (parm < 0 || cntr < 0 ||
            !t.TryGetColumn(cntr, 7, out ReadOnlySpan<long> vehTicks) || vehTicks.Length != 1 ||
            !t.TryGetColumn(cntr, 8, out ReadOnlySpan<long> moving) || moving.Length != 1)
        {
            return LoadStatus.MigrationFailed;
        }

        long veh0 = vehTicks[0];
        long mov0 = moving[0];
        bool ok = t.SetColumn(parm, 240, new byte[1]);
        ok &= t.SetColumn(cntr, 173, new long[1]);
        int cbgt = t.FindBlock(SaveFormat.FourCc("CBGT"u8));
        if (cbgt >= 0)
        {
            ok &= t.SetColumn(cbgt, 31, new byte[1]);
        }

        if (t.FindBlock(SaveFormat.FourCc("RESD"u8)) >= 0 && t.FindBlock(SaveFormat.FourCc("CKPI"u8)) < 0)
        {
            t.AddBlock(SaveFormat.FourCc("CKPI"u8), 1, BlockFlags.Critical);
            t.AddColumn(1, new int[1]);
            t.AddColumn(2, new int[1]);
            t.AddColumn(3, new[] { veh0 });
            t.AddColumn(4, new[] { mov0 });
            t.AddColumn(5, new int[1]);
            t.AddColumn(6, new int[1]);
            t.AddColumn(7, new long[City.CityKpi.Days]);
            int fields = SaveSchema.KpiFields.Length;
            for (int pass = 0; pass < 2; pass++)
            {
                int len = pass == 0 ? City.CityKpi.Hours : 1;
                for (int i = 0; i < fields; i++)
                {
                    uint id = (uint)(8 + (pass * fields) + i);
                    if (IsLongField(SaveSchema.KpiFields[i]))
                    {
                        t.AddColumn(id, new long[len]);
                    }
                    else
                    {
                        t.AddColumn(id, new int[len]);
                    }
                }
            }
        }

        return ok ? LoadStatus.Ok : LoadStatus.MigrationFailed;
    }

    private static bool IsLongField(string name) => name.StartsWith("Dur", StringComparison.Ordinal) || name == nameof(KpiHour.VehTicks) || name == nameof(KpiHour.MovingTicks);
}
