using Godot;
using TrafficCity.Tools;

namespace TrafficCity.Ui;

/// <summary>
/// Строка опций инструмента «Дороги» (ui-spec §3.2, §5.1): профиль из каталога ядра, режим (Строить / Заменить / Снос), зеркало для несимметричных профилей,
/// снап (G) и сетка (Shift+G). Кнопки и клавиши меняют одно и то же состояние (<see cref="RoadsTool"/>, <see cref="WorldGrid"/>); <see cref="Sync"/> раз в кадр подтягивает
/// внешние изменения (клавиши) в кнопки без сигналов.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class RoadsOptionsStrip
{
    private readonly RoadsTool _tool;
    private readonly WorldGrid _grid;
    private readonly HBoxContainer _row;
    private readonly OptionButton _profile;
    private readonly Button[] _mode = new Button[3];
    private readonly Button _mirror;
    private readonly Button _snap;
    private readonly Button _shown;
    private readonly Label _hint;
    private string _hintShown = string.Empty;

    public RoadsOptionsStrip(RoadsTool tool, WorldGrid grid)
    {
        _tool = tool;
        _grid = grid;
        _row = new HBoxContainer { Name = "RoadsOptions", MouseFilter = Control.MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 8);
        _row.AddChild(Secondary("Профиль:"));

        _profile = new OptionButton
        {
            FocusMode = Control.FocusModeEnum.None,
            CustomMinimumSize = new Vector2(250f, 32f),
            TooltipText = "Профиль дороги из каталога ядра ([ и ] листают)",
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        RoadCatalog cat = tool.Catalog;
        for (int i = 0; i < cat.Count; i++)
        {
            _profile.AddItem(cat[i].Name, i);
        }

        _profile.ItemSelected += idx => _tool.SetProfile((int)idx);
        _row.AddChild(_profile);
        _row.AddChild(new VSeparator());

        var group = new ButtonGroup();
        string[] names = { "Строить", "Заменить  U", "Снос  X" };
        for (int i = 0; i < 3; i++)
        {
            var mode = (RoadsMode)i;
            Button b = Btn(names[i], toggle: true);
            b.ButtonGroup = group;
            b.Pressed += () => _tool.SetMode(mode);
            _mode[i] = b;
            _row.AddChild(b);
        }

        _mode[0].TooltipText = "Построить новую дорогу: клик - начало, клик - конец, цепочкой";
        _mode[1].TooltipText = "Заменить профиль существующей дороги (U): навести на дорогу и кликнуть";
        _mode[2].TooltipText = "Снести дорогу (X): навести на дорогу и кликнуть";

        _mirror = Btn("Зеркало  M", toggle: true);
        _mirror.TooltipText = "Развернуть несимметричный профиль (M)";
        _mirror.Toggled += _ => _tool.ToggleMirror();
        _row.AddChild(_mirror);
        _row.AddChild(new VSeparator());

        _snap = Btn("Снап  G", toggle: true);
        _snap.TooltipText = "Привязка к сетке (G); Shift - временно без привязки";
        _snap.Toggled += on =>
        {
            if (on != _grid.SnapOn)
            {
                _grid.ToggleSnap();
            }
        };
        _row.AddChild(_snap);
        _shown = Btn("Сетка  Shift+G", toggle: true);
        _shown.TooltipText = "Показать сетку (Shift+G)";
        _shown.Toggled += on =>
        {
            if (on != _grid.Shown)
            {
                _grid.ToggleShown();
            }
        };
        _row.AddChild(_shown);

        _hint = Secondary(string.Empty);
        _hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _hint.ClipText = true;
        _row.AddChild(_hint);
        Sync();
    }

    public Control Root => _row;

    /// <summary>Подтянуть состояние кнопок из инструмента и сетки (клавиши меняют его мимо кнопок).</summary>
    public void Sync()
    {
        if (_profile.Selected != _tool.ProfileIndex)
        {
            _profile.Select(_tool.ProfileIndex);
        }

        for (int i = 0; i < 3; i++)
        {
            _mode[i].SetPressedNoSignal((int)_tool.Mode == i);
        }

        bool asym = _tool.Choice.Asymmetric && _tool.Choice.Profile >= 0;
        _mirror.Visible = asym;
        _mirror.SetPressedNoSignal(_tool.Mirror);
        _snap.SetPressedNoSignal(_grid.SnapOn);
        _shown.SetPressedNoSignal(_grid.Shown);

        string hint = _tool.Mode switch
        {
            RoadsMode.Build => _tool.HasAnchor ? "Клик - дальше; Esc - закончить" : "Клик - начало дороги",
            RoadsMode.Upgrade => "Клик по дороге - заменить",
            _ => "Клик по дороге - снести",
        };
        if (hint != _hintShown)
        {
            _hintShown = hint;
            _hint.Text = hint;
        }
    }

    /// <summary>
    /// Замки профилей (UI16, progression.md §4.2): закрытые профили в списке серые с пометкой; выбрать их нельзя (ядро всё равно отклонит с <c>Locked</c>).
    /// Вызывается при новой версии данных прогрессии (1 Гц), не в кадре; текст пункта меняется только при смене замка.
    /// </summary>
    public void ApplyLocks(CityPanelData d)
    {
        RoadCatalog cat = _tool.Catalog;
        if (!cat.FromProfiles)
        {
            return;
        }

        for (int i = 0; i < cat.Count && i < _profile.ItemCount; i++)
        {
            int prof = cat[i].Profile;
            bool locked = (uint)prof < (uint)d.ProfileOpen.Length && !d.ProfileOpen[prof];
            if (_profile.IsItemDisabled(i) == locked)
            {
                continue;
            }

            _profile.SetItemDisabled(i, locked);
            _profile.SetItemText(i, locked ? cat[i].Name + "  — закрыт" : cat[i].Name);
            _profile.SetItemTooltip(i, locked ? "Профиль откроется на одной из следующих вех (панель «Город», C)" : string.Empty);
        }
    }

    private static Label Secondary(string text) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.LabelSecondary,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static Button Btn(string text, bool toggle) => new()
    {
        Text = text,
        ToggleMode = toggle,
        FocusMode = Control.FocusModeEnum.None,
        CustomMinimumSize = new Vector2(0f, 32f),
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
    };
}
