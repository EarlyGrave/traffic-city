using System;

namespace Sim.Core.Agents;

/// <summary>
/// Маски кандидатов фаз 7 и 8, которые заполняет фаза 6 (ADR-0019, A3). Всё производное: вне хэша и сейва, пересобирается каждым тиком.
/// Бит <c>i</c> - слот машины. Фаза 6 пишет биты своих индексов (чанки воркеров кратны 64 - слово маски принадлежит одному воркеру),
/// фаза 7 пересчитывает <see cref="Arrive"/> у обработанных машин.
/// <list type="bullet">
/// <item><see cref="Trans"/> - фаза 7: идущая смена полосы, намерение <c>LaneChangeDir</c>, <c>S &gt;= LaneLength</c>;</item>
/// <item><see cref="Arrive"/> - фаза 8: <c>Lane == DestLane &amp;&amp; S &gt;= DestS</c>, не автобус.</item>
/// </list>
/// </summary>
internal sealed class TickMasks
{
    public readonly ulong[] Trans;
    public readonly ulong[] Arrive;

    public TickMasks(int maxVehicles)
    {
        int words = (Math.Max(0, maxVehicles) + 63) >> 6;
        Trans = new ulong[words];
        Arrive = new ulong[words];
    }

    /// <summary>Слов маски, покрывающих <paramref name="highWater"/> слотов.</summary>
    public static int WordsFor(int highWater) => (highWater + 63) >> 6;

    /// <summary>Начало фазы 6: обнуляет маски (однопоточно, до запуска воркеров).</summary>
    public void Begin(int highWater)
    {
        int words = Math.Min(WordsFor(highWater), Trans.Length);
        Array.Clear(Trans, 0, words);
        Array.Clear(Arrive, 0, words);
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
