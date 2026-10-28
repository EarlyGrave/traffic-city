using System;
using System.Globalization;
using Godot;
using Sim.Core.Api;
using Sim.Host;
using TrafficCity.Tools;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// Инструменты игрока в клиенте (UI4): <see cref="ToolCommandService"/> поверх <see cref="SimThreadHost"/> (T13), инструмент «Дороги», его вид и строка опций,
/// undo/redo (Ctrl+Z / Ctrl+Y и кнопки ↶ ↷), тосты по итогам действий. Ввод инструмента идёт из <see cref="_UnhandledInput"/>: клики по панелям сюда не доходят.
/// </summary>
public partial class SimHost
{
    private const float RightClickMovePx = 4f;

    private ToolCommandService? _tools;
    private RoadsTool? _roadTool;
    private RoadsToolView? _roadsView;
    private RoadsOptionsStrip? _roadsStrip;
    private Vector2 _toolMouse = new(-1f, -1f);
    private ToolMods _toolMods;
    private bool _toolOverUi;
    private int _toolNetVersion = -1;
    private bool _toolPressed;
    private bool _rmbActive;
    private Vector2 _rmbDown;
    private int _toolToastSeq;
    private int _shownUndo = -1;
    private int _shownRedo = -1;
    private ActionMeta _shownUndoMeta;
    private ActionMeta _shownRedoMeta;
    private bool _demolitionUndoToastShown;

    /// <summary>Инструмент «Дороги» (null до старта игры и без UI): для проверок и скриптов.</summary>
    public RoadsTool? Roads => _roadTool;

    public ToolCommandService? Tools => _tools;

    /// <summary>Метка у курсора сейчас (пусто, если скрыта).</summary>
    public string ToolLabelText => _roadsView?.LabelText ?? string.Empty;

    /// <summary>Создать адаптер и инструмент «Дороги» (после запуска потока симуляции и сетки; <see cref="AfterStart"/>).</summary>
    private void InitTools()
    {
        if (_host == null || _ui == null || _grid == null || _frame == null || _tools != null)
        {
            return;
        }

        _tools = new ToolCommandService(_host);
        RoadCatalog catalog = RoadCatalog.FromView(_frame.Network.View);
        var world = new RoadsWorld(catalog, () => _frame?.Network, _grid, () => _cityHud, () => _paused);
        _roadsWorld = world;
        _catalog = catalog;
        _roadTool = new RoadsTool(_tools, world);
        _roadTool.Message += ToolToast;
        _roadTool.Logged += GD.Print;
        _roadTool.Confirm = (text, ok, act) => _ui.Confirm.Ask(text, ok, act);
        _roadTool.OptionsChanged += _ui.Router.NotifyChanged;
        _roadsStrip = new RoadsOptionsStrip(_roadTool, _grid);
        _ui.RegisterToolOptions(_roadTool, _roadsStrip.Root);
        _roadsView = new RoadsToolView(this, _ui.CursorLayer);
        _ui.ToolFactory = id =>
        {
            if (id == "roads")
            {
                _roadTool.SetMode(RoadsMode.Build);
                return _roadTool;
            }

            if (id == "bulldoze")
            {
                _roadTool.SetMode(RoadsMode.Remove);
                return _roadTool;
            }

            return null;
        };
        _ui.UndoRequested += DoUndo;
        _ui.RedoRequested += DoRedo;
        _tools.ActionCompleted += OnToolAction;
        if (_ui.Router.Tool is UiPlaceholderTool { Id: "roads" })
        {
            _ui.Router.SetTool(_roadTool); // --ui-state build: роутер поднял заглушку раньше хоста
        }

        GD.Print(string.Create(
            CultureInfo.InvariantCulture,
            $"[tool] roads tool ready: catalog {(catalog.FromProfiles ? "profiles" : "legacy")} x{catalog.Count}, net_version {_frame.NetworkVersion}"));
    }

    /// <summary>Раз в кадр: ответы ядра, наведение, призрак и метка, состояние ↶ ↷.</summary>
    private void ProcessTools()
    {
        if (_tools == null || _roadTool == null || _ui == null || _camera == null || _roadsView == null || _roadsStrip == null)
        {
            return;
        }

        _tools.Pump();
        _roadTool.Tick(); // «Снести» из инспектора: клик после прихода превью (подтверждение P13)
        ActionMeta nu = _tools.NextUndo;
        ActionMeta nr = _tools.NextRedo;
        if (_tools.UndoDepth != _shownUndo || _tools.RedoDepth != _shownRedo || nu != _shownUndoMeta || nr != _shownRedoMeta)
        {
            _shownUndo = _tools.UndoDepth;
            _shownRedo = _tools.RedoDepth;
            _shownUndoMeta = nu;
            _shownRedoMeta = nr;
            // сумму возврата за undo (city-economy §8.3 UndoCredits) хост пока не отдаёт клиенту - в тултипе её нет
            _ui.SetUndoRedo(_shownUndo, _shownRedo, _shownUndo > 0 ? RejectMessages.ActionLabel(nu) : string.Empty, _shownRedo > 0 ? RejectMessages.ActionLabel(nr) : string.Empty);
        }

        if (!ReferenceEquals(_ui.Router.Tool, _roadTool))
        {
            _roadsView.Hide();
            _toolMouse = new Vector2(-1f, -1f);
            return;
        }

        _roadsStrip.Sync();
        Viewport vp = GetViewport();
        Vector2 mp = vp.GetMousePosition();
        // курсор вне окна - не над миром (иначе луч из-за края окна «наводит» инструмент на случайную дорогу)
        bool overUi = vp.GuiGetHoveredControl() != null || _ui.WorldInputBlocked || !vp.GetVisibleRect().HasPoint(mp);
        ToolMods mods = Input.IsActionPressed(UiActions.SnapOverride) ? ToolMods.Shift : ToolMods.None;
        int ver = _frame?.NetworkVersion ?? -1;
        if (mp != _toolMouse || mods != _toolMods || overUi != _toolOverUi || ver != _toolNetVersion)
        {
            _toolMouse = mp;
            _toolMods = mods;
            _toolOverUi = overUi;
            _toolNetVersion = ver;
            _roadTool.OnHover(PointerAt(mp, mods, overUi, ToolButton.Primary));
        }

        _roadsView.Update(_roadTool, _tools, _camera.Camera, mp, vp.GetVisibleRect().Size, overUi);
    }

    private ToolPointer PointerAt(Vector2 screen, ToolMods mods, bool overUi, ToolButton button)
    {
        if (overUi || _camera == null)
        {
            return new ToolPointer(0f, 0f, false, mods, button);
        }

        Camera3D cam = _camera.Camera;
        Vector3 o = cam.ProjectRayOrigin(screen);
        Vector3 d = cam.ProjectRayNormal(screen);
        if (d.Y > -1e-4f)
        {
            return new ToolPointer(0f, 0f, false, mods, button);
        }

        float t = -o.Y / d.Y;
        return new ToolPointer(o.X + (d.X * t), o.Z + (d.Z * t), true, mods, button);
    }

    /// <summary>Ввод инструментов из <see cref="_UnhandledInput"/>; true - событие израсходовано.</summary>
    private bool HandleToolInput(InputEvent e)
    {
        if (_tools == null || _roadTool == null || _ui == null)
        {
            return false;
        }

        if (e is InputEventKey { Pressed: true, Echo: false })
        {
            if (UiActions.Pressed(e, UiActions.Undo))
            {
                DoUndo();
                return true;
            }

            if (UiActions.Pressed(e, UiActions.Redo))
            {
                DoRedo();
                return true;
            }
        }

        if (!ReferenceEquals(_ui.Router.Tool, _roadTool))
        {
            return false;
        }

        if (e is InputEventKey { Pressed: true, Echo: false })
        {
            if (UiActions.Pressed(e, UiActions.RoadsPrevProfile))
            {
                _roadTool.CycleProfile(-1);
            }
            else if (UiActions.Pressed(e, UiActions.RoadsNextProfile))
            {
                _roadTool.CycleProfile(1);
            }
            else if (UiActions.Pressed(e, UiActions.RoadsReplace))
            {
                _roadTool.SetMode(_roadTool.Mode == RoadsMode.Upgrade ? RoadsMode.Build : RoadsMode.Upgrade);
            }
            else if (UiActions.Pressed(e, UiActions.RoadsMirror))
            {
                _roadTool.ToggleMirror();
            }
            else
            {
                return false;
            }

            return true;
        }

        if (e is InputEventMouseButton mb)
        {
            ToolMods mods = Input.IsActionPressed(UiActions.SnapOverride) ? ToolMods.Shift : ToolMods.None;
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    _toolPressed = true;
                    _roadTool.OnPress(PointerAt(mb.Position, mods, false, ToolButton.Primary));
                    return true;
                }

                if (_toolPressed)
                {
                    _toolPressed = false;
                    _roadTool.OnRelease(PointerAt(mb.Position, mods, false, ToolButton.Primary));
                    return true;
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right)
            {
                // ПКМ-drag - панорама камеры (событие камере тоже нужно, не расходуем); клик без сдвига (< 4 px) - отмена цепочки
                if (mb.Pressed)
                {
                    _rmbActive = true;
                    _rmbDown = mb.Position;
                }
                else if (_rmbActive)
                {
                    _rmbActive = false;
                    if (mb.Position.DistanceTo(_rmbDown) < RightClickMovePx)
                    {
                        _roadTool.OnRelease(PointerAt(mb.Position, mods, false, ToolButton.Secondary));
                    }
                }
            }
        }

        return false;
    }

    private void DoUndo()
    {
        if (_tools == null)
        {
            return;
        }

        uint id = _tools.Undo();
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[tool] undo requested #{id} (stack {_tools.UndoDepth}){(_paused ? ", paused" : string.Empty)}"));
        if (_paused)
        {
            ToolToast("Отмена применится после снятия паузы", false);
        }
    }

    private void DoRedo()
    {
        if (_tools == null)
        {
            return;
        }

        uint id = _tools.Redo();
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[tool] redo requested #{id} (stack {_tools.RedoDepth}){(_paused ? ", paused" : string.Empty)}"));
        if (_paused)
        {
            ToolToast("Повтор применится после снятия паузы", false);
        }
    }

    private void OnToolAction(ToolResult r)
    {
        ToolAck a = r.Ack;
        GD.Print(string.Create(
            CultureInfo.InvariantCulture,
            $"[tool] ack #{a.ActionId} {a.Kind} tick={a.Tick} applied={a.Applied} rejected={a.Rejected} reason={a.FirstReason} nothing={a.Nothing} recorded={a.Recorded} truncated={a.Truncated} lots_lost={a.ZoneLotsLost} net_version={a.NetworkVersion} undo={a.UndoDepth} redo={a.RedoDepth}{(r.Message.Length > 0 ? " - " + r.Message : string.Empty)}"));
        if (a.Nothing)
        {
            ToolToast(r.Message, false);
        }
        else if (a.Kind == ToolActionKind.Undo && a.Subject == CommandKind.RemoveRoad && a.Applied > 0 && !_demolitionUndoToastShown)
        {
            _demolitionUndoToastShown = true; // один раз за сессию, ui-spec §5.5
            ToolToast("Дорога восстановлена; машины, которые уехали, не вернутся", false);
        }
        else if (a.Kind == ToolActionKind.Do && a.Applied > 0 && !a.Recorded)
        {
            ToolToast("Это действие нельзя отменить", false); // нет обратной команды: стек undo его не запомнил
        }

        // отказы (CommandRejected) уже приходят тостом «Не удалось: ...» из событий ядра (UI6) - второй раз не показываем
    }

    /// <summary>Тост от инструмента. Тосты ниже порога «Хорошо» (<see cref="UiNotifyCenter.ToastThreshold"/>) попадают только в список, поэтому не-ошибки идут как предупреждение.</summary>
    private void ToolToast(string text, bool error)
    {
        if (_ui == null || text.Length == 0)
        {
            return;
        }

        NotifySeverity sev = error ? NotifySeverity.Error : NotifySeverity.Warning;
        _ui.Notify.Center.Add(new NotifyRaw(900_000 + _toolToastSeq++, sev, text, text, text, text, default), _host?.Tick ?? 0);
    }
}
