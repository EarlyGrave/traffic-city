using System;
using System.Globalization;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Host;

namespace TrafficCity.Tools;

/// <summary>Режим инструмента «Узлы» (ui-spec §5.4): управление узлом и план светофора, знаки рукавов, запреты манёвров, флаги полос.</summary>
public enum NodesMode
{
    Control,
    Arms,
    Bans,
    Lanes,
}

/// <summary>Режим управления узлом (ui-spec §5.4 п. 2).</summary>
public enum NodeControlChoice
{
    /// <summary>Без светофора и знаков (<see cref="NodeControl.None"/>).</summary>
    Uncontrolled,

    /// <summary>Главная / второстепенная (<see cref="NodeControl.Yield"/>).</summary>
    Priority,

    /// <summary>Светофор, фиксированный цикл.</summary>
    SignalFixed,

    /// <summary>Светофор, адаптивный (по спросу, со стрелкой).</summary>
    SignalAdaptive,
}

/// <summary>Пресет плана светофора (MVP, как прежняя <c>NodePanel</c>).</summary>
public enum SignalPresetChoice
{
    Adaptive,
    Fixed,
    Flash,
}

/// <summary>Мир инструмента «Узлы»: копия сети последнего кадра (главный поток). В тестах - вью ядра до старта хоста.</summary>
public interface INodesWorld
{
    bool TryGetNet(out NetworkView net);
}

/// <summary>
/// Инструмент «Узлы» (UI10, ui-spec §5.4): клик по узлу выбирает его; кнопки строки опций ставят режим управления (<c>SetNodeControl</c> + план),
/// пресет и длительность цикла (<c>SetSignalPlan</c>; протяжка ползунка - один пункт undo); в режиме «Рукава» клик по иконке рукава циклически меняет
/// знак (<c>SetArmControl</c>: авто → главная → уступи → стоп), в «Запретах» клик по стрелке выключает/включает манёвр (<c>SetTurnBan</c>, превью на наведение
/// показывает перестройку узла), в «Полосах» клик по полосе переключает <c>BusOnly</c>, Shift+клик - <c>NoLaneChangeOut</c> (<c>SetLaneFlags</c>).
/// Всё - через <see cref="ToolCommandService"/>. Чистый C# без Godot, без аллокаций на наведение.
/// </summary>
public sealed class NodesTool : ToolBase
{
    public const float NodePickM = 8f;
    public const float ArmPickM = 5f;
    public const float ArrowPickM = 2f;
    public const float LanePickM = 2.5f;

    public const int MinCycleS = 30;
    public const int MaxCycleS = 120;
    public const int DefaultCycleS = 48;

    private const ushort YellowTicks = 60;
    private const ushort AllRedTicks = 20;
    private const int TicksPerSecond = 20;

    private readonly INodesWorld _world;
    private readonly NodeLayout _layout = new();
    private readonly byte[] _armCtl = new byte[NodeLayout.MaxArms];
    private readonly Command[] _group = new Command[2];
    private uint _cycleActionId;
    private int _layoutVersion = -1;
    private bool _hasPreview;

    public NodesTool(ToolCommandService service, INodesWorld world)
        : base(service)
    {
        _world = world;
    }

    /// <summary>Ошибка или подсказка для тоста.</summary>
    public event Action<string, bool>? Message;

    public event Action<string>? Logged;

    /// <summary>Сменились режим, выбор или опции (строка опций перерисуется).</summary>
    public event Action? OptionsChanged;

    public override string Id => "nodes";

    public override string DisplayName => "Узлы";

    public NodesMode Mode { get; private set; } = NodesMode.Control;

    /// <summary>Раскладка выбранного узла (рукава в слотах ядра). <see cref="NodeLayout.Node"/> &lt; 0 - узел не выбран.</summary>
    public NodeLayout Layout => _layout;

    public bool HasSelection => _layout.Node >= 0;

    /// <summary>Последний выбранный игроком пресет (ядро план узла клиенту не отдаёт; при смене узла - адаптивный).</summary>
    public SignalPresetChoice Preset { get; private set; } = SignalPresetChoice.Adaptive;

    public int CycleS { get; private set; } = DefaultCycleS;

    /// <summary>Знак рукава, известный клиенту (0 авто, 1 главная, 2 уступи, 3 стоп): ставится этим инструментом, ядро его не отдаёт.</summary>
    public byte ArmControl(int arm) => _armCtl[arm];

    /// <summary>Наведённая цель: иконка рукава (-1 - нет).</summary>
    public int HoverArm { get; private set; } = -1;

    public int HoverFrom { get; private set; } = -1;

    public int HoverTo { get; private set; } = -1;

    public bool HoverLaneValid { get; private set; }

    public LaneHit HoverLane { get; private set; }

    /// <summary>Узел под курсором (кольцо наведения), -1 - нет.</summary>
    public int HoverNode { get; private set; } = -1;

    public void SetMode(NodesMode mode)
    {
        if (Mode == mode)
        {
            return;
        }

        Mode = mode;
        ClearHover();
        OptionsChanged?.Invoke();
    }

    /// <summary>Выбрать узел (клик или «Править» из инспектора).</summary>
    public bool Select(int node)
    {
        if (!_world.TryGetNet(out NetworkView net))
        {
            return false;
        }

        int was = _layout.Node;
        if (!_layout.Build(net, node))
        {
            _layout.Clear();
            OptionsChanged?.Invoke();
            return false;
        }

        _layoutVersion = net.Version;
        if (was != node)
        {
            Array.Clear(_armCtl);
            Preset = SignalPresetChoice.Adaptive;
            CycleS = DefaultCycleS;
            _cycleActionId = 0;
        }

        Logged?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[nodes] select node #{node} arms {_layout.Count} control {_layout.Control} ban 0x{_layout.TurnBan:X}"));
        OptionsChanged?.Invoke();
        return true;
    }

    public void Deselect()
    {
        _layout.Clear();
        ClearHover();
        OptionsChanged?.Invoke();
    }

    /// <summary>Раз в кадр: сеть сменилась - пересобрать раскладку (запреты, режим); узел исчез - снять выбор.</summary>
    public void Tick()
    {
        if (_layout.Node < 0 || !_world.TryGetNet(out NetworkView net) || net.Version == _layoutVersion)
        {
            return;
        }

        int node = _layout.Node;
        int gen = _layout.Generation;
        if (!net.IsNodeAlive(node) || net.Nodes[node].Generation != gen || !_layout.Build(net, node))
        {
            _layout.Clear();
            Message?.Invoke("Узел исчез", false);
        }

        _layoutVersion = net.Version;
        OptionsChanged?.Invoke();
    }

    /// <summary>Режим управления узлом (ui-spec §5.4 п. 2): одно действие игрока, светофор - вместе с планом.</summary>
    public uint ApplyControl(NodeControlChoice choice)
    {
        if (!HasSelection)
        {
            return 0;
        }

        EntityRef r = _layout.Ref;
        NodeControl ctl = choice switch
        {
            NodeControlChoice.Uncontrolled => NodeControl.None,
            NodeControlChoice.Priority => NodeControl.Yield,
            _ => NodeControl.Signal,
        };
        _group[0] = Command.MakeSetNodeControl(new SetNodeControlCmd { Node = r.Index, Control = ctl, NodeGen = r.Generation });
        int n = 1;
        if (ctl == NodeControl.Signal)
        {
            Preset = choice == NodeControlChoice.SignalAdaptive ? SignalPresetChoice.Adaptive : SignalPresetChoice.Fixed;
            _group[1] = MakePlan(r, Preset, CycleS);
            n = 2;
        }

        uint id = Service.Submit(new ReadOnlySpan<Command>(_group, 0, n));
        _cycleActionId = 0;
        Logged?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[nodes] node #{r.Index} control {choice} -> action #{id}"));
        OptionsChanged?.Invoke();
        return id;
    }

    /// <summary>Пресет плана светофора (только у узла со светофором).</summary>
    public uint ApplyPreset(SignalPresetChoice preset)
    {
        if (!HasSelection || _layout.Control != NodeControl.Signal)
        {
            return 0;
        }

        Preset = preset;
        _cycleActionId = 0;
        Command c = MakePlan(_layout.Ref, preset, CycleS);
        uint id = Service.Submit(in c);
        Logged?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[nodes] node #{_layout.Node} preset {preset} -> action #{id}"));
        OptionsChanged?.Invoke();
        return id;
    }

    /// <summary>
    /// Длительность цикла фиксированного плана, с. <paramref name="continuing"/> - продолжение протяжки ползунка: обратная дописывается в тот же пункт undo
    /// (<c>TrySubmitAction(..., mergeIntoActionId)</c>), отмена вернёт план до начала протяжки.
    /// </summary>
    public uint SetCycle(int seconds, bool continuing)
    {
        seconds = Math.Clamp(seconds, MinCycleS, MaxCycleS);
        if (!HasSelection || _layout.Control != NodeControl.Signal || Preset == SignalPresetChoice.Adaptive || seconds == CycleS)
        {
            CycleS = seconds;
            return 0;
        }

        CycleS = seconds;
        Command c = MakePlan(_layout.Ref, Preset, seconds);
        uint id = continuing && _cycleActionId != 0 ? Service.Submit(in c, _cycleActionId) : Service.Submit(in c);
        if (id != 0)
        {
            _cycleActionId = id;
        }

        return id;
    }

    /// <summary>Конец протяжки ползунка: следующий сдвиг - новый пункт undo.</summary>
    public void EndCycleDrag() => _cycleActionId = 0;

    /// <summary>Знак рукава по кругу: авто → главная → уступи → стоп → авто (ui-spec §5.4 п. 4).</summary>
    public uint CycleArm(int arm)
    {
        if (!HasSelection || (uint)arm >= (uint)_layout.Count)
        {
            return 0;
        }

        var next = (byte)((_armCtl[arm] + 1) & 3);
        var c = Command.MakeSetArmControl(new SetArmControlCmd { Node = _layout.Ref, ArmSlot = (byte)arm, Control = next });
        uint id = Service.Submit(in c);
        if (id != 0)
        {
            _armCtl[arm] = next;
        }

        Logged?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[nodes] node #{_layout.Node} arm {arm} -> {next}, action #{id}"));
        OptionsChanged?.Invoke();
        return id;
    }

    /// <summary>Запрет манёвра рукав → рукав: переключить (ui-spec §5.4 п. 5).</summary>
    public uint ToggleBan(int from, int to)
    {
        if (!HasSelection || from == to || (uint)from >= (uint)_layout.Count || (uint)to >= (uint)_layout.Count)
        {
            return 0;
        }

        Command c = BanCommand(from, to);
        uint id = Service.Submit(in c);
        Logged?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[nodes] node #{_layout.Node} ban {from}->{to} = {c.SetTurnBan.Banned}, action #{id}"));
        return id;
    }

    /// <summary>Флаг полосы: <paramref name="mask"/> = <see cref="LaneFlags.BusOnly"/> или <see cref="LaneFlags.NoLaneChangeOut"/>, значение - обратное текущему.</summary>
    public uint ToggleLaneFlag(in LaneHit lane, byte mask)
    {
        var c = Command.MakeSetLaneFlags(new SetLaneFlagsCmd
        {
            Seg = lane.Seg,
            Dir = lane.Dir,
            LaneNo = lane.LaneNo,
            Mask = mask,
            Flags = (byte)((lane.Flags & mask) != 0 ? 0 : mask),
        });
        uint id = Service.Submit(in c);
        Logged?.Invoke(string.Create(CultureInfo.InvariantCulture, $"[nodes] seg #{lane.Seg.Index} dir {lane.Dir} lane {lane.LaneNo} flag {mask} -> {c.SetLaneFlags.Flags}, action #{id}"));
        return id;
    }

    /// <summary>Команда плана для пресета: адаптивный - со стрелкой по спросу, фиксированный - зелёный по половине цикла, мигающий жёлтый.</summary>
    public static Command MakePlan(EntityRef node, SignalPresetChoice preset, int cycleS)
    {
        var p = new SetSignalPlanCmd { Node = node.Index, NodeGen = node.Generation, PhaseCount = 2, YellowTicks = YellowTicks, AllRedTicks = AllRedTicks };
        int green = Math.Max(100, ((cycleS * TicksPerSecond) / 2) - YellowTicks - AllRedTicks);
        switch (preset)
        {
            case SignalPresetChoice.Adaptive:
                p.Mode = SignalMode.Adaptive;
                p.Flags = SignalPlanFlags.Arrows;
                break;
            case SignalPresetChoice.Fixed:
                p.Mode = SignalMode.Fixed;
                p.Green0 = (ushort)green;
                p.Green1 = (ushort)green;
                break;
            default:
                p.Mode = SignalMode.FlashYellow;
                p.Green0 = (ushort)green;
                p.Green1 = (ushort)green;
                break;
        }

        return Command.MakeSetSignalPlan(in p);
    }

    public override void OnHover(in ToolPointer at)
    {
        int arm = -1;
        int from = -1;
        int to = -1;
        bool laneOk = false;
        LaneHit lane = default;
        int node = -1;
        if (at.OnGround && _world.TryGetNet(out NetworkView net))
        {
            switch (Mode)
            {
                case NodesMode.Arms when HasSelection:
                    arm = _layout.PickArm(at.X, at.Z, ArmPickM);
                    break;
                case NodesMode.Bans when HasSelection:
                    _layout.PickArrow(at.X, at.Z, ArrowPickM, out from, out to);
                    break;
                case NodesMode.Lanes:
                    laneOk = NodeLayout.PickLane(net, at.X, at.Z, LanePickM, out lane);
                    break;
                default:
                    break;
            }

            if (arm < 0 && from < 0 && !laneOk && Mode != NodesMode.Lanes)
            {
                node = NodeLayout.PickNode(net, at.X, at.Z, NodePickM);
            }
        }

        HoverArm = arm;
        HoverFrom = from;
        HoverTo = to;
        HoverLaneValid = laneOk;
        HoverLane = lane;
        HoverNode = node;

        // превью только для запрета: перестройка узла даёт предупреждения (ui-spec §5.4 п. 7)
        if (from >= 0)
        {
            Command c = BanCommand(from, to);
            Service.SetPreview(in c);
            _hasPreview = true;
        }
        else if (_hasPreview)
        {
            Service.ClearPreview();
            _hasPreview = false;
        }
    }

    public override void OnRelease(in ToolPointer at)
    {
        base.OnRelease(in at);
        if (at.Button == ToolButton.Secondary)
        {
            if (HasSelection)
            {
                Deselect();
            }

            return;
        }

        OnHover(in at);
        if (HoverArm >= 0)
        {
            CycleArm(HoverArm);
        }
        else if (HoverFrom >= 0)
        {
            ToggleBan(HoverFrom, HoverTo);
        }
        else if (HoverLaneValid)
        {
            LaneHit l = HoverLane;
            ToggleLaneFlag(in l, (at.Mods & ToolMods.Shift) != 0 ? LaneFlags.NoLaneChangeOut : LaneFlags.BusOnly);
        }
        else if (HoverNode >= 0)
        {
            Select(HoverNode);
        }
    }

    public override void OnCancel()
    {
        base.OnCancel();
        EndCycleDrag();
    }

    public override bool CancelGesture()
    {
        if (HasSelection)
        {
            Deselect();
            return true;
        }

        return base.CancelGesture();
    }

    public override void OnExit()
    {
        base.OnExit();
        _hasPreview = false;
        ClearHover();
    }

    private Command BanCommand(int from, int to) => Command.MakeSetTurnBan(new SetTurnBanCmd
    {
        Node = _layout.Ref,
        FromArm = (byte)from,
        ToArm = (byte)to,
        Banned = (byte)(_layout.Banned(from, to) ? 0 : 1),
    });

    private void ClearHover()
    {
        HoverArm = -1;
        HoverFrom = -1;
        HoverTo = -1;
        HoverLaneValid = false;
        HoverNode = -1;
    }
}
