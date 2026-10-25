using System;
using System.Threading;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Tests.Fixtures;

/// <summary>
/// Устойчивый замер аллокаций окна кода текущим потоком. Два источника ложных ненулей, оба зависят от соседних параллельных
/// тестов, а не от кода под тестом:
/// <list type="number">
/// <item>Первый вход процесса в редкий путь внутри окна (первая вытеснялка кэша, первый деспаун и т.п.): JIT/загрузчик типов кладёт
/// в управляемую кучу потока десятки байт - килобайты. Если путь уже прогрет другим тестом, байт нет, поэтому падение плавающее.
/// Лечится тем, что окно сначала выполняется один раз без замера: измеряется уже второе прохождение установившегося режима.</item>
/// <item><see cref="GC.GetAllocatedBytesForCurrentThread"/> точен, пока в окне нет сборки мусора. GC, запущенный другим потоком,
/// обнуляет allocation context потока, и счётчик прыгает на неиспользованный остаток контекста (до ~8 КБ) без аллокаций.
/// Окно с изменившимся <see cref="GC.CollectionCount"/> отбрасывается и повторяется. Фоновый GC считается невидимым для
/// CollectionCount, поэтому в csproj отключён concurrent GC.</item>
/// </list>
/// <see cref="MeasureSimThreads"/> - то же для симуляции с воркерами (ADR-0017): считает аллокации потока теста и потоков WorkerPool
/// (каждый читает свой счётчик), но не фоновых потоков рантайма. <c>GC.GetTotalAllocatedBytes</c> для этого не годится: tiered-JIT,
/// таймеры и xUnit аллоцируют ~170 Б в произвольном окне, и под нагрузкой (окно длиннее) это попадает в каждое окно подряд.
/// Окно, прошедшее оба условия, возвращается как есть: реальная аллокация ядра (на каждый тик/запрос) не маскируется.
/// Код в окне должен быть установившимся режимом: он выполняется минимум дважды.
/// </summary>
internal static class AllocationProbe
{
    private const int MaxWindows = 200;

    /// <summary>Прогревающий проход окна, затем замер; возвращает байты, аллоцированные потоком в первом окне без GC.</summary>
    /// <param name="executions">Сколько раз окно выполнено всего, включая прогревающее (>= 2); нужно тестам, чьи пост-проверки зависят от числа тиков.</param>
    public static long Measure(Action window, out int executions)
    {
        window();
        for (int attempt = 1; attempt <= MaxWindows; attempt++)
        {
            int gcBefore = GC.CollectionCount(0);
            long before = GC.GetAllocatedBytesForCurrentThread();
            window();
            long after = GC.GetAllocatedBytesForCurrentThread();
            if (GC.CollectionCount(0) == gcBefore)
            {
                executions = 1 + attempt;
                return after - before;
            }
        }

        throw new InvalidOperationException(
            $"GC срабатывал в каждом из {MaxWindows} окон замера: измерить аллокации потока нельзя (само окно аллоцирует много или процесс перегружен).");
    }

    public static long Measure(Action window) => Measure(window, out _);

    /// <summary>Как <see cref="Measure(Action)"/>, но суммирует поток теста и все потоки пула воркеров симуляции <paramref name="sim"/>.</summary>
    public static long MeasureSimThreads(Simulation sim, Action window)
    {
        WorkerPool? pool = sim.World.Pool;
        var job = new SnapshotJob(pool?.Workers ?? 1);
        window();
        for (int attempt = 1; attempt <= MaxWindows; attempt++)
        {
            int gcBefore = GC.CollectionCount(0);
            long before = job.Snapshot(pool);
            window();
            long after = job.Snapshot(pool);
            if (before >= 0 && after >= 0 && GC.CollectionCount(0) == gcBefore)
            {
                return after - before;
            }
        }

        throw new InvalidOperationException(
            $"GC срабатывал или воркеры не откликались в каждом из {MaxWindows} окон замера: измерить аллокации потоков симуляции нельзя.");
    }

    /// <summary>Задание пула: каждый поток один раз читает собственный счётчик аллокаций и ждёт остальных (иначе один воркер забрал бы все чанки).</summary>
    private sealed class SnapshotJob : IRangeJob
    {
        private readonly long[] _bytes;
        private readonly int[] _seen;
        private int _reported;

        public SnapshotJob(int workers)
        {
            _bytes = new long[workers];
            _seen = new int[workers];
        }

        /// <summary>Сумма счётчиков всех потоков симуляции или -1, если воркер не успел откликнуться (машина перегружена): окно повторяют.</summary>
        public long Snapshot(WorkerPool? pool)
        {
            if (pool is null || pool.Workers <= 1)
            {
                return GC.GetAllocatedBytesForCurrentThread();
            }

            Array.Clear(_seen);
            Volatile.Write(ref _reported, 0);
            pool.Wake();
            pool.Run(this, pool.Workers * 4 * WorkerPool.ChunkSize);
            pool.Rest();
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
}
