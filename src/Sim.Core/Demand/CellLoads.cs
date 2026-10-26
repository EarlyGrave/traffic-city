using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Demand;

/// <summary>
/// Загрузка клеток по периодам (zoning-demand.md §9.1): <c>L[c, p]</c> - число машинных ног периода <c>p</c> через клетку <c>c</c> (клетки <see cref="CellLine"/> по прямой между центрами ячеек
/// дома и цели), по паре «сегодня/вчера» <c>((c * P + p) &lt;&lt; 1) | (day &amp; 1)</c>. Ноги копятся при завершении (micro и aggregate одинаково), модель BPR читает вчерашнюю половину.
/// <see cref="Cap"/> - производное (не в сейве и хэше): пропускная способность клетки за период, пересчитывается при смене версии сети (<c>NetDirty</c>).
/// </summary>
internal sealed class CellLoads
{
    /// <summary>Q8-аргумент таблицы BPR не больше 8.0 (дальше F растёт, но времени ноги это не меняет: потолок).</summary>
    public const int BprMaxQ8 = 2048;

    /// <summary>Насыщающий поток полосы, машин в час (ADR-0021, спека §9.1).</summary>
    public const int SatFlowPerHour = 1800;

    /// <summary><c>F(x) = 1 + 0.15 x^4</c> в Q8 по <c>x</c> в Q8: целая таблица, строится один раз.</summary>
    private static readonly int[] _bpr = BuildBpr();

    public readonly int Cells;
    public readonly int Periods;
    public readonly int[] L;           // [Cells * Periods * 2]: пара «сегодня/вчера», индекс ((c * P + p) << 1) | (day & 1)
    public readonly int[] Cap;         // [Cells] машин за период на клетку (0 - дорог нет); производное
    private readonly long[] _laneMm;
    private int _capVersion = -1;

    public CellLoads(int cells, int periods)
    {
        Cells = cells;
        Periods = periods;
        L = new int[checked(cells * periods * 2)];
        Cap = new int[cells];
        _laneMm = new long[cells];
    }

    private static int[] BuildBpr()
    {
        var t = new int[BprMaxQ8 + 1];
        for (int x = 0; x <= BprMaxQ8; x++)
        {
            long x4 = (long)x * x * x * x;
            t[x] = 256 + (int)((15L * x4) / (100L * 256L * 256L * 256L));
        }

        return t;
    }

    /// <summary><c>F(x)</c> в Q8 по <c>x = L / Cap</c> в Q8 (отрицательное и сверхбольшое зажимаются).</summary>
    public static int Bpr(long xQ8) => _bpr[xQ8 < 0 ? 0 : (xQ8 > BprMaxQ8 ? BprMaxQ8 : (int)xQ8)];

    /// <summary>Сутки <paramref name="day"/> закончились: половина «сегодня» следующих суток обнуляется (там лежали позавчерашние числа).</summary>
    public void ClearDay(int newDay)
    {
        int slot = newDay & 1;
        for (int i = slot; i < L.Length; i += 2)
        {
            L[i] = 0;
        }
    }

    /// <summary>Машинная нога <paramref name="o"/> - <paramref name="d"/> периода <paramref name="period"/> завершена в сутки <paramref name="day"/>: +1 каждой клетке линии.</summary>
    public void AddCarLeg(int cellsX, int o, int d, int period, int day)
    {
        int slot = day & 1;
        var line = new CellLine(o, d, cellsX);
        while (line.Next(out int cell))
        {
            L[(((cell * Periods) + period) << 1) | slot]++;
        }
    }

    /// <summary>Пропускная способность клеток (<c>Σ полосо-км ячейки · q_sat · часы периода</c>, делённая на длину клетки в км: ноги пересекают клетку по одной полосе-сечению), пересчёт при смене сети.</summary>
    public void EnsureCap(World w, CityWorld c)
    {
        NetworkStore net = w.Net;
        if (_capVersion == net.Version)
        {
            return;
        }

        Array.Clear(_laneMm);
        for (int s = 0; s < net.SegmentCount; s++)
        {
            ref readonly SegmentData sd = ref net.Segments[s];
            int lanes = sd.LanesAB + sd.LanesBA;
            if (lanes == 0)
            {
                continue;
            }

            Vec2 a = net.Nodes[sd.NodeA].Position;
            Vec2 b = net.Nodes[sd.NodeB].Position;
            int cell = c.Grid.CellOf((a.X + b.X) * 0.5f, (a.Z + b.Z) * 0.5f);
            _laneMm[cell] += (long)lanes * DetMath.ToIntSat(sd.Length * 1000f);
        }

        long cellMm = Math.Max(1L, (long)DetMath.ToIntSat(c.Grid.CellM * 1000f));
        long denom = cellMm * 72_000L * Periods;   // 72 000 тиков = час симуляции
        for (int i = 0; i < Cells; i++)
        {
            long cap = _laneMm[i] * SatFlowPerHour * c.DayTicks / denom;
            Cap[i] = _laneMm[i] > 0 ? (int)Math.Min(int.MaxValue, Math.Max(1L, cap)) : 0;
        }

        _capVersion = net.Version;
    }

    /// <summary>
    /// Модельная стоимость машинной ноги, дс (§9.1): <c>Σ t0_c · F(L[c, p] / Cap[c])</c>, <c>t0_c</c> - поровну клеткам линии от априорного свободного времени пары. Загрузка - вчерашняя
    /// (<paramref name="day"/> - текущие сутки). 0 - машина для пары недоступна. Поправка <c>K</c> здесь не применяется.
    /// </summary>
    public int ModelCarDs(World w, CityWorld c, int o, int d, int period, int day)
    {
        c.Skims.Prepare(w);
        int t0 = c.Skims.Prior(o, d, TravelMode.Car);
        if (t0 == 0)
        {
            return 0;
        }

        EnsureCap(w, c);
        int slot = (day + 1) & 1;
        long sumF = 0;
        int n = 0;
        var line = new CellLine(o, d, c.Grid.CellsX);
        while (line.Next(out int cell))
        {
            n++;
            int cap = Cap[cell];
            sumF += cap > 0 ? Bpr((long)L[(((cell * Periods) + period) << 1) | slot] * 256 / cap) : 256;
        }

        long ds = t0 * sumF / (256L * n);
        return ds < 1 ? 1 : (ds > int.MaxValue ? int.MaxValue : (int)ds);
    }
}

/// <summary>Клетки прямой между центрами двух ячеек сетки (Брезенхэм по целым, включая обе крайние), в порядке от <c>o</c> к <c>d</c>. Без аллокаций.</summary>
internal struct CellLine
{
    private readonly int _cellsX;
    private readonly int _x1;
    private readonly int _z1;
    private readonly int _dx;
    private readonly int _dz;
    private readonly int _sx;
    private readonly int _sz;
    private int _x;
    private int _z;
    private int _err;
    private bool _done;

    public CellLine(int o, int d, int cellsX)
    {
        _cellsX = cellsX;
        _x = o % cellsX;
        _z = o / cellsX;
        _x1 = d % cellsX;
        _z1 = d / cellsX;
        _dx = Math.Abs(_x1 - _x);
        _dz = -Math.Abs(_z1 - _z);
        _sx = _x < _x1 ? 1 : -1;
        _sz = _z < _z1 ? 1 : -1;
        _err = _dx + _dz;
        _done = false;
    }

    public bool Next(out int cell)
    {
        if (_done)
        {
            cell = -1;
            return false;
        }

        cell = (_z * _cellsX) + _x;
        if (_x == _x1 && _z == _z1)
        {
            _done = true;
            return true;
        }

        int e2 = 2 * _err;
        if (e2 >= _dz)
        {
            _err += _dz;
            _x += _sx;
        }

        if (e2 <= _dx)
        {
            _err += _dx;
            _z += _sz;
        }

        return true;
    }
}
