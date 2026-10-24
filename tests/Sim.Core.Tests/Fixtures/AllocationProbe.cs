using System;

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
}
