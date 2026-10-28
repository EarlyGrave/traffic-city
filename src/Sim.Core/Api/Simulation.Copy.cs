using System;

namespace Sim.Core.Api;

// Копия видимого снапшота для клиента на другом потоке (ADR-0024 п. 3, scale.md S8). Только чтение состояния между Step; хэш не затрагивает.
public sealed partial class Simulation
{
    /// <summary>Номер вида: растёт при каждом <see cref="SetRenderView"/>/<see cref="SetRenderViewAll"/>. Не в хэше.</summary>
    public int ViewVersion => _disposed ? 0 : _w.RenderView.Version;

    /// <summary>
    /// Компактная копия снапшота машин: только слоты с <c>Flags != 0</c> (для вида «всё», прямоугольника и пустого вида это ровно записанные фазой 10),
    /// в порядке слотов. <paramref name="cur"/>[k] - слот на <see cref="SnapshotTick"/>, <paramref name="prev"/>[k] - тот же слот тиком раньше
    /// (нули, если слот вне прошлого буфера или не записан). Сопоставление - по <c>Index</c> и <c>Generation</c> (§5.2). Используется
    /// <c>min(cur.Length, prev.Length)</c> слотов. Возвращает false, если живых слотов больше ёмкости: скопированы первые, а <paramref name="count"/> -
    /// сколько нужно. Аллокаций нет; вызывать между <c>Step</c> с того же потока, что и <c>Step</c>.
    /// </summary>
    public bool CopyVisibleVehicles(Span<VehicleRenderData> cur, Span<VehicleRenderData> prev, out int count)
    {
        ReadOnlySpan<VehicleRenderData> c = Vehicles;
        ReadOnlySpan<VehicleRenderData> p = PreviousVehicles;
        int cap = Math.Min(cur.Length, prev.Length);
        int n = 0;
        for (int i = 0; i < c.Length; i++)
        {
            if (c[i].Flags == 0)
            {
                continue;
            }

            if (n < cap)
            {
                cur[n] = c[i];
                prev[n] = i < p.Length ? p[i] : default;
            }

            n++;
        }

        count = n;
        return n <= cap;
    }

    /// <summary>То же для пешеходов (<see cref="Pedestrians"/>/<see cref="PreviousPedestrians"/>); без секции peds - 0 и true.</summary>
    public bool CopyVisiblePeds(Span<PedestrianRenderData> cur, Span<PedestrianRenderData> prev, out int count)
    {
        ReadOnlySpan<PedestrianRenderData> c = Pedestrians;
        ReadOnlySpan<PedestrianRenderData> p = PreviousPedestrians;
        int cap = Math.Min(cur.Length, prev.Length);
        int n = 0;
        for (int i = 0; i < c.Length; i++)
        {
            if (c[i].Flags == 0)
            {
                continue;
            }

            if (n < cap)
            {
                cur[n] = c[i];
                prev[n] = i < p.Length ? p[i] : default;
            }

            n++;
        }

        count = n;
        return n <= cap;
    }
}
