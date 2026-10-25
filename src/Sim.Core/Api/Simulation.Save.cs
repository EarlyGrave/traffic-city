using System;
using Sim.Core.Save;

namespace Sim.Core.Api;

// Сохранение и загрузка (save-format.md §6, ADR-0012). Ядро не знает про файлы: байты уходят в Span / SaveSink, приходят из Span / SaveSource.
public sealed partial class Simulation
{
    /// <summary>Фасад вокруг уже построенного мира (загрузчик сейва).</summary>
    internal static Simulation Wrap(World world) => new(world);

    /// <summary>
    /// Точная длина контейнера в байтах (с блоками хоста длиной из <paramref name="host"/>); -1 - симуляция освобождена или описание блоков хоста неверно.
    /// Только между <see cref="Step"/>.
    /// </summary>
    public long MeasureSave(ReadOnlySpan<HostBlock> host = default)
    {
        if (_disposed)
        {
            return -1;
        }

        return WorldSaver.Measure(_w, host, out long length) == SaveStatus.Ok ? length : -1;
    }

    /// <summary>
    /// Снимок состояния в готовый буфер: канонический контейнер (одно состояние - одни байты). Состояние не меняется (хэш до и после равен). В steady state
    /// не аллоцирует (первый вызов создаёт рабочие буферы; блоки хоста копируются в них). Только между <see cref="Step"/>.
    /// </summary>
    public SaveStatus Save(Span<byte> dest, out long written, ReadOnlySpan<HostBlock> host = default, ReadOnlySpan<byte> hostData = default)
    {
        written = 0;
        return _disposed ? SaveStatus.Disposed : WorldSaver.Save(_w, dest, host, hostData, out written);
    }

    /// <summary>То же в поток хоста (<see cref="SaveSink"/> вызывается кусками не более 1 МБ): для сейвов больше 2 ГБ и FFI-колбэка.</summary>
    public SaveStatus Save(SaveSink sink, ReadOnlySpan<HostBlock> host = default, ReadOnlySpan<byte> hostData = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        return _disposed ? SaveStatus.Disposed : WorldSaver.Save(_w, sink, host, hostData);
    }

    /// <summary>
    /// Загрузка из памяти: <c>Create</c> без генератора. Ошибка - код (не исключение) и место поломки в <paramref name="report"/>; мир при этом не создаётся.
    /// Воркеры запускаются по <paramref name="rt"/> (число потоков в сейв не входит).
    /// </summary>
    public static LoadStatus Load(ReadOnlySpan<byte> src, in LoadOptions o, in SimRuntimeOptions rt, out Simulation? sim, out LoadReport report) =>
        WorldLoader.Load(src, in o, in rt, out sim, out report);

    /// <summary>То же из потока (<see cref="SaveSource"/> читается целиком).</summary>
    public static LoadStatus Load(SaveSource src, in LoadOptions o, in SimRuntimeOptions rt, out Simulation? sim, out LoadReport report)
    {
        ArgumentNullException.ThrowIfNull(src);
        return WorldLoader.Load(src, in o, in rt, out sim, out report);
    }

    /// <summary>Заголовок сейва по первым 64 байтам (без загрузки): список сейвов, предпросмотр.</summary>
    public static LoadStatus InspectSave(ReadOnlySpan<byte> head, out SaveHeaderInfo info) => SaveReader.Inspect(head, out info);

    /// <summary>Блоки хоста из загруженного сейва (камера, meta): срезы <see cref="LoadedHostData"/>. Пусто, если сейв не из <c>Load</c> или блоков нет.</summary>
    public ReadOnlySpan<HostBlock> LoadedHostBlocks => _disposed ? default : _w.LoadedHostBlocks;

    public ReadOnlySpan<byte> LoadedHostData => _disposed ? default : _w.LoadedHostData;
}
