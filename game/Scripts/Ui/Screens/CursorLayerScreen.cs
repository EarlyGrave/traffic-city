using Godot;

namespace TrafficCity.Ui;

/// <summary>Слой меток у курсора (метка инструмента, подсказки): полноэкранный, мышь пропускает; лежит над панелями, под модальным слоем.</summary>
public sealed class CursorLayerScreen : UiScreen
{
    /// <summary>Контейнер меток.</summary>
    public Control Layer { get; private set; } = null!;

    public override UiDock Dock => UiDock.Root;

    public override bool IsOpen => Layer.Visible;

    protected override void Build(Control parent)
    {
        Layer = new Control { Name = "CursorLayer", MouseFilter = Control.MouseFilterEnum.Ignore }.WithFullRect();
        parent.AddChild(Layer);
    }

    public override void SetOpen(bool open) => Layer.Visible = open;
}
