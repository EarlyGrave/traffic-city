using Sim.Core.City;

namespace Sim.Core.Api;

/// <summary>Внешнее подключение для клиента (маркер края карты, new-game-scratch.md §2.1).</summary>
public struct OutsideInfo
{
    public int Node;                // узел сети (NodeFlags.Outside)
    public float X;                 // позиция узла, м
    public float Z;
    public int JobsTotal;           // внешние места
    public int JobsTaken;           // заняты жителями
    public int ArrivalsToday;       // доехало за текущие сутки
    public int ArrivalsInTransit;   // переселенцы в пути от узла
}

// Внешние подключения (ADR-0037): маркеры въезда и сводка. Без секции outside - 0/false.
public sealed partial class Simulation
{
    /// <summary>Число внешних подключений (0 без секции <c>outside</c>).</summary>
    public int OutsideCount => _disposed || _w.City?.Outside == null ? 0 : _w.City.Outside.Count;

    /// <summary>Подключение <paramref name="k"/>; <c>false</c> вне диапазона. Подсчёт «в пути» - проход по жителям (не для каждого кадра на миллионе жителей).</summary>
    public bool GetOutside(int k, out OutsideInfo info)
    {
        info = default;
        CityWorld? c = _disposed ? null : _w.City;
        OutsideState? o = c?.Outside;
        if (c == null || o == null || (uint)k >= (uint)o.Count)
        {
            return false;
        }

        int node = o.Node[k];
        if (_w.Net.NodeAlive(node))
        {
            info.X = _w.Net.Nodes[node].Position.X;
            info.Z = _w.Net.Nodes[node].Position.Z;
        }

        info.Node = node;
        info.JobsTotal = o.Jobs[k];
        info.JobsTaken = o.Taken[k];
        info.ArrivalsToday = o.ArrivalsDay[k];
        int code = OutsideRef.Of(k);
        CityResidents r = c.Residents;
        int plan = CityResidents.MaxTours;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (r.Pool.IsAlive(i) && (r.Flags[i] & ResidentFlags.Arriving) != 0 && r.PlanDest[i * plan] == code)
            {
                info.ArrivalsInTransit++;
            }
        }

        return true;
    }
}
