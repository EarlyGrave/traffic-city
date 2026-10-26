using System;

namespace Sim.Core.City;

/// <summary>Биты <see cref="LotStore.Flags"/>.</summary>
internal static class LotFlags
{
    public const byte Lost = 1;       // дорога снесена: участок остаётся в списке, здание заброшено
    public const byte Painted = 2;    // зону задал игрок (автозонирование и рост его не трогают)
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

    public LotStore(int capacity)
    {
        Capacity = capacity;
        Seg = new int[capacity];
        SegGen = new int[capacity];
        Side = new byte[capacity];
        S0Mm = new int[capacity];
        S1Mm = new int[capacity];
        Zone = new byte[capacity];
        Density = new byte[capacity];
        Building = new int[capacity];
        Flags = new byte[capacity];
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
        Version++;
        return true;
    }
}
