using System;
using Sim.Core.Tools;
using Sim.Core.Util;

namespace Sim.Core.Api;

// Инструменты игрока: превью (player-tools.md §7). Между Step, тот же поток.
public sealed partial class Simulation
{
    /// <summary>
    /// Превью команды инструмента против применённого состояния (очередь команд не учитывается): вердикт общего валидатора, стоимость, привязка, последствия.
    /// Состояние не меняется (хэш и байты сейва до/после равны). <paramref name="issues"/> - предупреждения и причина отказа (считаются все, записывается сколько влезло),
    /// <paramref name="axis"/> - ось будущей дороги. Без аллокаций (scratch связности выделяется один раз при первом превью, которому нужна связность).
    /// Для команд, которых валидатор не знает, - <see cref="RejectReason.NotSupported"/>.
    /// </summary>
    public RejectReason Preview(in Command cmd, out PreviewResult r, Span<PreviewIssue> issues, Span<Vec2> axis = default, PreviewOptions flags = PreviewOptions.None)
    {
        if (_disposed)
        {
            r = default;
            r.RoutesThrough = -1;
            r.Verdict = RejectReason.NotSupported;
            return r.Verdict;
        }

        return ToolPreview.Run(_w, in cmd, out r, issues, axis, flags);
    }

    /// <summary>Стоимости и границы инструментов (конфигурация хоста: в сейв и хэш не входит, после <c>Load</c> задаётся заново).</summary>
    public void SetToolsConfig(in ToolsDesc tools)
    {
        if (!_disposed)
        {
            _w.ToolsCfg = tools;
        }
    }

    /// <summary>Текущая конфигурация инструментов.</summary>
    public ToolsDesc ToolsConfig => _disposed ? default : _w.ToolsCfg;
}
