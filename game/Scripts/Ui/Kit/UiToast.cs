using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Стопка тостов: короткие сообщения снизу справа, исчезают сами. Пул из <see cref="Capacity"/> плашек создаётся один раз:
/// новый тост берёт свободную плашку, а если все заняты — самую старую. <c>toasts.Push("Сохранено", UiTone.Info, "ок")</c>.
/// Таймеры и прозрачность — в <c>_Process</c>, который выключен, пока тостов нет; в кадре ничего не выделяется.
/// </summary>
public sealed partial class UiToast : VBoxContainer
{
    public const int Capacity = 4;
    public const float DefaultSeconds = 3f;

    private readonly PanelContainer[] _items = new PanelContainer[Capacity];
    private readonly Label[] _texts = new Label[Capacity];
    private readonly UiBadge[] _tags = new UiBadge[Capacity];
    private readonly float[] _left = new float[Capacity];
    private readonly int[] _order = new int[Capacity];
    private int _serial;

    public UiToast()
    {
        // ставится в угол родителя на весь экран: низ-право, растёт вверх
        AnchorLeft = 1f;
        AnchorTop = 1f;
        AnchorRight = 1f;
        AnchorBottom = 1f;
        GrowHorizontal = GrowDirection.Begin;
        GrowVertical = GrowDirection.Begin;
        OffsetRight = -Tokens.Space4;
        OffsetBottom = -Tokens.Space4;
        MouseFilter = MouseFilterEnum.Ignore;
        Alignment = AlignmentMode.End;
        for (int i = 0; i < Capacity; i++)
        {
            var panel = new PanelContainer { ThemeTypeVariation = ThemeBuilder.Toast, Visible = false, MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ShrinkEnd };
            var row = new HBoxContainer();
            panel.AddChild(row);
            _tags[i] = new UiBadge();
            row.AddChild(_tags[i]);
            _texts[i] = new Label();
            row.AddChild(_texts[i]);
            _items[i] = panel;
            AddChild(panel);
        }

        SetProcess(false);
    }

    /// <summary>Показать тост. <paramref name="seconds"/> &lt;= 0 — не скрывать сам (витрина).</summary>
    public void Push(string text, UiTone tone = UiTone.Neutral, string tag = "", float seconds = DefaultSeconds)
    {
        int slot = 0;
        for (int i = 0; i < Capacity; i++)
        {
            if (!_items[i].Visible)
            {
                slot = i;
                break;
            }

            if (_order[i] < _order[slot])
            {
                slot = i;
            }
        }

        _texts[slot].Text = text;
        _tags[slot].Visible = tag.Length > 0;
        _tags[slot].Set(tag, tone);
        _left[slot] = seconds > 0f ? seconds : float.PositiveInfinity;
        _order[slot] = ++_serial;
        PanelContainer p = _items[slot];
        p.Modulate = Colors.White;
        p.Visible = true;
        MoveChild(p, GetChildCount() - 1); // новый — снизу
        SetProcess(true);
    }

    public override void _Process(double delta)
    {
        bool any = false;
        for (int i = 0; i < Capacity; i++)
        {
            if (!_items[i].Visible)
            {
                continue;
            }

            _left[i] -= (float)delta;
            if (_left[i] <= 0f)
            {
                _items[i].Visible = false;
                continue;
            }

            any = true;
            if (_left[i] < Tokens.AnimSlow)
            {
                _items[i].Modulate = new Color(1f, 1f, 1f, _left[i] / Tokens.AnimSlow);
            }
        }

        SetProcess(any);
    }
}
