using System;
using System.Globalization;
using System.Text;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Host;
using TrafficCity.Tools;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// Инспекторы MVP (UI3, ui-spec.md §7): выбор в мире кликом (машина -> пешеход -> узел -> сегмент), наполнение правой панели
/// (<see cref="UiInspector"/>) 4 раза в секунду, кнопки «Маршрут», «Следовать», «Заменить профиль», «Снести», пресеты светофора.
/// Данные - только кадр хоста (<see cref="SimFrame"/>: <c>SelectedVehicleInfo</c>, <c>SelectedPedInfo</c>, оверлеи по подписке) и копия сети.
/// Тексты собираются в общий <see cref="StringBuilder"/>; в узлы уходят только изменившиеся строки.
/// </summary>
public partial class SimHost
{
    private const double InspectInterval = 0.25;
    private const double GoneCloseSec = 2.0;
    private const int FollowLostFrames = 40;
    private const float SegmentPickMinRadius = 8f;

    private static readonly string[] _nums = MakeNums(200);
    private static readonly string[] _signalLetters = { "·", "К", "Ж", "З" };

    private readonly StringBuilder _isb = new(96);
    private readonly NodeArms _arms = new();
    private readonly SignalMetrics _metricsCopy = new();
    private InspectKind _selKind;
    private VehicleHandle _selVeh;
    private int _selNode = -1;
    private int _selNodeGen;
    private int _selSeg = -1;
    private int _selSegGen;
    private string _selTitle = string.Empty;
    private bool _routeShown;
    private bool _following;
    private bool _followHasSet;
    private Vector3 _followSet;
    private int _followMissing;
    private int _vehHint = -1;
    private int _pedHint = -1;
    private double _inspTimer;
    private double _goneTimer = -1;
    private InspectKind _filledKind;
    private bool _pickPeds;
    private RoadsWorld? _roadsWorld;
    private RoadCatalog? _catalog;
    private int _nameSeg = -1;
    private int _nameVer = -1;
    private string _nameStr = string.Empty;
    private SegmentLoad _segLoad;
    private bool _segLoadValid;
    private MeshInstance3D? _segSelNode;
    private ImmediateMesh? _segSelMesh;

    /// <summary>Что выбрано в мире (для инспектора и скриптовых проверок).</summary>
    public InspectKind SelectedKind => _selKind;

    /// <summary>Камера следует за выбранной машиной или пешеходом.</summary>
    public bool Following => _following;

    /// <summary>Лента маршрута выбранной машины показана (кнопка «Маршрут»).</summary>
    public bool RouteShown => _routeShown;

    /// <summary>Последний кадр симуляции (скриптовые проверки: координаты объектов для кликов).</summary>
    public SimFrame? Frame => _frame;

    /// <summary>Узел-риг камеры (скриптовые проверки).</summary>
    public RtsCamera? CameraRig => _camera;

    private static string[] MakeNums(int n)
    {
        var a = new string[n];
        for (int i = 0; i < n; i++)
        {
            a[i] = i.ToString(CultureInfo.InvariantCulture);
        }

        return a;
    }

    /// <summary>Подписки инспектора (ADR-0024 п. 3): узлу - сигналы, переходы, пропуск; сегменту - нагрузка.</summary>
    private HostOverlays InspectOverlays() => _selKind switch
    {
        InspectKind.Node => HostOverlays.LaneSignals | HostOverlays.CrossingStates | HostOverlays.SignalStates | HostOverlays.NodeThroughput | HostOverlays.SignalMetrics,
        InspectKind.Segment => HostOverlays.SegmentLoads,
        _ => HostOverlays.None,
    };

    /// <summary>Подключить инспектор к UI (после сборки сцены): действия, × и Esc для «Следовать».</summary>
    private void AttachInspector()
    {
        if (_ui == null)
        {
            return;
        }

        _ui.Inspector.ActionPressed += OnInspectorAction;
        _ui.Router.InspectorCancelGesture = () =>
        {
            if (!_following)
            {
                return false;
            }

            StopFollow("esc");
            return true;
        };
    }

    // ---- выбор ----

    /// <summary>Сбросить выбор целиком (без события роутера): ядро, хост, кольцо узла, подсветка сегмента, камера, маршрут.</summary>
    private void ResetSelection()
    {
        SendCommand(Command.MakeClearSelection());
        _host?.SetSelectedVehicle(VehicleHandle.Null);
        _selVeh = VehicleHandle.Null;
        SetSelectedPed(PedHandle.Null);
        _selNode = -1;
        _nodePicker?.Clear();
        _selSeg = -1;
        if (_segSelNode != null)
        {
            _segSelNode.Visible = false;
        }

        StopFollow(string.Empty);
        _roadTool?.ReleasePin();
        _routeShown = false;
        _routeDirty = true;
        _routeTick = -1;
        _selKind = InspectKind.None;
        _selTitle = string.Empty;
        _inspTimer = 0;
        _goneTimer = -1;
        _filledKind = InspectKind.None;
        _vehHint = -1;
        _pedHint = -1;
        _segLoadValid = false;
    }

    private void SelectVehicle(VehicleHandle h)
    {
        ResetSelection();
        _selKind = InspectKind.Vehicle;
        _selVeh = h;
        _host?.SetSelectedVehicle(h);
        SendCommand(Command.MakeSelectVehicle(h)); // подсветка в слое машин и окно маршрута в кадре
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ui3] select vehicle #{h.Index} gen {h.Generation}"));
    }

    private void SelectPed(PedHandle h)
    {
        ResetSelection();
        _selKind = InspectKind.Pedestrian;
        SetSelectedPed(h);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ui3] select pedestrian #{h.Index} gen {h.Generation}"));
    }

    /// <summary>Выбрать узел (клик, «Показать» в уведомлении или <c>--select-node</c>): кольцо + инспектор; <paramref name="focus"/> - поставить камеру на узел.</summary>
    private void SelectNode(NetworkView net, int node, bool focus)
    {
        if (!net.IsNodeAlive(node))
        {
            return;
        }

        ResetSelection();
        _selKind = InspectKind.Node;
        _selNode = node;
        _selNodeGen = net.Nodes[node].Generation;
        _nodePicker?.ShowAt(in net.Nodes[node]);
        if (focus)
        {
            Vec2 p = net.Nodes[node].Position;
            _camera?.SetPose(new Vector3(p.X, 0f, p.Z), 110f, 55f, 0f);
        }

        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ui3] select node #{node} control {net.Nodes[node].Control}"));
    }

    private void SelectSegment(NetworkView net, int seg)
    {
        if (!net.IsSegmentAlive(seg))
        {
            return;
        }

        ResetSelection();
        _selKind = InspectKind.Segment;
        _selSeg = seg;
        _selSegGen = net.Segments[seg].Generation;
        DrawSegmentHighlight(net, seg);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ui3] select segment #{seg} profile {net.Segments[seg].Profile} length {net.Segments[seg].Length:0.0}"));
    }

    /// <summary>Пользователь закрыл инспектор (×, Esc): выбор снимается везде.</summary>
    private void ClearSelection()
    {
        bool had = _selKind != InspectKind.None;
        ResetSelection();
        if (had)
        {
            GD.Print("[ui3] inspector closed");
        }
    }

    /// <summary>Что показывать в правой панели UI: тип выбранного в мире объекта.</summary>
    private void SyncUiInspector() => _ui?.Router.SetInspector(_selKind);

    // ---- клик ----

    /// <summary>
    /// Клик в мире (ADR-0024 п. 4): запросы к потоку симуляции через почтовый ящик, ответы - в следующих кадрах (<see cref="HandleResponses"/>).
    /// Порядок (UI3): машина -> пешеход (если пешеходы видны агентами) -> узел -> сегмент; узлы и сегменты ищутся по копии сети в кадре.
    /// Любой клик в мире останавливает «Следовать». <paramref name="mp"/> - точка события в координатах вьюпорта.
    /// </summary>
    private void PickAtMouse(Vector2 mp)
    {
        if (_host == null || _camera == null)
        {
            return;
        }

        Camera3D cam = _camera.Camera;
        Vector3 origin = cam.ProjectRayOrigin(mp);
        Vector3 dir = cam.ProjectRayNormal(mp);
        if (dir.Y > -1e-4f)
        {
            return;
        }

        StopFollow("click");
        float t = -origin.Y / dir.Y;
        _pickHit = origin + (dir * t);
        _pickDist = origin.DistanceTo(_pickHit);
        _pickVehicleRadius = MathF.Max(RoutePickMinRadius, _pickDist * 0.012f);
        if (_info.Active && _info.Layer is InfoMode.LayerTraffic or InfoMode.LayerPeds)
        {
            // клик в слое «Трафик»/«Пешеходы» (UI7, ui-spec.md §8): инспектор сегмента; мимо дороги - узел, затем сброс выбора
            _pickStage = PickStage.None;
            PickSegmentFirst();
            return;
        }

        _pickPeds = _peds != null && PedsAsAgents(_camera.Distance);
        _pickStage = PickStage.Vehicle;
        _pickId = _host.TryRequestPickVehicle(_pickHit.X, _pickHit.Z, _pickVehicleRadius);
        if (_pickId == 0)
        {
            PickStatic();
        }
    }

    /// <summary>Ответы потока симуляции на запросы клика (почтовый ящик, ADR-0024 п. 4); устаревшие id пропускаются.</summary>
    private void HandleResponses()
    {
        if (_host == null)
        {
            return;
        }

        while (_host.TryReadResponse(out HostResponse r))
        {
            if (r.RequestId != _pickId || _pickStage == PickStage.None)
            {
                continue;
            }

            if (_pickStage == PickStage.Vehicle)
            {
                if (r.Found)
                {
                    _pickStage = PickStage.None;
                    SelectVehicle(r.Vehicle);
                }
                else if (_pickPeds)
                {
                    _pickStage = PickStage.Ped;
                    _pickId = _host.TryRequestPickPedestrian(_pickHit.X, _pickHit.Z, MathF.Max(1.5f, _pickDist * 0.01f));
                    if (_pickId == 0)
                    {
                        PickStatic();
                    }
                }
                else
                {
                    PickStatic();
                }
            }
            else if (r.Found)
            {
                _pickStage = PickStage.None;
                SelectPed(r.Pedestrian);
            }
            else
            {
                PickStatic();
            }
        }
    }

    /// <summary>Клик в инфо-слое дорог/тротуаров: сегмент приоритетнее узла (ему и принадлежит heatmap под курсором).</summary>
    private void PickSegmentFirst()
    {
        if (_frame != null && _roadsWorld != null
            && _roadsWorld.TrySegment(_pickHit.X, _pickHit.Z, MathF.Max(SegmentPickMinRadius, _pickDist * 0.012f), out SegmentHit hit))
        {
            SelectSegment(_frame.Network.View, hit.Ref.Index);
            return;
        }

        PickStatic();
    }

    /// <summary>Машины и пешехода под курсором нет: узел, затем сегмент; ничего - снять выбор.</summary>
    private void PickStatic()
    {
        _pickStage = PickStage.None;
        if (_frame == null)
        {
            return;
        }

        NetworkView net = _frame.Network.View;
        int node = _nodePicker?.Pick(net, _pickHit.X, _pickHit.Z, MathF.Max(NodePicker.MinRadius, _pickDist * 0.01f)) ?? -1;
        if (node >= 0)
        {
            SelectNode(net, node, false);
            return;
        }

        if (_roadsWorld != null && _roadsWorld.TrySegment(_pickHit.X, _pickHit.Z, MathF.Max(SegmentPickMinRadius, _pickDist * 0.012f), out SegmentHit hit))
        {
            SelectSegment(net, hit.Ref.Index);
            return;
        }

        if (_selKind != InspectKind.None)
        {
            GD.Print("[ui3] click on empty ground: selection cleared");
        }

        ResetSelection();
    }

    // ---- кнопки ----

    private void OnInspectorAction(int idx)
    {
        switch (_selKind)
        {
            case InspectKind.Vehicle when idx == 0:
                _routeShown = !_routeShown;
                _routeDirty = true;
                _routeTick = -1;
                GD.Print("[ui3] route " + (_routeShown ? "shown" : "hidden"));
                break;
            case InspectKind.Vehicle or InspectKind.Pedestrian when idx == (_selKind == InspectKind.Vehicle ? 1 : 0):
                if (_following)
                {
                    StopFollow("button");
                }
                else
                {
                    StartFollow();
                }

                break;
            case InspectKind.Node when idx == 0:
                EditNode(_selNode, NodesMode.Control);
                break;
            case InspectKind.Node:
                ApplyNodePreset(idx - 1);
                break;
            case InspectKind.Segment when idx == 2:
                EditNode(-1, NodesMode.Lanes);
                break;
            case InspectKind.Segment when idx == 0:
                SegmentTool(RoadsMode.Upgrade);
                break;
            case InspectKind.Segment when idx == 1:
                SegmentTool(RoadsMode.Remove);
                break;
            default:
                break;
        }

        _inspTimer = 0; // кнопка меняет то, что показано: обновить сразу
    }

    private void ApplyNodePreset(int idx)
    {
        if (_frame == null || _selNode < 0 || idx > 2)
        {
            return;
        }

        if (_frame.Network.View.Nodes[_selNode].Control != NodeControl.Signal)
        {
            // «Поставить светофор» у узла без светофора: режим и план одним действием (undo)
            if (_nodesTool != null && _nodesTool.Select(_selNode))
            {
                _nodesTool.ApplyControl(NodeControlChoice.SignalAdaptive);
            }

            return;
        }

        var preset = (NodeArms.NodePreset)idx;
        OnNodePreset(_selNode, preset);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ui3] node #{_selNode} preset {preset}"));
    }

    /// <summary>«Заменить профиль» / «Снести»: инструмент «Дороги» (UI4) в нужном режиме, наведённый на выбранный сегмент.</summary>
    private void SegmentTool(RoadsMode mode)
    {
        if (_roadTool == null || _ui == null || _roadsWorld == null || !_roadsWorld.TryHit(_selSeg, out SegmentHit hit))
        {
            return;
        }

        _roadTool.SetMode(mode);
        if (mode == RoadsMode.Upgrade)
        {
            // в списке сразу другой профиль: «заменить на такой же» ядру не нужно
            int same = _roadTool.Catalog.IndexOfProfile(hit.Profile);
            if (same >= 0 && same == _roadTool.ProfileIndex)
            {
                _roadTool.SetProfile(same + 1);
            }
        }

        _ui.Router.SetTool(_roadTool);
        _ui.Router.NotifyChanged();
        _roadTool.PinSegment(in hit, click: mode == RoadsMode.Remove);
        GD.Print(string.Create(CultureInfo.InvariantCulture, $"[ui3] segment #{_selSeg} -> roads tool {mode}, profile {_roadTool.Choice.Name}"));
    }

    // ---- «Следовать» ----

    private void StartFollow()
    {
        if (_camera == null || (_selKind != InspectKind.Vehicle && _selKind != InspectKind.Pedestrian))
        {
            return;
        }

        _following = true;
        _followHasSet = false;
        _followMissing = 0;
        _ui?.Inspector.SetActionPressed(_selKind == InspectKind.Vehicle ? 1 : 0, true);
        GD.Print("[ui3] follow on");
    }

    private void StopFollow(string why)
    {
        if (!_following)
        {
            return;
        }

        _following = false;
        _followHasSet = false;
        if (_ui != null && (_selKind == InspectKind.Vehicle || _selKind == InspectKind.Pedestrian))
        {
            _ui.Inspector.SetActionPressed(_selKind == InspectKind.Vehicle ? 1 : 0, false);
        }

        if (why.Length > 0)
        {
            GD.Print("[ui3] follow off: " + why);
        }
    }

    /// <summary>Раз в кадр: камера на интерполированной позиции объекта; пользователь сдвинул камеру сам - следование снимается.</summary>
    private void UpdateFollow(SimFrame f, float alpha)
    {
        if (!_following || _camera == null)
        {
            return;
        }

        if (_followHasSet && _camera.Center.DistanceSquaredTo(_followSet) > 0.01f)
        {
            StopFollow("camera moved");
            return;
        }

        bool found = _selKind == InspectKind.Vehicle
            ? TryVehiclePosition(f, alpha, out Vector3 p)
            : TryPedPosition(f, alpha, out p);
        if (!found)
        {
            if (++_followMissing > FollowLostFrames)
            {
                StopFollow("object lost");
            }

            return;
        }

        _followMissing = 0;
        _camera.SetCenter(p);
        _followSet = _camera.Center;
        _followHasSet = true;
    }

    /// <summary>Позиция выбранной машины в кадре (интерполяция Previous -> Current), поиск с подсказкой по прошлому месту в компактном списке.</summary>
    private bool TryVehiclePosition(SimFrame f, float alpha, out Vector3 p)
    {
        p = Vector3.Zero;
        ReadOnlySpan<VehicleRenderData> cur = f.Vehicles;
        ReadOnlySpan<VehicleRenderData> prev = f.PreviousVehicles;
        ushort gen = (ushort)_selVeh.Generation;
        int i = _vehHint;
        if ((uint)i >= (uint)cur.Length || cur[i].Index != _selVeh.Index || cur[i].Generation != gen)
        {
            i = -1;
            for (int k = 0; k < cur.Length; k++)
            {
                if (cur[k].Index == _selVeh.Index && cur[k].Generation == gen && (cur[k].Flags & (byte)VehicleRenderFlags.Active) != 0)
                {
                    i = k;
                    break;
                }
            }

            _vehHint = i;
            if (i < 0)
            {
                return false;
            }
        }

        VehicleRenderData c = cur[i];
        VehicleRenderData q = prev[i];
        bool interp = (q.Flags & (byte)VehicleRenderFlags.Active) != 0 && q.Generation == c.Generation;
        p = interp ? new Vector3(q.X + ((c.X - q.X) * alpha), 0f, q.Z + ((c.Z - q.Z) * alpha)) : new Vector3(c.X, 0f, c.Z);
        return true;
    }

    private bool TryPedPosition(SimFrame f, float alpha, out Vector3 p)
    {
        p = Vector3.Zero;
        ReadOnlySpan<PedestrianRenderData> cur = f.Pedestrians;
        ReadOnlySpan<PedestrianRenderData> prev = f.PreviousPedestrians;
        ushort gen = (ushort)_selPed.Generation;
        int i = _pedHint;
        if ((uint)i >= (uint)cur.Length || cur[i].Index != _selPed.Index || cur[i].Generation != gen)
        {
            i = -1;
            for (int k = 0; k < cur.Length; k++)
            {
                if (cur[k].Index == _selPed.Index && cur[k].Generation == gen && (cur[k].Flags & PedRenderFlags.Active) != 0)
                {
                    i = k;
                    break;
                }
            }

            _pedHint = i;
            if (i < 0)
            {
                return false;
            }
        }

        PedestrianRenderData c = cur[i];
        PedestrianRenderData q = prev[i];
        bool interp = (q.Flags & PedRenderFlags.Active) != 0 && q.Generation == c.Generation;
        p = interp ? new Vector3(q.X + ((c.X - q.X) * alpha), 0f, q.Z + ((c.Z - q.Z) * alpha)) : new Vector3(c.X, 0f, c.Z);
        return true;
    }

    // ---- наполнение панели ----

    /// <summary>Раз в кадр: камера «Следовать»; 4 раза в секунду - строки инспектора; объект исчез - примечание и автозакрытие через 2 с (ui-spec.md §7).</summary>
    private void UpdateInspect(SimFrame f, float alpha, double delta)
    {
        UpdateFollow(f, alpha);
        if (_ui == null || _selKind == InspectKind.None)
        {
            return;
        }

        if (_selKind == InspectKind.Segment && f.SegmentLoadsFresh && (uint)_selSeg < (uint)f.SegmentLoads.Length)
        {
            // нагрузка - тяжёлый оверлей: свежая копия раз в 10 тиков, в остальных кадрах пусто - держим последнюю
            _segLoad = f.SegmentLoads[_selSeg];
            _segLoadValid = true;
        }

        UiInspector view = _ui.Inspector;
        if (_filledKind != _selKind)
        {
            _filledKind = _selKind;
            ConfigureActions(view, f.Network.View);
            view.SetNote(string.Empty);
            view.SetArmRows(0);
            _inspTimer = 0;
        }

        if (_goneTimer >= 0)
        {
            _goneTimer -= delta;
            if (_goneTimer < 0)
            {
                GD.Print("[ui3] inspector auto-closed: object gone");
                ResetSelection();
            }

            return;
        }

        _inspTimer -= delta;
        if (_inspTimer > 0)
        {
            return;
        }

        int state = _selKind switch
        {
            InspectKind.Vehicle => FillVehicle(view, f),
            InspectKind.Pedestrian => FillPed(view, f),
            InspectKind.Node => FillNode(view, f),
            InspectKind.Segment => FillSegment(view, f),
            _ => 1,
        };
        if (state == 0)
        {
            return; // кадр ещё не несёт выбранный объект: попробовать в следующем
        }

        _inspTimer = InspectInterval;
        if (state < 0)
        {
            view.SetNote(_selKind switch
            {
                InspectKind.Vehicle => "Машина доехала или исчезла",
                InspectKind.Pedestrian => "Пешеход дошёл или исчез",
                _ => "Объект снесён",
            });
            for (int i = 0; i < UiInspector.MaxActions; i++)
            {
                view.Action(i).Disabled = true;
            }

            StopFollow("object gone");
            _roadTool?.ReleasePin();
            if (_segSelNode != null)
            {
                _segSelNode.Visible = false;
            }

            _goneTimer = GoneCloseSec;
            GD.Print("[ui3] selected object gone");
        }
    }

    /// <summary>Кнопки по типу объекта (раз при смене выбора).</summary>
    private void ConfigureActions(UiInspector view, NetworkView net)
    {
        switch (_selKind)
        {
            case InspectKind.Vehicle:
                view.SetAction(0, "Маршрут", toggle: true, pressed: _routeShown, enabled: true, "Показать путь машины поверх карты");
                view.SetAction(1, "Следовать", toggle: true, pressed: _following, enabled: true, "Камера едет за машиной. Остановить: Esc или щелчок по карте");
                view.SetActionCount(2);
                break;
            case InspectKind.Pedestrian:
                view.SetAction(0, "Следовать", toggle: true, pressed: _following, enabled: true, "Камера идёт за пешеходом. Остановить: Esc или щелчок по карте");
                view.SetActionCount(1);
                break;
            case InspectKind.Segment:
                bool tool = _roadTool != null;
                view.SetAction(0, "Заменить профиль", toggle: false, pressed: false, enabled: tool, tool ? "Инструмент «Дороги» в режиме «Заменить» на этой дороге" : "Нет инструмента «Дороги»");
                view.SetAction(1, "Снести", toggle: false, pressed: false, enabled: tool, tool ? "Снести эту дорогу (спросит подтверждение, если пропадут участки или остановки)" : "Нет инструмента «Дороги»");
                view.SetAction(2, "Полосы", toggle: false, pressed: false, enabled: _nodesTool != null, "Флаги полос: автобусная, запрет перестроения (инструмент «Узлы»)");
                view.SetActionCount(3);
                break;
            case InspectKind.Node:
                bool signal = net.IsNodeAlive(_selNode) && net.Nodes[_selNode].Control == NodeControl.Signal;
                bool nodes = _nodesTool != null;
                view.SetAction(0, "Править", toggle: false, pressed: false, enabled: nodes, "Инструмент «Узлы» на этом узле: режим, светофор, знаки, запреты (J)");
                if (signal)
                {
                    view.SetAction(1, "Адаптивный", toggle: false, pressed: false, enabled: true, "Светофор по спросу, со стрелками");
                    view.SetAction(2, "24/24 с", toggle: false, pressed: false, enabled: true, "Фиксированный цикл: 24 с на каждое направление");
                    view.SetAction(3, "Мигающий", toggle: false, pressed: false, enabled: true, "Мигающий жёлтый: перекрёсток нерегулируемый");
                    view.SetActionCount(4);
                }
                else
                {
                    view.SetAction(1, "Поставить светофор", toggle: false, pressed: false, enabled: nodes, "Адаптивный светофор на этом узле");
                    view.SetActionCount(2);
                }

                break;
            default:
                view.SetActionCount(0);
                break;
        }
    }

    /// <summary>1 - показано, 0 - кадр ещё без объекта, -1 - объект исчез.</summary>
    private int FillVehicle(UiInspector view, SimFrame f)
    {
        if (!f.SelectedVehicle.Equals(_selVeh))
        {
            return 0;
        }

        if (!f.SelectedVehicleValid)
        {
            return -1;
        }

        VehicleInfo vi = f.SelectedVehicleInfo;
        CultureInfo inv = CultureInfo.InvariantCulture;
        NetworkView net = f.Network.View;
        if (_selTitle.Length == 0)
        {
            bool bus = vi.Type < _types.Length && _types[vi.Type].Category == VehicleCategory.Bus;
            _selTitle = string.Create(inv, $"{(bus ? "Автобус" : "Легковая")} #{_selVeh.Index}");
        }

        view.SetTitle(_selTitle);
        view.SetRow(0, "Скорость", _isb.Clear().Append(inv, $"{vi.Speed * 3.6f:0} км/ч"));

        _isb.Clear();
        if (vi.DestZone >= 0 && vi.DestZone < net.Zones.Length)
        {
            ZoneData z = net.Zones[vi.DestZone];
            _isb.Append(inv, $"зона {vi.DestZone}");
            if (z.IsExternal != 0)
            {
                _isb.Append(" (выезд из города)");
            }

            if (TryVehiclePosition(f, 1f, out Vector3 p))
            {
                float dx = z.Center.X - p.X;
                float dz = z.Center.Z - p.Z;
                _isb.Append(inv, $", {MathF.Sqrt((dx * dx) + (dz * dz)) / 1000f:0.0} км");
            }
        }
        else
        {
            _isb.Append('—');
        }

        view.SetRow(1, "Цель", _isb);
        long trip = Math.Max(0L, f.SnapshotTick - vi.SpawnTick) / Simulation.TicksPerSecond;
        view.SetRow(2, "В пути", _isb.Clear().Append(inv, $"{trip / 60}:{trip % 60:00}"));
        view.SetRow(3, "Состояние", VehicleState(net, in vi));
        view.SetRow(4, "Где", LaneName(net, vi.Lane));
        view.SetRowCount(5);
        return 1;
    }

    /// <summary>Едет / ждёт светофор / в пробке / на остановке / застрял: флаги снапшота, скорость и узел в конце полосы.</summary>
    private static string VehicleState(NetworkView net, in VehicleInfo vi)
    {
        if ((vi.Flags & (byte)VehicleRenderFlags.Stuck) != 0)
        {
            return "застряла";
        }

        if ((vi.Flags & (byte)VehicleRenderFlags.AtStop) != 0)
        {
            return "на остановке";
        }

        if (vi.Speed >= 0.5f)
        {
            return "едет";
        }

        if (net.IsLaneAlive(vi.Lane))
        {
            LaneData lane = net.Lanes[vi.Lane];
            int end = -1;
            if (lane.Kind != LaneKind.Connector && net.IsSegmentAlive(lane.Segment))
            {
                SegmentData s = net.Segments[lane.Segment];
                end = lane.Direction == 0 ? s.NodeB : s.NodeA;
            }

            if (net.IsNodeAlive(end) && net.Nodes[end].Control == NodeControl.Signal && lane.Length - vi.S < 40f)
            {
                return "ждёт светофор";
            }
        }

        return "стоит в пробке";
    }

    /// <summary>Название дороги под машиной (профиль сегмента) или «перекрёсток» для connector'а; строка кэшируется по сегменту и версии сети.</summary>
    private string LaneName(NetworkView net, int lane)
    {
        if (!net.IsLaneAlive(lane))
        {
            return "—";
        }

        LaneData l = net.Lanes[lane];
        if (l.Kind == LaneKind.Connector)
        {
            return "перекрёсток";
        }

        return SegmentName(net, l.Segment);
    }

    private string SegmentName(NetworkView net, int seg)
    {
        if (!net.IsSegmentAlive(seg))
        {
            return "—";
        }

        if (seg == _nameSeg && net.Version == _nameVer)
        {
            return _nameStr;
        }

        _catalog ??= RoadCatalog.FromView(in net);
        SegmentData s = net.Segments[seg];
        int ci = _catalog.IndexOfProfile(s.Profile);
        float width = s.Profile >= 0 && s.Profile < net.RoadProfiles.Length ? net.RoadProfiles[s.Profile].WidthMm / 1000f : ((s.LanesAB + s.LanesBA) * 3.5f) + 4f;
        _nameStr = ci >= 0 ? _catalog[ci].Name : RoadCatalog.NameOf(s.Kind, s.LanesAB, s.LanesBA, width);
        _nameSeg = seg;
        _nameVer = net.Version;
        return _nameStr;
    }

    private int FillPed(UiInspector view, SimFrame f)
    {
        if (!f.SelectedPed.Equals(_selPed))
        {
            return 0;
        }

        if (!f.SelectedPedValid)
        {
            return -1;
        }

        PedInfo info = f.SelectedPedInfo;
        CultureInfo inv = CultureInfo.InvariantCulture;
        if (_selTitle.Length == 0)
        {
            _selTitle = string.Create(inv, $"Пешеход #{_selPed.Index}");
        }

        view.SetTitle(_selTitle);
        view.SetRow(0, "Состояние", info.State switch
        {
            PedState.PendingPath => "ищет путь",
            PedState.Walking => "идёт",
            PedState.AtCrossing => "ждёт у перехода",
            PedState.Crossing => "переходит дорогу",
            PedState.AtStop => "ждёт автобус",
            PedState.OnBus => "едет в автобусе",
            _ => "—",
        });
        view.SetRow(1, "Этап", info.Leg switch
        {
            PedLeg.ToStop => "к остановке",
            PedLeg.FromStop => "от остановки к цели",
            _ => "пешком к цели",
        });
        view.SetRow(2, "Житель", info.Resident >= 0 ? _isb.Clear().Append(inv, $"#{info.Resident}") : _isb.Clear().Append('—'));
        int trip = info.TripTicks / Simulation.TicksPerSecond;
        view.SetRow(3, "В пути", _isb.Clear().Append(inv, $"{trip / 60}:{trip % 60:00}"));
        view.SetRow(4, "Ждёт", info.WaitTicks > 0 ? _isb.Clear().Append(inv, $"{info.WaitTicks / (float)Simulation.TicksPerSecond:0} с") : _isb.Clear().Append('—'));
        view.SetRow(5, "Осталось", _isb.Clear().Append(inv, $"{info.StepsLeft} участков пути"));
        view.SetRowCount(6);
        return 1;
    }

    private int FillNode(UiInspector view, SimFrame f)
    {
        NetworkView net = f.Network.View;
        if (!net.IsNodeAlive(_selNode) || net.Nodes[_selNode].Generation != _selNodeGen)
        {
            return -1;
        }

        if ((f.Overlays & HostOverlays.SignalStates) == 0)
        {
            return 0; // подписка на оверлеи узла ещё не дошла до кадра
        }

        CultureInfo inv = CultureInfo.InvariantCulture;
        ref readonly NodeData nd = ref net.Nodes[_selNode];
        _arms.Collect(f, net, _selNode);
        if (_selTitle.Length == 0)
        {
            _selTitle = string.Create(inv, $"{(nd.ArmCount >= 3 ? "Перекрёсток" : "Узел")} #{_selNode}");
        }

        view.SetTitle(_selTitle);
        int row = 0;
        view.SetRow(row++, "Управление", nd.Control switch
        {
            NodeControl.Signal => "светофор",
            NodeControl.Yield => "главная дорога (уступи)",
            NodeControl.Stop => "знак «Стоп»",
            NodeControl.RightHandRule => "правило правой руки",
            _ => "без регулирования",
        });
        view.SetRow(row++, "Рукава", _nums[Math.Min(_arms.Count, _nums.Length - 1)]);
        bool signal = nd.Control == NodeControl.Signal && nd.Signal >= 0;
        ReadOnlySpan<SignalState> ss = f.SignalStates;
        if (signal && nd.Signal < ss.Length)
        {
            SignalState st = ss[nd.Signal];
            string interval = st.Interval switch
            {
                SignalInterval.Green => "зелёный",
                SignalInterval.Yellow => "жёлтый",
                SignalInterval.AllRed => "все красные",
                _ => "выключен",
            };
            view.SetRow(row++, "Фаза", _isb.Clear().Append(inv, $"{st.Phase + 1} · {interval}, смена через {st.TicksToChange / (float)Simulation.TicksPerSecond:0.0} с"));
        }
        else if (signal)
        {
            view.SetRow(row++, "Фаза", "план не активен");
        }

        int queued = 0;
        for (int a = 0; a < _arms.Count; a++)
        {
            queued += _arms.Queue(a);
        }

        view.SetRow(row++, "В очередях", _isb.Clear().Append(inv, $"{queued} машин"));
        ReadOnlySpan<uint> thr = f.NodeThroughput;
        view.SetRow(row++, "Проехало", _isb.Clear().Append(inv, $"{(_selNode < thr.Length ? thr[_selNode] : 0u)} машин"));
        if (signal && f.SignalMetrics != null)
        {
            f.SignalMetrics.CopyTo(_metricsCopy);
            _isb.Clear();
            for (int m = 0; m < 3; m++)
            {
                long g = _metricsCopy.Grants[m];
                double w = g > 0 ? _metricsCopy.WaitTicks[m] / (double)g / Simulation.TicksPerSecond : 0.0;
                _isb.Append(m == 0 ? "↑ " : m == 1 ? " · → " : " · ← ").Append(inv, $"{w:0.0}");
            }

            _isb.Append(" с");
            view.SetRow(row++, "Ожидание, город", _isb);
        }

        view.SetRowCount(row);
        FillArmTable(view);
        view.SetNote(_arms.AnyCrossing ? string.Empty : "Пешеходных переходов нет");
        return 1;
    }

    private void FillArmTable(UiInspector view)
    {
        Color text = UiPalette.Text;
        Color dim = UiPalette.TextSecondary;
        view.SetArmCell(0, 0, "Рукав", dim);
        view.SetArmCell(0, 1, "↑", dim);
        view.SetArmCell(0, 2, "→", dim);
        view.SetArmCell(0, 3, "←", dim);
        view.SetArmCell(0, 4, "↶", dim);
        view.SetArmCell(0, 5, "Очередь", dim);
        view.SetArmCell(0, 6, "Переход", dim);
        int n = Math.Min(_arms.Count, UiInspector.MaxArmRows);
        for (int a = 0; a < n; a++)
        {
            int r = a + 1;
            view.SetArmCell(r, 0, _arms.ArmName(a), text);
            for (int m = 0; m < NodeArms.Maneuvers; m++)
            {
                byte sig = _arms.Signal(a, m);
                Color c = sig switch
                {
                    (byte)LaneSignal.Green => UiPalette.SignalGreen,
                    (byte)LaneSignal.Yellow => UiPalette.SignalYellow,
                    (byte)LaneSignal.Red => UiPalette.SignalRed,
                    _ => dim,
                };
                view.SetArmCell(r, 1 + m, _signalLetters[Math.Min((int)sig, 3)], c);
            }

            view.SetArmCell(r, 5, _nums[Math.Min(_arms.Queue(a), _nums.Length - 1)], text);
            if (_arms.HasCrossing(a))
            {
                int sig = _arms.CrossingSignal(a);
                int occ = _arms.CrossingOccupancy(a);
                string s = sig == 1 ? (occ == 2 ? "идут" : "идите")
                    : sig == 2 ? "заканч."
                    : occ == 1 ? "стоп, вызов" : occ == 2 ? "стоп, занят" : "стоп";
                view.SetArmCell(r, 6, s, sig == 1 ? UiPalette.SignalGreen : sig == 2 ? UiPalette.SignalYellow : text);
            }
            else
            {
                view.SetArmCell(r, 6, "—", dim);
            }
        }

        view.SetArmRows(n);
    }

    private int FillSegment(UiInspector view, SimFrame f)
    {
        NetworkView net = f.Network.View;
        if (!net.IsSegmentAlive(_selSeg) || net.Segments[_selSeg].Generation != _selSegGen)
        {
            return -1;
        }

        CultureInfo inv = CultureInfo.InvariantCulture;
        SegmentData s = net.Segments[_selSeg];
        if (_selTitle.Length == 0)
        {
            _selTitle = string.Create(inv, $"Дорога #{_selSeg}");
        }

        view.SetTitle(_selTitle);
        view.SetRow(0, "Профиль", SegmentName(net, _selSeg));
        view.SetRow(1, "Длина", _isb.Clear().Append(inv, $"{s.Length:0} м"));
        _isb.Clear();
        if (s.LanesAB > 0 && s.LanesBA > 0)
        {
            _isb.Append(inv, $"{s.LanesAB} + {s.LanesBA}");
        }
        else
        {
            _isb.Append(inv, $"{Math.Max(s.LanesAB, s.LanesBA)}, одностороннее");
        }

        view.SetRow(2, "Полосы", _isb);
        view.SetRow(3, "Ограничение", _isb.Clear().Append(inv, $"{s.SpeedLimit * 3.6f:0} км/ч"));
        if (_segLoadValid)
        {
            SegmentLoad l = _segLoad;
            if (l.Count == 0)
            {
                view.SetRow(4, "Загрузка", "свободно");
                view.SetRow(5, "Средняя скорость", "—");
            }
            else
            {
                view.SetRow(4, "Загрузка", _isb.Clear().Append(inv, $"{l.Count} маш. · {l.Density:0} на полосо-км"));
                view.SetRow(5, "Средняя скорость", _isb.Clear().Append(inv, $"{l.SpeedRatio * s.SpeedLimit * 3.6f:0} км/ч ({l.SpeedRatio * 100f:0}%)"));
            }
        }
        else
        {
            view.SetRow(4, "Загрузка", "…");
            view.SetRow(5, "Средняя скорость", "…");
        }

        view.SetRow(6, "Состояние", (s.Flags & SegmentFlags.Closing) != 0 ? "перекрывается" : "открыта");
        view.SetRowCount(7);
        return 1;
    }

    // ---- подсветка сегмента ----

    /// <summary>Контур выбранного сегмента: две тонкие ленты по краям проезжей части (прямая или квадратичная Безье, как ось сегмента).</summary>
    private void DrawSegmentHighlight(NetworkView net, int seg)
    {
        if (_segSelMesh == null)
        {
            _segSelMesh = new ImmediateMesh();
            _segSelNode = new MeshInstance3D
            {
                Name = "SegmentSelection",
                Mesh = _segSelMesh,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = Palette.NodePick, // тот же цвет, что кольцо выбранного узла
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                    NoDepthTest = true,
                },
            };
            AddChild(_segSelNode);
        }

        _segSelMesh.ClearSurfaces();
        SegmentData s = net.Segments[seg];
        Vec2 a = net.Nodes[s.NodeA].Position;
        Vec2 b = net.Nodes[s.NodeB].Position;
        bool curve = (s.Flags & SegmentFlags.Curve) != 0;
        float half = (s.Profile >= 0 && s.Profile < net.RoadProfiles.Length ? net.RoadProfiles[s.Profile].WidthMm / 2000f : (((s.LanesAB + s.LanesBA) * 3.5f) + 4f) * 0.5f) + 0.6f;
        const int steps = 16;
        const float y = 0.6f;
        const float band = 0.7f;
        // контур от стоп-линии до стоп-линии: внутрь перекрёстков (радиус узла) не заходит
        float len = MathF.Max(1f, s.Length);
        float t0 = Math.Clamp(net.Nodes[s.NodeA].Radius / len, 0f, 0.45f);
        float t1 = 1f - Math.Clamp(net.Nodes[s.NodeB].Radius / len, 0f, 0.45f);
        _segSelMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        for (int side = -1; side <= 1; side += 2)
        {
            Vector3 prevOut = default;
            Vector3 prevIn = default;
            for (int k = 0; k <= steps; k++)
            {
                float t = t0 + ((t1 - t0) * k / steps);
                Vector2 p = Bez(a, b, s.Cx, s.Cz, curve, t);
                Vector2 tan = Bez(a, b, s.Cx, s.Cz, curve, Math.Min(1f, t + 0.01f)) - Bez(a, b, s.Cx, s.Cz, curve, Math.Max(0f, t - 0.01f));
                Vector2 n = tan.LengthSquared() > 1e-8f ? new Vector2(-tan.Y, tan.X).Normalized() : Vector2.Zero;
                var outer = new Vector3(p.X + (n.X * side * (half + band)), y, p.Y + (n.Y * side * (half + band)));
                var inner = new Vector3(p.X + (n.X * side * half), y, p.Y + (n.Y * side * half));
                if (k > 0)
                {
                    _segSelMesh.SurfaceAddVertex(prevOut);
                    _segSelMesh.SurfaceAddVertex(prevIn);
                    _segSelMesh.SurfaceAddVertex(outer);
                    _segSelMesh.SurfaceAddVertex(prevIn);
                    _segSelMesh.SurfaceAddVertex(inner);
                    _segSelMesh.SurfaceAddVertex(outer);
                }

                prevOut = outer;
                prevIn = inner;
            }
        }

        _segSelMesh.SurfaceEnd();
        _segSelNode!.Visible = true;
    }

    private static Vector2 Bez(Vec2 a, Vec2 b, float cx, float cz, bool curve, float t)
    {
        if (!curve)
        {
            return new Vector2(a.X + ((b.X - a.X) * t), a.Z + ((b.Z - a.Z) * t));
        }

        float u = 1f - t;
        return new Vector2((u * u * a.X) + (2f * u * t * cx) + (t * t * b.X), (u * u * a.Z) + (2f * u * t * cz) + (t * t * b.Z));
    }
}
