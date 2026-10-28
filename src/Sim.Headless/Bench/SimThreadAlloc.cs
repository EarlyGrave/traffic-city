using System;
using System.Threading;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Headless.Bench;

/// <summary>
/// Счётчик аллокаций потоков симуляции: поток вызывающего + все воркеры <see cref="WorkerPool"/> (каждый читает собственный
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/> заданием пула). Фоновые потоки рантайма (tiered-JIT, таймеры) не входят:
/// <c>GC.GetTotalAllocatedBytes</c> ловил их аллокации и давал ложные ~3 Б/тик на s10k-peds с 4 потоками.
/// </summary>
internal sealed class SimThreadAlloc : IRangeJob
{
    private readonly WorkerPool? _pool;
    private readonly long[] _bytes;
    private readonly int[] _seen;
    private int _reported;

    public SimThreadAlloc(Simulation sim)
    {
        _pool = sim.World.Pool;
        int n = _pool is { Workers: > 1 } ? _pool.Workers : 1;
        _bytes = new long[n];
        _seen = new int[n];
    }

    /// <summary>Сумма счётчиков всех потоков симуляции; при неотклике воркера (перегрузка) повторяет до 5 раз.</summary>
    public long Snapshot()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            long v = TrySnapshot();
            if (v >= 0)
            {
                return v;
            }
        }

        throw new InvalidOperationException("воркеры пула не откликнулись при чтении счётчиков аллокаций");
    }

    private long TrySnapshot()
    {
        if (_pool is not { Workers: > 1 })
        {
            return GC.GetAllocatedBytesForCurrentThread();
        }

        Array.Clear(_seen);
        Volatile.Write(ref _reported, 0);
        _pool.Wake();
        _pool.Run(this, _pool.Workers * 4 * WorkerPool.ChunkSize);
        _pool.Rest();
        if (Volatile.Read(ref _reported) != _bytes.Length)
        {
            return -1;
        }

        long sum = 0;
        foreach (long b in _bytes)
        {
            sum += b;
        }

        return sum;
    }

    public void RunRange(int worker, int begin, int end)
    {
        if (Interlocked.Exchange(ref _seen[worker], 1) != 0)
        {
            return;
        }

        _bytes[worker] = GC.GetAllocatedBytesForCurrentThread();
        Interlocked.Increment(ref _reported);
        long deadline = Environment.TickCount64 + 2000;
        var sw = new SpinWait();
        while (Volatile.Read(ref _reported) < _bytes.Length && Environment.TickCount64 < deadline)
        {
            sw.SpinOnce(-1);
        }
    }
}
