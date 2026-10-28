using TrafficCity.Ui;

namespace TrafficCity.Tools;

/// <summary>Кнопка мыши, нажатая в мире.</summary>
public enum ToolButton
{
    Primary = 0,
    Secondary = 1,
}

/// <summary>Модификаторы ввода инструмента (ui-spec §12): Shift - строить цепочкой, Ctrl - без снапа и т.п. (раскладку задаёт UI4).</summary>
[System.Flags]
public enum ToolMods
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

/// <summary>Положение указателя в мире: точка на земле (метры, X/Z ядра) и признак «луч попал в землю». Без Godot-типов, чтобы инструменты проверялись юнит-тестами.</summary>
public readonly record struct ToolPointer(float X, float Z, bool OnGround, ToolMods Mods = ToolMods.None, ToolButton Button = ToolButton.Primary);

/// <summary>
/// Инструмент мира (ui-spec §12): активный один, события раздаёт роутер/<c>SimHost</c> (<c>_UnhandledInput</c>), клики по панелям сюда не доходят.
/// Инструмент формирует команды ядра и общается с ядром только через <see cref="ToolCommandService"/> (превью, применение, undo).
/// <see cref="OnCancel"/> - отмена протяжки без побочных эффектов; <see cref="IUiTool.CancelGesture"/> (Esc из роутера) обычно зовёт её и сообщает, было ли что отменять.
/// </summary>
public interface ITool : IUiTool
{
    /// <summary>Указатель двигается (без нажатия или с нажатием - тянем). Здесь обновляется превью (<see cref="ToolCommandService.SetPreview"/>).</summary>
    void OnHover(in ToolPointer at);

    void OnPress(in ToolPointer at);

    /// <summary>Кнопка отпущена: обычно здесь команда уходит в <c>ToolCommandService.Submit</c>.</summary>
    void OnRelease(in ToolPointer at);

    void OnCancel();
}

/// <summary>Основа инструмента: флаг «идёт протяжка», Esc через <see cref="IUiTool.CancelGesture"/> и вызов <see cref="OnCancel"/>.</summary>
public abstract class ToolBase : ITool
{
    protected ToolBase(ToolCommandService service)
    {
        Service = service;
    }

    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    protected ToolCommandService Service { get; }

    /// <summary>Нажата основная кнопка и ещё не отпущена/не отменена.</summary>
    protected bool Dragging { get; set; }

    public virtual void OnHover(in ToolPointer at)
    {
    }

    public virtual void OnPress(in ToolPointer at)
    {
        Dragging = at.Button == ToolButton.Primary;
    }

    public virtual void OnRelease(in ToolPointer at)
    {
        Dragging = false;
    }

    public virtual void OnCancel()
    {
        Dragging = false;
    }

    public bool CancelGesture()
    {
        bool was = Dragging;
        OnCancel();
        return was;
    }

    public virtual void OnEnter()
    {
    }

    public virtual void OnExit()
    {
        OnCancel();
        Service.ClearPreview();
    }
}
