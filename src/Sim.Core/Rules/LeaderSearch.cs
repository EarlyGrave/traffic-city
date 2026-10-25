namespace Sim.Core.Rules;

/// <summary>Результат поиска лидера: <see cref="Vehicle"/> == -1 и <see cref="Virtual"/> == false - дорога свободна.</summary>
internal struct LeaderInfo
{
    public int Vehicle;      // индекс лидера или -1
    public float Gap;        // зазор бампер-в-бампер до лидера / препятствия, м (Idm.FreeGap = свободно)
    public float Speed;      // скорость лидера (0 для препятствия)
    public bool Virtual;     // препятствие в конце полосы без продолжения (тупик, закрытый выезд)
    public int Hops;         // переходов по цепочке полос при поиске (счётчик работы SimWork; 0 - лидер на своей полосе)

    public static LeaderInfo Free => new() { Vehicle = -1, Gap = Idm.FreeGap, Speed = 0f, Virtual = false };
}

/// <summary>
/// Поиск лидера (core-api 6.4): сосед по списку полосы за O(1), иначе просмотр цепочки следующих полос
/// (хвост первой непустой), либо виртуальное препятствие в конце полосы без продолжения. Дальность просмотра -
/// <see cref="LookaheadDistance"/> и не более <see cref="MaxLookaheadLanes"/> полос (спека: глубина 2 для длинных полос; здесь
/// ограничение по расстоянию, потому что connector'ы короткие). Ничего не аллоцирует.
/// </summary>
internal static class LeaderSearch
{
    public const float LookaheadDistance = 150f;
    public const int MaxLookaheadLanes = 6;

    /// <summary>
    /// Лидер машины <paramref name="self"/> на полосе <paramref name="lane"/> в позиции <paramref name="sSelf"/>.
    /// <paramref name="firstAhead"/> - ближайший впереди член списка полосы (или -1 - тогда просмотр цепочки).
    /// <paramref name="s"/> - массив позиций (S на начало тика для клэмпа I6, иначе текущий).
    /// </summary>
    public static LeaderInfo Find(in RuleEnv e, float[] s, int self, int lane, float sSelf, int firstAhead, bool useRoute)
    {
        Lod.QueueMeso? q = e.Occ.Queue;
        if (firstAhead != -1)
        {
            if (q != null && q.IsQueued(firstAhead))
            {
                // Q-meso: лидер - машина очереди (автобус за ней); её S - позиция въезда, позиция «сейчас» - синтез очереди от головы.
                q.SynthMember(firstAhead, lane, (float)q.TickNow, out float sq, out float vq);
                return new LeaderInfo { Vehicle = firstAhead, Gap = sq - e.V.Length[firstAhead] - sSelf, Speed = vq, Virtual = false };
            }

            return new LeaderInfo
            {
                Vehicle = firstAhead,
                Gap = s[firstAhead] - e.V.Length[firstAhead] - sSelf,
                Speed = e.V.Speed[firstAhead],
                Virtual = false,
            };
        }

        float dist = e.Net.LaneLength[lane] - sSelf;
        int cur = lane;
        for (int hop = 1; hop <= MaxLookaheadLanes; hop++)
        {
            int next = hop == 1 && useRoute && lane == e.V.Lane[self] ? e.FirstHop(self, lane) : e.NextLane(self, cur, hop, useRoute);
            if (next < 0)
            {
                return new LeaderInfo { Vehicle = -1, Gap = dist, Speed = 0f, Virtual = true, Hops = hop };
            }

            int tail = e.Occ.LaneTail[next];
            if (tail != -1)
            {
                if (tail == self)
                {
                    return new LeaderInfo { Vehicle = -1, Gap = Idm.FreeGap, Speed = 0f, Virtual = false, Hops = hop };
                }

                if (q != null && q.IsQueued(tail))
                {
                    // Q-meso: хвост следующей полосы - машина очереди: оценка хвоста за O(1).
                    return new LeaderInfo { Vehicle = tail, Gap = dist + q.TailS(next) - e.V.Length[tail], Speed = q.TailSpeed(next), Virtual = false, Hops = hop };
                }

                return new LeaderInfo
                {
                    Vehicle = tail,
                    Gap = dist + s[tail] - e.V.Length[tail],
                    Speed = e.V.Speed[tail],
                    Virtual = false,
                    Hops = hop,
                };
            }

            dist += e.Net.LaneLength[next];
            if (dist > LookaheadDistance)
            {
                return new LeaderInfo { Vehicle = -1, Gap = Idm.FreeGap, Speed = 0f, Virtual = false, Hops = hop };
            }

            cur = next;
        }

        return new LeaderInfo { Vehicle = -1, Gap = Idm.FreeGap, Speed = 0f, Virtual = false, Hops = MaxLookaheadLanes };
    }

    /// <summary>
    /// Лидер с учётом ghost: ближайший из лидера на основной полосе и лидера в списке исходной полосы (во время смены полосы).
    /// </summary>
    public static LeaderInfo FindFor(in RuleEnv e, float[] s, int i)
    {
        int lane = e.V.Lane[i];
        LeaderInfo li = Find(e, s, i, lane, s[i], e.V.Ahead[i], useRoute: true);
        if (e.V.GhostLane[i] != -1 && e.V.GhostAhead[i] != -1)
        {
            LeaderInfo g = Find(e, s, i, e.V.GhostLane[i], s[i], e.V.GhostAhead[i], useRoute: false);
            int hops = li.Hops + g.Hops;
            if (g.Gap < li.Gap)
            {
                li = g;
            }

            li.Hops = hops;
        }

        return li;
    }
}
