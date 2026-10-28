using System;
using System.Globalization;
using Sim.Core.Api;
using Sim.Core.Util;
using Sim.Host;

namespace TrafficCity.Tools;

/// <summary>Режим инструмента «Дороги» (ui-spec §5.1): построить новую, заменить профиль существующей, снести.</summary>
public enum RoadsMode
{
    Build,
    Upgrade,
    Remove,
}

/// <summary>К чему прилип курсор при строительстве (ui-spec §5.1 п. 3, иконка у курсора: узел, сетка).</summary>
public enum PointSnap
{
    Free,
    Grid,
    Node,
}

/// <summary>
/// Инструмент «Дороги» (UI4, ui-spec §5.1): клик-клик строит прямую по профилю каталога цепочкой, «Заменить» меняет профиль сегмента под курсором,
/// «Снос» убирает сегмент (с подтверждением P13, если пропадут участки или остановки). Всё общение с ядром - через <see cref="ToolCommandService"/>
/// (превью на наведение, <c>Submit</c> на клик); мир (сетка, узлы, поиск сегмента) - через <see cref="IRoadsWorld"/>. Чистый C# без Godot.
/// Кривые (Tab) и drag по нескольким сегментам ждут ядра (T5) и не входят.
/// </summary>
public sealed class RoadsTool : ToolBase
{
    /// <summary>Радиус привязки к существующему узлу, м (ui-spec §5.1 п. 3).</summary>
    public const float NodeSnapM = 6f;

    /// <summary>Меньший шаг от якоря не считается дорогой (превью не просим).</summary>
    public const float MinStepM = 1f;

    /// <summary>Радиус поиска сегмента под курсором в режимах «Заменить» и «Снос», м.</summary>
    public const float SegmentPickM = 12f;

    private readonly IRoadsWorld _world;
    private ToolPointer _last;
    private bool _haveLast;

    private Command _cmd;
    private bool _hasCmd;

    public RoadsTool(ToolCommandService service, IRoadsWorld world)
        : base(service)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    /// <summary>Изменились режим, профиль или зеркало: перерисовать строку опций и состояние роутера.</summary>
    public event Action? OptionsChanged;

    /// <summary>Сообщение игроку (текст, ошибка ли): хост показывает тостом.</summary>
    public event Action<string, bool>? Message;

    /// <summary>Строка журнала <c>[tool] ...</c> (хост печатает в консоль).</summary>
    public event Action<string>? Logged;

    /// <summary>Спросить подтверждение (P13): текст, подпись кнопки, действие при согласии. Нет подписчика - действие выполняется сразу.</summary>
    public Action<string, string, Action>? Confirm { get; set; }

    // "bulldoze" в режиме сноса: кнопка «Снос» в тулбаре подсвечивается по Id, а клавиша X и кнопка включают тот же инструмент в режиме Remove
    public override string Id => Mode == RoadsMode.Remove ? "bulldoze" : "roads";

    public override string DisplayName => Mode == RoadsMode.Remove ? "Снос" : "Дороги";

    public RoadsMode Mode { get; private set; }

    public int ProfileIndex { get; private set; }

    public bool Mirror { get; private set; }

    public RoadCatalog Catalog => _world.Catalog;

    public IRoadsWorld World => _world;

    public RoadChoice Choice => _world.Catalog[Math.Clamp(ProfileIndex, 0, _world.Catalog.Count - 1)];

    /// <summary>Растёт при любом изменении того, что рисует призрак (курсор, якорь, команда, опции): вид пересобирает ленту по смене.</summary>
    public int StateVersion { get; private set; }

    public bool HasAnchor { get; private set; }

    public Vec2 Anchor { get; private set; }

    public bool CursorValid { get; private set; }

    public Vec2 Cursor { get; private set; }

    public PointSnap CursorSnap { get; private set; }

    public bool HoverValid { get; private set; }

    public SegmentHit Hover { get; private set; }

    /// <summary>Под курсором дорога уже такого профиля (режим «Заменить»): команды нет.</summary>
    public bool HoverSame { get; private set; }

    /// <summary>Есть команда, к которой запрошено превью.</summary>
    public bool HasCommand => _hasCmd;

    public ref readonly Command WantedCommand => ref _cmd;

    /// <summary>Зеркало реально применяется: включено и профиль несимметричный.</summary>
    public bool MirrorActive => Mirror && Choice.Asymmetric && Choice.Profile >= 0;

    public void SetMode(RoadsMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        OnCancel();
        Service.ClearPreview();
        Mode = mode;
        _hasCmd = false;
        HoverValid = false;
        Changed();
        Replay();
    }

    public void SetProfile(int index)
    {
        int n = _world.Catalog.Count;
        int clamped = n == 0 ? 0 : ((index % n) + n) % n;
        if (clamped == ProfileIndex)
        {
            return;
        }

        ProfileIndex = clamped;
        Changed();
        Replay();
    }

    /// <summary>Листать профили по кругу (клавиши <c>[</c> и <c>]</c>).</summary>
    public void CycleProfile(int delta) => SetProfile(ProfileIndex + delta);

    public void ToggleMirror()
    {
        Mirror = !Mirror;
        Changed();
        Replay();
    }

    public override void OnEnter()
    {
        Changed();
        Replay();
    }

    public override void OnHover(in ToolPointer at)
    {
        _last = at;
        _haveLast = true;
        Recompute();
    }

    public override void OnPress(in ToolPointer at)
    {
        // клик решается на отпускании (как у кнопок): нажатие без отпускания ничего не делает
    }

    public override void OnRelease(in ToolPointer at)
    {
        _last = at;
        _haveLast = true;
        if (at.Button == ToolButton.Secondary)
        {
            if (HasAnchor)
            {
                OnCancel();
                Replay();
            }

            return;
        }

        Recompute();
        switch (Mode)
        {
            case RoadsMode.Build:
                ClickBuild();
                break;
            case RoadsMode.Upgrade:
                ClickUpgrade();
                break;
            default:
                ClickRemove();
                break;
        }
    }

    public override void OnCancel()
    {
        base.OnCancel();
        if (HasAnchor)
        {
            HasAnchor = false;
            _hasCmd = false;
            Service.ClearPreview();
            Changed(options: false);
        }
    }

    /// <summary>Esc из роутера: первая отмена сбрасывает цепочку (true), вторая выходит из инструмента.</summary>
    public override bool CancelGesture()
    {
        bool was = HasAnchor;
        OnCancel();
        if (was)
        {
            Replay();
        }

        return was;
    }

    public override void OnExit()
    {
        base.OnExit();
        _hasCmd = false;
        HoverValid = false;
        CursorValid = false;
        _haveLast = false;
        Changed(options: false);
    }

    private void Replay()
    {
        if (_haveLast)
        {
            Recompute();
        }
    }

    private void Changed(bool options = true)
    {
        StateVersion++;
        if (options)
        {
            OptionsChanged?.Invoke();
        }
    }

    private void Recompute()
    {
        StateVersion++;
        if (!_last.OnGround)
        {
            CursorValid = false;
            HoverValid = false;
            _hasCmd = false;
            Service.ClearPreview();
            return;
        }

        if (Mode == RoadsMode.Build)
        {
            HoverValid = false;
            ResolvePoint(in _last, out Vec2 p, out PointSnap snap);
            Cursor = p;
            CursorSnap = snap;
            CursorValid = true;
            if (HasAnchor && Dist(Anchor, p) >= MinStepM)
            {
                _cmd = BuildCommand(Anchor, p);
                _hasCmd = true;
                Service.SetPreview(in _cmd);
            }
            else
            {
                _hasCmd = false;
                Service.ClearPreview();
            }

            return;
        }

        CursorValid = true;
        Cursor = new Vec2(_last.X, _last.Z);
        if (!_world.TrySegment(_last.X, _last.Z, SegmentPickM, out SegmentHit hit))
        {
            HoverValid = false;
            _hasCmd = false;
            Service.ClearPreview();
            return;
        }

        HoverValid = true;
        Hover = hit;
        HoverSame = false;
        if (Mode == RoadsMode.Upgrade)
        {
            RoadChoice c = Choice;
            HoverSame = c.Profile >= 0 && hit.Profile == c.Profile && (!c.Asymmetric || hit.Mirrored == MirrorActive);
            if (HoverSame)
            {
                _hasCmd = false;
                Service.ClearPreview();
                return;
            }

            _cmd = UpgradeCommand(in hit);
        }
        else
        {
            var rm = new RemoveRoadCmd { Seg = hit.Ref, Flags = 0 };
            _cmd = Command.MakeRemoveRoad(in rm);
        }

        _hasCmd = true;
        Service.SetPreview(in _cmd);
    }

    private void ResolvePoint(in ToolPointer at, out Vec2 p, out PointSnap snap)
    {
        var raw = new Vec2(at.X, at.Z);
        if (_world.TryNode(at.X, at.Z, NodeSnapM, out Vec2 node))
        {
            p = node;
            snap = PointSnap.Node;
        }
        else if (_world.SnapOn && (at.Mods & ToolMods.Shift) == 0)
        {
            p = _world.SnapToGrid(raw);
            snap = PointSnap.Grid;
        }
        else
        {
            p = raw;
            snap = PointSnap.Free;
        }
    }

    private Command BuildCommand(Vec2 a, Vec2 b)
    {
        RoadChoice c = Choice;
        bool mir = MirrorActive;
        var p = new BuildRoadCmd
        {
            Ax = a.X,
            Az = a.Z,
            Bx = b.X,
            Bz = b.Z,
            SpeedLimit = c.SpeedMps,
            LanesAB = mir ? c.LanesBwd : c.LanesFwd,
            LanesBA = mir ? c.LanesFwd : c.LanesBwd,
            Kind = c.Kind,
            Flags = mir ? BuildRoadFlags.Mirrored : (byte)0,
            Profile = c.CommandProfile,
        };
        return Command.MakeBuildRoad(in p);
    }

    private Command UpgradeCommand(in SegmentHit hit)
    {
        RoadChoice c = Choice;
        bool mir = MirrorActive;
        var p = new UpgradeRoadCmd
        {
            Seg = hit.Ref,
            SpeedLimit = 0f,
            LanesAB = mir ? c.LanesBwd : c.LanesFwd,
            LanesBA = mir ? c.LanesFwd : c.LanesBwd,
            Kind = c.Kind,
            Flags = mir ? UpgradeRoadFlags.Mirrored : (byte)0,
            Profile = c.CommandProfile,
        };
        return Command.MakeUpgradeRoad(in p);
    }

    // Превью по этой же команде пришло - слушаемся вердикта; ответа ещё нет - клик разрешён (валидатор применения тот же, ui-spec §6.1)
    private bool Gate(out RejectReason reason)
    {
        reason = RejectReason.None;
        if (!_hasCmd)
        {
            return false;
        }

        if (Service.PreviewMatches(in _cmd))
        {
            reason = Service.Preview.Verdict;
            return reason == RejectReason.None;
        }

        return true;
    }

    private void ClickBuild()
    {
        if (!CursorValid)
        {
            return;
        }

        if (!HasAnchor)
        {
            Anchor = Cursor;
            HasAnchor = true;
            Dragging = true;
            Log(string.Create(CultureInfo.InvariantCulture, $"[tool] anchor ({Anchor.X:0.##}, {Anchor.Z:0.##}) snap={CursorSnap} profile={Choice.Name}"));
            Changed(options: false);
            Recompute();
            return;
        }

        if (!_hasCmd)
        {
            return;
        }

        if (!Gate(out RejectReason reason))
        {
            Reject(reason);
            return;
        }

        Vec2 end = Cursor;
        if (Submit("build", in _cmd))
        {
            Anchor = end; // цепочка: конец становится началом следующей
            Changed(options: false);
            Recompute();
        }
    }

    private void ClickUpgrade()
    {
        if (!HoverValid)
        {
            return;
        }

        if (HoverSame)
        {
            Message?.Invoke("Здесь уже такая дорога", false);
            return;
        }

        if (!Gate(out RejectReason reason))
        {
            Reject(reason);
            return;
        }

        Submit("upgrade", in _cmd);
    }

    private void ClickRemove()
    {
        if (!HoverValid || !_hasCmd)
        {
            return;
        }

        if (!Gate(out RejectReason reason))
        {
            Reject(reason);
            return;
        }

        if (RemoveLosses(out string text) && Confirm != null)
        {
            Command cmd = _cmd;
            Confirm(text, "Снести", () => Submit("remove", in cmd));
            return;
        }

        Submit("remove", in _cmd);
    }

    /// <summary>Снос потеряет участки под застройку, зоны без проезда или остановки: нужно подтверждение P13 (ui-spec §5.1, снос п. 4).</summary>
    private bool RemoveLosses(out string text)
    {
        text = string.Empty;
        if (!Service.PreviewMatches(in _cmd))
        {
            return false;
        }

        PreviewReply r = Service.Preview;
        int lots = 0;
        bool access = r.Result.ZonesLosingAccess > 0;
        bool stops = r.Result.BusStopsLost > 0;
        foreach (PreviewIssue i in r.IssueSpan)
        {
            if (i.Code == IssueCode.ZoneLotsLost)
            {
                lots += i.Value;
            }
            else if (i.Code == IssueCode.ZoneAccessLost)
            {
                access = true;
            }
            else if (i.Code == IssueCode.StopAccessLost)
            {
                stops = true;
            }
        }

        if (lots == 0 && !access && !stops)
        {
            return false;
        }

        var sb = new System.Text.StringBuilder("Снести дорогу?");
        if (lots > 0)
        {
            sb.Append('\n').Append(RejectMessages.Issue(IssueCode.ZoneLotsLost, lots));
        }

        if (r.Result.ZonesLosingAccess > 0)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"\nЗон останется без проезда: {r.Result.ZonesLosingAccess}"));
        }
        else if (access)
        {
            sb.Append('\n').Append(RejectMessages.Issue(IssueCode.ZoneAccessLost, 0));
        }

        if (r.Result.BusStopsLost > 0)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"\nОстановок потеряется: {r.Result.BusStopsLost}"));
        }
        else if (stops)
        {
            sb.Append('\n').Append(RejectMessages.Issue(IssueCode.StopAccessLost, 0));
        }

        text = sb.ToString();
        return true;
    }

    private bool Submit(string what, in Command cmd)
    {
        uint id = Service.Submit(in cmd);
        if (id == 0)
        {
            Message?.Invoke("Очередь команд полна, повторите", true);
            Log("[tool] submit " + what + " refused: queue full");
            return false;
        }

        string verdict = Service.PreviewMatches(in cmd) ? Service.Preview.Verdict.ToString() : "pending";
        string cost = Service.PreviewMatches(in cmd) ? Service.Preview.Result.Cost.ToString(CultureInfo.InvariantCulture) : "?";
        Log(string.Create(CultureInfo.InvariantCulture, $"[tool] submit #{id} {what} preview_verdict={verdict} cost={cost}"));
        return true;
    }

    private void Reject(RejectReason reason)
    {
        if (reason == RejectReason.None || RejectMessages.IsSilent(reason))
        {
            return;
        }

        string text = RejectMessages.Full(reason);
        Log("[tool] click blocked: " + reason + " - " + text);
        Message?.Invoke(text, true);
    }

    private void Log(string s) => Logged?.Invoke(s);

    private static float Dist(Vec2 a, Vec2 b)
    {
        float dx = a.X - b.X;
        float dz = a.Z - b.Z;
        return MathF.Sqrt((dx * dx) + (dz * dz));
    }
}
