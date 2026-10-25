namespace Sim.Core.Api;

/// <summary>
/// Параметры среды выполнения (ADR-0017): не входят в хэш, сейв и JSON сценария; результат симуляции от них не зависит.
/// </summary>
public struct SimRuntimeOptions
{
    /// <summary>Максимум потоков (включая вызывающий); больше не имеет смысла для фаз по чанкам из 256 машин.</summary>
    public const int MaxWorkerThreads = 16;

    /// <summary>
    /// Число потоков, считающих параллельные фазы (включая поток, вызывающий <c>Step</c>). 0 и 1 (по умолчанию) - потоки не создаются;
    /// значения выше <see cref="MaxWorkerThreads"/> обрезаются. Воркеры (<c>WorkerThreads - 1</c> штук) создаются в <c>Create</c> и
    /// останавливаются в <c>Dispose</c>.
    /// </summary>
    public int WorkerThreads;
}
