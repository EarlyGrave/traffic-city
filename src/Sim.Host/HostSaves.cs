using System;
using System.Text;
using Sim.Core.Api;

namespace Sim.Host;

/// <summary>
/// Сохранение и загрузка мира через хост (save-format.md §6, ADR-0012, ADR-0024 п. 5): ядром владеет поток симуляции, поэтому снимок делается под
/// <see cref="SimThreadHost.AcquireExclusive"/>, а загрузка создаёт новое ядро (клиент затем пересоздаёт <see cref="SimThreadHost"/> и слои по нему).
/// Файлов здесь нет: байты отдаёт/принимает клиент. Метаданные клиента (сценарий, подпись для списка, камера) - JSON в блоке хоста <c>meta</c>
/// (save-format.md §6: имя сценария, версия игры, время), ядро его не разбирает.
/// </summary>
public static class HostSaves
{
    /// <summary>FourCC блока <c>meta</c> (ASCII-байты в порядке имени, как в контейнере).</summary>
    public const uint MetaBlockId = 'm' | ('e' << 8) | ('t' << 16) | ((uint)'a' << 24);

    /// <summary>Заголовок сейва - первые 64 байта (<see cref="Simulation.InspectSave"/>).</summary>
    public const int HeaderBytes = 64;

    /// <summary>
    /// Снимок мира. Ядро стоит (эксклюзивный доступ), пока строится <paramref name="metaJson"/> (<paramref name="makeMeta"/> видит то же состояние, что попадёт в файл:
    /// тик, хэш, жители) и пишутся байты. Состояние ядра не меняется. Возвращает код ядра; при ошибке <paramref name="bytes"/> пусто.
    /// </summary>
    public static SaveStatus Save(SimThreadHost host, Func<Simulation, string>? makeMeta, out byte[] bytes, out string metaJson, out long tick, out ulong stateHash)
    {
        ArgumentNullException.ThrowIfNull(host);
        bytes = Array.Empty<byte>();
        metaJson = string.Empty;
        tick = 0;
        stateHash = 0;
        using ExclusiveAccess ex = host.AcquireExclusive();
        Simulation? sim = ex.Simulation;
        if (sim == null)
        {
            return SaveStatus.Disposed;
        }

        tick = sim.Tick;
        stateHash = sim.ComputeStateHash();
        metaJson = makeMeta?.Invoke(sim) ?? string.Empty;
        byte[] meta = Encoding.UTF8.GetBytes(metaJson);
        HostBlock[] blocks = meta.Length == 0 ? Array.Empty<HostBlock>() : new[] { new HostBlock { Id = MetaBlockId, Offset = 0, Length = meta.Length } };
        long size = sim.MeasureSave(blocks);
        if (size < 0)
        {
            return SaveStatus.InvalidHostBlock;
        }

        var buf = new byte[size];
        SaveStatus st = sim.Save(buf, out long written, blocks, meta);
        if (st != SaveStatus.Ok)
        {
            return st;
        }

        bytes = written == size ? buf : buf.AsSpan(0, (int)written).ToArray();
        return SaveStatus.Ok;
    }

    /// <summary>
    /// Загрузка из байтов (строгая проверка хэша состояния, если версии хэша совпали). <paramref name="metaJson"/> - блок <c>meta</c> хоста или пусто
    /// (корпус тестов и чужие сейвы). Мир создаётся без генератора; число потоков - из <paramref name="rt"/>.
    /// </summary>
    public static LoadStatus Load(ReadOnlySpan<byte> bytes, in SimRuntimeOptions rt, out Simulation? sim, out string metaJson, out LoadReport report)
    {
        var o = new LoadOptions { StrictHash = 1 };
        LoadStatus st = Simulation.Load(bytes, in o, in rt, out sim, out report);
        metaJson = string.Empty;
        if (st != LoadStatus.Ok || sim == null)
        {
            return st;
        }

        ReadOnlySpan<HostBlock> blocks = sim.LoadedHostBlocks;
        for (int i = 0; i < blocks.Length; i++)
        {
            if (blocks[i].Id == MetaBlockId)
            {
                metaJson = Encoding.UTF8.GetString(sim.LoadedHostData.Slice(blocks[i].Offset, blocks[i].Length));
                break;
            }
        }

        return st;
    }

    /// <summary>
    /// Заголовок сейва по началу файла <c>.tcsave</c> (список сейвов): тик, версия мира и хэш без распаковки и загрузки. В конверте заголовок контейнера
    /// продублирован в байтах 32..96, у сырого контейнера - первые 64 байта; нужны первые 96 байт файла.
    /// </summary>
    public static LoadStatus Peek(ReadOnlySpan<byte> fileHead, out SaveHeaderInfo info)
    {
        ReadOnlySpan<byte> head = SaveEnvelope.IsEnvelope(fileHead)
            ? (fileHead.Length >= SaveEnvelope.EnvelopeHeaderSize ? fileHead.Slice(32, HeaderBytes) : default)
            : (fileHead.Length > HeaderBytes ? fileHead[..HeaderBytes] : fileHead);
        return Simulation.InspectSave(head, out info);
    }
}
