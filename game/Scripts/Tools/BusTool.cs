using System;
using System.Collections.Generic;
using System.Globalization;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Host;

namespace TrafficCity.Tools;

/// <summary>Режим инструмента «Транспорт».</summary>
public enum BusMode
{
    /// <summary>Линия не выбрана: клик по остановке выбирает её линию.</summary>
    Select = 0,

    /// <summary>Новая линия: клики ставят остановки по порядку.</summary>
    Build = 1,

    /// <summary>Правка выбранной линии: клик - добавить остановку, ПКМ по остановке - удалить.</summary>
    Edit = 2,
}

/// <summary>Что инструменту «Транспорт» нужно от клиента: копия сети последнего кадра, пауза, замки прогрессии, тип ТС автобуса.</summary>
public interface IBusWorld
{
    NetworkSnapshot? Network { get; }

    bool Paused { get; }

    /// <summary>Признак <c>Feature.BusLines</c> открыт (или прогрессии в сценарии нет).</summary>
    bool LinesUnlocked { get; }

    /// <summary>Тип ТС для новой линии (категория Bus) или -1, если в сценарии автобусов нет.</summary>
    int BusVehicleType { get; }
}

/// <summary>
/// Инструмент «Транспорт» (UI11, ui-spec §5.3, player-tools §8.7): создание линии кликами по дорогам, правка (вставка, удаление остановок), параметры, снятие.
/// Новая линия: первый клик ставит <c>CreateBusRoute</c> (приостановленной), после подтверждения ядра каждая остановка - <c>InsertBusStop</c>, слитый в тот же пункт undo
/// (<see cref="ToolCommandService.Submit(in Command, uint)"/>); «Готово» - <c>SetBusRouteParams(Resume)</c> в тот же пункт. Итог - одно действие undo на всю линию.
/// Каждая правка существующей линии - отдельный пункт undo.
/// </summary>
public sealed class BusTool : ToolBase
{
    public const float PickRadius = 14f;
    public const float StopPickRadius = 18f;
    public const int DefaultHeadwayTicks = 120 * Simulation.TicksPerSecond;
    public const int MinHeadwayTicks = 20 * Simulation.TicksPerSecond;
    public const int MaxHeadwayTicks = 3600 * Simulation.TicksPerSecond;

    private readonly IBusWorld _world;
    private readonly List<int> _gensBefore = new();
    private BusMode _mode;
    private EntityRef _route = EntityRef.None;
    private uint _createId;
    private int _awaitVersion = -1;
    private uint _chainId;
    private bool _hasQueued;
    private Candidate _queued;
    private Candidate _hover;
    private int _hoverStop = -1;      // индекс остановки (в BusStops) под курсором
    private int _hoverStopRoute = -1;
    private bool _cursorValid;
    private int _version;
    private int _lastNetVersion = -1;

    public BusTool(ToolCommandService service, IBusWorld world)
        : base(service)
    {
        _world = world;
        service.ActionCompleted += OnAck;
    }

    public override string Id => "transit";

    public override string DisplayName => "Транспорт";

    public event Action<string, bool>? Message;

    public event Action<string>? Logged;

    public event Action? OptionsChanged;

    /// <summary>Подтверждение опасного действия (P13): текст, кнопка, действие.</summary>
    public Action<string, string, Action>? Confirm { get; set; }

    public BusMode Mode => _mode;

    /// <summary>Выбранная или строящаяся линия; <c>Index &lt; 0</c> - нет.</summary>
    public EntityRef Route => _route;

    /// <summary>Ждём от ядра новую линию (после первого клика новой линии).</summary>
    public bool Creating => _createId != 0;

    public bool CursorValid => _cursorValid;

    public bool HoverValid => _hover.Valid;

    public float HoverX => _hover.X;

    public float HoverZ => _hover.Z;

    public int HoverStop => _hoverStop;

    /// <summary>Растёт при любой смене того, что рисует вид (наведение, линия, превью).</summary>
    public int StateVersion => _version;

    public bool LinesUnlocked => _world.LinesUnlocked;

    public override void OnEnter()
    {
        _version++;
        OptionsChanged?.Invoke();
    }

    public override void OnExit()
    {
        FinishBuild(cancelled: true);
        base.OnExit();
        _cursorValid = false;
        _version++;
    }

    /// <summary>«Новая линия»: дальше клики по дорогам ставят остановки.</summary>
    public void StartNewLine()
    {
        if (!_world.LinesUnlocked)
        {
            Message?.Invoke("Автобусные линии ещё не открыты — см. вехи в панели «Город» (C)", false);
            return;
        }

        if (_world.BusVehicleType < 0)
        {
            Message?.Invoke("В этом сценарии нет автобусов", false);
            return;
        }

        FinishBuild(cancelled: true);
        _mode = BusMode.Build;
        _route = EntityRef.None;
        _chainId = 0;
        Changed();
    }

    /// <summary>Выбрать линию для правки (из списка, инспектора или «Показать» в уведомлении).</summary>
    public void SelectRoute(int index)
    {
        NetworkSnapshot? snap = _world.Network;
        if (snap == null || !snap.View.IsBusRouteAlive(index))
        {
            return;
        }

        FinishBuild(cancelled: true);
        _route = snap.View.BusRouteRef(index);
        _mode = BusMode.Edit;
        Changed();
    }

    public void ClearSelection()
    {
        FinishBuild(cancelled: true);
        _mode = BusMode.Select;
        _route = EntityRef.None;
        Changed();
    }

    /// <summary>Завершить новую линию: возобновить (линия начинает выпускать автобусы) и перейти к правке.</summary>
    public void Finish() => FinishBuild(cancelled: false);

    /// <summary>Интервал выпуска: шаг ±<paramref name="steps"/> по шкале 30 с … 60 мин.</summary>
    public void StepHeadway(int steps)
    {
        if (!TryRouteData(out BusRouteData r))
        {
            return;
        }

        int cur = Math.Abs(r.HeadwayTicks);
        int next = NextHeadway(cur, steps);
        if (next == cur)
        {
            return;
        }

        var p = new SetBusRouteParamsCmd { Route = _route, HeadwayTicks = next, VehicleType = -1 };
        Submit(Command.MakeSetBusRouteParams(in p), "интервал " + HeadwayText(next));
    }

    public void ToggleSuspend()
    {
        if (!TryRouteData(out BusRouteData r))
        {
            return;
        }

        var p = new SetBusRouteParamsCmd { Route = _route, HeadwayTicks = 0, VehicleType = -1, Flags = r.Suspended ? BusRouteFlags.Resume : BusRouteFlags.Suspend };
        Submit(Command.MakeSetBusRouteParams(in p), r.Suspended ? "возобновить" : "приостановить");
    }

    /// <summary>Снять линию (с подтверждением P13).</summary>
    public void RemoveRoute()
    {
        if (!TryRouteData(out BusRouteData r))
        {
            return;
        }

        EntityRef route = _route;
        string text = "Удалить линию " + LineName(route.Index) + " и её остановки (" + r.StopCount.ToString(CultureInfo.InvariantCulture) + ")? Пассажиры выйдут, автобусы доедут и исчезнут. Можно отменить (Ctrl+Z).";
        void Act()
        {
            Submit(Command.MakeRemoveBusRoute(new RemoveBusRouteCmd { Route = route }), "снять линию");
            _route = EntityRef.None;
            _mode = BusMode.Select;
            Changed();
        }

        if (Confirm != null)
        {
            Confirm(text, "Удалить", Act);
        }
        else
        {
            Act();
        }
    }

    public static string LineName(int index) => "№" + (index + 1).ToString(CultureInfo.InvariantCulture);

    public static string HeadwayText(int ticks)
    {
        int sec = Math.Abs(ticks) / Simulation.TicksPerSecond;
        return sec % 60 == 0 ? (sec / 60).ToString(CultureInfo.InvariantCulture) + " мин" : (sec / 60).ToString(CultureInfo.InvariantCulture) + " мин " + (sec % 60).ToString(CultureInfo.InvariantCulture) + " с";
    }

    public static int NextHeadway(int cur, int steps)
    {
        ReadOnlySpan<int> scale = stackalloc int[] { 30, 60, 90, 120, 180, 240, 300, 420, 600, 900, 1200, 1800, 2700, 3600 };
        int sec = cur / Simulation.TicksPerSecond;
        int at = 0;
        for (int i = 0; i < scale.Length; i++)
        {
            if (scale[i] <= sec)
            {
                at = i;
            }
        }

        at = Math.Clamp(at + steps, 0, scale.Length - 1);
        return scale[at] * Simulation.TicksPerSecond;
    }

    /// <summary>Данные выбранной линии (живой).</summary>
    public bool TryRouteData(out BusRouteData data)
    {
        data = default;
        NetworkSnapshot? snap = _world.Network;
        if (snap == null || _route.Index < 0)
        {
            return false;
        }

        NetworkView v = snap.View;
        if (!v.IsBusRouteAlive(_route.Index) || v.BusRoutes[_route.Index].Generation != _route.Generation)
        {
            return false;
        }

        data = v.BusRoutes[_route.Index];
        return true;
    }

    public override void OnHover(in ToolPointer at)
    {
        _cursorValid = at.OnGround;
        NetworkSnapshot? snap = _world.Network;
        Candidate c = default;
        int stop = -1;
        int stopRoute = -1;
        if (snap != null && at.OnGround)
        {
            NetworkView v = snap.View;
            stop = NearestStop(v, at.X, at.Z, out stopRoute);
            c = FindLane(v, at.X, at.Z);
        }

        if (!SameCandidate(c, _hover) || stop != _hoverStop || at.OnGround != _cursorValid)
        {
            _version++;
        }

        _hover = c;
        _hoverStop = stop;
        _hoverStopRoute = stopRoute;
        UpdatePreview();
    }

    public override void OnPress(in ToolPointer at)
    {
    }

    public override void OnRelease(in ToolPointer at)
    {
        OnHover(at);
        if (at.Button == ToolButton.Secondary)
        {
            SecondaryClick();
            return;
        }

        switch (_mode)
        {
            case BusMode.Select:
                if (_hoverStopRoute >= 0)
                {
                    SelectRoute(_hoverStopRoute);
                }
                else
                {
                    Message?.Invoke("Выберите линию: клик по остановке или список в строке опций. Новая линия — кнопка «Новая линия»", false);
                }

                break;
            case BusMode.Build:
                BuildClick(at.Mods);
                break;
            case BusMode.Edit:
                EditClick(at.Mods);
                break;
        }
    }

    public override bool CancelGesture()
    {
        if (_mode == BusMode.Build)
        {
            FinishBuild(cancelled: true);
            return true;
        }

        if (_mode == BusMode.Edit)
        {
            ClearSelection();
            return true;
        }

        return false;
    }

    /// <summary>Раз в кадр: дождаться новой линии и поставить отложенную первую остановку.</summary>
    public void Tick()
    {
        NetworkSnapshot? snap = _world.Network;
        if (snap == null)
        {
            return;
        }

        if (snap.Version != _lastNetVersion)
        {
            _lastNetVersion = snap.Version;
            if (_mode == BusMode.Edit && !TryRouteData(out _) && _createId == 0)
            {
                _mode = BusMode.Select;  // линию сняли (undo, событие)
                _route = EntityRef.None;
            }

            Changed();
        }

        if (_createId == 0 || _awaitVersion < 0 || snap.Version < _awaitVersion)
        {
            return;
        }

        NetworkView v = snap.View;
        int found = -1;
        for (int i = 0; i < v.BusRoutes.Length; i++)
        {
            int before = i < _gensBefore.Count ? _gensBefore[i] : 0;
            if (v.IsBusRouteAlive(i) && v.BusRoutes[i].Generation != before && v.BusRoutes[i].StopCount == 0)
            {
                found = i;
                break;
            }
        }

        _awaitVersion = -1;
        if (found < 0)
        {
            Logged?.Invoke("[bus] new route not found after create ack");
            _createId = 0;
            _hasQueued = false;
            _mode = BusMode.Select;
            Changed();
            return;
        }

        _route = v.BusRouteRef(found);
        _chainId = _createId;
        _createId = 0;
        Logged?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[bus] route created #{found} gen {_route.Generation}"));
        if (_hasQueued)
        {
            _hasQueued = false;
            InsertStop(_queued, -1, chain: true);
        }

        Changed();
    }

    private void BuildClick(ToolMods mods)
    {
        if (_createId != 0)
        {
            Message?.Invoke(_world.Paused ? "Линия появится после снятия паузы" : "Линия ещё создаётся", false);
            return;
        }

        // клик по первой остановке строящейся линии - завершить (ui-spec §5.3 п. 3)
        if (_route.Index >= 0 && _hoverStopRoute == _route.Index && TryRouteData(out BusRouteData rd) && rd.StopCount >= 2 && _world.Network is { } fs && FirstStopOf(rd, fs.View) == _hoverStop)
        {
            FinishBuild(cancelled: false);
            return;
        }

        if (!_hover.Valid)
        {
            Message?.Invoke("Остановку можно поставить только на дорогу", false);
            return;
        }

        if (_route.Index < 0)
        {
            NetworkSnapshot? snap = _world.Network;
            if (snap == null)
            {
                return;
            }

            _gensBefore.Clear();
            ReadOnlySpan<BusRouteData> routes = snap.View.BusRoutes;
            for (int i = 0; i < routes.Length; i++)
            {
                _gensBefore.Add(routes[i].Generation);
            }

            var create = new CreateBusRouteCmd { HeadwayTicks = DefaultHeadwayTicks, VehicleType = _world.BusVehicleType, Flags = BusRouteFlags.Suspend };
            _createId = Service.Submit(Command.MakeCreateBusRoute(in create));
            if (_createId == 0)
            {
                Message?.Invoke("Очередь команд занята, попробуйте ещё раз", false);
                return;
            }

            _queued = _hover;
            _hasQueued = true;
            Logged?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[bus] create route #{_createId}"));
            Changed();
            return;
        }

        _ = mods;
        InsertStop(_hover, -1, chain: true);
    }

    private void EditClick(ToolMods mods)
    {
        if (_hoverStopRoute >= 0 && _hoverStopRoute != _route.Index)
        {
            SelectRoute(_hoverStopRoute);
            return;
        }

        if (!_hover.Valid || !TryRouteData(out BusRouteData r))
        {
            return;
        }

        // Shift - вставить после ближайшей остановки линии, иначе - в конец
        int pos = -1;
        if ((mods & ToolMods.Shift) != 0 && _world.Network is { } snap)
        {
            int k = NearestStopOnRoute(snap.View, r, _hover.X, _hover.Z);
            pos = k >= 0 ? k + 1 : -1;
        }

        InsertStop(_hover, pos, chain: false);
    }

    private void SecondaryClick()
    {
        if (_mode == BusMode.Build)
        {
            FinishBuild(cancelled: true);
            return;
        }

        if (_mode == BusMode.Edit && _hoverStop >= 0 && _hoverStopRoute == _route.Index && _world.Network is { } snap)
        {
            EntityRef stop = snap.View.BusStopRef(_hoverStop);
            Submit(Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = stop }), "удалить остановку");
        }
    }

    private void InsertStop(in Candidate c, int position, bool chain)
    {
        var ins = new InsertBusStopCmd { Route = _route, Position = position, Seg = c.Seg, Dir = c.Dir, LaneNo = c.LaneNo, Kind = (byte)BusStopKind.Curb, S = c.S };
        Command cmd = Command.MakeInsertBusStop(in ins);
        uint id = chain && _chainId != 0 ? Service.Submit(in cmd, _chainId) : Service.Submit(in cmd);
        if (id == 0)
        {
            Message?.Invoke("Очередь команд занята, попробуйте ещё раз", false);
            return;
        }

        if (chain)
        {
            _chainId = id;
        }

        Logged?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[bus] insert stop #{id} route {_route.Index} seg {c.Seg.Index} dir {c.Dir} s {c.S:0.0} pos {position}"));
        Changed();
    }

    private void FinishBuild(bool cancelled)
    {
        if (_mode != BusMode.Build)
        {
            return;
        }

        _hasQueued = false;
        if (_createId != 0)
        {
            // линия ещё не подтверждена: дождёмся и сразу снимем - проще оставить приостановленной; игрок увидит её в списке
            _createId = 0;
            _awaitVersion = -1;
            _mode = BusMode.Select;
            Changed();
            return;
        }

        if (_route.Index < 0 || !TryRouteData(out BusRouteData r))
        {
            _mode = BusMode.Select;
            _route = EntityRef.None;
            Changed();
            return;
        }

        if (r.StopCount == 0)
        {
            Command rm = Command.MakeRemoveBusRoute(new RemoveBusRouteCmd { Route = _route });
            _ = _chainId != 0 ? Service.Submit(in rm, _chainId) : Service.Submit(in rm);
            _mode = BusMode.Select;
            _route = EntityRef.None;
            _chainId = 0;
            Changed();
            return;
        }

        if (cancelled)
        {
            Message?.Invoke("Линия " + LineName(_route.Index) + " сохранена приостановленной: «Возобновить» в строке опций", false);
        }
        else
        {
            var p = new SetBusRouteParamsCmd { Route = _route, HeadwayTicks = 0, VehicleType = -1, Flags = BusRouteFlags.Resume };
            Command cmd = Command.MakeSetBusRouteParams(in p);
            _ = _chainId != 0 ? Service.Submit(in cmd, _chainId) : Service.Submit(in cmd);
        }

        _chainId = 0;
        _mode = BusMode.Edit;
        Changed();
    }

    private void Submit(in Command cmd, string what)
    {
        uint id = Service.Submit(in cmd);
        if (id == 0)
        {
            Message?.Invoke("Очередь команд занята, попробуйте ещё раз", false);
            return;
        }

        Logged?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[bus] {what} #{id} route {_route.Index}"));
        if (_world.Paused)
        {
            Message?.Invoke("Изменение применится после снятия паузы", false);
        }
    }

    private void OnAck(ToolResult r)
    {
        if (_createId == 0 || r.Ack.ActionId != _createId)
        {
            return;
        }

        if (!r.Ok)
        {
            _createId = 0;
            _hasQueued = false;
            _mode = BusMode.Select;
            Changed();
            return; // текст отказа уже пришёл тостом из событий ядра
        }

        _awaitVersion = r.Ack.NetworkVersion;
    }

    private void UpdatePreview()
    {
        if (!_hover.Valid || _mode == BusMode.Select || _createId != 0)
        {
            Service.ClearPreview();
            return;
        }

        if (_route.Index < 0)
        {
            var create = new CreateBusRouteCmd { HeadwayTicks = DefaultHeadwayTicks, VehicleType = Math.Max(0, _world.BusVehicleType), Flags = BusRouteFlags.Suspend };
            Service.SetPreview(Command.MakeCreateBusRoute(in create));
            return;
        }

        var ins = new InsertBusStopCmd { Route = _route, Position = -1, Seg = _hover.Seg, Dir = _hover.Dir, LaneNo = _hover.LaneNo, Kind = (byte)BusStopKind.Curb, S = _hover.S };
        Service.SetPreview(Command.MakeInsertBusStop(in ins));
    }

    private void Changed()
    {
        _version++;
        OptionsChanged?.Invoke();
    }

    private static int FirstStopOf(BusRouteData r, NetworkView v) => r.StopCount > 0 && (uint)r.FirstStop < (uint)v.BusRouteStops.Length ? v.BusRouteStops[r.FirstStop] : -1;

    private static int NearestStopOnRoute(NetworkView v, in BusRouteData r, float x, float z)
    {
        int best = -1;
        float bd = float.MaxValue;
        for (int k = 0; k < r.StopCount; k++)
        {
            int slot = r.FirstStop + k;
            if ((uint)slot >= (uint)v.BusRouteStops.Length)
            {
                break;
            }

            int st = v.BusRouteStops[slot];
            if (!TryStopPos(v, st, out Vec2 p))
            {
                continue;
            }

            float d = ((p.X - x) * (p.X - x)) + ((p.Z - z) * (p.Z - z));
            if (d < bd)
            {
                bd = d;
                best = k;
            }
        }

        return best;
    }

    /// <summary>Позиция остановки на её полосе.</summary>
    public static bool TryStopPos(NetworkView v, int stop, out Vec2 pos)
    {
        pos = default;
        if (!v.IsBusStopAlive(stop))
        {
            return false;
        }

        int lane = v.BusStops[stop].Lane;
        if ((uint)lane >= (uint)v.Lanes.Length)
        {
            return false;
        }

        pos = LanePos(v, lane, v.BusStops[stop].S);
        return true;
    }

    /// <summary>Точка на полилинии полосы на расстоянии <paramref name="s"/> от её начала.</summary>
    public static Vec2 LanePos(NetworkView v, int lane, float s)
    {
        LaneData l = v.Lanes[lane];
        ReadOnlySpan<Vec2> pts = v.LanePoints;
        ReadOnlySpan<float> ps = v.LanePointS;
        int first = l.FirstPoint;
        for (int k = 0; k + 1 < l.PointCount; k++)
        {
            float s0 = ps[first + k];
            float s1 = ps[first + k + 1];
            if (s <= s1 || k + 2 == l.PointCount)
            {
                float t = s1 - s0 > 1e-6f ? Math.Clamp((s - s0) / (s1 - s0), 0f, 1f) : 0f;
                Vec2 a = pts[first + k];
                Vec2 b = pts[first + k + 1];
                return new Vec2(a.X + ((b.X - a.X) * t), a.Z + ((b.Z - a.Z) * t));
            }
        }

        return l.PointCount > 0 ? pts[first] : default;
    }

    private static int NearestStop(NetworkView v, float x, float z, out int route)
    {
        route = -1;
        int best = -1;
        float bd = StopPickRadius * StopPickRadius;
        for (int i = 0; i < v.BusStops.Length; i++)
        {
            if (!TryStopPos(v, i, out Vec2 p) || !v.IsBusRouteAlive(v.BusStops[i].Route))
            {
                continue;
            }

            float d = ((p.X - x) * (p.X - x)) + ((p.Z - z) * (p.Z - z));
            if (d < bd)
            {
                bd = d;
                best = i;
                route = v.BusStops[i].Route;
            }
        }

        return best;
    }

    // Ближайшая крайняя правая полоса (LaneNo 0) живого сегмента: сторона дороги выбирается сама - курсор ближе к правой полосе своего направления.
    private static Candidate FindLane(NetworkView v, float x, float z)
    {
        Candidate best = default;
        float bd = PickRadius * PickRadius;
        ReadOnlySpan<Vec2> pts = v.LanePoints;
        ReadOnlySpan<float> ps = v.LanePointS;
        for (int i = 0; i < v.Lanes.Length; i++)
        {
            LaneData l = v.Lanes[i];
            if (l.Kind != LaneKind.Normal || l.LaneNo != 0 || !v.IsSegmentAlive(l.Segment) || (l.Flags & (LaneFlags.Closed | LaneFlags.Draining)) != 0)
            {
                continue;
            }

            for (int k = 0; k + 1 < l.PointCount; k++)
            {
                Vec2 a = pts[l.FirstPoint + k];
                Vec2 b = pts[l.FirstPoint + k + 1];
                float dx = b.X - a.X;
                float dz = b.Z - a.Z;
                float len2 = (dx * dx) + (dz * dz);
                float t = len2 > 1e-6f ? Math.Clamp((((x - a.X) * dx) + ((z - a.Z) * dz)) / len2, 0f, 1f) : 0f;
                float px = a.X + (dx * t);
                float pz = a.Z + (dz * t);
                float d = ((px - x) * (px - x)) + ((pz - z) * (pz - z));
                if (d < bd)
                {
                    bd = d;
                    float s0 = ps[l.FirstPoint + k];
                    float s1 = ps[l.FirstPoint + k + 1];
                    float s = Math.Clamp(s0 + ((s1 - s0) * t), 0f, l.Length);
                    best = new Candidate(true, v.SegmentRef(l.Segment), l.Direction, l.LaneNo, s, px, pz);
                }
            }
        }

        return best;
    }

    private static bool SameCandidate(in Candidate a, in Candidate b) =>
        a.Valid == b.Valid && a.Seg.Index == b.Seg.Index && a.Dir == b.Dir && MathF.Abs(a.S - b.S) < 0.25f;

    /// <summary>Место будущей остановки: сегмент, направление, полоса, S вдоль полосы и точка в мире.</summary>
    public readonly record struct Candidate(bool Valid, EntityRef Seg, byte Dir, byte LaneNo, float S, float X, float Z);

    /// <summary>Первая остановка (индекс в BusStops) линии - для отрисовки и замыкания.</summary>
    public static int FirstStop(NetworkView v, in BusRouteData r) => FirstStopOf(r, v);
}
