using System.Globalization;
using Godot;
using TrafficCity.Tools;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// Инструмент «Зоны» в клиенте (UI9, ui-spec §5.2): создание, кадр (наведение, вид, строка опций), ввод мыши, автослой «Зоны» инфо-режима на время работы инструмента.
/// Команды и undo - тот же <see cref="ToolCommandService"/>, что у «Дорог».
/// </summary>
public partial class SimHost
{
    private ZonesTool? _zonesTool;
    private ZonesWorld? _zonesWorld;
    private ZonesToolView? _zonesView;
    private ZonesOptionsStrip? _zonesStrip;
    private bool _zonesStripDirty = true;
    private Vector2 _zonesMouse = new(-1f, -1f);
    private ToolMods _zonesMods;
    private bool _zonesPressed;
    private bool _zonesRmb;
    private Vector2 _zonesRmbDown;
    private long _zonesMaxPaintM2 = 250_000;
    private bool _zonesSavedActive;
    private int _zonesSavedLayer;
    private bool _zonesLayerHeld;
    private string? _zonesToolArg;
    private float[]? _zonesRectArg;

    /// <summary>Инструмент «Зоны» (null до старта игры и без UI).</summary>
    public ZonesTool? Zones => _zonesTool;

    private void InitZonesTool()
    {
        if (_tools == null || _ui == null || _host == null || _zonesTool != null)
        {
            return;
        }

        _zonesWorld = new ZonesWorld(() => _frame, () => _host, () => _hasCity, () => _paused, _zonesMaxPaintM2);
        _zonesTool = new ZonesTool(_tools, _zonesWorld);
        _zonesTool.Message += ToolToast;
        _zonesTool.Logged += GD.Print;
        _zonesTool.OptionsChanged += () =>
        {
            _zonesStripDirty = true;
            _ui.Router.NotifyChanged();
        };
        _zonesTool.Entered += EnterZonesLayer;
        _zonesTool.Exited += ExitZonesLayer;
        _zonesStrip = new ZonesOptionsStrip(_zonesTool);
        _ui.RegisterToolOptions(_zonesTool, _zonesStrip.Root);
        _zonesView = new ZonesToolView(this, _ui.CursorLayer);
        if (_ui.Router.Tool is UiPlaceholderTool { Id: "zones" })
        {
            _ui.Router.SetTool(_zonesTool);
        }

        if (_zonesToolArg != null)
        {
            ApplyZonesArgs();
        }
    }

    /// <summary>Вход в инструмент: слой «Зоны» включается сам, прежнее состояние инфо-режима запоминается (ui-spec §5.2 п. 2).</summary>
    private void EnterZonesLayer()
    {
        if (_ui == null || _zonesLayerHeld)
        {
            return;
        }

        UiRouter r = _ui.Router;
        _zonesSavedActive = r.InfoActive;
        _zonesSavedLayer = r.InfoLayer;
        _zonesLayerHeld = true;
        r.SetInfoLayer(InfoMode.LayerZones);
    }

    /// <summary>Выход: инфо-режим и слой возвращаются как были.</summary>
    private void ExitZonesLayer()
    {
        if (_ui == null || !_zonesLayerHeld)
        {
            return;
        }

        _zonesLayerHeld = false;
        UiRouter r = _ui.Router;
        if (r.InfoLayer != _zonesSavedLayer)
        {
            r.SetInfoLayer(_zonesSavedLayer);
        }

        r.SetInfo(_zonesSavedActive);
    }

    /// <summary>Раз в кадр (из <see cref="ProcessTools"/>): true - инструмент «Зоны» активен и кадр обработан.</summary>
    private bool ProcessZonesTool()
    {
        if (_zonesTool == null || _zonesView == null || _zonesStrip == null || _zonesWorld == null || _ui == null || _camera == null)
        {
            return false;
        }

        if (!ReferenceEquals(_ui.Router.Tool, _zonesTool))
        {
            _zonesView.Hide();
            _zonesMouse = new Vector2(-1f, -1f);
            return false;
        }

        _zonesWorld.Refresh();
        if (_zonesStripDirty)
        {
            _zonesStripDirty = false;
            _zonesStrip.Sync();
        }

        Viewport vp = GetViewport();
        Vector2 mp = vp.GetMousePosition();
        bool overUi = vp.GuiGetHoveredControl() != null || _ui.WorldInputBlocked || !vp.GetVisibleRect().HasPoint(mp);
        ToolMods mods = Input.IsKeyPressed(Key.Shift) ? ToolMods.Shift : ToolMods.None;
        if (!_zonesTool.Demo && (mp != _zonesMouse || mods != _zonesMods))
        {
            _zonesMouse = mp;
            _zonesMods = mods;
            _zonesTool.OnHover(PointerAt(mp, mods, overUi, ToolButton.Primary));
        }
        else
        {
            _zonesTool.Tick();
        }

        _zonesView.Update(_zonesTool, mp, vp.GetVisibleRect().Size, overUi && !_zonesTool.Demo);
        return true;
    }

    /// <summary>Мышь инструмента «Зоны»; true - событие израсходовано.</summary>
    private bool HandleZonesInput(InputEvent e)
    {
        if (_zonesTool == null || _ui == null || !ReferenceEquals(_ui.Router.Tool, _zonesTool) || e is not InputEventMouseButton mb)
        {
            return false;
        }

        ToolMods mods = Input.IsKeyPressed(Key.Shift) ? ToolMods.Shift : ToolMods.None;
        if (mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Pressed)
            {
                _zonesPressed = true;
                _zonesTool.OnPress(PointerAt(mb.Position, mods, false, ToolButton.Primary));
                return true;
            }

            if (_zonesPressed)
            {
                _zonesPressed = false;
                _zonesTool.OnRelease(PointerAt(mb.Position, mods, false, ToolButton.Primary));
                return true;
            }
        }
        else if (mb.ButtonIndex == MouseButton.Right)
        {
            // ПКМ-drag - панорама камеры; клик без сдвига отменяет протяжку
            if (mb.Pressed)
            {
                _zonesRmb = true;
                _zonesRmbDown = mb.Position;
            }
            else if (_zonesRmb)
            {
                _zonesRmb = false;
                if (mb.Position.DistanceTo(_zonesRmbDown) < RightClickMovePx)
                {
                    _zonesTool.OnRelease(PointerAt(mb.Position, mods, false, ToolButton.Secondary));
                }
            }
        }

        return false;
    }

    /// <summary>Скриншоты: <c>--zones-tool res|com|ind|erase[:0|1]</c> открывает инструмент, <c>--zones-rect x0,z0,x1,z1</c> показывает протяжку этого прямоугольника.</summary>
    private void ApplyZonesArgs()
    {
        if (_zonesTool == null || _ui == null || _zonesToolArg == null)
        {
            return;
        }

        string[] parts = _zonesToolArg.Split(':');
        _zonesTool.SetKind(parts[0] switch
        {
            "com" => ZoneChoice.Commercial,
            "ind" => ZoneChoice.Industrial,
            "erase" => ZoneChoice.Erase,
            _ => ZoneChoice.Residential,
        });
        if (parts.Length > 1 && parts[1] == "1")
        {
            _zonesTool.SetDensity(1);
        }

        _ui.Router.SetTool(_zonesTool);
        if (_zonesRectArg != null && _zonesWorld != null)
        {
            _zonesWorld.Refresh();
            _zonesTool.DemoDrag(_zonesRectArg[0], _zonesRectArg[1], _zonesRectArg[2], _zonesRectArg[3]);
        }

        GD.Print(string.Create(
            CultureInfo.InvariantCulture,
            $"[ui9] zones tool {_zonesToolArg}: rect {_zonesTool.X0:0},{_zonesTool.Z0:0}..{_zonesTool.X1:0},{_zonesTool.Z1:0} paint={_zonesTool.Paint} abandon={_zonesTool.Abandon} lost={_zonesTool.Lost} same={_zonesTool.Same} marks={_zonesTool.MarkCount}"));
    }
}
