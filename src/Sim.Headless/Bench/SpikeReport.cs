using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Sim.Headless.Bench;

/// <summary>
/// Самые долгие тики окна замера (<c>bench --phases --spike-report</c>, N3): индекс тика, время и во сколько раз каждая фаза длиннее своей медианы в окне.
/// Пик всех параллельных фаз сразу (x2-x8) - хост (вытеснение воркеров соседями, см. <c>environment.host_foreign_cores</c>); пик одной фазы - алгоритм.
/// Вызывается до сортировки <paramref name="samples"/>.
/// </summary>
internal static class SpikeReport
{
    public static void Write(double[] samples, PhaseTimer timer, bool peds, TextWriter w)
    {
        int count = peds ? PhaseTimer.TotalCount : PhaseTimer.PhaseCount;
        double[][] ph = new double[samples.Length][];
        double[] med = new double[count];
        for (int p = 0; p < count; p++)
        {
            double[] col = new double[samples.Length];
            for (int t = 0; t < samples.Length; t++)
            {
                col[t] = timer.TickBreakdown(t, peds)?[p] ?? 0;
            }

            Array.Sort(col);
            med[p] = col[col.Length / 2];
        }

        double total = samples.OrderBy(x => x).ElementAt(samples.Length / 2);
        w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"spike-report: median tick {total:F2} ms; phase medians (ms): {string.Join(" ", Enumerable.Range(0, count).Select(p => $"{PhaseTimer.Names[p]}={med[p]:F2}"))}"));
        foreach (int t in Enumerable.Range(0, samples.Length).OrderByDescending(i => samples[i]).Take(10))
        {
            double[]? b = timer.TickBreakdown(t, peds);
            string parts = b == null ? "-" : string.Join(" ", Enumerable.Range(0, count).Where(p => b[p] > 1.5 * med[p] && b[p] > 0.5).Select(p => $"{PhaseTimer.Names[p]}={b[p]:F1}(x{b[p] / Math.Max(med[p], 1e-3):F1})"));
            w.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  tick {t,5} (mod 20={t % 20,2}, mod 100={t % 100,3}): {samples[t]:F1} ms (x{samples[t] / total:F1}); inflated phases: {parts}"));
        }
    }
}
