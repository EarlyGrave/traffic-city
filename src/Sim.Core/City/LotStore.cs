using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>Биты <see cref="LotStore.Flags"/>.</summary>
internal static class LotFlags
{
    public const byte Lost = 1;       // дорога снесена: участок остаётся в списке, здание заброшено
    public const byte Painted = 2;    // зону задал игрок (автозонирование и рост его не трогают)
}

/// <summary>Значения <see cref="LotStore.Orient"/>.</summary>
internal static class LotOrient
{
    public const byte Legacy = 255;   // участок вдоль дороги без привязки к сетке (сейвы до v8, режим legacy)
    public const byte Max = 3;        // 0..3: +x, -x, +z, -z
}

/// <summary>
/// Участки (zoning-demand.md §2.1): SoA с ёмкостью <c>max_lots</c>, только дописываются (<see cref="Count"/>); индекс - идентификатор, поколения нет
/// (устаревшую ссылку на сегмент ловит <see cref="SegGen"/>). Состояние (в сейве, блок <c>LOTS</c>): сегмент, сторона, отрезок фасада, зона, плотность, здание, флаги.
/// Производные (не сохраняются, пересчитываются <see cref="LotBuilder.RestoreDerived"/>): центр, курс, ячейка, точки доступа машины и пешехода, компоненты связности.
/// </summary>
internal sealed class LotStore
{
    public readonly int Capacity;
    public int Count;

    // ---- состояние ----
    public readonly int[] Seg;
    public readonly int[] SegGen;
    public readonly byte[] Side;         // 0 справа от оси A->B, 1 слева (как PedEdgeData.Side)
    public readonly int[] S0Mm;
    public readonly int[] S1Mm;
    public readonly byte[] Zone;         // LotZone
    public readonly byte[] Density;
    public readonly int[] Building;      // индекс здания или -1
    public readonly byte[] Flags;

    // ---- состояние участков на мировой сетке (ADR-0030, road-profiles.md §4.4; столбцы LOTS v8). Заполняет RP5; до него Orient = 255 (legacy), остальное 0 ----
    public readonly int[] CellX;         // передний левый угол, клетки мировой сетки 8 м
    public readonly int[] CellZ;
    public readonly byte[] CellsF;       // ширина по фасаду, клеток
    public readonly byte[] CellsD;       // глубина, клеток
    public readonly byte[] Orient;       // ось глубины +x, -x, +z, -z (0..3); 255 - legacy

    // ---- производные ----
    public readonly float[] X;
    public readonly float[] Z;
    public readonly float[] Heading;     // рад, фасадом к дороге
    public readonly int[] Cell;          // ячейка CityGrid
    public readonly int[] CarLane;       // полоса направления этой стороны (-1: нет полос в этом направлении)
    public readonly float[] CarS;        // м вдоль CarLane
    public readonly int[] PedEdge;       // ребро тротуара (-1: нет сети пешеходов)
    public readonly int[] PedSMm;
    public readonly int[] RoadComp;      // компонента дорожной связности (по сегментам), -1 без полос
    public readonly int[] PedComp;       // компонента пешеходной связности или -1

    /// <summary>Версия данных для клиента (<c>LotsVersion</c>): растёт при любом изменении состояния участков.</summary>
    public int Version;

    /// <summary>
    /// Виртуальные участки внешних подключений (ADR-0037): слоты <c>Capacity + k</c> за пределами <see cref="Count"/>, только производные поля (центр, ячейка, полоса, компонента) -
    /// чтобы ноги и стоимости между «местом» здания и узлом подключения шли тем же кодом, что между участками. Ни в сейв, ни в хэш, ни в обходы по <c>Count</c> не входят.
    /// </summary>
    public const int VirtualSlots = CityOutsideDesc.Max;

    public LotStore(int lotCapacity)
    {
        Capacity = lotCapacity;
        int capacity = lotCapacity + VirtualSlots;
        Seg = new int[capacity];
        SegGen = new int[capacity];
        Side = new byte[capacity];
        S0Mm = new int[capacity];
        S1Mm = new int[capacity];
        Zone = new byte[capacity];
        Density = new byte[capacity];
        Building = new int[capacity];
        Flags = new byte[capacity];
        CellX = new int[capacity];
        CellZ = new int[capacity];
        CellsF = new byte[capacity];
        CellsD = new byte[capacity];
        Orient = new byte[capacity];
        Array.Fill(Orient, LotOrient.Legacy);
        X = new float[capacity];
        Z = new float[capacity];
        Heading = new float[capacity];
        Cell = new int[capacity];
        CarLane = new int[capacity];
        CarS = new float[capacity];
        PedEdge = new int[capacity];
        PedSMm = new int[capacity];
        RoadComp = new int[capacity];
        PedComp = new int[capacity];
        Array.Fill(Building, -1);
        Array.Fill(CarLane, -1);
        Array.Fill(PedEdge, -1);
        Array.Fill(RoadComp, -1);
        Array.Fill(PedComp, -1);
    }

    /// <summary>Манхэттенское расстояние между центрами участков, целые метры (геометрия: оценка времени ноги без скима).</summary>
    public int ManhattanM(int a, int b) => DetMath.ToIntSat(MathF.Abs(X[a] - X[b]) + MathF.Abs(Z[a] - Z[b]));

    /// <summary>Евклидово расстояние между центрами участков, целые метры (геометрия: порог пешей доступности).</summary>
    public int EuclidM(int a, int b)
    {
        float dx = X[a] - X[b];
        float dz = Z[a] - Z[b];
        return DetMath.ToIntSat(MathF.Sqrt((dx * dx) + (dz * dz)));
    }

    /// <summary>Резервирует следующий слот; false - ёмкость исчерпана.</summary>
    public bool TryAdd(int seg, int segGen, byte side, int s0Mm, int s1Mm, out int index)
    {
        if (Count >= Capacity)
        {
            index = -1;
            return false;
        }

        index = Count++;
        Seg[index] = seg;
        SegGen[index] = segGen;
        Side[index] = side;
        S0Mm[index] = s0Mm;
        S1Mm[index] = s1Mm;
        Zone[index] = 0;
        Density[index] = 0;
        Building[index] = -1;
        Flags[index] = 0;
        CellX[index] = 0;
        CellZ[index] = 0;
        CellsF[index] = 0;
        CellsD[index] = 0;
        Orient[index] = LotOrient.Legacy;
        Version++;
        return true;
    }
}
