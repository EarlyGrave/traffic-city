using Godot;

namespace TrafficCity.Ui;

/// <summary>Инспектор (UI3) в правой док-панели: содержимое по выбору в мире наполняет SimHost, видимость ведёт роутер (Esc - через его порядок).</summary>
public sealed class InspectorScreen : UiScreen
{
    /// <summary>Панель инспектора.</summary>
    public UiInspector View { get; private set; } = null!;

    public override bool IsOpen => Ui.Router.Inspected != InspectKind.None;

    protected override void Build(Control parent)
    {
        View = new UiInspector(parent);
        View.CloseRequested += Ui.Router.CloseInspector;
    }

    /// <summary>Открыть без объекта - макет (<see cref="InspectKind.Demo"/>); закрыть - как кнопка ×.</summary>
    public override void SetOpen(bool open)
    {
        if (open)
        {
            Ui.Router.SetInspector(InspectKind.Demo);
        }
        else
        {
            Ui.Router.CloseInspector();
        }
    }

    public override void Ready()
    {
        if (ArgValue("--ui-state") == "inspect")
        {
            Ui.Router.SetInspector(InspectKind.Demo);
        }
    }

    public override void Refresh()
    {
        View.Visible = IsOpen;
        if (Ui.Router.Inspected == InspectKind.Demo)
        {
            FillDemo();
        }
    }

    /// <summary>Макет инспектора без выбранного объекта (<c>--ui-state inspect</c>): строки по ui-spec.md §3.3.</summary>
    private void FillDemo()
    {
        View.SetTitle("Инспектор (макет)");
        View.SetRow(0, "Скорость", "—");
        View.SetRow(1, "Цель", "—");
        View.SetRow(2, "В пути", "—");
        View.SetRow(3, "Состояние", "—");
        View.SetRowCount(4);
        View.SetArmRows(0);
        View.SetNote("Щёлкните по машине, пешеходу, узлу или дороге");
        View.SetAction(0, "Маршрут", toggle: true, pressed: false, enabled: false, "Показать маршрут");
        View.SetAction(1, "Следовать", toggle: true, pressed: false, enabled: false, "Камера следует за объектом");
        View.SetActionCount(2);
    }
}
