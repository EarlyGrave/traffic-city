using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Host;

namespace TrafficCity.Tools;

/// <summary>Итог действия игрока для клиента: подтверждение хоста плюс готовый текст ошибки (пустой при успехе).</summary>
public readonly struct ToolResult
{
    public ToolResult(in ToolAck ack, string message)
    {
        Ack = ack;
        Message = message;
    }

    public ToolAck Ack { get; }

    /// <summary>Метка и подсказка первого отказа (<see cref="RejectMessages.Full"/>) или «нечего отменять»; пусто, если все команды применены.</summary>
    public string Message { get; }

    public bool Ok => Ack.Ok;
}

/// <summary>
/// Адаптер протокола инструментов игрока на главном потоке (T13, ADR-0023/0024; ui-spec §12). Всё общение с ядром идёт через <see cref="SimThreadHost"/>:
/// <list type="bullet">
/// <item>превью - <see cref="SetPreview"/> / <see cref="Pump"/> / <see cref="Preview"/>: не чаще одного запроса за тик и только при смене команды, не больше одного в полёте,
/// ответ приходит в переиспользуемый <see cref="PreviewReply"/> (0 аллокаций в стационаре); главный поток не ждёт;</item>
/// <item>применение - <see cref="Submit(in Command)"/> (группа команд = одно действие игрока); итог - событие <see cref="ActionCompleted"/> из <see cref="Pump"/>;</item>
/// <item>undo/redo - <see cref="Undo"/>/<see cref="Redo"/>: стек обратных команд ядра (player-tools.md §8) ведёт хост, здесь только запрос и глубины.</item>
/// </list>
/// Вызывать только с главного потока; <see cref="Pump"/> - раз за кадр.
/// </summary>
public sealed class ToolCommandService
{
    private readonly SimThreadHost _host;
    private readonly PreviewReply _scratch = new();

    private Command _want;
    private PreviewOptions _wantFlags;
    private bool _active;
    private bool _dirty;
    private Command _sentCmd;
    private Command _shown;
    private long _sentId;
    private long _sentTick = -1;
    private bool _inFlight;
    private long _requests;

    public ToolCommandService(SimThreadHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    /// <summary>Итог применённого (или отклонённого) действия; вызывается из <see cref="Pump"/> на главном потоке.</summary>
    public event Action<ToolResult>? ActionCompleted;

    /// <summary>Пришёл новый ответ превью (<see cref="Preview"/> обновлён).</summary>
    public event Action? PreviewUpdated;

    /// <summary>Последний ответ превью. Валиден, пока <see cref="HasPreview"/>; тот же объект на всё время жизни сервиса.</summary>
    public PreviewReply Preview { get; } = new();

    public bool HasPreview { get; private set; }

    /// <summary>Показанное превью относится именно к этой команде (ответ уже пришёл на неё, а не на предыдущий кадр курсора).</summary>
    public bool PreviewMatches(in Command cmd) => HasPreview && SameCommand(in _shown, in cmd);

    /// <summary>Превью задано, но ответа на текущую команду ещё нет (метка показывает «…», ui-spec §6.1).</summary>
    public bool PreviewPending => _active && (_dirty || _inFlight);

    /// <summary>Команда, к которой относится <see cref="Preview"/> (для сверки «превью == применение»).</summary>
    public ref readonly Command PreviewCommand => ref _shown;

    public int UndoDepth => _host.UndoDepth;

    public int RedoDepth => _host.RedoDepth;

    public bool CanUndo => _host.UndoDepth > 0;

    public bool CanRedo => _host.RedoDepth > 0;

    /// <summary>Сколько запросов превью отправлено хосту (метрика троттлинга).</summary>
    public long PreviewRequests => _requests;

    /// <summary>
    /// Задаёт команду, превью которой нужно показывать. Одинаковая команда повторно не пересчитывается (пока тик ядра тот же). Не отправляет сразу: запрос уйдёт
    /// в ближайшем <see cref="Pump"/> с учётом троттлинга.
    /// </summary>
    public void SetPreview(in Command cmd, PreviewOptions flags = PreviewOptions.None)
    {
        if (!_active || _wantFlags != flags || !SameCommand(in _want, in cmd))
        {
            _want = cmd;
            _wantFlags = flags;
            _dirty = true;
        }

        _active = true;
    }

    /// <summary>Снять превью (инструмент вышел или курсор ушёл с карты). Уже летящий ответ будет проигнорирован.</summary>
    public void ClearPreview()
    {
        _active = false;
        _dirty = false;
        HasPreview = false;
    }

    /// <summary>Поставить одну команду как действие игрока. Возвращает id действия (&gt; 0) или 0 (очередь полна / хост остановлен).</summary>
    public uint Submit(in Command cmd) => _host.TrySubmitAction(new ReadOnlySpan<Command>(in cmd));

    /// <summary>Группа команд = одно действие игрока (протяжка цепочки дорог), атомарно в одном тике и один пункт undo.</summary>
    public uint Submit(ReadOnlySpan<Command> group) => _host.TrySubmitAction(group);

    public uint Undo() => _host.TryUndo();

    public uint Redo() => _host.TryRedo();

    /// <summary>Раз за кадр: отправить превью (если пора) и разобрать ответы хоста. Не блокирует.</summary>
    public void Pump()
    {
        // не больше одного запроса в полёте, поэтому ответ с чужим id невозможен; устаревший (команда сменилась) всё равно показываем до прихода нового
        if (_inFlight && _host.TryReadPreview(_scratch) && _scratch.Id == _sentId)
        {
            _inFlight = false;
            if (_active)
            {
                Preview.CopyFrom(_scratch);
                _shown = _sentCmd;
                HasPreview = true;
                PreviewUpdated?.Invoke();
            }
        }

        // «на изменение» (сменилась команда) либо «раз за тик» (сменилось состояние ядра)
        if (_active && !_inFlight && (_dirty || _host.Tick != _sentTick))
        {
            long id = _host.TryRequestPreview(in _want, _wantFlags);
            if (id > 0)
            {
                _sentId = id;
                _sentCmd = _want;
                _sentTick = _host.Tick;
                _inFlight = true;
                _dirty = false;
                _requests++;
            }
        }

        while (_host.TryReadToolAck(out ToolAck ack))
        {
            string msg = string.Empty;
            if (ack.Nothing)
            {
                msg = RejectMessages.UndoNothing(ack.Kind);
            }
            else if (ack.Rejected > 0)
            {
                msg = RejectMessages.Full(ack.FirstReason);
            }

            ActionCompleted?.Invoke(new ToolResult(in ack, msg));
        }
    }

    private static bool SameCommand(in Command a, in Command b)
    {
        ReadOnlySpan<byte> x = MemoryMarshal.AsBytes(new ReadOnlySpan<Command>(in a));
        ReadOnlySpan<byte> y = MemoryMarshal.AsBytes(new ReadOnlySpan<Command>(in b));
        return x.SequenceEqual(y);
    }
}
