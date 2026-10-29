using Godot;

namespace TrafficCity.Ui.Kit;

/// <summary>
/// Панель кита: кремовая плашка с контуром и твёрдой тенью, внутри — вертикальная колонка <see cref="Body"/> и необязательный
/// заголовок. <c>new UiPanel("Бюджет").Add(label).Add(row)</c>. Вариант темы можно сменить (карточка — <see cref="UiCard"/>).
/// </summary>
public partial class UiPanel : PanelContainer
{
    public UiPanel()
        : this(null)
    {
    }

    public UiPanel(string? title, string? variation = null)
    {
        if (variation != null)
        {
            ThemeTypeVariation = variation;
        }

        Body = new VBoxContainer();
        AddChild(Body);
        if (!string.IsNullOrEmpty(title))
        {
            Title = new Label { Text = title, ThemeTypeVariation = UiTheme.LabelHeader };
            Body.AddChild(Title);
        }
    }

    public VBoxContainer Body { get; }

    public Label? Title { get; }

    public UiPanel Add(Control child)
    {
        Body.AddChild(child);
        return this;
    }

    /// <summary>Строка из нескольких элементов (кнопки диалога, пары «подпись — значение»).</summary>
    public UiPanel AddRow(params Control[] children)
    {
        var row = new HBoxContainer();
        foreach (Control c in children)
        {
            row.AddChild(c);
        }

        Body.AddChild(row);
        return this;
    }

    public UiPanel MinWidth(float width)
    {
        CustomMinimumSize = new Vector2(width, CustomMinimumSize.Y);
        return this;
    }
}

/// <summary>Карточка: белая, радиус кнопки, тень 4 px — элемент списка внутри панели.</summary>
public sealed partial class UiCard : UiPanel
{
    public UiCard()
        : this(null)
    {
    }

    public UiCard(string? title)
        : base(title, ThemeBuilder.Card)
    {
    }
}
