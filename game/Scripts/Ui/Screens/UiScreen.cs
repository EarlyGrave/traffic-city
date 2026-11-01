using Godot;
using Sim.Core.Api;

namespace TrafficCity.Ui;

/// <summary>Место экрана в раскладке интерфейса: корень (свои полосы и слои) или правая док-панель.</summary>
public enum UiDock
{
    /// <summary>Полноэкранный корень темы: экран сам задаёт якоря (полосы, модальные слои).</summary>
    Root,

    /// <summary>Правая док-панель 320 px под верхней полосой: дети идут друг под другом.</summary>
    Right,

    /// <summary>Колонка тостов справа снизу над тулбаром.</summary>
    Toasts,
}

/// <summary>
/// Узкий контекст, через который экраны видят интерфейс и мир: роутер состояния, хост симуляции (снапшоты и команды),
/// другие экраны и пару общих действий. <see cref="UiRoot"/> его реализует, но экраны про него не знают.
/// </summary>
public interface IUiContext
{
    /// <summary>Состояние интерфейса (инструмент, инфо, инспектор, меню) и роутер Esc.</summary>
    UiRouter Router { get; }

    /// <summary>Хост симуляции (данные панелей, скорость, команды); null - сцена без мира (галерея, главное меню до старта).</summary>
    SimHost? Host { get; }

    /// <summary>Показано главное меню: игры нет, игровые панели скрыты.</summary>
    bool MainMenu { get; }

    /// <summary>Аргументы запуска после <c>--</c>.</summary>
    string[] Args { get; }

    /// <summary>Контейнер места в раскладке.</summary>
    Control Slot(UiDock dock);

    /// <summary>Другой зарегистрированный экран.</summary>
    T Screen<T>()
        where T : UiScreen;

    /// <summary>Настоящий инструмент по id кнопки или клавиши (задаёт хост); null или null-результат - заглушка.</summary>
    System.Func<string, IUiTool?>? ToolFactory { get; }

    /// <summary>Применить сохранённые настройки (тема, масштаб, палитра, пресет рендера).</summary>
    void ApplySettings();

    /// <summary>Пометить текущее событие ввода обработанным.</summary>
    void MarkHandled();
}

/// <summary>
/// Экран интерфейса (ui-framework-plan.md, «Экраны»): объявляет место в доке, горячую клавишу, место в порядке Esc и замок по вехе;
/// <see cref="UiRoot"/> создаёт экраны по <see cref="UiScreenRegistry"/> и раздаёт им ввод, Esc и обновления. Новый экран = класс + строка в реестре.
/// </summary>
public abstract class UiScreen
{
    /// <summary>Контекст интерфейса (после <see cref="Attach"/>).</summary>
    protected IUiContext Ui { get; private set; } = null!;

    /// <summary>Место в раскладке.</summary>
    public virtual UiDock Dock => UiDock.Right;

    /// <summary>Горячая клавиша (действие <see cref="UiActions"/>): переключает экран; null - без клавиши.</summary>
    public virtual StringName? Hotkey => null;

    /// <summary>Место в порядке Esc: больше - закрывается раньше; меньше нуля - Esc экран не закрывает.</summary>
    public virtual int EscPriority => -1;

    /// <summary>Модальный экран: получает Esc и ввод и под открытым меню игры, раньше остальных (по <see cref="EscPriority"/>).</summary>
    public virtual bool Modal => false;

    /// <summary>Признак, который открывает экран (веха прогрессии); null - открыт всегда.</summary>
    public virtual Feature? Lock => null;

    /// <summary>Экран открыт.</summary>
    public abstract bool IsOpen { get; }

    /// <summary>Экран можно открыть сейчас: есть мир и признак <see cref="Lock"/> открыт (без прогрессии - открыт).</summary>
    public virtual bool CanOpen => Lock is not { } f || Ui.Host is not { IsReady: true } h || !h.CityPanel.HasProgression || h.CityPanel.FeatureOpen(f);

    internal void Attach(IUiContext ui)
    {
        Ui = ui;
        Build(ui.Slot(Dock));
    }

    /// <summary>Построить дерево экрана в контейнере <paramref name="parent"/> (своего места в раскладке).</summary>
    protected abstract void Build(Control parent);

    /// <summary>Открыть или закрыть.</summary>
    public abstract void SetOpen(bool open);

    public void Open() => SetOpen(true);

    public void Close() => SetOpen(false);

    public void Toggle() => SetOpen(!IsOpen);

    /// <summary>Esc: закрыть экран, если открыт; true - Esc израсходован.</summary>
    public virtual bool HandleEsc()
    {
        if (!IsOpen)
        {
            return false;
        }

        Close();
        return true;
    }

    /// <summary>Ввод (кроме Esc) по порядку реестра: true - дальше не передавать. По умолчанию - <see cref="Hotkey"/> переключает экран.</summary>
    public virtual bool HandleInput(InputEvent e)
    {
        if (Hotkey == null || !UiActions.Pressed(e, Hotkey) || !CanOpen)
        {
            return false;
        }

        Toggle();
        return true;
    }

    /// <summary>После создания всех экранов (<c>_Ready</c> корня): связи с другими экранами, состояние старта.</summary>
    public virtual void Ready()
    {
    }

    /// <summary>Состояние роутера или режима (главное меню) сменилось.</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>Каждый кадр.</summary>
    public virtual void Process(double delta)
    {
    }

    /// <summary>Аргумент запуска <paramref name="name"/> задан.</summary>
    protected bool HasArg(string name) => System.Array.IndexOf(Ui.Args, name) >= 0;

    /// <summary>Значение аргумента запуска (<c>--name value</c>) или пустая строка.</summary>
    protected string ArgValue(string name)
    {
        int i = System.Array.IndexOf(Ui.Args, name);
        return i >= 0 && i + 1 < Ui.Args.Length ? Ui.Args[i + 1] : string.Empty;
    }

    /// <summary>Полоса на всю ширину у верхнего или нижнего края корня; <paramref name="lift"/> - подъём над нижним краем.</summary>
    protected static PanelContainer Bar(Control root, string name, string variation, bool top, float height, float lift)
    {
        var p = new PanelContainer
        {
            Name = name,
            ThemeTypeVariation = variation,
            CustomMinimumSize = new Vector2(0f, height),
            MouseFilter = Control.MouseFilterEnum.Stop,
        };
        p.AnchorLeft = 0f;
        p.AnchorRight = 1f;
        p.AnchorTop = top ? 0f : 1f;
        p.AnchorBottom = top ? 0f : 1f;
        p.OffsetLeft = 0f;
        p.OffsetRight = 0f;
        p.OffsetTop = top ? 0f : -(height + lift);
        p.OffsetBottom = top ? height : -lift;
        root.AddChild(p);
        return p;
    }

    /// <summary>Кнопка тулбара/легенды: без фокуса (иначе Space/Enter нажимали бы её и «съедали» клавиши времени).</summary>
    protected static Button MakeButton(string text, bool toggle, bool disabled, float minWidth = 0f) => new()
    {
        Text = text,
        ToggleMode = toggle,
        Disabled = disabled,
        FocusMode = Control.FocusModeEnum.None,
        CustomMinimumSize = new Vector2(minWidth, 36f),
    };
}
