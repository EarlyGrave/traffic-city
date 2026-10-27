using System;
using Sim.Core.City;
using Sim.Core.Util;

namespace Sim.Core.Parking;

/// <summary>
/// Окрестности участков (parking.md §2.4, производное): для каждого участка до <c>max_candidates</c> объектов, отсортированных по <c>(WalkMm, индекс)</c> - обочины, гаражи и
/// стоянки с флагом <see cref="ParkFlags.Public"/> и стоянка здания самого участка, с <c>WalkMm &lt;= walk_max_m</c> и общей дорожной компонентой (при сети пешеходов - и пешеходной).
/// <c>WalkMm</c> - манхэттен между точкой объекта и центром участка, целые мм (стоянка своего здания - 0). Плоские массивы со стоянкой <see cref="Stride"/>. Поиск соседей - корзинная
/// сетка 100 м (counting sort, выделена в конструкторе). Пересборка целиком - <see cref="RebuildAll"/> (<c>Create</c>, <c>Load</c>, <c>BuildRoad</c>), для участков в радиусе от точки - <see cref="RebuildNear"/>
/// (создание и снос одного объекта); результат локальной пересборки равен полной (тест). Ни одна пересборка не выделяет память.
/// </summary>
internal sealed class Catchment
{
    public const float BucketM = 100f;
    private const float MarginM = 200f;

    private readonly ParkingStore _p;
    private readonly float _ox;
    private readonly float _oz;
    private readonly int _gx;
    private readonly int _gz;
    private readonly int[] _bucketStart;
    private readonly int[] _bucketItems;
    private readonly int[] _bucketCursor;

    public readonly int Stride;           // max_candidates
    public readonly int[] Count;          // [max_lots]
    public readonly int[] Fac;            // [max_lots * Stride]
    public readonly int[] WalkMm;         // [max_lots * Stride]

    public Catchment(ParkingStore p, in CityGrid grid, int maxLots, int stride, int maxObjects)
    {
        _p = p;
        Stride = stride;
        _ox = grid.OriginX - MarginM;
        _oz = grid.OriginZ - MarginM;
        _gx = Math.Max(1, DetMath.ToIntSat(MathF.Ceiling(((grid.CellsX * grid.CellM) + (2f * MarginM)) / BucketM)));
        _gz = Math.Max(1, DetMath.ToIntSat(MathF.Ceiling(((grid.CellsZ * grid.CellM) + (2f * MarginM)) / BucketM)));
        _bucketStart = new int[checked((_gx * _gz) + 1)];
        _bucketCursor = new int[_bucketStart.Length];
        _bucketItems = new int[Math.Max(1, maxObjects)];
        Count = new int[maxLots];
        Fac = new int[checked(maxLots * stride)];
        WalkMm = new int[checked(maxLots * stride)];
        Array.Fill(Fac, -1);
    }

    private int BucketX(float x) => Math.Clamp(DetMath.ToIntSat(MathF.Floor((x - _ox) / BucketM)), 0, _gx - 1);

    private int BucketZ(float z) => Math.Clamp(DetMath.ToIntSat(MathF.Floor((z - _oz) / BucketM)), 0, _gz - 1);

    /// <summary>Объект учитывается в окрестностях: жив, не <c>Overflow</c>, привязан к живому участку (есть дорожная компонента).</summary>
    private bool Indexed(int f) => f > 0 && _p.Pool.IsAlive(f) && _p.RoadComp[f] >= 0;

    /// <summary>Корзинная сетка объектов (counting sort по корзине, внутри корзины - по возрастанию индекса).</summary>
    public void RebuildGrid()
    {
        Array.Clear(_bucketStart);
        int high = _p.Pool.HighWater;
        for (int f = 1; f < high; f++)
        {
            if (Indexed(f))
            {
                _bucketStart[(BucketZ(_p.Z[f]) * _gx) + BucketX(_p.X[f]) + 1]++;
            }
        }

        for (int b = 0; b < _gx * _gz; b++)
        {
            _bucketStart[b + 1] += _bucketStart[b];
        }

        Array.Copy(_bucketStart, _bucketCursor, _bucketStart.Length);
        for (int f = 1; f < high; f++)
        {
            if (Indexed(f))
            {
                _bucketItems[_bucketCursor[(BucketZ(_p.Z[f]) * _gx) + BucketX(_p.X[f])]++] = f;
            }
        }
    }

    /// <summary>Полная пересборка: корзины и окрестности всех участков.</summary>
    public void RebuildAll(World w, CityWorld c)
    {
        RebuildGrid();
        LotStore l = c.Lots;
        for (int lot = 0; lot < l.Count; lot++)
        {
            RebuildLot(w, c, lot);
        }

        for (int lot = l.Count; lot < Count.Length; lot++)
        {
            Clear(lot);
        }
    }

    /// <summary>
    /// Локальная пересборка (создание, снос или смена одного объекта в точке <paramref name="x"/>, <paramref name="z"/>): корзины целиком (O(объекты)), окрестности только тех участков, чей центр
    /// в манхэттен-радиусе <c>walk_max_m</c> от точки. Для участка вне радиуса объект не мог входить в окрестность ни до, ни после правки.
    /// </summary>
    public void RebuildNear(World w, CityWorld c, float x, float z)
    {
        RebuildGrid();
        float r = (_p.Cfg.WalkMaxMm * 0.001f) + 1f;
        LotStore l = c.Lots;
        for (int lot = 0; lot < l.Count; lot++)
        {
            if (MathF.Abs(l.X[lot] - x) + MathF.Abs(l.Z[lot] - z) <= r || l.RoadComp[lot] < 0)
            {
                RebuildLot(w, c, lot);
            }
        }
    }

    private void Clear(int lot)
    {
        Count[lot] = 0;
        int b = lot * Stride;
        for (int k = 0; k < Stride; k++)
        {
            Fac[b + k] = -1;
            WalkMm[b + k] = 0;
        }
    }

    private void RebuildLot(World w, CityWorld c, int lot)
    {
        Clear(lot);
        LotStore l = c.Lots;
        if (l.RoadComp[lot] < 0 || (l.Flags[lot] & LotFlags.Lost) != 0)
        {
            return;
        }

        bool ped = w.Net.PedNet != null;
        float cx = l.X[lot];
        float cz = l.Z[lot];
        float reach = _p.Cfg.WalkMaxMm * 0.001f;
        int bx0 = BucketX(cx - reach);
        int bx1 = BucketX(cx + reach);
        int bz0 = BucketZ(cz - reach);
        int bz1 = BucketZ(cz + reach);
        int n = 0;
        int b0 = lot * Stride;
        for (int bz = bz0; bz <= bz1; bz++)
        {
            for (int bx = bx0; bx <= bx1; bx++)
            {
                int cell = (bz * _gx) + bx;
                for (int i = _bucketStart[cell]; i < _bucketStart[cell + 1]; i++)
                {
                    int f = _bucketItems[i];
                    bool own = _p.Lot[f] == lot && _p.Kind[f] != ParkKind.Curb;
                    if (!own && (_p.Flags[f] & ParkFlags.Public) == 0)
                    {
                        continue;
                    }

                    if (_p.RoadComp[f] != l.RoadComp[lot] || (ped && _p.PedComp[f] != l.PedComp[lot]))
                    {
                        continue;
                    }

                    int walk = own ? 0 : Manhattan(_p.X[f] - cx, _p.Z[f] - cz);
                    if (walk > _p.Cfg.WalkMaxMm)
                    {
                        continue;
                    }

                    n = Insert(b0, n, f, walk);
                }
            }
        }

        Count[lot] = n;
    }

    /// <summary>Вставка в отсортированный по <c>(WalkMm, индекс)</c> массив не длиннее <see cref="Stride"/>; возвращает новую длину.</summary>
    private int Insert(int b0, int n, int f, int walk)
    {
        int pos = n;
        while (pos > 0 && (WalkMm[b0 + pos - 1] > walk || (WalkMm[b0 + pos - 1] == walk && Fac[b0 + pos - 1] > f)))
        {
            pos--;
        }

        if (pos >= Stride)
        {
            return n;
        }

        int last = n < Stride ? n : Stride - 1;
        for (int k = last; k > pos; k--)
        {
            Fac[b0 + k] = Fac[b0 + k - 1];
            WalkMm[b0 + k] = WalkMm[b0 + k - 1];
        }

        Fac[b0 + pos] = f;
        WalkMm[b0 + pos] = walk;
        return n < Stride ? n + 1 : n;
    }

    /// <summary>Манхэттен в целых мм: сумма модулей компонент (м -> мм усечением, как у всех геометрических решений ядра).</summary>
    internal static int Manhattan(float dxM, float dzM) =>
        DetMath.ToIntSat(MathF.Abs(dxM) * 1000f) + DetMath.ToIntSat(MathF.Abs(dzM) * 1000f);

    /// <summary>true - окрестности (<see cref="Count"/>, <see cref="Fac"/>, <see cref="WalkMm"/>) равны окрестностям <paramref name="other"/> на всех участках (тест локальной пересборки).</summary>
    public bool SameAs(Catchment other)
    {
        return Count.AsSpan().SequenceEqual(other.Count) && Fac.AsSpan().SequenceEqual(other.Fac) && WalkMm.AsSpan().SequenceEqual(other.WalkMm);
    }
}
