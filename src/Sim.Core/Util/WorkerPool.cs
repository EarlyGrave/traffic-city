#pragma warning disable RS0030 // единственное место ядра, где разрешён System.Threading.Thread (ADR-0017)
using System;
using System.Threading;

namespace Sim.Core.Util;

/// <summary>Задание параллельной фазы: функция диапазона индексов. Пишет только данные индексов диапазона и аккумуляторы воркера <c>worker</c>.</summary>
internal interface IRangeJob
{
    void RunRange(int worker, int begin, int end);
}

/// <summary>
/// Исполнитель диапазонов (ADR-0017): <c>W - 1</c> постоянных потоков + вызывающий (воркер 0). Чанки по <see cref="ChunkSize"/> индексов
/// раздаются атомарным счётчиком; воркер заявляется на чанк CAS'ом по счётчику эпохи, поэтому главный поток не ждёт воркера, который
/// не успел начать (он ждёт только уже взятые чанки). Без аллокаций после конструктора. Воркеры крутятся (spin) внутри <c>Step</c>
/// (<see cref="Wake"/> .. <see cref="Rest"/>) и спят на событии между вызовами (замер park против «всегда spin»: hotpath.md §8.6).
/// </summary>
internal sealed class WorkerPool : IDisposable
{
    public const int ChunkSize = 256;

    private const int Closed = int.MaxValue;

    // Диагностика для теста «потоки не утекают»: число запущенных и ещё не завершённых воркеров по всему процессу.
    private static int _liveThreads;

    private readonly Thread[] _threads;
    private readonly ManualResetEventSlim _wake = new(false, 0);

    // Старшие 32 бита - эпоха задания, младшие - номер следующего чанка (Closed - задание завершено, брать нечего).
    private long _next = Closed;
    private IRangeJob? _job;
    private int _count;
    private int _chunks;
    private int _done;
    private int _epoch;
    private int _awake;
    private int _stop;
    private bool _disposed;

    /// <param name="workers">общее число потоков, включая вызывающий (≥ 1).</param>
    public WorkerPool(int workers)
    {
        Workers = Math.Max(1, workers);
        _threads = new Thread[Workers - 1];
        for (int k = 0; k < _threads.Length; k++)
        {
            int id = k + 1;
            Interlocked.Increment(ref _liveThreads);
            _threads[k] = new Thread(() => WorkerLoop(id))
            {
                IsBackground = true,
                Name = "sim-worker-" + id,
            };
            _threads[k].Start();
        }
    }

    /// <summary>Воркеры всех пулов процесса, запущенные и не завершившиеся (диагностика, на симуляцию не влияет).</summary>
    public static int LiveThreads => Volatile.Read(ref _liveThreads);

    /// <summary>Общее число потоков, включая вызывающий.</summary>
    public int Workers { get; }

    /// <summary>Начало <c>Step</c>: будит воркеров, дальше они крутятся до <see cref="Rest"/>.</summary>
    public void Wake()
    {
        if (_threads.Length == 0)
        {
            return;
        }

        Volatile.Write(ref _awake, 1);
        _wake.Set();
    }

    /// <summary>Конец <c>Step</c>: воркеры уходят в сон.</summary>
    public void Rest()
    {
        if (_threads.Length == 0)
        {
            return;
        }

        Volatile.Write(ref _awake, 0);
        _wake.Reset();
    }

    /// <summary>Выполняет <paramref name="job"/> на диапазоне [0, count); возвращается, когда все чанки закончены.</summary>
    public void Run(IRangeJob job, int count)
    {
        int chunks = (count + ChunkSize - 1) / ChunkSize;
        if (_threads.Length == 0 || chunks <= 1 || _disposed)
        {
            if (count > 0)
            {
                job.RunRange(0, 0, count);
            }

            return;
        }

        _job = job;
        _count = count;
        Volatile.Write(ref _chunks, chunks);
        Volatile.Write(ref _done, 0);
        _epoch++;
        Volatile.Write(ref _next, (long)_epoch << 32); // публикация: после неё воркеры могут брать чанки
        while (true)
        {
            int k = (int)Interlocked.Increment(ref _next) - 1;
            if (k >= chunks)
            {
                break;
            }

            RunChunk(0, k);
            Interlocked.Increment(ref _done);
        }

        if (Volatile.Read(ref _done) != chunks)
        {
            var sw = new SpinWait();
            while (Volatile.Read(ref _done) != chunks)
            {
                sw.SpinOnce(-1);
            }
        }

        Volatile.Write(ref _next, ((long)_epoch << 32) | (uint)Closed); // закрыть: поля задания больше не трогают
        _job = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Volatile.Write(ref _stop, 1);
        _wake.Set();
        for (int k = 0; k < _threads.Length; k++)
        {
            _threads[k].Join();
        }

        _wake.Dispose();
    }

    private void RunChunk(int worker, int k)
    {
        int begin = k * ChunkSize;
        int end = Math.Min(_count, begin + ChunkSize);
        _job!.RunRange(worker, begin, end);
    }

    private void WorkerLoop(int id)
    {
        try
        {
            WorkerBody(id);
        }
        finally
        {
            Interlocked.Decrement(ref _liveThreads);
        }
    }

    private void WorkerBody(int id)
    {
        var sw = new SpinWait();
        while (Volatile.Read(ref _stop) == 0)
        {
            if (Volatile.Read(ref _awake) == 0)
            {
                _wake.Wait();
                sw = default;
                continue;
            }

            long x = Volatile.Read(ref _next);
            int k = (int)x;
            if (k < Volatile.Read(ref _chunks)
                && Interlocked.CompareExchange(ref _next, x + 1, x) == x)
            {
                // CAS успел: эпоха x ещё идёт (чанк k не взят и не закончен), поля задания принадлежат ей.
                RunChunk(id, k);
                Interlocked.Increment(ref _done);
                sw = default;
                continue;
            }

            sw.SpinOnce(-1);
        }
    }
}
