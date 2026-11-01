using System;
using System.Collections.Generic;
using Sim.Core.Api;

namespace TrafficCity.Ui;

/// <summary>Запись списка уведомлений: одно событие или серия одинаковых (агрегат «×N»).</summary>
public sealed class NotifyEntry
{
    public int Id { get; init; }

    public NotifyRaw Raw { get; set; }

    public int Count { get; set; } = 1;

    public long FirstTick { get; init; }

    public long LastTick { get; set; }

    /// <summary>Не открывалась в списке с момента последнего обновления (бейдж на кнопке «События»).</summary>
    public bool Unread { get; set; } = true;

    /// <summary>Объект последнего события серии - его покажет «Показать».</summary>
    public NotifySubject Subject => Raw.Subject;

    public NotifySeverity Severity => Raw.Severity;

    public string Text => UiNotifyMap.Render(Raw, Count);
}

/// <summary>
/// Ядро уведомлений без Godot (юнит-тестируется): кольцо последних записей, агрегация одинаковых событий за 10 с игрового времени
/// (ui-spec.md §6.4), порог важности для тостов и очередь тостов с интервалом не чаще 1 в 3 с. Время игры - тики ядра (20 Гц), время тостов - секунды окна.
/// </summary>
public sealed class UiNotifyCenter
{
    /// <summary>Размер кольца списка.</summary>
    public const int RingSize = 50;

    /// <summary>Окно агрегации, тики (10 с при 20 Гц).</summary>
    public const long AggregateWindowTicks = 200;

    private readonly NotifyEntry?[] _ring = new NotifyEntry?[RingSize];
    private readonly List<NotifyEntry> _pending = new();
    private int _head;
    private int _count;
    private int _nextId = 1;
    private double _lastToastSec = double.NegativeInfinity;

    /// <summary>Новая запись (не слияние): для списка и бейджа.</summary>
    public event Action<NotifyEntry>? Added;

    /// <summary>Существующая запись выросла (×N): перерисовать её тост и строку списка.</summary>
    public event Action<NotifyEntry>? Updated;

    /// <summary>Тосты только для записей не ниже этой важности; остальное - только в списке.</summary>
    public NotifySeverity ToastThreshold { get; set; } = NotifySeverity.Good;

    /// <summary>Минимальный интервал между новыми тостами, с.</summary>
    public double ToastIntervalSec { get; set; } = 3.0;

    /// <summary>Сколько тостов держать в очереди: лишние (самые старые) остаются только в списке.</summary>
    public int MaxPending { get; set; } = 6;

    public int Count => _count;

    /// <summary>Тостов ждёт показа.</summary>
    public int PendingToasts => _pending.Count;

    /// <summary>Число записей с флагом «не прочитано».</summary>
    public int UnreadCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < _count; i++)
            {
                if (At(i).Unread)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>Запись по порядку от новой (0) к старой.</summary>
    public NotifyEntry Newest(int i) => At(_count - 1 - i);

    /// <summary>Передать события ядра; ненужные игроку отбрасываются в <see cref="UiNotifyMap.TryMap"/>.</summary>
    public void Push(ReadOnlySpan<SimEvent> events)
    {
        for (int i = 0; i < events.Length; i++)
        {
            Push(in events[i]);
        }
    }

    public void Push(in SimEvent e)
    {
        if (UiNotifyMap.TryMap(in e, out NotifyRaw raw))
        {
            Add(raw, e.Tick);
        }
    }

    /// <summary>Добавить разобранное уведомление (сливается с последней записью того же ключа внутри окна).</summary>
    public NotifyEntry Add(in NotifyRaw raw, long tick)
    {
        for (int i = _count - 1; i >= 0; i--)
        {
            NotifyEntry e = At(i);
            if (e.Raw.Key == raw.Key && tick - e.LastTick <= AggregateWindowTicks)
            {
                e.Count++;
                e.LastTick = Math.Max(e.LastTick, tick);
                e.Raw = raw;
                e.Unread = true;
                Updated?.Invoke(e);
                return e;
            }
        }

        var entry = new NotifyEntry { Id = _nextId++, Raw = raw, FirstTick = tick, LastTick = tick };
        if (_count == RingSize)
        {
            _pending.Remove(_ring[_head]!);
            _ring[_head] = entry;
            _head = (_head + 1) % RingSize;
        }
        else
        {
            _ring[(_head + _count) % RingSize] = entry;
            _count++;
        }

        if (raw.Severity >= ToastThreshold)
        {
            _pending.Add(entry);
            if (_pending.Count > MaxPending)
            {
                _pending.RemoveAt(0);
            }
        }

        Added?.Invoke(entry);
        return entry;
    }

    /// <summary>Достать следующий тост: не раньше чем через <see cref="ToastIntervalSec"/> после предыдущего; иначе запись ждёт в очереди.</summary>
    public bool TryDequeueToast(double nowSec, out NotifyEntry entry)
    {
        if (_pending.Count > 0 && nowSec - _lastToastSec >= ToastIntervalSec)
        {
            entry = _pending[0];
            _pending.RemoveAt(0);
            _lastToastSec = nowSec;
            return true;
        }

        entry = null!;
        return false;
    }

    /// <summary>Пометить всё прочитанным (список открыт).</summary>
    public void MarkAllRead()
    {
        for (int i = 0; i < _count; i++)
        {
            At(i).Unread = false;
        }
    }

    private NotifyEntry At(int i) => _ring[(_head + i) % RingSize]!;
}
