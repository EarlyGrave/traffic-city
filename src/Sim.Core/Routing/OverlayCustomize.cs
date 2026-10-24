using System;

namespace Sim.Core.Routing;

// Кастомизация оверлея (routing.md 9.3): строка матрицы = одна Дейкстра внутри ячейки (h = 0), полная кастомизация в Create,
// волна по бюджету релаксаций после пересчёта весов. Единица работы - релаксация (дуга графа или элемент матрицы подъячейки).
internal sealed partial class Overlay
{
    private readonly uint[] _stamp;          // [DirSegCap] == _cur -> _gv валиден
    private readonly float[] _gv;            // [DirSegCap]
    private readonly IndexedHeap4 _heap;
    private uint _cur;

    /// <summary>Волна идёт: есть грязные ячейки (хэшируется вместе с курсором).</summary>
    public bool WaveActive;

    /// <summary>Курсор волны: глобальный id ячейки и номер строки (уровень ℓ выводится из id, порядок ячеек - по возрастанию уровня).</summary>
    public int WaveCell;

    public int WaveRow;

    /// <summary>Строк и релаксаций, выполненных волнами (<c>CustomizeRows</c>, <c>CustomizeWork</c>).</summary>
    public long CustomizeRows;

    public long CustomizeWork;

    /// <summary>Строки и релаксации полных кастомизаций (Create и перестройки после BuildRoad) - отдельно от волн.</summary>
    public long FullRows;

    public long FullWork;

    // ---- полная кастомизация ----

    /// <summary>Пересчитывает все матрицы по уровням снизу вверх по текущим весам графа; ячейки чистые, волна остановлена.</summary>
    public void CustomizeAll()
    {
        for (int l = 1; l <= Levels; l++)
        {
            for (int gc = CellBase[l]; gc < CellBase[l + 1]; gc++)
            {
                int rows = EntryCount[gc];
                for (int r = 0; r < rows; r++)
                {
                    FullWork += CustomizeRow(gc, l, r);
                    FullRows++;
                }

                Dirty[gc] = 0;
            }
        }

        int n = _g.DirSegCount;
        for (int u = 0; u < n; u++)
        {
            CustomizedSegCost[u] = _g.SegCost[u];
        }

        WaveActive = false;
        WaveCell = 0;
        WaveRow = 0;
    }

    // ---- волна ----

    /// <summary>
    /// Вызывается после <see cref="RouteGraph.RecalcCosts"/>: сегмент, чей вес ушёл от считанного больше чем на
    /// <c>customize_eps</c>, делает грязной ячейку уровня 1 начала сегмента (вес <c>SegCost(x)</c> входит только в ячейку, где
    /// сегмент - выход) и её предков; волна стартует с начала.
    /// </summary>
    public void AfterRecalc(int weightsVersion)
    {
        WeightsVersion = weightsVersion;
        if (!Disabled)
        {
            MarkChanged(false);
        }
    }

    // Пометка по порогу + синхронизация CustomizedSegCost для сегментов грязных ячеек уровня 1 (веса во время волны не меняются,
    // поэтому значение, по которому ячейка будет считана, известно сразу). force - есть грязные ячейки помимо порога (BuildRoad).
    private void MarkChanged(bool force)
    {
        bool any = force;
        int n = _g.DirSegCount;
        for (int u = 0; u < n; u++)
        {
            float c = CustomizedSegCost[u];
            if (MathF.Abs(_g.SegCost[u] - c) > _eps * c)
            {
                MarkAncestors(_g.StartNode[u]);
                any = true;
            }
        }

        for (int u = 0; u < n; u++)
        {
            if (Dirty[CellOfNode(1, _g.StartNode[u])] != 0)
            {
                CustomizedSegCost[u] = _g.SegCost[u];
            }
        }

        if (any || WaveActive)
        {
            WaveActive = true;
            WaveCell = 0;
            WaveRow = 0;
        }
    }

    // Ячейка узла на всех уровнях - грязная (ячейка и её предки).
    private void MarkAncestors(int node)
    {
        for (int l = 1; l <= Levels; l++)
        {
            Dirty[CellOfNode(l, node)] = 1;
        }
    }

    /// <summary>
    /// Строки волны, пока работа в этом тике &lt; <paramref name="budget"/> (0 = без ограничения); проверка между строками, минимум
    /// одна строка за вызов, пока волна идёт. Порядок: ячейки по возрастанию id (= уровень ↑, id ↑), строки по возрастанию.
    /// Возвращает работу тика.
    /// </summary>
    public int RunWave(int budget)
    {
        int work = 0;
        while (true)
        {
            Settle();
            if (!WaveActive || (budget > 0 && work >= budget))
            {
                break;
            }

            int gc = WaveCell;
            work += CustomizeRow(gc, LevelOf(gc), WaveRow);
            CustomizeRows++;
            if (++WaveRow >= EntryCount[gc])
            {
                Dirty[gc] = 0;
                WaveCell = gc + 1;
                WaveRow = 0;
            }
        }

        CustomizeWork += work;
        return work;
    }

    // Курсор - на ближайшую грязную ячейку с входами; нет такой - волна закончена.
    private void Settle()
    {
        if (!WaveActive)
        {
            return;
        }

        while (WaveCell < CellCap)
        {
            if (Dirty[WaveCell] != 0)
            {
                if (EntryCount[WaveCell] > 0)
                {
                    return;
                }

                Dirty[WaveCell] = 0; // нет входов - нечего считать
            }

            WaveCell++;
            WaveRow = 0;
        }

        WaveActive = false;
        WaveCell = 0;
        WaveRow = 0;
    }

    // ---- строка матрицы ----

    // Строка r ячейки gc уровня level: Дейкстра из входа e (G = 0), записывает стоимости до выходов ячейки. Возвращает работу.
    private int CustomizeRow(int gc, int level, int r)
    {
        int e = Entries[EntryFirst[gc] + r];
        int exitCap = ExitCap[gc];
        int rowBase = MatrixFirst[gc] + (r * exitCap);
        Array.Fill(Matrix, Inf, rowBase, exitCap);
        _cur++;
        if (_cur == 0)
        {
            Array.Clear(_stamp);
            _cur = 1;
        }

        int work = level == 1 ? ExpandBase(e, 0f, rowBase) : ExpandUp(level, e, 0f, rowBase);
        while (_heap.Count > 0)
        {
            int v = _heap.Pop(out float gv);
            work += level == 1 ? ExpandBase(v, gv, rowBase) : ExpandUp(level, v, gv, rowBase);
        }

        return work;
    }

    // Уровень 1: дуги графа DirSeg. Вершина с CutLevel >= 1 - выход ячейки (записать, не раскрывать), иначе внутренняя.
    private int ExpandBase(int v, float gv, int rowBase)
    {
        int first = _g.ArcFirst[v];
        int end = _g.ArcFirst[v + 1];
        for (int k = first; k < end; k++)
        {
            int w = _g.ArcTo[k];
            float nd = gv + _g.ArcCost[k] + _g.SegCost[w];
            if (CutLevel[w] >= 1)
            {
                int idx = ExitIdx[w];
                if (idx >= 0 && nd < Matrix[rowBase + idx])
                {
                    Matrix[rowBase + idx] = nd;
                }
            }
            else
            {
                Relax(w, nd);
            }
        }

        return end - first;
    }

    // Уровень ℓ >= 2: дуги - строка v матрицы подъячейки S = cell_{ℓ-1}(EndNode v) ко всем выходам S.
    // Выход S с CutLevel >= ℓ - выход этой ячейки (записать), иначе - вход соседней подъячейки внутри неё (продолжить).
    private int ExpandUp(int level, int v, float gv, int rowBase)
    {
        int dirCap = _g.DirSegCapacity;
        int s = CellOfNode(level - 1, _g.EndNode[v]);
        int ri = EntryIdx[((level - 2) * dirCap) + v];
        if (ri < 0)
        {
            return 1; // по построению не бывает: v - вход подъячейки
        }

        int mb = MatrixFirst[s] + (ri * ExitCap[s]);
        int xf = ExitFirst[s];
        int xn = ExitCount[s];
        for (int c = 0; c < xn; c++)
        {
            float cost = Matrix[mb + c];
            if (float.IsPositiveInfinity(cost))
            {
                continue;
            }

            int x = Exits[xf + c];
            float nd = gv + cost;
            if (CutLevel[x] >= level)
            {
                int idx = ExitIdx[((level - 1) * dirCap) + x];
                if (idx >= 0 && nd < Matrix[rowBase + idx])
                {
                    Matrix[rowBase + idx] = nd;
                }
            }
            else
            {
                Relax(x, nd);
            }
        }

        return xn;
    }

    private void Relax(int w, float nd)
    {
        if (_stamp[w] == _cur && nd >= _gv[w])
        {
            return;
        }

        _stamp[w] = _cur;
        _gv[w] = nd;
        _heap.PushOrDecrease(w, nd);
    }
}
