using System;
using Sim.Core.Api;
using Sim.Core.Lod;
using Sim.Core.Routing;
using Sim.Core.Util;

namespace Sim.Core.Save;

// Условные блоки: OVLY (есть при routing.levels > 0), LODS (lod.mode != off), QMSO (lod.meso_model = queue).
internal static partial class SaveSchema
{
    private static readonly Func<World, int> _ovCells = static w => w.Overlay!.CellCap;
    private static readonly Func<World, int> _lodCellsN = static w => w.Lod!.Cells;

    private static LoadStatus FinishOverlayScalars(LoadContext c)
    {
        var r = new Overlay.OverlayRestore(c.OvOriginX, c.OvOriginZ, c.OvNx1, c.OvNz1, c.OvEntriesLen, c.OvExitsLen, c.OvMatrixLen, c.OvBuiltNodes, c.OvBuiltDirSegs);
        LoadStatus st = Overlay.TryRestore(c.W.Net, c.W.Graph, in c.W.Routing, in r, out Overlay? ov);
        if (st != LoadStatus.Ok || ov == null)
        {
            return st == LoadStatus.Ok ? LoadStatus.InvalidValue : st;
        }

        ov.WeightsVersion = c.OvWeightsVersion;
        ov.Disabled = c.OvDisabled;
        ov.Rebuilds = c.OvRebuilds;
        ov.WaveActive = c.OvWaveActive;
        ov.WaveCell = c.OvWaveCell;
        ov.WaveRow = c.OvWaveRow;
        ov.CustomizeRows = c.OvCustomizeRows;
        ov.CustomizeWork = c.OvCustomizeWork;
        ov.FullRows = c.OvFullRows;
        ov.FullWork = c.OvFullWork;
        c.Ov = ov;
        c.W.Overlay = ov;
        return LoadStatus.Ok;
    }

    private static BlockDef OverlayBlock() => new("OVLY", "оверлей маршрутизации: раскладка входов/выходов, матрицы, курсор волны", new[]
    {
        Col.Scalar<int>(1, "Levels", w => w.Overlay!.Levels, (c, v) => v == c.W.Routing.Levels ? LoadStatus.Ok : LoadStatus.InvalidValue),
        Col.Scalar<float>(2, "OriginX", w => w.Overlay!.Origin.X, (c, v) => c.OvOriginX = v),
        Col.Scalar<float>(3, "OriginZ", w => w.Overlay!.Origin.Z, (c, v) => c.OvOriginZ = v),
        Col.Scalar<int>(4, "Nx1", w => w.Overlay!.LevelNx[1], (c, v) => c.OvNx1 = v),
        Col.Scalar<int>(5, "Nz1", w => w.Overlay!.LevelNz[1], (c, v) => c.OvNz1 = v),
        Col.Scalar<int>(6, "EntriesLen", w => w.Overlay!.EntriesLength, (c, v) => c.OvEntriesLen = v),
        Col.Scalar<int>(7, "ExitsLen", w => w.Overlay!.ExitsLength, (c, v) => c.OvExitsLen = v),
        Col.Scalar<int>(8, "MatrixLen", w => w.Overlay!.MatrixLength, (c, v) => c.OvMatrixLen = v),
        Col.Scalar<int>(9, "BuiltNodes", w => w.Overlay!.BuiltNodes, (c, v) => c.OvBuiltNodes = v),
        Col.Scalar<int>(10, "BuiltDirSegs", w => w.Overlay!.BuiltDirSegs, (c, v) => c.OvBuiltDirSegs = v),
        Col.Scalar<int>(11, "WeightsVersion", w => w.Overlay!.WeightsVersion, (c, v) => c.OvWeightsVersion = v),
        Col.Flag(12, "Disabled", w => w.Overlay!.Disabled, (c, v) => c.OvDisabled = v),
        Col.Scalar<long>(13, "Rebuilds", w => w.Overlay!.Rebuilds, (c, v) => c.OvRebuilds = v),
        Col.Flag(14, "WaveActive", w => w.Overlay!.WaveActive, (c, v) => c.OvWaveActive = v),
        Col.Scalar<int>(15, "WaveCell", w => w.Overlay!.WaveCell, (c, v) => c.OvWaveCell = v),
        Col.Scalar<int>(16, "WaveRow", w => w.Overlay!.WaveRow, (c, v) => c.OvWaveRow = v),
        Col.Scalar<long>(17, "CustomizeRows", w => w.Overlay!.CustomizeRows, (c, v) => c.OvCustomizeRows = v),
        Col.Scalar<long>(18, "CustomizeWork", w => w.Overlay!.CustomizeWork, (c, v) => c.OvCustomizeWork = v),
        Col.Scalar<long>(19, "FullRows", w => w.Overlay!.FullRows, (c, v) => c.OvFullRows = v),
        Col.Scalar<long>(20, "FullWork", w => w.Overlay!.FullWork, (c, v) =>
        {
            c.OvFullWork = v;
            return FinishOverlayScalars(c);
        }),
        Col.Array<int>(21, "EntryCount", w => w.Overlay!.EntryCount, _ovCells, "CellCap"),
        Col.Array<int>(22, "EntryCap", w => w.Overlay!.EntryCap, _ovCells, "CellCap"),
        Col.Array<int>(23, "ExitCount", w => w.Overlay!.ExitCount, _ovCells, "CellCap"),
        Col.Array<int>(24, "ExitCap", w => w.Overlay!.ExitCap, _ovCells, "CellCap"),
        Col.Array<byte>(25, "Dirty", w => w.Overlay!.Dirty, _ovCells, "CellCap"),
        Col.Custom(
            26,
            "Entries",
            ColumnType.I32,
            4,
            "Σ EntryCap",
            "0",
            w => w.Overlay!.EntrySlots(),
            (w, first, n, dst) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<int>(w.Overlay!.Entries, (int)first, n)).CopyTo(dst),
            (c, n, src) => StoreSlots(c.W.Overlay!.Entries, n, src)),
        Col.Custom(
            27,
            "Exits",
            ColumnType.I32,
            4,
            "Σ ExitCap",
            "0",
            w => w.Overlay!.ExitSlots(),
            (w, first, n, dst) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<int>(w.Overlay!.Exits, (int)first, n)).CopyTo(dst),
            (c, n, src) => StoreSlots(c.W.Overlay!.Exits, n, src)),
        Col.Custom(
            28,
            "Matrix",
            ColumnType.F32,
            4,
            "Σ EntryCap · ExitCap",
            "+inf",
            w => w.Overlay!.MatrixSlots(),
            (w, first, n, dst) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<float>(w.Overlay!.Matrix, (int)first, n)).CopyTo(dst),
            (c, n, src) =>
            {
                float[] a = c.W.Overlay!.Matrix;
                if (n > a.Length)
                {
                    return LoadStatus.CapacityExceeded;
                }

                src.CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new Span<float>(a, 0, (int)n)));
                return LoadStatus.Ok;
            }),
        Col.Array<float>(29, "CustomizedSegCost", w => w.Overlay!.CustomizedSegCost, w => w.Overlay!.BuiltDirSegs, "BuiltDirSegs"),
    })
    {
        After = c => c.W.Overlay!.FinishRestore(),
        PresentText = "routing.levels > 0",
        Present = static w => w.Overlay != null,
    };

    private static LoadStatus StoreSlots(int[] a, long n, ReadOnlySpan<byte> src)
    {
        if (n > a.Length)
        {
            return LoadStatus.CapacityExceeded;
        }

        src.CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new Span<int>(a, 0, (int)n)));
        return LoadStatus.Ok;
    }

    private static BlockDef LodBlock() => new("LODS", "LOD: сетка, уровни ячеек, слоты фокуса, уровни машин", new[]
    {
        Col.Scalar<float>(1, "OriginX", w => w.Lod!.OriginX, (c, v) => c.LodOriginX = v),
        Col.Scalar<float>(2, "OriginZ", w => w.Lod!.OriginZ, (c, v) => c.LodOriginZ = v),
        Col.Scalar<int>(3, "CellsX", w => w.Lod!.CellsX, (c, v) => c.LodCellsX = v),
        Col.Scalar<int>(4, "CellsZ", w => w.Lod!.CellsZ, (c, v) =>
        {
            c.LodCellsZ = v;
            LoadStatus st = LodCells.TryRestore(c.W.Net, c.W.LodCfg.CellM, c.LodOriginX, c.LodOriginZ, c.LodCellsX, v, out LodCells? cells);
            if (st != LoadStatus.Ok)
            {
                return st;
            }

            WorldBuilder.AttachLod(c.W, cells);
            return LoadStatus.Ok;
        }),
        Col.Flag(5, "LodEvalRequested", w => w.Lod!.LodEvalRequested, (c, v) => c.W.Lod!.LodEvalRequested = v),
        Col.Scalar<long>(6, "LastLodEvalTick", w => w.Lod!.LastLodEvalTick, (c, v) => c.W.Lod!.LastLodEvalTick = v),
        Col.Array<byte>(7, "CellLevel", w => w.Lod!.CellLevel, _lodCellsN, "Cells"),
        Col.Array<long>(8, "CellHoldUntil", w => w.Lod!.CellHoldUntil, _lodCellsN, "Cells"),
        Col.Array<byte>(9, "CellCongStreak", w => w.Lod!.CellCongStreak, _lodCellsN, "Cells"),
        Col.Field<LodFocusSlot>(10, "FocusKind", w => w.Lod!.Focus, static _ => LodCells.SlotCount, "8", nameof(LodFocusSlot.Kind)),
        Col.Field<LodFocusSlot>(11, "FocusCx0", w => w.Lod!.Focus, static _ => LodCells.SlotCount, "8", nameof(LodFocusSlot.Cx0)),
        Col.Field<LodFocusSlot>(12, "FocusCz0", w => w.Lod!.Focus, static _ => LodCells.SlotCount, "8", nameof(LodFocusSlot.Cz0)),
        Col.Field<LodFocusSlot>(13, "FocusCx1", w => w.Lod!.Focus, static _ => LodCells.SlotCount, "8", nameof(LodFocusSlot.Cx1)),
        Col.Field<LodFocusSlot>(14, "FocusCz1", w => w.Lod!.Focus, static _ => LodCells.SlotCount, "8", nameof(LodFocusSlot.Cz1)),
        Col.Field<LodFocusSlot>(15, "FocusCenterCx", w => w.Lod!.Focus, static _ => LodCells.SlotCount, "8", nameof(LodFocusSlot.CenterCx)),
        Col.Field<LodFocusSlot>(16, "FocusCenterCz", w => w.Lod!.Focus, static _ => LodCells.SlotCount, "8", nameof(LodFocusSlot.CenterCz)),
        Col.Array<byte>(17, "VehicleLod", w => w.Vehicles.Lod, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<float>(18, "MesoAllow", w => w.Vehicles.MesoAllow, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<float>(19, "MesoNext", w => w.Vehicles.MesoNext, _vehicleHw, "HighWater", "-1", _vehPool, -1f),
    })
    {
        After = SaveValidator.ValidateLod,
        PresentText = "lod.mode != off",
        Present = static w => w.Lod != null,
    };

    private static BlockDef QueueMesoBlock() => new("QMSO", "Q-meso: время въезда/готовности машин, колесо таймеров, события полос", new[]
    {
        Col.Scalar<long>(1, "PendingDistMm", w => w.Queue!.PendingDistMm, (c, v) => c.W.Queue!.PendingDistMm = v),
        Col.Scalar<long>(2, "Events", w => w.Queue!.Events, (c, v) => c.W.Queue!.Events = v),
        Col.Scalar<long>(3, "Wakes", w => w.Queue!.Wakes, (c, v) => c.W.Queue!.Wakes = v),
        Col.Array<int>(4, "QEnter", w => w.Queue!.QEnter, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<int>(5, "QReady", w => w.Queue!.QReady, _vehicleHw, "HighWater", "0", _vehPool),
        Col.Array<int>(6, "WheelTick", w => w.Queue!.WheelTickArray, _vehicleHw, "HighWater", "-1", _vehPool, -1),
        Col.Array<int>(7, "LaneLastExit", w => w.Queue!.LaneLastExit, _laneN, "LaneCount"),
    })
    {
        After = c =>
        {
            LoadStatus st = SaveValidator.ValidateQueueMeso(c);
            if (st == LoadStatus.Ok)
            {
                c.W.Queue!.RestoreDerived();
            }

            return st;
        },
        PresentText = "lod.meso_model = queue",
        Present = static w => w.Queue != null,
    };
}
