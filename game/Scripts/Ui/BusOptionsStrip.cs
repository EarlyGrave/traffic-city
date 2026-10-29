using System;
using System.Collections.Generic;
using System.Globalization;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Host;
using TrafficCity.Tools;

namespace TrafficCity.Ui;

/// <summary>
/// Строка опций инструмента «Транспорт» (UI11, ui-spec §5.3, §7.6): «Новая линия» / «Готово», список линий, сводка выбранной (остановки, интервал, ожидающие, состояние),
/// интервал −/+, «Приостановить»/«Возобновить», «Удалить линию» (подтверждение P13). Пересборка - только по <see cref="BusTool.OptionsChanged"/> (смена сети или выбора).
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class BusOptionsStrip
{
    private readonly BusTool _tool;
    private readonly Func<NetworkSnapshot?> _net;
    private readonly Func<SimFrame?> _frame;
    private readonly HBoxContainer _row;
    private readonly Button _new;
    private readonly Button _done;
    private readonly OptionButton _list;
    private readonly Label _stats;
    private readonly Button _slower;
    private readonly Button _faster;
    private readonly Button _suspend;
    private readonly Button _remove;
    private readonly Label _hint;
    private readonly List<int> _routes = new();

    public BusOptionsStrip(BusTool tool, Func<NetworkSnapshot?> net, Func<SimFrame?> frame)
    {
        _tool = tool;
        _net = net;
        _frame = frame;
        _row = new HBoxContainer { Name = "BusOptions", MouseFilter = Control.MouseFilterEnum.Ignore };
        _row.AddThemeConstantOverride("separation", 8);

        _new = Btn("Новая линия");
        _new.TooltipText = "Клики по дорогам ставят остановки по порядку; Enter или клик по первой остановке — готово";
        _new.Pressed += _tool.StartNewLine;
        _row.AddChild(_new);
        _done = Btn("Готово");
        _done.TooltipText = "Завершить линию и выпустить автобусы (Enter)";
        _done.Pressed += _tool.Finish;
        _row.AddChild(_done);
        _row.AddChild(new VSeparator());

        _row.AddChild(Secondary("Линия:"));
        _list = new OptionButton { FocusMode = Control.FocusModeEnum.None, CustomMinimumSize = new Vector2(150f, 32f), SizeFlagsVertical = Control.SizeFlags.ShrinkCenter };
        _list.TooltipText = "Выбрать линию для правки";
        _list.ItemSelected += OnPick;
        _row.AddChild(_list);
        _stats = Secondary(string.Empty);
        _row.AddChild(_stats);

        _slower = Btn("−");
        _slower.TooltipText = "Реже: интервал больше, автобусов меньше, содержание дешевле";
        _slower.Pressed += () => _tool.StepHeadway(1);
        _faster = Btn("+");
        _faster.TooltipText = "Чаще: интервал меньше, автобусов больше";
        _faster.Pressed += () => _tool.StepHeadway(-1);
        _row.AddChild(Secondary("Интервал"));
        _row.AddChild(_slower);
        _row.AddChild(_faster);
        _suspend = Btn("Приостановить");
        _suspend.Pressed += _tool.ToggleSuspend;
        _row.AddChild(_suspend);
        _remove = Btn("Удалить линию");
        _remove.Pressed += _tool.RemoveRoute;
        _row.AddChild(_remove);

        _hint = Secondary(string.Empty);
        _hint.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _hint.ClipText = true;
        _row.AddChild(_hint);
        Sync();
    }

    public Control Root => _row;

    public void Sync()
    {
        bool unlocked = _tool.LinesUnlocked;
        _new.Disabled = !unlocked || _tool.Mode == BusMode.Build;
        _new.TooltipText = unlocked ? "Клики по дорогам ставят остановки по порядку; Enter или клик по первой остановке — готово" : "Автобусные линии ещё не открыты — вехи в панели «Город» (C)";
        _done.Visible = _tool.Mode == BusMode.Build;
        _done.Disabled = _tool.Creating || !_tool.TryRouteData(out BusRouteData bd) || bd.StopCount == 0;

        NetworkSnapshot? snap = _net();
        _list.Clear();
        _routes.Clear();
        int selected = -1;
        if (snap != null)
        {
            NetworkView v = snap.View;
            for (int i = 0; i < v.BusRoutes.Length; i++)
            {
                if (!v.IsBusRouteAlive(i))
                {
                    continue;
                }

                BusRouteData r = v.BusRoutes[i];
                string name = BusTool.LineName(i) + " · " + r.StopCount.ToString(CultureInfo.InvariantCulture) + " ост." + (r.Suspended ? " · пауза" : string.Empty);
                _list.AddItem(name, _routes.Count);
                _list.SetItemIcon(_routes.Count, Swatch(BusToolView.LineColor(i)));
                if (i == _tool.Route.Index)
                {
                    selected = _routes.Count;
                }

                _routes.Add(i);
            }
        }

        if (_routes.Count == 0)
        {
            _list.AddItem("Линий нет", 0);
            _list.Disabled = true;
        }
        else
        {
            _list.Disabled = false;
        }

        _list.Selected = selected;
        BusRouteData rd = default;
        bool has = _tool.Mode == BusMode.Edit && _tool.TryRouteData(out rd);
        _slower.Disabled = !has;
        _faster.Disabled = !has;
        _suspend.Disabled = !has;
        _remove.Disabled = !has;
        _suspend.Text = has && rd.Suspended ? "Возобновить" : "Приостановить";
        _suspend.TooltipText = has && rd.Suspended ? "Линия снова выпускает автобусы" : "Новых автобусов не будет, идущие доедут рейс; содержание автобусов не начисляется";
        _stats.Text = has ? Stats(rd, snap) : string.Empty;
        _hint.Text = _tool.Mode switch
        {
            BusMode.Build => "Клик по дороге — остановка; ПКМ или Esc — прервать (линия останется приостановленной)",
            BusMode.Edit => "Клик — остановка в конец, Shift+клик — после ближайшей; ПКМ по остановке — удалить",
            _ => unlocked ? "Клик по остановке на карте или список — выбрать линию" : "Линии ещё не открыты: вехи — панель «Город» (C)",
        };
    }

    private string Stats(in BusRouteData r, NetworkSnapshot? snap)
    {
        var inv = CultureInfo.InvariantCulture;
        int waiting = 0;
        SimFrame? f = _frame();
        if (snap != null && f != null)
        {
            ReadOnlySpan<ushort> w = f.BusStopWaiting;
            NetworkView v = snap.View;
            for (int k = 0; k < r.StopCount; k++)
            {
                int slot = r.FirstStop + k;
                if ((uint)slot < (uint)v.BusRouteStops.Length && (uint)v.BusRouteStops[slot] < (uint)w.Length)
                {
                    waiting += w[v.BusRouteStops[slot]];
                }
            }
        }

        string s = r.StopCount.ToString(inv) + " ост. · раз в " + BusTool.HeadwayText(r.HeadwayTicks);
        if (waiting > 0)
        {
            s += " · ждут " + waiting.ToString(inv);
        }

        return r.Suspended ? s + " · приостановлена" : s;
    }

    private void OnPick(long item)
    {
        int i = (int)item;
        if ((uint)i < (uint)_routes.Count)
        {
            _tool.SelectRoute(_routes[i]);
        }
    }

    private static ImageTexture Swatch(Color c)
    {
        Image img = Image.CreateEmpty(12, 12, false, Image.Format.Rgba8);
        img.Fill(c);
        return ImageTexture.CreateFromImage(img);
    }

    private static Label Secondary(string text) => new()
    {
        Text = text,
        ThemeTypeVariation = UiTheme.LabelSecondary,
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        MouseFilter = Control.MouseFilterEnum.Ignore,
    };

    private static Button Btn(string text) => new()
    {
        Text = text,
        FocusMode = Control.FocusModeEnum.None,
        CustomMinimumSize = new Vector2(0f, 32f),
        SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
    };
}
