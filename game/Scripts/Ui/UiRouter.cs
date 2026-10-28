using System;

namespace TrafficCity.Ui;

/// <summary>Режим интерфейса для показа и скриншотов (ui-spec.md §2). Реальное состояние - флаги <see cref="UiRouter"/>, режимы совместимы.</summary>
public enum UiMode
{
    Overview,
    Build,
    Info,
    Inspect,
}

/// <summary>Что открыто в правой панели-инспекторе.</summary>
public enum InspectKind
{
    None,
    Vehicle,
    Pedestrian,
    Node,

    /// <summary>Макет без выбранного объекта (<c>--ui-state inspect</c>); закрывается Esc.</summary>
    Demo,
}

/// <summary>
/// Активный инструмент (ui-spec.md §12): каталог настоящих инструментов - T13 / UI4 (<c>game/Scripts/Tools/</c>). Здесь только то, что нужно
/// роутеру: имя, отмена протяжки, вход и выход.
/// </summary>
public interface IUiTool
{
    string Id { get; }

    string DisplayName { get; }

    /// <summary>Отменить незавершённую протяжку. true - было что отменять (Esc израсходован).</summary>
    bool CancelGesture();

    void OnEnter();

    void OnExit();
}

/// <summary>Заглушка инструмента: кнопки тулбара до UI4 и <c>--ui-state build</c> (проверка роутера и строки опций).</summary>
public sealed class UiPlaceholderTool : IUiTool
{
    public UiPlaceholderTool(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public bool CancelGesture() => false;

    public void OnEnter()
    {
    }

    public void OnExit()
    {
    }
}

/// <summary>
/// Состояние интерфейса и роутер событий (ui-spec.md §2): активный инструмент, инфо-режим, инспектор, меню. Чистый C# без узлов Godot.
/// Источник истины по инструменту, инспектору и меню - здесь; инфо-режим мира (<c>InfoMode</c>) и выбор в ядре обновляет подписчик
/// (SimHost) по событиям <see cref="InfoChanged"/>, <see cref="InfoLayerChanged"/>, <see cref="InspectorCloseRequested"/>.
/// </summary>
public sealed class UiRouter
{
    /// <summary>Состояние изменилось (перерисовать видимость панелей).</summary>
    public event Action? StateChanged;

    /// <summary>Инфо-режим включён или выключен пользователем (клавиша, кнопка, Esc).</summary>
    public event Action<bool>? InfoChanged;

    /// <summary>Выбран слой инфо-режима (1-3).</summary>
    public event Action<int>? InfoLayerChanged;

    /// <summary>Пользователь закрыл инспектор (Esc, ×): подписчик снимает выбор в мире и в ядре.</summary>
    public event Action? InspectorCloseRequested;

    public IUiTool? Tool { get; private set; }

    public bool InfoActive { get; private set; }

    public int InfoLayer { get; private set; } = 1;

    public InspectKind Inspected { get; private set; }

    public bool MenuOpen { get; private set; }

    /// <summary>Главный из активных режимов (для подписей и скриншотов): стройка > инспектор > инфо > обзор.</summary>
    public UiMode Mode => Tool != null ? UiMode.Build : Inspected != InspectKind.None ? UiMode.Inspect : InfoActive ? UiMode.Info : UiMode.Overview;

    public void SetTool(IUiTool? tool)
    {
        if (ReferenceEquals(Tool, tool))
        {
            return;
        }

        Tool?.OnExit();
        Tool = tool;
        Tool?.OnEnter();
        StateChanged?.Invoke();
    }

    public void ToggleInfo() => SetInfo(!InfoActive);

    public void SetInfo(bool on)
    {
        if (InfoActive == on)
        {
            return;
        }

        InfoActive = on;
        InfoChanged?.Invoke(on);
        StateChanged?.Invoke();
    }

    /// <summary>Слой инфо-режима выбран пользователем; как и в мире, выбор слоя включает режим.</summary>
    public void SetInfoLayer(int layer)
    {
        InfoLayer = layer;
        InfoLayerChanged?.Invoke(layer);
        if (!InfoActive)
        {
            InfoActive = true;
            InfoChanged?.Invoke(true);
        }

        StateChanged?.Invoke();
    }

    /// <summary>Скриншоты (<c>--ui-state info</c>) и синхронизация: состояние мира уже выставлено, события подписчику не нужны.</summary>
    public void MirrorInfo(bool on, int layer)
    {
        if (InfoActive == on && InfoLayer == layer)
        {
            return;
        }

        InfoActive = on;
        InfoLayer = layer;
        StateChanged?.Invoke();
    }

    /// <summary>Обновить вид инспектора по выбору в мире (SimHost раз в кадр). Макет <see cref="InspectKind.Demo"/> живёт до Esc.</summary>
    public void SetInspector(InspectKind kind)
    {
        if (Inspected == kind || (Inspected == InspectKind.Demo && kind == InspectKind.None))
        {
            return;
        }

        Inspected = kind;
        StateChanged?.Invoke();
    }

    public void OpenMenu()
    {
        if (!MenuOpen)
        {
            MenuOpen = true;
            StateChanged?.Invoke();
        }
    }

    public void CloseMenu()
    {
        if (MenuOpen)
        {
            MenuOpen = false;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Закрыть инспектор пользователем (кнопка ×, Esc).</summary>
    public void CloseInspector()
    {
        if (Inspected == InspectKind.None)
        {
            return;
        }

        Inspected = InspectKind.None;
        InspectorCloseRequested?.Invoke();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Esc: меню -> отмена протяжки -> выход из инструмента -> закрыть инспектор -> выйти из инфо -> открыть меню (ui-spec.md §2).
    /// Всегда расходует событие (последний шаг - меню).
    /// </summary>
    public void Cancel()
    {
        if (MenuOpen)
        {
            CloseMenu();
        }
        else if (Tool != null)
        {
            if (!Tool.CancelGesture())
            {
                SetTool(null);
            }
        }
        else if (Inspected != InspectKind.None)
        {
            CloseInspector();
        }
        else if (InfoActive)
        {
            SetInfo(false);
        }
        else
        {
            OpenMenu();
        }
    }
}
