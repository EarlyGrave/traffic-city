using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Terrain;

namespace Sim.Core.Save;

// world_version 12 (terrain.md §8, ADR-0036; задача TR1): рельеф и вода.
//   PARM 241-255 - флаг рельефа и параметры (без рельефа - дефолты); блок TERR (есть <=> PARM.TerrainEnabled = 1) - сетка, Ground, Water, WaterKind, версии.
//   Производные (Lock, кольцо грязи) не сохраняются. Резерв: тег WSRC и столбцы TERR 13-19 (источники и течение воды, §3.4).
//   TR3: TERR 20 - max_grade_pct профилей, ‰ (длина ProfileCount, 0 в legacy); вне хэша (параметр каталога).
internal static partial class SaveSchema
{
    // ---- PARM 241-255 ----
    private static ColumnDef[] TerrainParamColumns() => new[]
    {
        Col.Flag(241, "TerrainEnabled", w => w.Terrain != null, (c, v) => c.Desc.Terrain.Enabled = v),
        Col.Scalar<int>(242, "TerrainMaxGradeStreetPm", w => w.TerrainCfg.MaxGradeStreetPm, (c, v) => c.Desc.Terrain.MaxGradeStreetPm = v, "80"),
        Col.Scalar<int>(243, "TerrainMaxGradeArterialPm", w => w.TerrainCfg.MaxGradeArterialPm, (c, v) => c.Desc.Terrain.MaxGradeArterialPm = v, "60"),
        Col.Scalar<int>(244, "TerrainMaxCutCm", w => w.TerrainCfg.MaxCutCm, (c, v) => c.Desc.Terrain.MaxCutCm = v, "1000"),
        Col.Scalar<int>(245, "TerrainMaxFillCm", w => w.TerrainCfg.MaxFillCm, (c, v) => c.Desc.Terrain.MaxFillCm = v, "600"),
        Col.Scalar<int>(246, "TerrainBankSlopePm", w => w.TerrainCfg.BankSlopePm, (c, v) => c.Desc.Terrain.BankSlopePm = v, "500"),
        Col.Scalar<int>(247, "TerrainBridgeClearanceCm", w => w.TerrainCfg.BridgeClearanceCm, (c, v) => c.Desc.Terrain.BridgeClearanceCm = v, "200"),
        Col.Scalar<int>(248, "TerrainMaxBridgeM", w => w.TerrainCfg.MaxBridgeM, (c, v) => c.Desc.Terrain.MaxBridgeM = v, "300"),
        Col.Flag(249, "TerrainAutoBridge", w => !w.TerrainCfg.NoAutoBridge, (c, v) => c.Desc.Terrain.NoAutoBridge = !v, "1"),
        Col.Scalar<int>(250, "TerrainMaxBrushM", w => w.TerrainCfg.MaxBrushM, (c, v) => c.Desc.Terrain.MaxBrushM = v, "96"),
        Col.Scalar<int>(251, "TerrainMaxDabCm", w => w.TerrainCfg.MaxDabCm, (c, v) => c.Desc.Terrain.MaxDabCm = v, "200"),
        Col.Scalar<int>(252, "TerrainMaxSamplesPerTick", w => w.TerrainCfg.MaxSamplesPerTick, (c, v) => c.Desc.Terrain.MaxSamplesPerTick = v, "32768"),
        Col.Scalar<int>(253, "TerrainMaxFloodSamples", w => w.TerrainCfg.MaxFloodSamples, (c, v) => c.Desc.Terrain.MaxFloodSamples = v, "65536"),
        Col.Scalar<int>(254, "LotsMaxSlopePct", w => w.TerrainCfg.LotsMaxSlopePct, (c, v) => c.Desc.Terrain.LotsMaxSlopePct = v, "15"),
        Col.Scalar<int>(255, "GradeMaxBoostPm", w => w.TerrainCfg.GradeMaxBoostPm, (c, v) => c.Desc.Terrain.GradeMaxBoostPm = v, "1100"),
    };

    private static readonly Func<World, int> _terrN = static w => w.Terrain!.Count;

    // ---- TERR: рельеф и вода (есть <=> PARM.TerrainEnabled = 1) ----
    private static BlockDef TerrainBlock() => new("TERR", "рельеф и вода: сетка отсчётов (шаг, начало, размеры, база), Ground и Water в см над базой, WaterKind, версии (terrain.md §2.1, §8)", new[]
    {
        Col.Scalar<int>(1, "CellMm", w => w.Terrain!.CellMm, (c, v) => { c.TerrCellMm = v; return TerrainParams.ValidCell(v) ? LoadStatus.Ok : LoadStatus.InvalidValue; }),
        Col.Scalar<float>(2, "OriginX", w => w.Terrain!.OriginX, (c, v) => c.TerrOriginX = v),
        Col.Scalar<float>(3, "OriginZ", w => w.Terrain!.OriginZ, (c, v) => c.TerrOriginZ = v),
        Col.Scalar<int>(4, "SamplesX", w => w.Terrain!.SamplesX, (c, v) => { c.TerrSamplesX = v; return Range(v, 2, TerrainStore.MaxSamples / 2); }),
        Col.Scalar<int>(5, "SamplesZ", w => w.Terrain!.SamplesZ, (c, v) => { c.TerrSamplesZ = v; return Range(v, 2, TerrainStore.MaxSamples / 2); }),
        Col.Scalar<int>(6, "BaseCm", w => w.Terrain!.BaseCm, (c, v) => c.TerrBaseCm = v),
        Col.Scalar<int>(7, "HeightUnitMm", w => TerrainStore.HeightUnitMm, (c, v) =>
        {
            LoadStatus st = v == TerrainStore.HeightUnitMm ? SaveValidator.ValidateTerrainGrid(c) : LoadStatus.InvalidValue;
            if (st == LoadStatus.Ok)
            {
                c.W.Terrain = new TerrainStore(c.TerrCellMm, c.TerrOriginX, c.TerrOriginZ, c.TerrSamplesX, c.TerrSamplesZ, c.TerrBaseCm);
            }

            return st;
        }, "10"),
        Col.Array<ushort>(8, "Ground", w => w.Terrain!.Ground, _terrN, "SamplesX * SamplesZ"),
        Col.Array<ushort>(9, "Water", w => w.Terrain!.Water, _terrN, "SamplesX * SamplesZ"),
        Col.Array<byte>(10, "WaterKind", w => w.Terrain!.WaterKind, _terrN, "SamplesX * SamplesZ"),
        Col.Scalar<int>(11, "Version", w => w.Terrain!.Version, (c, v) => { c.W.Terrain!.Version = v; return v >= 0 ? LoadStatus.Ok : LoadStatus.InvalidValue; }),
        Col.Scalar<int>(12, "OpSeq", w => w.Terrain!.OpSeq, (c, v) => { c.W.Terrain!.OpSeq = v; return v >= 0 ? LoadStatus.Ok : LoadStatus.InvalidValue; }),
        new ColumnDef(
            20,
            "ProfileMaxGradePm",
            ColumnType.I32,
            4,
            "ProfileCount (0 в legacy)",
            "0",
            w => w.Net.Profiles?.Count ?? 0,
            (w, first, n, dst) => MemoryMarshal.AsBytes(new ReadOnlySpan<int>(w.Net.Profiles!.MaxGradePm, (int)first, n)).CopyTo(dst),
            (c, n, src) =>
            {
                // TR3 (terrain.md §4.3): max_grade_pct профилей, ‰ (0 - предел по виду дороги). Каталог RPRF уже загружен (PreWorld).
                RoadProfileStore? p = c.W.Net.Profiles;
                if (n == 0)
                {
                    return LoadStatus.Ok;
                }

                if (p == null || n != p.Count)
                {
                    return LoadStatus.InvalidValue;
                }

                src.CopyTo(MemoryMarshal.AsBytes(new Span<int>(p.MaxGradePm, 0, (int)n)));
                for (int i = 0; i < p.Count; i++)
                {
                    if (p.MaxGradePm[i] < 0 || p.MaxGradePm[i] > 1000)
                    {
                        return LoadStatus.InvalidValue;
                    }
                }

                return LoadStatus.Ok;
            }) { Optional = true },   // сейвы w12 до TR3 - без столбца (пределы профилей по умолчанию)
    })
    {
        After = c => SaveValidator.ValidateTerrain(c),
        PresentText = "PARM.TerrainEnabled = 1",
        Present = static w => w.Terrain != null,
    };
}
