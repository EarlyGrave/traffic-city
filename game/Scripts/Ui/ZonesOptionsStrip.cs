using Godot;
using TrafficCity.Tools;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>
/// Строка опций инструмента «Зоны» (ui-spec §3.2, §5.2 п. 1): вид зоны (Жилая / Коммерческая / Промзона / Стереть), плотность, режим «Прямоугольник» (MVP; «Кисть вдоль дороги» - позже).
/// Кнопки и состояние <see cref="ZonesTool"/> синхронизируются по <see cref="ZonesTool.OptionsChanged"/>.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class ZonesOptionsStrip
{
    private static readonly string[] _kindNames = { "Жилая", "Коммерческая", "Промзона", "Стереть" };
    private static readonly ZoneChoice[] _kinds = { ZoneChoice.Residential, ZoneChoice.Commercial, ZoneChoice.Industrial, ZoneChoice.Erase };

    private readonly ZonesTool _tool;
    private readonly HBoxContainer _row;
    private readonly Button[] _kind = new Button[4];
    private readonly Button[] _density = new Button[2];
    private readonly Label _hint;

    public ZonesOptionsStrip(ZonesTool tool)
    {
        _tool = tool;
        _row = new HBoxContainer { Name = "ZonesOptions", MouseFilter = Control.MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 8);
        _row.AddChild(Secondary("Вид:"));

        var kinds = new ButtonGroup();
        for (int i = 0; i < _kind.Length; i++)
        {
            ZoneChoice k = _kinds[i];
            Button b = Btn(_kindNames[i], toggle: true);
            b.ButtonGroup = kinds;
            b.Pressed += () => _tool.SetKind(k);
            _kind[i] = b;
            if (k != ZoneChoice.Erase)
            {
                // образец цвета зоны, как в инфо-слое «Зоны»
                _row.AddChild(new ColorRect
                {
                    Color = k == ZoneChoice.Residential ? Palette.ZoneResidentialSrgb : k == ZoneChoice.Commercial ? Palette.ZoneCommercialSrgb : Palette.ZoneIndustrialSrgb,
                    CustomMinimumSize = new Vector2(12f, 12f),
                    SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
                    MouseFilter = Control.MouseFilterEnum.Ignore,
                });
            }

            _row.AddChild(b);
        }

        _kind[0].TooltipText = "Жилая зона: дома и жители";
        _kind[1].TooltipText = "Коммерческая зона: магазины и офисы";
        _kind[2].TooltipText = "Промзона: заводы и склады";
        _kind[3].TooltipText = "Стереть зону: участки становятся незонированными, здания другого вида уйдут в упадок";
        _row.AddChild(new VSeparator());

        _row.AddChild(Secondary("Плотность:"));
        var dens = new ButtonGroup();
        string[] dn = { "Малая", "Высокая" };
        for (int i = 0; i < _density.Length; i++)
        {
            byte d = (byte)i;
            Button b = Btn(dn[i], toggle: true);
            b.ButtonGroup = dens;
            b.Pressed += () => _tool.SetDensity(d);
            _density[i] = b;
            _row.AddChild(b);
        }

        _row.AddChild(new VSeparator());
        Button rect = Btn("Прямоугольник", toggle: true);
        rect.ButtonPressed = true;
        rect.TooltipText = "Режим кисти: прямоугольник по клеткам сетки. «Кисть вдоль дороги» - позже";
        rect.MouseFilter = Control.MouseFilterEnum.Ignore;
        _row.AddChild(rect);

        _hint = Secondary("Тяните по карте - закрасить; ПКМ или Esc - отмена");
        _hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _hint.ClipText = true;
        _row.AddChild(_hint);
        Sync();
    }

    public Control Root => _row;

    /// <summary>Подтянуть кнопки из состояния инструмента без сигналов.</summary>
    public void Sync()
    {
        for (int i = 0; i < _kind.Length; i++)
        {
            _kind[i].SetPressedNoSignal(_tool.Kind == _kinds[i]);
        }

        bool erase = _tool.Kind == ZoneChoice.Erase;
        for (int i = 0; i < _density.Length; i++)
        {
            _density[i].Disabled = erase;
            _density[i].SetPressedNoSignal(!erase && _tool.Density == i);
        }

    }

    private static Label Secondary(string text) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.LabelSecondary,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static UiButton Btn(string text, bool toggle) => new UiButton(text, toggle ? UiButtonKind.Toggle : UiButtonKind.Secondary)
    {
        FocusMode = Control.FocusModeEnum.None,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
    };
}
