using System;
using Sim.Core.Lod;
using Sim.Core.Metrics;

namespace Sim.Core.Agents;

/// <summary>
/// Маски кандидатов фаз 7-9 и накопители метрик, которые заполняет фаза 6 (ADR-0019, A3/A4). Всё производное: вне хэша и сейва, пересобирается
/// каждым тиком. Бит <c>i</c> - слот машины. Фаза 6 пишет биты и целые накопители своих индексов (чанки воркеров кратны 64 - слово маски
/// принадлежит одному воркеру), дальше фазы 7, 8, 8b - последовательные и правят то, что изменили сами (<see cref="Retune"/>, <see cref="Remove"/>).
/// <list type="bullet">
/// <item><see cref="Trans"/> - фаза 7: идущая смена полосы, намерение <c>LaneChangeDir</c>, <c>S &gt;= LaneLength</c>;</item>
/// <item><see cref="Arrive"/> - фаза 8: <c>Lane == DestLane &amp;&amp; S &gt;= DestS</c>, не автобус (фаза 7 пересчитывает бит у обработанных);</item>
/// <item><see cref="StuckEv"/> - фаза 9: признак «застряла» (<c>StillTicks &gt;= порога</c>) расходится с флагом <c>Stuck</c>.</item>
/// </list>
/// Накопители - целые, поэтому порядок суммирования не важен и результат совпадает с прежним проходом фазы 9 побитово.
/// </summary>
internal sealed class TickMasks
{
    public readonly ulong[] Trans;
    public readonly ulong[] Arrive;
    public readonly ulong[] StuckEv;

    // Накопители метрик по состоянию «после фазы 6 + поправки фаз 7, 8, 8b».
    public long Active;
    public long Moving;
    public long DistMm;
    public long MesoNow;
    public long MesoDistMm;
    public long Stuck;

    // Пороги тика (кладёт IntegratePhase.Prepare).
    public float StuckSpeed;
    public int StuckTicks;

    public TickMasks(int maxVehicles)
    {
        int words = (Math.Max(0, maxVehicles) + 63) >> 6;
        Trans = new ulong[words];
        Arrive = new ulong[words];
        StuckEv = new ulong[words];
    }

    /// <summary>Слов маски, покрывающих <paramref name="highWater"/> слотов.</summary>
    public static int WordsFor(int highWater) => (highWater + 63) >> 6;

    /// <summary>Начало фазы 6: обнуляет маски и накопители (однопоточно, до запуска воркеров).</summary>
    public void Begin(int highWater, float stuckSpeed, int stuckTicks)
    {
        int words = Math.Min(WordsFor(highWater), Trans.Length);
        Array.Clear(Trans, 0, words);
        Array.Clear(Arrive, 0, words);
        Array.Clear(StuckEv, 0, words);
        Active = 0;
        Moving = 0;
        DistMm = 0;
        MesoNow = 0;
        MesoDistMm = 0;
        Stuck = 0;
        StuckSpeed = stuckSpeed;
        StuckTicks = stuckTicks;
    }

    /// <summary>Шаг пути за тик в мм (то же округление, что у прежнего прохода фазы 9).</summary>
    public static long StepMm(float speed) => (long)((speed * MetricsPhase.MmPerSecondTick) + 0.5f);

    private void Apply(float speed, bool meso, int sign)
    {
        long step = StepMm(speed);
        DistMm += sign * step;
        if (meso)
        {
            MesoDistMm += sign * step;
        }

        if (speed > StuckSpeed)
        {
            Moving += sign;
        }
    }

    /// <summary>
    /// Поправка: у машины <paramref name="i"/> после фазы 6 изменились скорость и/или бит уровня (переходный клэмп фазы 7, въезд в ячейку другого
    /// уровня, остановка автобуса фазы 8b). Прежний вклад (<paramref name="oldSpeed"/>, <paramref name="oldMeso"/>) снимается, текущий добавляется.
    /// </summary>
    public void Retune(VehicleStore v, int i, float oldSpeed, bool oldMeso)
    {
        float speed = v.Speed[i];
        bool meso = v.Lod.Length != 0 && (v.Lod[i] & LodBits.Meso) != 0;
        if (speed == oldSpeed && meso == oldMeso)
        {
            return;
        }

        Apply(oldSpeed, oldMeso, -1);
        Apply(speed, meso, 1);
        if (meso != oldMeso)
        {
            MesoNow += meso ? 1 : -1;
        }
    }

    /// <summary>Поправка: живая машина <paramref name="i"/> с учтённым вкладом исчезает (деспаун фазы 8 или 8b). Вызывать до <c>Despawn</c>.</summary>
    public void Remove(VehicleStore v, int i)
    {
        bool meso = v.Lod.Length != 0 && (v.Lod[i] & LodBits.Meso) != 0;
        Active--;
        Apply(v.Speed[i], meso, -1);
        if (meso)
        {
            MesoNow--;
        }

        if (v.StillTicks[i] >= StuckTicks)
        {
            Stuck--;
        }
    }

    public static bool Test(ulong[] mask, int i) => ((mask[i >> 6] >> (i & 63)) & 1UL) != 0UL;

    public static void Set(ulong[] mask, int i, bool on)
    {
        if (on)
        {
            mask[i >> 6] |= 1UL << (i & 63);
        }
        else
        {
            mask[i >> 6] &= ~(1UL << (i & 63));
        }
    }
}
