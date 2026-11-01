using System.Globalization;
using Godot;
using Sim.Core.Api;
using TrafficCity.Tools;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// Инструмент «Узлы» в клиенте (UI10, ui-spec §5.4): создание, кадр (наведение, иконки, строка опций), ввод мыши, «Править» из инспектора узла
/// и «Полосы» из инспектора сегмента. Команды и undo - тот же <see cref="ToolCommandService"/>, что у «Дорог».
/// </summary>
public partial class SimHost
{
    private NodesTool? _nodesTool;
    private NodesToolView? _nodesView;
    private NodesOptionsStrip? _nodesStrip;
    private bool _nodesStripDirty = true;
    private int _nodesRing = -1;
    private Vector2 _nodesMouse = new(-1f, -1f);
    private ToolMods _nodesMods;
    private int _nodesNetVersion = -1;
    private bool _nodesPressed;
    private bool _nodesRmb;
    private Vector2 _nodesRmbDown;

    /// <summary>Инструмент «Узлы» (null до старта игры и без UI).</summary>
    public NodesTool? Nodes => _nodesTool;

    private void InitNodesTool()
    {
        if (_tools == null || _ui == null || _nodesTool != null)
        {
            return;
        }

        _nodesTool = new NodesTool(_tools, new NodesWorld(() => _frame?.Network));
        _nodesTool.Message += ToolToast;
        _nodesTool.Logged += GD.Print;
        _nodesTool.OptionsChanged += () =>
        {
            _nodesStripDirty = true;
            _ui.Router.NotifyChanged();
        };
        _nodesStrip = new NodesOptionsStrip(_nodesTool);
        _ui.RegisterToolOptions(_nodesTool, _nodesStrip.Root);
        _nodesView = new NodesToolView(_ui.CursorLayer);
        if (_ui.Router.Tool is UiPlaceholderTool { Id: "nodes" })
        {
            _ui.Router.SetTool(_nodesTool);
        }

        if (_nodesToolArg.HasValue)
        {
            EditNode(_selectNodeArg, _nodesToolArg.Value); // скриншоты: --select-node signal --nodes-tool bans
        }
    }

    /// <summary>Открыть инструмент «Узлы» в режиме <paramref name="mode"/> на узле <paramref name="node"/> (-1 - без выбора).</summary>
    private void EditNode(int node, NodesMode mode)
    {
        if (_nodesTool == null || _ui == null)
        {
            return;
        }

        // инструмент ведёт свой выбор узла: инспектор закрывается, чтобы не перекрывать строку опций
        ClearSelection();
        SyncUiInspector();
        _nodesRing = -2;
        _nodesTool.SetMode(mode);
        if (node >= 0)
        {
            _nodesTool.Select(node);
        }

        _ui.Router.SetTool(_nodesTool);
        _ui.Router.NotifyChanged();
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ui10] edit node #{node} mode {mode}"));
    }

    /// <summary>Раз в кадр (из <see cref="ProcessTools"/>): true - инструмент «Узлы» активен и кадр обработан.</summary>
    private bool ProcessNodesTool()
    {
        if (_nodesTool == null || _nodesView == null || _nodesStrip == null || _ui == null || _camera == null)
        {
            return false;
        }

        if (!ReferenceEquals(_ui.Router.Tool, _nodesTool))
        {
            _nodesView.Hide();
            _nodesMouse = new Vector2(-1f, -1f);
            return false;
        }

        _nodesTool.Tick();
        if (_nodesStripDirty)
        {
            _nodesStripDirty = false;
            _nodesStrip.Sync();
        }

        // кольцо выбранного узла - то же, что у инспектора
        int sel = _nodesTool.Layout.Node;
        if (sel != _nodesRing && _frame != null)
        {
            _nodesRing = sel;
            NetworkView net = _frame.Network.View;
            if (net.IsNodeAlive(sel))
            {
                _nodePicker?.ShowAt(in net.Nodes[sel]);
            }
            else
            {
                _nodePicker?.Clear();
            }
        }

        Viewport vp = GetViewport();
        Vector2 mp = vp.GetMousePosition();
        bool overUi = vp.GuiGetHoveredControl() != null || _ui.WorldInputBlocked || !vp.GetVisibleRect().HasPoint(mp);
        ToolMods mods = Input.IsKeyPressed(Key.Shift) ? ToolMods.Shift : ToolMods.None;
        int ver = _frame?.NetworkVersion ?? -1;
        if (mp != _nodesMouse || mods != _nodesMods || ver != _nodesNetVersion)
        {
            _nodesMouse = mp;
            _nodesMods = mods;
            _nodesNetVersion = ver;
            _nodesTool.OnHover(PointerAt(mp, mods, overUi, ToolButton.Primary));
        }

        _nodesView.Update(_nodesTool, _tools!, _camera.Camera, mp, overUi);
        return true;
    }

    /// <summary>Мышь инструмента «Узлы»; true - событие израсходовано.</summary>
    private bool HandleNodesInput(InputEvent e)
    {
        if (_nodesTool == null || _ui == null || !ReferenceEquals(_ui.Router.Tool, _nodesTool) || e is not InputEventMouseButton mb)
        {
            return false;
        }

        ToolMods mods = Input.IsKeyPressed(Key.Shift) ? ToolMods.Shift : ToolMods.None;
        if (mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _nodesPressed = true;
                _nodesTool.OnPress(PointerAt(mb.Position, mods, false, ToolButton.Primary));
                return true;
            }

            if (_nodesPressed)
            {
                _nodesPressed = false;
                _nodesTool.OnRelease(PointerAt(mb.Position, mods, false, ToolButton.Primary));
                return true;
            }
        }
        else if (mb.ButtonIndex == MouseButton.Right)
        {
            // ПКМ-drag - панорама камеры; клик без сдвига снимает выбор узла
            if (mb.Pressed)
            {
                _nodesRmb = true;
                _nodesRmbDown = mb.Position;
            }
            else if (_nodesRmb)
            {
                _nodesRmb = false;
                if (mb.Position.DistanceTo(_nodesRmbDown) < RightClickMovePx)
                {
                    _nodesTool.OnRelease(PointerAt(mb.Position, mods, false, ToolButton.Secondary));
                }
            }
        }

        return false;
    }
}
