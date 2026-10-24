using System;

namespace Sim.Core.Gen;

/// <summary>
/// Плоский граф-план города (city-generator §3.1): решётка с джиттером, удаления и слияния выполняются здесь,
/// в <see cref="Network.NetworkBuilder"/> уходит только финальный граф. Все массивы выделяются один раз.
/// </summary>
internal sealed class GridPlan
{
    public const int MaxArmSlots = 8;
    public const byte FlagBorder = 1;
    public const byte FlagOnArterialLine = 2;
    public const byte FlagShifted = 4;
    public const byte FlagMerged = 8;

    public readonly int Nx;
    public readonly int Nz;
    public readonly int NodeCap;
    public readonly int EdgeCap;

    // узлы: p = j * (Nx + 1) + i
    public readonly float[] X;
    public readonly float[] Z;
    public readonly byte[] NodeAlive;
    public readonly byte[] NodeFlags;
    public readonly byte[] Deg;
    public readonly int[] Adj;          // [NodeCap * MaxArmSlots], -1 = пусто
    public readonly int[] NodeOut;

    // рёбра: горизонтальные e = j * Nx + i, затем вертикальные e = (Nz + 1) * Nx + j * (Nx + 1) + i
    public readonly int[] EdgeA;
    public readonly int[] EdgeB;
    public readonly byte[] EdgeAlive;
    public readonly byte[] EdgeKind;
    public readonly int[] EdgeOut;

    // артериальные линии: столбцы i (вертикальные линии) и строки j
    public readonly byte[] ArtX;
    public readonly byte[] ArtZ;

    // рабочие буферы
    public readonly int[] Queue;
    public readonly int[] UnionParent;
    public readonly int[] Candidates;
    public readonly int[] Removed;
    public readonly float[] ArmAngles;
    public readonly int[] ArmOrder;
    public readonly int[] SaveAdjP = new int[MaxArmSlots];
    public readonly int[] SaveAdjQ = new int[MaxArmSlots];
    public readonly int[] SaveEdgeA = new int[MaxArmSlots];
    public readonly int[] SaveEdgeB = new int[MaxArmSlots];

    public GridPlan(int nx, int nz)
    {
        Nx = nx;
        Nz = nz;
        NodeCap = (nx + 1) * (nz + 1);
        EdgeCap = ((nz + 1) * nx) + (nz * (nx + 1));
        X = new float[NodeCap];
        Z = new float[NodeCap];
        NodeAlive = new byte[NodeCap];
        NodeFlags = new byte[NodeCap];
        Deg = new byte[NodeCap];
        Adj = new int[NodeCap * MaxArmSlots];
        NodeOut = new int[NodeCap];
        EdgeA = new int[EdgeCap];
        EdgeB = new int[EdgeCap];
        EdgeAlive = new byte[EdgeCap];
        EdgeKind = new byte[EdgeCap];
        EdgeOut = new int[EdgeCap];
        ArtX = new byte[nx + 1];
        ArtZ = new byte[nz + 1];
        Queue = new int[NodeCap];
        UnionParent = new int[NodeCap];
        Candidates = new int[Math.Max(EdgeCap, NodeCap)];
        Removed = new int[EdgeCap];
        ArmAngles = new float[MaxArmSlots];
        ArmOrder = new int[MaxArmSlots];
        Array.Fill(Adj, -1);
        Array.Fill(NodeOut, -1);
        Array.Fill(EdgeOut, -1);
    }

    public int HorizontalEdge(int i, int j) => (j * Nx) + i;

    public int VerticalEdge(int i, int j) => ((Nz + 1) * Nx) + (j * (Nx + 1)) + i;

    public void AddAdj(int p, int e)
    {
        Adj[(p * MaxArmSlots) + Deg[p]] = e;
        Deg[p]++;
    }

    public void RemoveAdj(int p, int e)
    {
        int baseIdx = p * MaxArmSlots;
        int d = Deg[p];
        for (int s = 0; s < d; s++)
        {
            if (Adj[baseIdx + s] == e)
            {
                for (int t = s; t < d - 1; t++)
                {
                    Adj[baseIdx + t] = Adj[baseIdx + t + 1];
                }

                Adj[baseIdx + d - 1] = -1;
                Deg[p]--;
                return;
            }
        }
    }

    public int Other(int e, int p) => EdgeA[e] == p ? EdgeB[e] : EdgeA[e];

    public float EdgeLength(int e)
    {
        float dx = X[EdgeB[e]] - X[EdgeA[e]];
        float dz = Z[EdgeB[e]] - Z[EdgeA[e]];
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }

    public int Find(int v)
    {
        while (UnionParent[v] != v)
        {
            UnionParent[v] = UnionParent[UnionParent[v]];
            v = UnionParent[v];
        }

        return v;
    }

    /// <summary>Объединение: корень с меньшим индексом подвешивается под больший.</summary>
    public void Union(int a, int b)
    {
        int ra = Find(a);
        int rb = Find(b);
        if (ra == rb)
        {
            return;
        }

        if (ra < rb)
        {
            UnionParent[ra] = rb;
        }
        else
        {
            UnionParent[rb] = ra;
        }
    }

    /// <summary>Проверка геометрии узла (city-generator §3.6).</summary>
    public bool Validate(int p)
    {
        int d = Deg[p];
        if (d > CityGenerator.MaxArms)
        {
            return false;
        }

        int baseIdx = p * MaxArmSlots;
        for (int s = 0; s < d; s++)
        {
            int e = Adj[baseIdx + s];
            int a = EdgeA[e];
            int b = EdgeB[e];
            if (a == b)
            {
                return false;
            }

            int w = a == p ? b : a;
            float dx = X[w] - X[p];
            float dz = Z[w] - Z[p];
            if (MathF.Sqrt((dx * dx) + (dz * dz)) < CityGenerator.MinSegmentLength)
            {
                return false;
            }

            ArmAngles[s] = MathF.Atan2(dz, dx);
            for (int t = 0; t < s; t++)
            {
                if (Other(Adj[baseIdx + t], p) == w)
                {
                    return false;
                }
            }
        }

        if (d < 2)
        {
            return true;
        }

        for (int s = 1; s < d; s++)
        {
            float v = ArmAngles[s];
            int t = s - 1;
            while (t >= 0 && ArmAngles[t] > v)
            {
                ArmAngles[t + 1] = ArmAngles[t];
                t--;
            }

            ArmAngles[t + 1] = v;
        }

        for (int s = 0; s < d - 1; s++)
        {
            if (ArmAngles[s + 1] - ArmAngles[s] < CityGenerator.MinArmAngle)
            {
                return false;
            }
        }

        return (ArmAngles[0] + (2f * MathF.PI) - ArmAngles[d - 1]) >= CityGenerator.MinArmAngle;
    }
}
