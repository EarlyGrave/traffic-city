using System;
using Sim.Core.Network;

namespace Sim.Core.Rules.Intersections;

/// <summary>
/// Построение масок фаз светофора по геометрии узла (intersections.md 6.2): рукава группируются в оси,
/// фаза = все connector'ы, входящие с рукавов оси. Работает только с публичными данными сети
/// (<c>NodeArms</c>, <c>SegmentPoints</c>, <c>LaneData.FromLane/Segment</c>), поэтому не зависит от раскладки
/// connector'ов (кто и как их генерирует). Бит маски = <c>connector - Node.FirstConnector</c> (LocalBit;
/// ConnectorStore обязан использовать то же соглашение). Без аллокаций (stackalloc).
/// </summary>
internal static class SignalMasks
{
    public const int MaxPhases = 8;
    public const int MaxMaskedConnectors = 64;
    private const float OppositeDot = -0.8660254f; // cos(150 град): рукава «напротив» друг друга

    /// <summary>
    /// Заполняет <paramref name="masks"/> (длина >= 8) масками фаз узла. Фазы упорядочены по углу оси
    /// (ось, ближайшая к X, - фаза 0, как в <c>SignalPlanSeed</c>). false: у узла нет connector'ов / рукавов,
    /// connector'ов больше 64 или осей больше 8.
    /// </summary>
    public static bool Build(NetworkStore net, int node, Span<ulong> masks, out int phaseCount)
    {
        phaseCount = 0;
        masks.Slice(0, MaxPhases).Clear();
        ref readonly NodeData nd = ref net.Nodes[node];
        int arms = nd.ArmCount;
        int conns = nd.ConnectorCount;
        if (nd.FirstArm < 0 || arms <= 0 || arms > NetworkStore.MaxArms || conns <= 0 || conns > MaxMaskedConnectors)
        {
            return false;
        }

        Span<float> dx = stackalloc float[NetworkStore.MaxArms];
        Span<float> dz = stackalloc float[NetworkStore.MaxArms];
        Span<int> axisOf = stackalloc int[NetworkStore.MaxArms];
        for (int a = 0; a < arms; a++)
        {
            ArmDirection(net, node, net.NodeArms[nd.FirstArm + a], out dx[a], out dz[a]);
            axisOf[a] = -1;
        }

        // Оси: обход по часовой, ближайший к противоположному свободный рукав (угол >= 150 град), иначе рукав один.
        Span<float> key = stackalloc float[MaxPhases];
        int axes = 0;
        for (int a = 0; a < arms; a++)
        {
            if (axisOf[a] >= 0)
            {
                continue;
            }

            int best = -1;
            float bestDot = OppositeDot;
            for (int b = a + 1; b < arms; b++)
            {
                if (axisOf[b] >= 0)
                {
                    continue;
                }

                float dot = (dx[a] * dx[b]) + (dz[a] * dz[b]);
                if (dot <= bestDot)
                {
                    bestDot = dot;
                    best = b;
                }
            }

            if (axes == MaxPhases)
            {
                return false;
            }

            axisOf[a] = axes;
            if (best >= 0)
            {
                axisOf[best] = axes;
            }

            key[axes] = FoldedAngle(dx[a], dz[a]);
            axes++;
        }

        // Порядок фаз: по возрастанию угла оси (ранг, оси <= 8).
        Span<int> phaseOfAxis = stackalloc int[MaxPhases];
        for (int i = 0; i < axes; i++)
        {
            int rank = 0;
            for (int j = 0; j < axes; j++)
            {
                if (key[j] < key[i] || (key[j] == key[i] && j < i))
                {
                    rank++;
                }
            }

            phaseOfAxis[i] = rank;
        }

        for (int c = 0; c < conns; c++)
        {
            int fromSeg = net.Lanes[net.Lanes[nd.FirstConnector + c].FromLane].Segment;
            for (int a = 0; a < arms; a++)
            {
                if (net.NodeArms[nd.FirstArm + a] == fromSeg)
                {
                    masks[phaseOfAxis[axisOf[a]]] |= 1UL << c;
                    break;
                }
            }
        }

        phaseCount = axes;
        return true;
    }

    /// <summary>Единичное направление от узла вдоль рукава (по ближайшей к узлу точке оси сегмента).</summary>
    private static void ArmDirection(NetworkStore net, int node, int seg, out float x, out float z)
    {
        ref readonly SegmentData s = ref net.Segments[seg];
        int p = s.NodeA == node ? s.FirstPoint : s.FirstPoint + s.PointCount - 1;
        Util.Vec2 pos = net.Nodes[node].Position;
        float vx = net.SegmentPoints[p].X - pos.X;
        float vz = net.SegmentPoints[p].Z - pos.Z;
        float len = MathF.Sqrt((vx * vx) + (vz * vz));
        if (len < 1e-6f)
        {
            x = 1f;
            z = 0f;
            return;
        }

        x = vx / len;
        z = vz / len;
    }

    /// <summary>Угол оси, свёрнутый в (-45, 135] град: ось X (0 или 180) -> ~0, ось Z -> ~90.</summary>
    private static float FoldedAngle(float x, float z)
    {
        float th = MathF.Atan2(z, x);
        const float Quarter = MathF.PI / 4f;
        if (th <= -Quarter)
        {
            th += MathF.PI;
        }
        else if (th > 3f * Quarter)
        {
            th -= MathF.PI;
        }

        return th;
    }
}
