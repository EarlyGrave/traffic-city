using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;

namespace Sim.Core.Metrics;

/// <summary>
/// Жалобы жителей (PG4, progression.md §6.2): накопитель часа по видам (место -> счёт случаев), выпуск на сдвиге часа KPI (до <see cref="PerKind"/> мест на вид со счётом
/// &gt;= <see cref="MinCount"/>, всего &lt;= <see cref="PerHour"/>, ничья - меньший индекс места), остывание места <see cref="CooldownHours"/> игровых часов, кольцо последних
/// <see cref="RingSize"/> жалоб и событие <see cref="SimEventKind.Complaint"/>. Только вывод: на поведение не влияет, вне хэша и сейва (после загрузки накопитель и остывание пусты).
/// Пишется только в однопоточных фазах (2r, 9, 9r) в фиксированном порядке; без PRNG; все массивы выделены при создании, 0 Б/тик.
/// </summary>
internal sealed class Complaints
{
    public const int Kinds = 9;             // 1-6 прежние, 7 - резерв BL3 (HighRent), 8-9 - BL2
    public const int Capacity = 256;        // [Старт] мест на вид за час; переполнение - в «прочее» (KPI его видит, жалобы - нет)
    public const int PerKind = 2;
    public const int PerHour = 6;
    public const int MinCount = 3;
    public const int CooldownHours = 6;
    public const int RingSize = 64;

    private const int Slots = Capacity;     // открытая адресация, заполнение <= MaxFill
    private const int MaxFill = Capacity * 3 / 4;

    private readonly int[] _idx = new int[Kinds * Slots];   // индекс места, -1 - пусто
    private readonly int[] _gen = new int[Kinds * Slots];
    private readonly int[] _cnt = new int[Kinds * Slots];   // < 0 - уже выпущено / исключено в этом выпуске
    private readonly int[] _used = new int[Kinds];
    private readonly long _cooldownTicks;

    /// <summary>Случаев без места в накопителе (переполнение) - только счётчик.</summary>
    public long Overflow;

    /// <summary>Кольцо последних жалоб; запись с порядковым номером <c>s</c> лежит в <c>Ring[(s - 1) % RingSize]</c>.</summary>
    public readonly ComplaintInfo[] Ring = new ComplaintInfo[RingSize];

    /// <summary>Номер последней выпущенной жалобы (0 - ни одной).</summary>
    public int Seq;

    public Complaints(int dayTicks)
    {
        Array.Fill(_idx, -1);
        _cooldownTicks = (long)Math.Max(1, dayTicks) * CooldownHours / 24;
    }

    /// <summary>Случай вида <paramref name="kind"/> у места (<paramref name="index"/>, <paramref name="gen"/>); индекс &lt; 0 - места нет.</summary>
    public void Add(ComplaintKind kind, int index, int gen) => Add(kind, index, gen, 1);

    /// <summary>Накоплено случаев вида у места в текущем часе (0 - нет; для тестов и отладки).</summary>
    public int Pending(ComplaintKind kind, int index, int gen)
    {
        int k = (int)kind - 1;
        if ((uint)k >= Kinds || index < 0)
        {
            return 0;
        }

        int b = k * Slots;
        int h = (int)(((uint)index * 2654435761u) >> 24) & (Slots - 1);
        for (int i = 0; i < Slots; i++)
        {
            int s = b + ((h + i) & (Slots - 1));
            if (_idx[s] < 0)
            {
                return 0;
            }

            if (_idx[s] == index && _gen[s] == gen)
            {
                return _cnt[s];
            }
        }

        return 0;
    }

    /// <summary>Сразу <paramref name="count"/> случаев вида <paramref name="kind"/> у места (суточные случаи жизни здания, building-lifecycle.md §2.2).</summary>
    public void Add(ComplaintKind kind, int index, int gen, int count)
    {
        int k = (int)kind - 1;
        if ((uint)k >= Kinds || index < 0)
        {
            return;
        }

        int b = k * Slots;
        int h = (int)(((uint)index * 2654435761u) >> 24) & (Slots - 1);
        for (int i = 0; i < Slots; i++)
        {
            int s = b + ((h + i) & (Slots - 1));
            if (_idx[s] == index && _gen[s] == gen)
            {
                _cnt[s] += count;
                return;
            }

            if (_idx[s] < 0)
            {
                if (_used[k] >= MaxFill)
                {
                    break;
                }

                _idx[s] = index;
                _gen[s] = gen;
                _cnt[s] = count;
                _used[k]++;
                return;
            }
        }

        Overflow++;
    }

    /// <summary>Выпуск на сдвиге часа KPI (фаза 9r): два прохода по видам 1..6 (сначала лучшее место каждого вида, затем второе), затем накопитель очищается.</summary>
    public void EmitHour(World w, CityWorld c)
    {
        int emitted = 0;
        for (int pass = 0; pass < PerKind && emitted < PerHour; pass++)
        {
            for (int k = 0; k < Kinds && emitted < PerHour; k++)
            {
                if (_used[k] > 0 && TryEmit(w, c, k))
                {
                    emitted++;
                }
            }
        }

        for (int k = 0; k < Kinds; k++)
        {
            if (_used[k] > 0)
            {
                Array.Fill(_idx, -1, k * Slots, Slots);
                _used[k] = 0;
            }
        }
    }

    private bool TryEmit(World w, CityWorld c, int k)
    {
        int b = k * Slots;
        var kind = (ComplaintKind)(k + 1);
        while (true)
        {
            int best = -1;
            for (int s = b; s < b + Slots; s++)
            {
                if (_idx[s] >= 0 && _cnt[s] >= MinCount &&
                    (best < 0 || _cnt[s] > _cnt[best] || (_cnt[s] == _cnt[best] && (_idx[s] < _idx[best] || (_idx[s] == _idx[best] && _gen[s] < _gen[best])))))
                {
                    best = s;
                }
            }

            if (best < 0)
            {
                return false;
            }

            int count = _cnt[best];
            _cnt[best] = -count;   // исключено из этого выпуска (второй проход берёт следующее)
            if (Cooling(kind, _idx[best], _gen[best], w.Tick) || !Place(w, c, kind, _idx[best], _gen[best], out EventPlace place))
            {
                continue;
            }

            Seq++;
            ref ComplaintInfo ci = ref Ring[(Seq - 1) % RingSize];
            ci = new ComplaintInfo { Tick = w.Tick, Seq = Seq, Kind = kind, Count = (ushort)Math.Min(count, ushort.MaxValue), Place = place };
            w.EmitAt(SimEventKind.Complaint, 0, (int)kind, Seq, in place, count);
            return true;
        }
    }

    // Остывание (вид, место): та же пара в кольце моложе CooldownHours игровых часов. Кольцо (64) длиннее окна: за 6 часов выпускается не больше 36 жалоб.
    private bool Cooling(ComplaintKind kind, int index, int gen, long now)
    {
        int n = Math.Min(Seq, RingSize);
        for (int i = 0; i < n; i++)
        {
            ref readonly ComplaintInfo ci = ref Ring[i];
            if (ci.Kind == kind && ci.Place.Index == index && ci.Place.Generation == gen && now - ci.Tick < _cooldownTicks)
            {
                return true;
            }
        }

        return false;
    }

    // Место жалобы и точка мира (ядро считает X/Z): сегмент - середина оси, здание - центр участка, остановка - точка на полосе. Сущности уже нет - false.
    private static bool Place(World w, CityWorld c, ComplaintKind kind, int index, int gen, out EventPlace place)
    {
        place = default;
        NetworkStore n = w.Net;
        switch (kind)
        {
            case ComplaintKind.LongWait:
                if (!n.SegmentAlive(index) || n.Segments[index].Generation != gen)
                {
                    return false;
                }

                place = PlaceEvents.SegmentPlace(n, index);
                return true;
            case ComplaintKind.LongStopWait:
                {
                    if ((uint)index >= (uint)n.BusStopCount || !n.BusStopPool.IsAlive(index) || n.BusStops[index].Generation != gen)
                    {
                        return false;
                    }

                    ref readonly BusStopData sd = ref n.BusStops[index];
                    float x = 0f;
                    float z = 0f;
                    if ((uint)sd.Lane < (uint)n.LaneCount)
                    {
                        int hint = 0;
                        LaneGeometry.Evaluate(n, sd.Lane, sd.S, ref hint, out Util.Vec2 pos, out _);
                        x = pos.X;
                        z = pos.Z;
                    }

                    place = new EventPlace(EntityKind.BusStop, index, gen, x, z);
                    return true;
                }

            default:
                {
                    BuildingStore bs = c.Buildings;
                    if (!bs.Pool.IsAlive(index) || bs.Pool.GenerationOf(index) != gen)
                    {
                        return false;
                    }

                    int lot = bs.Lot[index];
                    float x = (uint)lot < (uint)c.Lots.X.Length ? c.Lots.X[lot] : 0f;
                    float z = (uint)lot < (uint)c.Lots.Z.Length ? c.Lots.Z[lot] : 0f;
                    place = new EventPlace(EntityKind.Building, index, gen, x, z);
                    return true;
                }
        }
    }

    /// <summary>Жалобы с номером больше <paramref name="sinceSeq"/> от старых к новым (не старше кольца); возвращает число записанных.</summary>
    public int Copy(Span<ComplaintInfo> dst, int sinceSeq)
    {
        int first = Math.Max(Math.Max(sinceSeq, 0) + 1, Math.Max(1, Seq - RingSize + 1));
        int n = Math.Min(dst.Length, Seq - first + 1);
        for (int i = 0; i < n; i++)
        {
            dst[i] = Ring[(first + i - 1) % RingSize];
        }

        return Math.Max(0, n);
    }
}
