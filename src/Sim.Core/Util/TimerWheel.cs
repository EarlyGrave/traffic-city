using System;
using System.Diagnostics;

namespace Sim.Core.Util;

/// <summary>
/// Колесо таймеров (pedestrians.md §4.3): события «сущность <c>key</c> проснётся в тик <c>when</c>». Корзины по <c>tick &amp; (W - 1)</c>,
/// более далёкие события лежат в списке переполнения и раз в <c>W</c> тиков (на тике, кратном <c>W</c>) переносятся в корзины.
/// Всё на плоских <c>int[]</c>, ключ принадлежит ровно одному списку; после конструктора аллокаций нет. Порядок выдачи внутри тика - по возрастанию ключа.
/// Ключ можно ставить повторно только после выдачи (или <see cref="Clear"/>): отмены нет, сущность проверяет себя при пробуждении.
/// <see cref="Collect"/> зовут на каждом тике подряд (перенос из переполнения привязан к тикам, кратным <c>W</c>).
/// </summary>
internal sealed class TimerWheel
{
    public const int W = 4096;

    private readonly int[] _head = new int[W];
    private readonly int[] _next;
    private readonly int[] _prev;
    private readonly long[] _when;
    private int _overflow = -1;

    public TimerWheel(int keys)
    {
        _next = new int[Math.Max(1, keys)];
        _prev = new int[_next.Length];
        _when = new long[_next.Length];
        Clear();
    }

    public int Keys => _next.Length;

    /// <summary>Сколько ключей стоит в колесе (корзины и переполнение).</summary>
    public int Count { get; private set; }

    public void Clear()
    {
        Array.Fill(_head, -1);
        Array.Fill(_next, -2);   // -2: ключа в колесе нет
        Array.Fill(_prev, -1);
        Array.Fill(_when, -1L);
        _overflow = -1;
        Count = 0;
    }

    public bool Contains(int key) => _next[key] != -2;

    /// <summary>Ставит <paramref name="key"/> на тик <paramref name="when"/> &gt; <paramref name="now"/> (<paramref name="now"/> - тик, который исполняется или только что закончен).</summary>
    public void Schedule(int key, long when, long now)
    {
        Debug.Assert(_next[key] == -2, "key already scheduled");
        Debug.Assert(when > now, "event must be in the future");
        _when[key] = when;
        Count++;
        if (when - now < W)
        {
            int b = (int)(when & (W - 1));
            Link(key, ref _head[b]);
        }
        else
        {
            Link(key, ref _overflow);
        }
    }

    /// <summary>Снимает <paramref name="key"/> с колеса (событие не наступит); ключа нет - ничего. O(1): списки двусвязные.</summary>
    public void Cancel(int key)
    {
        int next = _next[key];
        if (next == -2)
        {
            return;
        }

        int prev = _prev[key];
        if (prev >= 0)
        {
            _next[prev] = next;
        }
        else
        {
            int b = (int)(_when[key] & (W - 1));
            if (_head[b] == key)
            {
                _head[b] = next;
            }
            else
            {
                Debug.Assert(_overflow == key, "head of an unknown list");
                _overflow = next;
            }
        }

        if (next >= 0)
        {
            _prev[next] = prev;
        }

        _next[key] = -2;
        _prev[key] = -1;
        _when[key] = -1;
        Count--;
    }

    private void Link(int key, ref int head)
    {
        int h = head;
        _next[key] = h;
        _prev[key] = -1;
        if (h >= 0)
        {
            _prev[h] = key;
        }

        head = key;
    }

    /// <summary>Кладёт в <paramref name="dst"/> ключи, наступившие в тик <paramref name="tick"/>, по возрастанию; возвращает их число (<c>dst</c> не короче числа ключей).</summary>
    public int Collect(long tick, Span<int> dst)
    {
        if ((tick & (W - 1)) == 0)
        {
            Migrate(tick);
        }

        int b = (int)(tick & (W - 1));
        int n = 0;
        for (int k = _head[b]; k >= 0;)
        {
            int next = _next[k];
            Debug.Assert(_when[k] == tick, "stale timer in bucket");
            dst[n++] = k;
            _next[k] = -2;
            _prev[k] = -1;
            _when[k] = -1;
            k = next;
        }

        _head[b] = -1;
        Count -= n;
        if (n > 1)
        {
            dst.Slice(0, n).Sort();
        }

        return n;
    }

    private void Migrate(long tick)
    {
        int k = _overflow;
        _overflow = -1;
        while (k >= 0)
        {
            int next = _next[k];
            long when = _when[k];
            if (when - tick < W)
            {
                int b = (int)(when & (W - 1));
                Link(k, ref _head[b]);
            }
            else
            {
                Link(k, ref _overflow);
            }

            k = next;
        }
    }
}
