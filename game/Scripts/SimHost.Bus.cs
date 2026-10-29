using System.Globalization;
using Godot;
using Sim.Core.Api;
using Sim.Host;
using TrafficCity.Tools;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// Инструмент «Транспорт» в клиенте (UI11, ui-spec §5.3, §7.6): создание, кадр (наведение, вид, строка опций), ввод мыши и Enter, «Показать» у событий линии.
/// Команды и undo - общий <see cref="ToolCommandService"/>.
/// </summary>
public partial class SimHost
{
    private BusTool? _busTool;
    private BusToolView? _busView;
    private BusOptionsStrip? _busStrip;
    private bool _busStripDirty = true;
    private Vector2 _busMouse = new(-1f, -1f);
    private bool _busPressed;
    private bool _busRmb;
    private Vector2 _busRmbDown;
    private string? _busToolArg;

    /// <summary>Инструмент «Транспорт» (null до старта игры и без UI).</summary>
    public BusTool? Bus => _busTool;

    private void InitBusTool()
    {
        if (_tools == null || _ui == null || _busTool != null)
        {
            return;
        }

        _busTool = new BusTool(_tools, new BusWorld(this));
        _busTool.Message += ToolToast;
        _busTool.Logged += GD.Print;
        _busTool.Confirm = (text, ok, act) => _ui.Confirm.Ask(text, ok, act);
        _busTool.OptionsChanged += () =>
        {
            _busStripDirty = true;
            _ui.Router.NotifyChanged();
        };
        _busStrip = new BusOptionsStrip(_busTool, () => _frame?.Network, () => _frame);
        _ui.RegisterToolOptions(_busTool, _busStrip.Root);
        _busView = new BusToolView(this, _ui.CursorLayer);
        if (_ui.Router.Tool is UiPlaceholderTool { Id: "transit" })
        {
            _ui.Router.SetTool(_busTool);
        }

        if (_busToolArg != null)
        {
            ApplyBusArgs();
        }
    }

    /// <summary>Раз в кадр (из <see cref="ProcessTools"/>): true - инструмент активен и кадр обработан.</summary>
    private bool ProcessBusTool()
    {
        if (_busTool == null || _busView == null || _busStrip == null || _ui == null || _tools == null)
        {
            return false;
        }

        if (!ReferenceEquals(_ui.Router.Tool, _busTool))
        {
            _busView.Hide();
            _busMouse = new Vector2(-1f, -1f);
            return false;
        }

        _busTool.Tick();
        if (_busStripDirty)
        {
            _busStripDirty = false;
            _busStrip.Sync();
        }

        Viewport vp = GetViewport();
        Vector2 mp = vp.GetMousePosition();
        bool overUi = vp.GuiGetHoveredControl() != null || _ui.WorldInputBlocked || !vp.GetVisibleRect().HasPoint(mp);
        if (mp != _busMouse && _busToolArg == null)
        {
            _busMouse = mp;
            _busTool.OnHover(PointerAt(mp, ToolMods.None, overUi, ToolButton.Primary));
        }

        _busView.Update(_busTool, _tools, _frame?.Network, mp, vp.GetVisibleRect().Size, overUi && _busToolArg == null);
        return true;
    }

    /// <summary>Мышь и Enter инструмента «Транспорт»; true - событие израсходовано.</summary>
    private bool HandleBusInput(InputEvent e)
    {
        if (_busTool == null || _ui == null || !ReferenceEquals(_ui.Router.Tool, _busTool))
        {
            return false;
        }

        if (e is InputEventKey { Pressed: true, Echo: false } k && (k.Keycode == Key.Enter || k.Keycode == Key.KpEnter))
        {
            _busTool.Finish();
            return true;
        }

        if (e is not InputEventMouseButton mb)
        {
            return false;
        }

        ToolMods mods = Input.IsKeyPressed(Key.Shift) ? ToolMods.Shift : ToolMods.None;
        if (mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed && mb.DoubleClick)
            {
                _busTool.Finish(); // двойной клик завершает открытую линию (ui-spec §5.3 п. 3)
                _busPressed = false;
                return true;
            }

            if (mb.Pressed)
            {
                _busPressed = true;
                return true;
            }

            if (_busPressed)
            {
                _busPressed = false;
                _busTool.OnRelease(PointerAt(mb.Position, mods, false, ToolButton.Primary));
                return true;
            }
        }
        else if (mb.ButtonIndex == MouseButton.Right)
        {
            // ПКМ-drag - панорама камеры; клик без сдвига - удалить остановку / прервать линию
            if (mb.Pressed)
            {
                _busRmb = true;
                _busRmbDown = mb.Position;
            }
            else if (_busRmb)
            {
                _busRmb = false;
                if (mb.Position.DistanceTo(_busRmbDown) < RightClickMovePx)
                {
                    _busTool.OnRelease(PointerAt(mb.Position, mods, false, ToolButton.Secondary));
                }
            }
        }

        return false;
    }

    /// <summary>«Показать» у события линии: инструмент «Транспорт» с этой линией (инспектор линии - строка опций, §7.6). false - инструмента нет или линия снята.</summary>
    private bool ShowBusRoute(int route)
    {
        if (_busTool == null || _ui == null || _frame == null || !_frame.Network.View.IsBusRouteAlive(route))
        {
            return false;
        }

        _ui.Router.SetTool(_busTool);
        _busTool.SelectRoute(route);
        return true;
    }

    /// <summary>Скриншоты: <c>--bus-tool new|N|new:x,z;x,z</c> - открыть инструмент: новая линия, линия N (с 0) или новая линия с наведением на точку.</summary>
    private void ApplyBusArgs()
    {
        if (_busTool == null || _ui == null || _busToolArg == null)
        {
            return;
        }

        _ui.Router.SetTool(_busTool);
        string[] parts = _busToolArg.Split(':');
        if (parts[0] == "new")
        {
            _busTool.StartNewLine();
        }
        else if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int r))
        {
            _busTool.SelectRoute(r);
        }

        if (parts.Length > 1)
        {
            string[] xz = parts[1].Split(',');
            if (xz.Length == 2 && float.TryParse(xz[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
                && float.TryParse(xz[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                _busTool.OnHover(new ToolPointer(x, z, true));
            }
        }

        GD.Print($"[ui11] bus tool {_busToolArg}: mode {_busTool.Mode} route {_busTool.Route.Index} hover {_busTool.HoverValid}");
    }

    /// <summary>Мир инструмента «Транспорт» поверх кадра хоста.</summary>
    private sealed class BusWorld : IBusWorld
    {
        private readonly SimHost _h;

        public BusWorld(SimHost h)
        {
            _h = h;
        }

        public NetworkSnapshot? Network => _h._frame?.Network;

        public bool Paused => _h._paused;

        public bool LinesUnlocked => _h._cityPanel.FeatureOpen(Feature.BusLines);

        public int BusVehicleType
        {
            get
            {
                for (int i = 0; i < _h._types.Length; i++)
                {
                    if (_h._types[i].Category == VehicleCategory.Bus)
                    {
                        return i;
                    }
                }

                return -1;
            }
        }
    }
}
