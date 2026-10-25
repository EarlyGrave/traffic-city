using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Routing;

/// <summary>
/// Многоуровневый оверлей ячеек (CRP, routing.md 9.1-9.3, ADR-0009). Уровень 1 - квадраты <c>cell_m</c> по bounding box сети,
/// уровень ℓ+1 - <c>fanout x fanout</c> ячеек уровня ℓ. У ячейки входы/выходы (граничные DirSeg) и плотная матрица
/// <c>вход x выход</c> стоимостей кратчайшего пути внутри ячейки. Плоские массивы без ссылок между объектами; всё выделяется в
/// конструкторе, <see cref="OnNetworkGrown"/>, <see cref="AfterRecalc"/> и <see cref="RunWave"/> не аллоцируют.
/// Кастомизация и волна - <c>OverlayCustomize.cs</c>.
/// </summary>
internal sealed partial class Overlay
{
    public const int MaxLevels = 4;
    public const int MaxFanout = 16;

    /// <summary>Предел числа ячеек всех уровней (защита от слишком мелкого <c>cell_m</c>).</summary>
    public const int MaxCells = 1 << 22;

    /// <summary>Нет пути.</summary>
    public const float Inf = float.PositiveInfinity;

    private readonly NetworkStore _net;
    private readonly RouteGraph _g;

    public readonly int Levels;
    public readonly int Fanout;
    public readonly float CellSize;
    public readonly Vec2 Origin;
    public readonly int CellCap;                 // ячеек всех уровней

    public readonly int[] LevelNx;               // [MaxLevels + 2] индекс - уровень (1..Levels)
    public readonly int[] LevelNz;
    public readonly int[] CellBase;              // CellBase[Levels + 1] = CellCap
    public readonly int[] LevelDiv;              // Fanout^(ℓ-1)
    public readonly int[] NodeCellX;             // [MaxNodes] ячейка уровня 1
    public readonly int[] NodeCellZ;
    public readonly byte[] CutLevel;             // [DirSegCap]

    public readonly int[] EntryFirst;            // [CellCap] диапазон в Entries
    public readonly int[] EntryCount;
    public readonly int[] EntryCap;
    public readonly int[] ExitFirst;             // [CellCap] диапазон в Exits
    public readonly int[] ExitCount;
    public readonly int[] ExitCap;
    public readonly int[] MatrixFirst;           // [CellCap] строка r, столбец c -> Matrix[MatrixFirst + r * ExitCap + c]
    public readonly byte[] Dirty;                // [CellCap] 0/1: ждёт кастомизации
    public readonly int[] Entries;               // [BoundaryCap] DirSeg
    public readonly int[] Exits;
    public readonly float[] Matrix;              // [MatrixCap] с, +inf = нет пути
    public readonly int[] EntryIdx;              // [Levels * DirSegCap] локальный индекс u во входах ячейки уровня ℓ или -1
    public readonly int[] ExitIdx;               // то же для выходов
    public readonly float[] CustomizedSegCost;   // [DirSegCap] SegCost, по которому считана ячейка (порог грязи)

    /// <summary>Как <see cref="RouteGraph.WeightsVersion"/> на момент последнего пересчёта весов (хэшируется).</summary>
    public int WeightsVersion;

    /// <summary>Оверлей выключен (не влезла перестройка после BuildRoad): запросы идут плоским A*.</summary>
    public bool Disabled;

    /// <summary>Полных перестроек после BuildRoad (<c>OverlayRebuilds</c>).</summary>
    public long Rebuilds;

    private readonly float _eps;
    private readonly bool _failed;
    private int _builtNodes;
    private int _builtDirSegs;

    private Overlay(NetworkStore net, RouteGraph g, in RoutingDesc cfg, Vec2 origin, int nx1, int nz1, int cells, in OverlayRestore? restore = null)
    {
        _net = net;
        _g = g;
        Levels = cfg.Levels;
        Fanout = cfg.Fanout;
        CellSize = cfg.CellSize;
        Origin = origin;
        _eps = cfg.CustomizeEps;
        CellCap = cells;

        LevelNx = new int[MaxLevels + 2];
        LevelNz = new int[MaxLevels + 2];
        CellBase = new int[MaxLevels + 2];
        LevelDiv = new int[MaxLevels + 2];
        int div = 1;
        int b = 0;
        for (int l = 1; l <= Levels; l++)
        {
            LevelDiv[l] = div;
            LevelNx[l] = (nx1 + div - 1) / div;
            LevelNz[l] = (nz1 + div - 1) / div;
            CellBase[l] = b;
            b += LevelNx[l] * LevelNz[l];
            div *= Fanout;
        }

        CellBase[Levels + 1] = b;

        int dirCap = g.DirSegCapacity;
        NodeCellX = new int[net.Nodes.Length];
        NodeCellZ = new int[net.Nodes.Length];
        CutLevel = new byte[dirCap];
        CustomizedSegCost = new float[dirCap];
        EntryIdx = new int[Levels * dirCap];
        ExitIdx = new int[Levels * dirCap];
        EntryFirst = new int[cells];
        EntryCount = new int[cells];
        EntryCap = new int[cells];
        ExitFirst = new int[cells];
        ExitCount = new int[cells];
        ExitCap = new int[cells];
        MatrixFirst = new int[cells];
        Dirty = new byte[cells];

        _stamp = new uint[dirCap];
        _gv = new float[dirCap];
        _heap = new IndexedHeap4(dirCap);

        if (restore is { } rs)
        {
            // Загрузка сейва: размеры общих массивов - как у живого мира (запас растёт вместе с историей), раскладку и матрицы заполняют столбцы блока.
            Entries = new int[rs.EntriesLen];
            Exits = new int[rs.ExitsLen];
            Matrix = new float[rs.MatrixLen];
            _builtNodes = rs.BuiltNodes;
            _builtDirSegs = rs.BuiltDirSegs;
            return;
        }

        AssignNodes(0, net.NodeCount);
        ComputeCuts(0, g.DirSegCount);
        _builtNodes = net.NodeCount;
        _builtDirSegs = g.DirSegCount;

        CountBoundary();
        ComputeCaps(out long eTot, out long xTot, out long mTot);
        // Запас под перестройки после BuildRoad (routing.md 9.2): раскладка с новыми запасами должна влезать в те же массивы.
        // x1.5 + фиксированная добавка (256 КБ матриц): у маленьких сетей матрица растёт квадратично от числа входов/выходов.
        long eCap = eTot + (eTot / 2) + 1024;
        long xCap = xTot + (xTot / 2) + 1024;
        long mCap = mTot + (mTot / 2) + 65536;
        if (eCap > int.MaxValue / 2 || xCap > int.MaxValue / 2 || mCap > int.MaxValue / 2)
        {
            _failed = true;
            Entries = Array.Empty<int>();
            Exits = Array.Empty<int>();
            Matrix = Array.Empty<float>();
            return;
        }

        Entries = new int[eCap];
        Exits = new int[xCap];
        Matrix = new float[mCap];
        Place();
    }

    /// <summary>
    /// Строит оверлей по текущей сети и графу и кастомизирует все ячейки (routing.md 9.3 "Create"). Граф должен быть
    /// актуальным (<see cref="RouteGraph.Rebuild"/>); веса - начальные. Вызывается в <c>Create</c>, аллокации допустимы.
    /// </summary>
    public static CreateStatus TryCreate(NetworkStore net, RouteGraph g, in RoutingDesc cfg, out Overlay? overlay)
    {
        overlay = null;
        if (g.DirSegCapacity >= (1 << OverlaySearcher.LevelShift))
        {
            return CreateStatus.InvalidCapacity; // план кодирует DirSeg | level << 28 (routing.md 9.2)
        }

        if (cfg.Levels < 1 || cfg.Levels > MaxLevels || cfg.Fanout < 2 || cfg.Fanout > MaxFanout ||
            !float.IsFinite(cfg.CellSize) || cfg.CellSize <= 0f || !float.IsFinite(cfg.CustomizeEps) || cfg.CustomizeEps < 0f)
        {
            return CreateStatus.InvalidCapacity;
        }

        float minX = 0f, minZ = 0f, maxX = 0f, maxZ = 0f;
        for (int n = 0; n < net.NodeCount; n++)
        {
            Vec2 p = net.Nodes[n].Position;
            if (n == 0)
            {
                minX = maxX = p.X;
                minZ = maxZ = p.Z;
                continue;
            }

            minX = MathF.Min(minX, p.X);
            maxX = MathF.Max(maxX, p.X);
            minZ = MathF.Min(minZ, p.Z);
            maxZ = MathF.Max(maxZ, p.Z);
        }

        double nx = Math.Max(1.0, Math.Ceiling((maxX - minX) / cfg.CellSize));
        double nz = Math.Max(1.0, Math.Ceiling((maxZ - minZ) / cfg.CellSize));
        if (nx * nz > MaxCells)
        {
            return CreateStatus.InvalidCapacity;
        }

        int nx1 = (int)nx;
        int nz1 = (int)nz;
        long cells = 0;
        long div = 1;
        for (int l = 1; l <= cfg.Levels; l++)
        {
            cells += ((nx1 + div - 1) / div) * ((nz1 + div - 1) / div);
            div *= cfg.Fanout;
        }

        if (cells > MaxCells)
        {
            return CreateStatus.InvalidCapacity;
        }

        var ov = new Overlay(net, g, in cfg, new Vec2(minX, minZ), nx1, nz1, (int)cells);
        if (ov._failed)
        {
            return CreateStatus.InvalidCapacity;
        }

        ov.CustomizeAll();
        ov.WeightsVersion = g.WeightsVersion;
        overlay = ov;
        return CreateStatus.Ok;
    }

    // ---- сейв (Save/): оверлей загружается с сохранённой раскладкой и матрицами, без кастомизации (на s1m она занимает секунды) ----

    /// <summary>Параметры, без которых нельзя выделить массивы оверлея при загрузке.</summary>
    internal readonly record struct OverlayRestore(float OriginX, float OriginZ, int Nx1, int Nz1, int EntriesLen, int ExitsLen, int MatrixLen, int BuiltNodes, int BuiltDirSegs);

    /// <summary>Заготовка оверлея для загрузки: массивы выделены, раскладка и матрицы пусты; дальше столбцы блока и <see cref="FinishRestore"/>.</summary>
    internal static LoadStatus TryRestore(NetworkStore net, RouteGraph g, in RoutingDesc cfg, in OverlayRestore r, out Overlay? overlay)
    {
        overlay = null;
        if (cfg.Levels < 1 || cfg.Levels > MaxLevels || cfg.Fanout < 2 || cfg.Fanout > MaxFanout || !float.IsFinite(cfg.CellSize) || cfg.CellSize <= 0f ||
            !float.IsFinite(cfg.CustomizeEps) || cfg.CustomizeEps < 0f || g.DirSegCapacity >= (1 << OverlaySearcher.LevelShift) ||
            !float.IsFinite(r.OriginX) || !float.IsFinite(r.OriginZ) || r.Nx1 < 1 || r.Nz1 < 1 || (long)r.Nx1 * r.Nz1 > MaxCells ||
            r.EntriesLen < 0 || r.ExitsLen < 0 || r.MatrixLen < 0 || r.EntriesLen > int.MaxValue / 2 || r.ExitsLen > int.MaxValue / 2 ||
            r.MatrixLen > int.MaxValue / 2 || r.BuiltNodes < 0 || r.BuiltNodes > net.NodeCount || r.BuiltDirSegs < 0 || r.BuiltDirSegs > g.DirSegCount)
        {
            return LoadStatus.InvalidValue;
        }

        long cells = 0;
        long div = 1;
        for (int l = 1; l <= cfg.Levels; l++)
        {
            cells += ((r.Nx1 + div - 1) / div) * ((r.Nz1 + div - 1) / div);
            div *= cfg.Fanout;
        }

        if (cells > MaxCells)
        {
            return LoadStatus.InvalidValue;
        }

        overlay = new Overlay(net, g, in cfg, new Vec2(r.OriginX, r.OriginZ), r.Nx1, r.Nz1, (int)cells, r);
        return LoadStatus.Ok;
    }

    /// <summary>
    /// Завершение загрузки после заполнения столбцов: смещения входов/выходов/матриц - префиксные суммы запасов (раскладка плотная), производные
    /// узел->ячейка, уровень разреза, индексы входов/выходов. Проверяет согласованность (счётчики не больше запасов, суммы помещаются в массивы,
    /// входы - допустимые DirSeg по возрастанию). Выключенный оверлей (<see cref="Disabled"/>) массивы раскладки не использует: проверка пропускается.
    /// </summary>
    internal LoadStatus FinishRestore()
    {
        AssignNodes(0, _builtNodes);
        ComputeCuts(0, _builtDirSegs, syncCost: false);
        if (Disabled)
        {
            return LoadStatus.Ok;
        }

        long eo = 0, xo = 0, mo = 0;
        for (int c = 0; c < CellCap; c++)
        {
            if (EntryCount[c] < 0 || ExitCount[c] < 0 || EntryCap[c] < EntryCount[c] || ExitCap[c] < ExitCount[c] || EntryCap[c] > Entries.Length ||
                ExitCap[c] > Exits.Length)
            {
                return LoadStatus.InvalidValue;
            }

            EntryFirst[c] = (int)eo;
            ExitFirst[c] = (int)xo;
            MatrixFirst[c] = (int)Math.Min(mo, int.MaxValue);
            eo += EntryCap[c];
            xo += ExitCap[c];
            mo += (long)EntryCap[c] * ExitCap[c];
            if (eo > Entries.Length || xo > Exits.Length || mo > Matrix.Length)
            {
                return LoadStatus.InvalidValue;
            }
        }

        Array.Fill(EntryIdx, -1);
        Array.Fill(ExitIdx, -1);
        int dirCap = _g.DirSegCapacity;
        int dirs = _g.DirSegCount;
        for (int c = 0; c < CellCap; c++)
        {
            int l = LevelOf(c);
            for (int k = 0; k < EntryCount[c]; k++)
            {
                int u = Entries[EntryFirst[c] + k];
                if ((uint)u >= (uint)dirs)
                {
                    return LoadStatus.InvalidValue;
                }

                EntryIdx[((l - 1) * dirCap) + u] = k;
            }

            for (int k = 0; k < ExitCount[c]; k++)
            {
                int u = Exits[ExitFirst[c] + k];
                if ((uint)u >= (uint)dirs)
                {
                    return LoadStatus.InvalidValue;
                }

                ExitIdx[((l - 1) * dirCap) + u] = k;
            }
        }

        return LoadStatus.Ok;
    }

    /// <summary>Размеры общих массивов раскладки (для сейва).</summary>
    internal int EntriesLength => Entries.Length;

    internal int ExitsLength => Exits.Length;

    internal int MatrixLength => Matrix.Length;

    internal int BuiltNodes => _builtNodes;

    internal int BuiltDirSegs => _builtDirSegs;

    /// <summary>Σ запасов входов (длина записанной части <see cref="Entries"/>); у выключенного оверлея раскладки нет.</summary>
    internal long EntrySlots()
    {
        if (Disabled)
        {
            return 0;
        }

        long n = 0;
        for (int c = 0; c < CellCap; c++)
        {
            n += EntryCap[c];
        }

        return n;
    }

    internal long ExitSlots()
    {
        if (Disabled)
        {
            return 0;
        }

        long n = 0;
        for (int c = 0; c < CellCap; c++)
        {
            n += ExitCap[c];
        }

        return n;
    }

    internal long MatrixSlots()
    {
        if (Disabled)
        {
            return 0;
        }

        long n = 0;
        for (int c = 0; c < CellCap; c++)
        {
            n += (long)EntryCap[c] * ExitCap[c];
        }

        return n;
    }

    // ---- геометрия ----

    /// <summary>Глобальный id ячейки уровня <paramref name="level"/> (1..Levels), содержащей узел.</summary>
    public int CellOfNode(int level, int node)
    {
        int div = LevelDiv[level];
        return CellBase[level] + (NodeCellZ[node] / div * LevelNx[level]) + (NodeCellX[node] / div);
    }

    /// <summary>
    /// Стоимость дуги оверлея уровня <paramref name="level"/> <c>a -> b</c> (клетка матрицы ячейки конца <c>a</c>), с; <c>+inf</c> - нет пути или
    /// <c>a</c>/<c>b</c> не вход/выход ячейки (маршрутизация: остаток стоимости плана, routing.md 9.7).
    /// </summary>
    public float ArcCostAt(int level, int a, int b)
    {
        int dirCap = _g.DirSegCapacity;
        int c = CellOfNode(level, _g.EndNode[a]);
        int r = EntryIdx[((level - 1) * dirCap) + a];
        int x = ExitIdx[((level - 1) * dirCap) + b];
        if (r < 0 || x < 0)
        {
            return Inf;
        }

        return Matrix[MatrixFirst[c] + (r * ExitCap[c]) + x];
    }

    /// <summary>Уровень (1..Levels) ячейки с глобальным id.</summary>
    public int LevelOf(int gc)
    {
        int l = 1;
        while (l < Levels && gc >= CellBase[l + 1])
        {
            l++;
        }

        return l;
    }

    private void AssignNodes(int from, int to)
    {
        int nx1 = LevelNx[1];
        int nz1 = LevelNz[1];
        for (int n = from; n < to; n++)
        {
            Vec2 p = _net.Nodes[n].Position;
            NodeCellX[n] = ClampCell(MathF.Floor((p.X - Origin.X) / CellSize), nx1);
            NodeCellZ[n] = ClampCell(MathF.Floor((p.Z - Origin.Z) / CellSize), nz1);
        }
    }

    private static int ClampCell(float f, int n) => !(f > 0f) ? 0 : (f >= n ? n - 1 : (int)f);

    private void ComputeCuts(int from, int to, bool syncCost = true)
    {
        for (int u = from; u < to; u++)
        {
            ref readonly SegmentData sd = ref _net.Segments[u >> 1];
            int lanes = (u & 1) == 0 ? sd.LanesAB : sd.LanesBA;
            byte cut = 0;
            if (lanes > 0)
            {
                int a = _g.StartNode[u];
                int b = _g.EndNode[u];
                for (int l = Levels; l >= 1; l--)
                {
                    if (CellOfNode(l, a) != CellOfNode(l, b))
                    {
                        cut = (byte)l;
                        break;
                    }
                }
            }

            CutLevel[u] = cut;
            if (syncCost)
            {
                CustomizedSegCost[u] = _g.SegCost[u];
            }
        }
    }

    // ---- раскладка входов/выходов ----

    private void CountBoundary()
    {
        Array.Clear(EntryCount);
        Array.Clear(ExitCount);
        int n = _g.DirSegCount;
        for (int u = 0; u < n; u++)
        {
            int cut = CutLevel[u];
            for (int l = 1; l <= cut; l++)
            {
                EntryCount[CellOfNode(l, _g.EndNode[u])]++;
                ExitCount[CellOfNode(l, _g.StartNode[u])]++;
            }
        }
    }

    /// <summary>Минимальный запас строк/столбцов ячейки под <c>BuildRoad</c> (routing.md 9.2): после любой операции <c>Cap &gt;= Count + MinSlack</c>.</summary>
    internal const int MinSlack = 4;

    /// <summary>
    /// Канонический запас строки/столбца ячейки (routing.md 9.2, save-format.md §13 п. 5): наименьшая ступенька лестницы 4, 8, 12, 16, 20, ..., 32, 40, ..., 64,
    /// 80, ... не меньшая <c>count + MinSlack</c> (шаг ступеньки: 4 до 16, дальше четверть степени двойки, то есть запас не больше <c>max(4, count / 4)</c>
    /// сверх минимума). Зависит только от числа входов/выходов, а не от истории роста сети: раскладка живого мира после <c>BuildRoad</c> (дописывание
    /// в запас; нехватка запаса - полная перестройка) совпадает с раскладкой, построенной заново по той же сети.
    /// </summary>
    internal static int CapFor(int count)
    {
        int need = count + MinSlack;
        if (need <= 16)
        {
            return (need + 3) & ~3;
        }

        int step = 1 << (System.Numerics.BitOperations.Log2((uint)(need - 1)) - 2);
        return (need + step - 1) / step * step;
    }

    // Запас строк/столбцов под BuildRoad: ступенька лестницы CapFor (канонический, не зависит от истории).
    private void ComputeCaps(out long entryTotal, out long exitTotal, out long matrixTotal)
    {
        long e = 0, x = 0, m = 0;
        for (int c = 0; c < CellCap; c++)
        {
            EntryCap[c] = CapFor(EntryCount[c]);
            ExitCap[c] = CapFor(ExitCount[c]);
            e += EntryCap[c];
            x += ExitCap[c];
            m += (long)EntryCap[c] * ExitCap[c];
        }

        entryTotal = e;
        exitTotal = x;
        matrixTotal = m;
    }

    // Раскладывает входы/выходы по возрастанию DirSeg и обнуляет матрицы (+inf). Ёмкость массивов уже проверена.
    private void Place()
    {
        int eo = 0, xo = 0, mo = 0;
        for (int c = 0; c < CellCap; c++)
        {
            EntryFirst[c] = eo;
            ExitFirst[c] = xo;
            MatrixFirst[c] = mo;
            eo += EntryCap[c];
            xo += ExitCap[c];
            mo += EntryCap[c] * ExitCap[c];
            EntryCount[c] = 0;
            ExitCount[c] = 0;
            Dirty[c] = 0;
        }

        Array.Fill(EntryIdx, -1);
        Array.Fill(ExitIdx, -1);
        int dirCap = _g.DirSegCapacity;
        int n = _g.DirSegCount;
        for (int u = 0; u < n; u++)
        {
            int cut = CutLevel[u];
            for (int l = 1; l <= cut; l++)
            {
                int ce = CellOfNode(l, _g.EndNode[u]);
                Entries[EntryFirst[ce] + EntryCount[ce]] = u;
                EntryIdx[((l - 1) * dirCap) + u] = EntryCount[ce]++;
                int cx = CellOfNode(l, _g.StartNode[u]);
                Exits[ExitFirst[cx] + ExitCount[cx]] = u;
                ExitIdx[((l - 1) * dirCap) + u] = ExitCount[cx]++;
            }
        }

        Array.Fill(Matrix, Inf, 0, mo);
    }

    // ---- BuildRoad (routing.md 9.3) ----

    /// <summary>
    /// После <c>BuildRoad</c> (граф уже <see cref="RouteGraph.Rebuild"/>): новые узлы - в ячейки, новые DirSeg - <c>CutLevel</c>,
    /// новые входы/выходы дописываются в свои ячейки всех уровней в пределах запаса, затронутые ячейки и их предки - грязные,
    /// волна начинается заново (порядок волны - по возрастанию уровня и id, поэтому ячейки новых вершин не ждут хвоста).
    /// Не хватило запаса - синхронная полная перестройка (<see cref="Rebuilds"/>++); не влезла - оверлей выключается.
    /// </summary>
    public void OnNetworkGrown()
    {
        if (Disabled)
        {
            return;
        }

        int nodes = _net.NodeCount;
        int dirs = _g.DirSegCount;
        AssignNodes(_builtNodes, nodes);
        int firstNew = _builtDirSegs;
        ComputeCuts(firstNew, dirs);
        _builtNodes = nodes;
        _builtDirSegs = dirs;

        bool overflow = false;
        int dirCap = _g.DirSegCapacity;
        for (int u = firstNew; u < dirs && !overflow; u++)
        {
            int a = _g.StartNode[u];
            int b = _g.EndNode[u];
            MarkAncestors(a);
            MarkAncestors(b);
            int cut = CutLevel[u];
            for (int l = 1; l <= cut; l++)
            {
                int ce = CellOfNode(l, b);
                int cx = CellOfNode(l, a);
                if (EntryCount[ce] + 1 + MinSlack > EntryCap[ce] || ExitCount[cx] + 1 + MinSlack > ExitCap[cx])
                {
                    overflow = true;
                    break;
                }

                Entries[EntryFirst[ce] + EntryCount[ce]] = u;
                EntryIdx[((l - 1) * dirCap) + u] = EntryCount[ce]++;
                Exits[ExitFirst[cx] + ExitCount[cx]] = u;
                ExitIdx[((l - 1) * dirCap) + u] = ExitCount[cx]++;
            }
        }

        if (overflow)
        {
            RebuildAll();
            return;
        }

        MarkChanged(true);
    }

    // Новая раскладка с запасом от текущих счётчиков + полная кастомизация. Не влезло в массивы - оверлей выключен.
    public void RebuildAll()
    {
        CountBoundary();
        ComputeCaps(out long e, out long x, out long m);
        if (e > Entries.Length || x > Exits.Length || m > Matrix.Length)
        {
            Disabled = true;
            WaveActive = false;
            WaveCell = 0;
            WaveRow = 0;
            return;
        }

        Place();
        Rebuilds++;
        CustomizeAll();
        WeightsVersion = _g.WeightsVersion;
    }
}
