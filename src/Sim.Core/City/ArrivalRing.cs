using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Кольцо прибытий (zoning-demand.md §4.2, §12): машина (фаза 8, до деспауна) и пешеход (событие 4p, до освобождения) кладут сюда индекс жителя, фаза 9r разбирает кольцо по порядку.
/// Порядок кладущих фаз фиксирован (4p, затем 8; внутри фазы - по возрастанию индекса агента), поэтому порядок обработки детерминирован. Кольцо пусто на границе тиков, в сейв не входит.
/// Ёмкость = число жителей: у жителя один агент, дважды за тик он прибыть не может.
/// </summary>
internal sealed class ArrivalRing
{
    private readonly RingBuffer<int> _ring;

    public ArrivalRing(int capacity)
    {
        _ring = new RingBuffer<int>(capacity < 1 ? 1 : capacity);
    }

    public int Count => _ring.Count;

    public bool IsEmpty => _ring.IsEmpty;

    /// <summary>false - кольцо полно (невозможно по построению; вызывающий считает ошибкой).</summary>
    public bool Push(int resident) => _ring.TryEnqueue(in resident);

    public bool TryPop(out int resident) => _ring.TryDequeue(out resident);

    public void Clear() => _ring.Clear();
}
