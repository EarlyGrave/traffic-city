namespace Sim.Core.Demand;

/// <summary>
/// Загрузка клеток по периодам (zoning-demand.md §9.1): <c>L[c, p]</c> - вчерашнее число машинных ног периода через клетку <c>c</c>. Z1 - данные и сейв (блок <c>LEDG</c>);
/// накопление и <c>Cap</c> (производное, пересчитывается при <c>NetDirty</c>) - Z5.
/// </summary>
internal sealed class CellLoads
{
    public readonly int Cells;
    public readonly int Periods;
    public readonly int[] L;           // [Cells * Periods * 2]: пара «сегодня/вчера», индекс ((c * P + p) << 1) | (day & 1)

    public CellLoads(int cells, int periods)
    {
        Cells = cells;
        Periods = periods;
        L = new int[checked(cells * periods * 2)];
    }
}
