using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Headless.Scenarios;
using Sim.Scenarios;

namespace Sim.Headless.Bench;

/// <summary>
/// <c>scratch-bot --scenario yuryev-polsky [--days 6] [--seeds default,11,12] [--threads N] [--every-hours K] [--summary] [--scenarios-dir d]</c>
/// (new-game-scratch.md §5, Y5): детерминированный бот «разумного игрока» для калибровки старта с нуля. Раз в игровой час через публичный API
/// (<c>Enqueue</c>) достраивает улиц-«гребёнку» от магистрали у центра (пролёты по 120 м, 4 пролёта = 480 м на улицу, по одной улице от узла магистрали), красит свободные участки R, затем C/I по спросу и
/// печатает строку часа: население, въехало, внешние/местные работы, казна, спрос, стройки, застрявшие. В конце каждого прогона - сводка с минутами игры
/// (сек симуляции / 60 = минуты на ×1) для первых жителей и вех. Ядро не меняет; ничего не пишет на диск.
/// </summary>
public static class ScratchBotCommand
{
    public const string Usage =
        "Usage: Sim.Headless scratch-bot [--scenario <name|path>] [--days N] [--seeds default,11,12] [--threads N] [--every-hours K] [--summary] [--scenarios-dir <dir>]\n" +
        "  deterministic 'sensible player' bot for the from-scratch start: builds streets off the arterial near the centre, paints R then C/I as demand allows,\n" +
        "  prints a line per game hour (population, arrivals, outside/local jobs, treasury, demand, stuck) and a summary per seed (default scenario yuryev-polsky, 6 days)\n";

    private static readonly string[] _allowed = { "--scenario", "--days", "--seeds", "--threads", "--every-hours", "--scenarios-dir", "--packages-dir" };
    private static readonly string[] _flags = { "--summary" };

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (!CliArgs.TryParse(args, _allowed, _flags, out CliArgs a)
            || !a.TryGetInt("--days", 1, out int days, 6)
            || !a.TryGetInt("--threads", 1, out int threads, 1)
            || !a.TryGetInt("--every-hours", 1, out int every, 1))
        {
            error.WriteLine($"scratch-bot: {a.Error}");
            error.Write(Usage);
            return RunCommand.ExitUsage;
        }

        string scenario = a.Get("--scenario") ?? "yuryev-polsky";
        string[] seeds = (a.Get("--seeds") ?? "default").Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool summaryOnly = a.Has("--summary");
        int rc = RunCommand.ExitOk;
        foreach (string s in seeds)
        {
            LoadedScenario sc;
            try
            {
                sc = PackageDirs.Load(ScenarioLocator.Resolve(scenario, a.Get("--scenarios-dir")), a);
                if (s != "default")
                {
                    if (!ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out ulong seed))
                    {
                        error.WriteLine($"scratch-bot: bad seed '{s}'");
                        return RunCommand.ExitUsage;
                    }

                    ScenarioDesc d = sc.Desc;
                    d.Seed = seed;
                    sc = new LoadedScenario(sc.Name, sc.TypeNames, d, sc.Catalog, sc.Packages, sc.Timeline);
                }
            }
            catch (ScenarioException e)
            {
                error.WriteLine($"error: {e.Message}");
                return RunCommand.ExitUsage;
            }

            var bot = new ScratchBot(sc.Desc, threads, output, summaryOnly ? int.MaxValue : every);
            if (!bot.Run(days, sc.Name))
            {
                error.WriteLine("scratch-bot: create failed");
                rc = RunCommand.ExitUsage;
            }
        }

        return rc;
    }
}

/// <summary>Бот одного прогона. Всё состояние - в полях; решения принимаются только по публичному API ядра.</summary>
internal sealed class ScratchBot
{
    private const int Q = 120;                  // пролёт улицы, м
    private const int Depth = 4;                // клеток вглубь от магистрали
    private const float StreetKmh = 40f;
    private const bool UseMidpoints = false;   // улицы и от середин пролётов магистрали: больше участков, но T-перекрёстки через ~80 м и очереди (stuck) - см. отчёт L89
    private const long Reserve = 10000;          // казну ниже этой суммы после стройки не оставляем
    private const int FreeLotsLow = 8;         // строим новый блок, когда свободных (не зонированных) участков меньше

    private readonly ScenarioDesc _d;
    private readonly int _threads;
    private readonly TextWriter _out;
    private readonly int _every;
    private Simulation _sim = null!;
    private readonly List<(int Node, int Side, int Pass)> _order = new();   // очередь улиц: узел магистрали, сторона (+1 к центру карты, -1 от центра), проход (0 - первые 4 пролёта, 1 - продолжение)
    private readonly List<bool> _used = new();
    private readonly List<int> _taken = new();
    private uint _tag = 1000;
    private LotRenderData[] _lots = new LotRenderData[4096];
    private readonly Dictionary<RejectReason, int> _rejects = new();

    // сводка
    private long _arrivals;
    private long _firstPopTick = -1, _firstArrivalTick = -1, _firstBuildingTick = -1, _firstActiveTick = -1;
    private readonly long[] _popDayEnd = new long[64];
    private long _minBalance = long.MaxValue;
    private int _maxStuck;
    private int _stuckDumps;
    private readonly bool _stuckDump = Environment.GetEnvironmentVariable("SCRATCH_STUCK_DUMP") == "1";
    private int _segmentsBuilt;
    private long _spent;
    private readonly List<(int Index, long Tick, int Pop)> _milestones = new();

    public ScratchBot(in ScenarioDesc d, int threads, TextWriter output, int every)
    {
        _d = d;
        _threads = threads;
        _out = output;
        _every = every;
    }

    private static string Fmt(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    public bool Run(int days, string name)
    {
        ScenarioDesc d = _d;
        CreateStatus st = Simulation.TryCreate(in d, new SimRuntimeOptions { WorkerThreads = _threads }, out Simulation? sim);
        if (st != CreateStatus.Ok || sim == null)
        {
            return false;
        }

        using (sim)
        {
            _sim = sim;
            PlanAnchors();
            long start = sim.Tick;
            int dayTicks = sim.CityDayTicks;
            int hourTicks = dayTicks / 24;
            long end = ((start / dayTicks) + days) * dayTicks;   // конец суток days (считая сутки старта первыми)
            _out.WriteLine(Fmt($"scratch-bot {name} seed={_d.Seed} start_tick={start} day_ticks={dayTicks} days={days}"));
            _out.WriteLine("day hh | pop arrv(cum) transit | out taken/total  loc_vac emp | treasury  dR dC dI | bR bC bI cons | segs lots(free/R/C/I) | stuck rej");
            int hour = 0;
            while (sim.Tick < end)
            {
                Act();
                long hourEnd = ((sim.Tick / hourTicks) + 1) * hourTicks;
                int rest = (int)(hourEnd - sim.Tick);
                if (rest > 0)
                {
                    sim.Step(rest);
                    Collect();
                }

                hour++;
                Sample(sim.Tick - start, hour);
                if (hour % _every == 0)
                {
                    PrintLine();
                }
            }

            PrintSummary(start);
        }

        return true;
    }

    private void PlanAnchors()
    {
        // узлы магистрали в пределах ±1700 м; город растёт вдоль магистрали от точки, ближайшей к центру карты (внутри ±900), ближние к ней первыми;
        // сначала сторона к центру, потом обратная; второй проход продолжает те же улицы дальше
        NodeDesc[] nodes = _d.Network.Nodes;
        var cand = new List<int>();
        int a0i = -1;
        double best = double.MaxValue;
        for (int i = 1; i < nodes.Length - 1; i++)
        {
            double r2 = ((double)nodes[i].X * nodes[i].X) + ((double)nodes[i].Z * nodes[i].Z);
            if (Math.Abs(nodes[i].X) <= 1700f && Math.Abs(nodes[i].Z) <= 1700f)
            {
                cand.Add(i);
            }

            if (Math.Abs(nodes[i].X) <= 900f && Math.Abs(nodes[i].Z) <= 900f && r2 < best)
            {
                best = r2;
                a0i = i;
            }
        }

        // кандидаты: узлы магистрали (код 2i) и середины пролётов между соседними узлами (код 2i+1, улица от середины - с AutoSplit)
        var codes = new List<int>();
        foreach (int i in cand)
        {
            codes.Add(i * 2);
            if (cand.Contains(i + 1) && UseMidpoints)
            {
                codes.Add((i * 2) + 1);
            }
        }

        cand = codes;
        NodeDesc a0 = nodes[a0i];
        cand.Sort((x, y) => D2(Anchor(x), a0).CompareTo(D2(Anchor(y), a0)));
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (int side in new[] { 1, -1 })
            {
                foreach (int code in cand)
                {
                    _order.Add((code, side, pass));
                    _used.Add(false);
                }
            }
        }

        _center = a0i;
    }

    private int _center;

    private NodeDesc Anchor(int code)
    {
        NodeDesc[] nodes = _d.Network.Nodes;
        int i = code >> 1;
        if ((code & 1) == 0)
        {
            return nodes[i];
        }

        return new NodeDesc { X = (nodes[i].X + nodes[i + 1].X) / 2f, Z = (nodes[i].Z + nodes[i + 1].Z) / 2f };
    }

    private static double D2(NodeDesc a, NodeDesc b) => (((double)a.X - b.X) * ((double)a.X - b.X)) + (((double)a.Z - b.Z) * ((double)a.Z - b.Z));

    /// <summary>Единичный вектор перпендикуляра к магистрали у якоря <paramref name="code"/>, направленный к центру карты.</summary>
    private (float Px, float Pz) Perp(int code)
    {
        NodeDesc[] nodes = _d.Network.Nodes;
        int k = code >> 1;
        NodeDesc a;
        NodeDesc b;
        if ((code & 1) == 0)
        {
            a = nodes[Math.Max(0, k - 1)];
            b = nodes[Math.Min(nodes.Length - 1, k + 1)];
        }
        else
        {
            a = nodes[k];
            b = nodes[k + 1];
        }

        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        float len = MathF.Sqrt((dx * dx) + (dz * dz));
        dx /= len;
        dz /= len;
        float px = -dz;
        float pz = dx;
        NodeDesc at = Anchor(code);
        if ((px * -at.X) + (pz * -at.Z) < 0f)
        {
            px = -px;
            pz = -pz;
        }

        return (px, pz);
    }

    private (float X, float Z) P((int Node, int Side, int Pass) a, int j)
    {
        NodeDesc n = Anchor(a.Node);
        (float px, float pz) = Perp(a.Node);
        return (n.X + (px * Q * j * a.Side), n.Z + (pz * Q * j * a.Side));
    }

    private static void Segment(List<(float, float, float, float)> list, (float X, float Z) a, (float X, float Z) b) => list.Add((a.X, a.Z, b.X, b.Z));

    private AreaRect _box = new() { MinX = -1e9f, MinZ = -1e9f, MaxX = 1e9f, MaxZ = 1e9f };

    /// <summary>Текущая открытая область карты (по вехам); без прогрессии - вся карта.</summary>
    private void RefreshBox()
    {
        AreaRect[]? areas = _d.Progression.Areas;
        if (areas != null && areas.Length > 0 && _sim.GetProgression(out ProgressionInfo pi))
        {
            _box = areas[Math.Min(Math.Max(0, pi.AreaStage), areas.Length - 1)];
        }
    }

    private bool InBox(float x, float z) => x >= _box.MinX + 5f && x <= _box.MaxX - 5f && z >= _box.MinZ + 5f && z <= _box.MaxZ - 5f;

    private List<(float, float, float, float)> NextUnit()
    {
        var segs = new List<(float, float, float, float)>();
        int spines = _segmentsBuilt == 0 ? 2 : 1;
        _taken.Clear();
        for (int e = 0; e < _order.Count && _taken.Count < spines; e++)
        {
            if (_used[e])
            {
                continue;
            }

            (int Node, int Side, int Pass) k = _order[e];
            (float fx, float fz) = P(k, (k.Pass + 1) * Depth);
            if (!InBox(fx, fz))
            {
                continue;   // за пределами открытой области: позже, когда область расширится
            }

            _used[e] = true;
            _taken.Add(e);
            for (int j = (k.Pass * Depth) + 1; j <= (k.Pass + 1) * Depth; j++)
            {
                Segment(segs, P(k, j - 1), P(k, j));
            }

            // перемычки к соседней уже построенной улице той же стороны и прохода (узлы магистрали через один): на 2-м и 4-м пролёте
            for (int n = 0; n < _order.Count; n++)
            {
                (int Node, int Side, int Pass) o = _order[n];
                if (n == e || !_used[n] || o.Side != k.Side || o.Pass != k.Pass || Math.Abs(o.Node - k.Node) != 2)
                {
                    continue;
                }

                foreach (int j in new[] { (k.Pass * Depth) + 2, (k.Pass * Depth) + 4 })
                {
                    Segment(segs, P(k, j), P(o, j));
                }
            }
        }

        return segs;
    }

    private static long Cost(List<(float, float, float, float)> segs)
    {
        double m = 0;
        foreach ((float ax, float az, float bx, float bz) in segs)
        {
            m += Math.Sqrt(((bx - ax) * (bx - ax)) + ((bz - az) * (bz - az)));
        }

        return (long)(m * 2 * 10);   // street_2: 2 полосы x road_per_m_lane.street
    }

    private bool HasMoreUnits => _used.Contains(false);

    private void Act()
    {
        RefreshBox();
        _sim.GetCityStats(out CityStats cs);
        int lotCount = _sim.LotCount;
        EnsureLots(lotCount);
        int got = _sim.GetLots(_lots.AsSpan(0, lotCount));
        CountLots(got, out int free, out int pendR, out int pendC, out int pendI);
        _sim.GetCityBudget(out CityBudgetInfo bud);

        // 1) улицы: пока свободных участков мало (или их нет совсем) и казны хватает
        if ((free < FreeLotsLow || _segmentsBuilt == 0) && HasMoreUnits)
        {
            List<(float, float, float, float)> segs = NextUnit();
            if (segs.Count > 0)
            {
                long cost = Cost(segs);
                if (bud.Balance - (cost * 16 / 10) >= Reserve || _segmentsBuilt == 0)   // оценка занижена (рельеф, узлы): ×1.6
                {
                    var cmds = new List<Command>();
                    foreach ((float ax, float az, float bx, float bz) in segs)
                    {
                        var c = new BuildRoadCmd { Ax = ax, Az = az, Bx = bx, Bz = bz, SpeedLimit = StreetKmh / 3.6f, LanesAB = 1, LanesBA = 1, Kind = RoadKind.Street, Flags = BuildRoadFlags.AutoSplit };
                        cmds.Add(Command.MakeBuildRoad(in c, _tag++));
                    }

                    int ok = Flush(cmds);
                    _segmentsBuilt += ok;
                    _spent += cost * ok / Math.Max(1, segs.Count);
                }
                else
                {
                    foreach (int e in _taken)
                    {
                        _used[e] = false;   // не хватило денег - попробуем позже
                    }
                }
            }
        }

        // 2) зонирование: список участков после стройки
        lotCount = _sim.LotCount;
        EnsureLots(lotCount);
        got = _sim.GetLots(_lots.AsSpan(0, lotCount));
        CountLots(got, out free, out pendR, out pendC, out pendI);
        _sim.GetCityStats(out cs);
        var paints = new List<Command>();
        int pop = cs.Population;
        int capR = 40 + (pop / 6);
        int needR = Math.Min(free * 8 / 10, capR - pendR);   // 20% свободных держим под C/I
        // C/I - по спросу и долям 60/25/15 от зонированных участков: C когда есть спрос или население >= 100 (до 25% R), I при спросе (до 15% R)
        // C/I: по одному участку, пока нет зданий соответствующего вида (в ядре они стартуют только от уже работающей C - см. отчёт L89), потом по долям 25/15% от R
        int needC = ((cs.DemandC > 0 || pop >= 100) && (_zC < 1 || (cs.BuildingsC > 0 && _zC * 100 < _zR * 25))) ? 1 : 0;
        int needI = (cs.DemandI > 10 && pop >= 30 && (_zI < 1 || (cs.BuildingsI > 0 && _zI * 100 < _zR * 15))) ? 1 : 0;
        var taken = new HashSet<int>();
        for (int i = 0; i < needC; i++)
        {
            Pick(taken, paints, LotZone.Commercial);
        }

        for (int i = 0; i < needI; i++)
        {
            Pick(taken, paints, LotZone.Industrial);
        }

        for (int i = 0; i < needR; i++)
        {
            Pick(taken, paints, LotZone.Residential);
        }

        if (paints.Count > 0)
        {
            Flush(paints);
        }
    }

    private void EnsureLots(int n)
    {
        if (_lots.Length < n)
        {
            _lots = new LotRenderData[Math.Max(n, _lots.Length * 2)];
        }
    }

    private void CountLots(int n, out int free, out int pendR, out int pendC, out int pendI)
    {
        free = pendR = pendC = pendI = 0;
        _zR = _zC = _zI = 0;
        for (int i = 0; i < n; i++)
        {
            ref readonly LotRenderData l = ref _lots[i];
            if ((l.Flags & LotRenderFlags.Lost) == 0)
            {
                switch ((LotZone)l.Zone)
                {
                    case LotZone.Residential:
                        _zR++;
                        break;
                    case LotZone.Commercial:
                        _zC++;
                        break;
                    case LotZone.Industrial:
                        _zI++;
                        break;
                    default:
                        break;
                }
            }

            if ((l.Flags & (LotRenderFlags.Lost | LotRenderFlags.HasBuilding)) != 0)
            {
                continue;
            }

            switch ((LotZone)l.Zone)
            {
                case LotZone.None:
                    if (InTown(i))
                    {
                        free++;
                    }

                    break;
                case LotZone.Residential:
                    pendR++;
                    break;
                case LotZone.Commercial:
                    pendC++;
                    break;
                case LotZone.Industrial:
                    pendI++;
                    break;
                default:
                    break;
            }
        }
    }

    private const double TownRadius = 1500;     // зонируем только в радиусе города от центра и в стартовой области (±1000 м)
    private const double IndustryRing = 350;   // промзона - на окраине: ближе всего к кольцу этого радиуса

    private double Dist(int i)
    {
        NodeDesc t = _d.Network.Nodes[_center];
        double dx = _lots[i].X - t.X;
        double dz = _lots[i].Z - t.Z;
        return Math.Sqrt((dx * dx) + (dz * dz));
    }

    private bool InTown(int i) => InBox(_lots[i].X, _lots[i].Z) && Dist(i) <= TownRadius;

    private bool IsFree(int i, HashSet<int> taken)
    {
        ref readonly LotRenderData l = ref _lots[i];
        return l.Zone == (byte)LotZone.None && (l.Flags & (LotRenderFlags.Lost | LotRenderFlags.HasBuilding)) == 0 && !taken.Contains(i) && InTown(i);
    }

    /// <summary>Выбрать свободный участок: R и C - ближайший к центру города, I - ближайший к кольцу окраины.</summary>
    private void Pick(HashSet<int> taken, List<Command> paints, LotZone zone)
    {
        int n = _sim.LotCount;
        int best = -1;
        double bestK = double.MaxValue;
        for (int i = 0; i < n; i++)
        {
            if (!IsFree(i, taken))
            {
                continue;
            }

            double d = Dist(i);
            double k = zone == LotZone.Industrial ? Math.Abs(d - IndustryRing) : d;
            if (k < bestK)
            {
                bestK = k;
                best = i;
            }
        }

        if (best >= 0)
        {
            taken.Add(best);
            paints.Add(Paint(best, zone));
        }
    }

    private Command Paint(int lot, LotZone zone)
    {
        var p = new PaintZoneCmd { X0 = _lots[lot].X - 1f, Z0 = _lots[lot].Z - 1f, X1 = _lots[lot].X + 1f, Z1 = _lots[lot].Z + 1f, ZoneKind = (byte)zone, Density = 0 };
        return Command.MakePaintZone(in p, _tag++);
    }

    /// <summary>Поставить команды в очередь, прогнать 1 тик, посчитать принятые; отказы - в счётчик по причинам.</summary>
    private int Flush(List<Command> cmds)
    {
        int ok = 0;
        int idx = 0;
        while (idx < cmds.Count)
        {
            int batchStart = idx;
            while (idx < cmds.Count && _sim.Enqueue(cmds[idx]))
            {
                idx++;
            }

            _sim.Step(1);
            Collect();
            foreach (SimEvent e in _sim.Events)
            {
                if (e.Kind == SimEventKind.CommandApplied && e.Tag >= 1000)
                {
                    ok++;
                }
                else if (e.Kind == SimEventKind.CommandRejected && e.Tag >= 1000)
                {
                    RejectReason r = (RejectReason)e.A;
                    _rejects[r] = _rejects.GetValueOrDefault(r) + 1;
                }
            }

            if (idx == batchStart)
            {
                break;   // очередь не принимает - не зацикливаться
            }
        }

        return ok;
    }

    private void Collect()
    {
        foreach (SimEvent e in _sim.Events)
        {
            if (e.Kind == SimEventKind.ImmigrantsArrived)
            {
                _arrivals += e.A;
            }
        }
    }

    // ---- выборки ----

    private CityStats _cs;
    private CityBudgetInfo _bud;
    private OutsideInfo _oi;
    private long _tNow;
    private int _hourNo;
    private int _free, _lr, _lc, _li;
    private int _zR, _zC, _zI;

    private void Sample(long tickFromStart, int hour)
    {
        RefreshBox();
        _tNow = tickFromStart;
        _hourNo = hour;
        _sim.GetCityStats(out _cs);
        _sim.GetCityBudget(out _bud);
        _sim.GetOutside(0, out _oi);
        int n = _sim.LotCount;
        EnsureLots(n);
        int got = _sim.GetLots(_lots.AsSpan(0, n));
        CountLots(got, out _free, out _lr, out _lc, out _li);
        long pop = _cs.Population;
        if (_firstPopTick < 0 && pop > 0)
        {
            _firstPopTick = tickFromStart;
        }

        if (_firstArrivalTick < 0 && _arrivals > 0)
        {
            _firstArrivalTick = tickFromStart;
        }

        if (_firstBuildingTick < 0 && (_cs.Constructing + _cs.Active) > 0)
        {
            _firstBuildingTick = tickFromStart;
        }

        if (_firstActiveTick < 0 && _cs.Active > 0)
        {
            _firstActiveTick = tickFromStart;
        }

        _minBalance = Math.Min(_minBalance, _bud.Balance);
        _maxStuck = Math.Max(_maxStuck, _sim.Counters.StuckVehicles);
        if (_sim.Counters.StuckVehicles > 0 && _stuckDumps < 2 && _stuckDump)
        {
            _stuckDumps++;
            _out.WriteLine(Fmt($"stuck report at hour {hour} (stuck={_sim.Counters.StuckVehicles}):"));
            StuckReport.Write(_sim, _out);
            DumpStuckPlaces();
        }

        if (_sim.GetProgression(out ProgressionInfo pi))
        {
            while (_milestones.Count < pi.Reached)
            {
                int mi = _milestones.Count;
                _sim.GetMilestone(mi, out MilestoneInfo m);
                _milestones.Add((mi, m.ReachedTick, m.Desc.Population));
            }
        }

        int dayIdx = (int)(_sim.Tick / _sim.CityDayTicks) - 1;
        if (_sim.Tick % _sim.CityDayTicks == 0 && dayIdx >= 0 && dayIdx < _popDayEnd.Length)
        {
            _popDayEnd[dayIdx] = pop;
        }
    }

    /// <summary>Диагностика (SCRATCH_STUCK_DUMP=1): где стоят застрявшие - сегмент и координаты его концов.</summary>
    private void DumpStuckPlaces()
    {
        Sim.Core.World w = _sim.World;
        var seen = new HashSet<int>();
        for (int i = 0; i < w.Vehicles.HighWater && seen.Count < 6; i++)
        {
            if (!w.Vehicles.Pool.IsAlive(i) || (w.Vehicles.Flags[i] & (byte)Sim.Core.Agents.VehicleFlags.Stuck) == 0)
            {
                continue;
            }

            int lane = w.Vehicles.Lane[i];
            LaneData ld = w.Net.Lanes[lane];
            if (ld.Segment >= 0 && seen.Add(ld.Segment))
            {
                SegmentData sg = w.Net.Segments[ld.Segment];
                NodeData na = w.Net.Nodes[sg.NodeA];
                NodeData nb = w.Net.Nodes[sg.NodeB];
                _out.WriteLine(Fmt($"  stuck on seg {ld.Segment} {sg.Kind} lanes={sg.LanesAB}/{sg.LanesBA} ({na.Position.X:F0},{na.Position.Z:F0})->({nb.Position.X:F0},{nb.Position.Z:F0}) dir={ld.Direction} lane={ld.Kind}"));
            }
            else if (ld.Segment < 0)
            {
                NodeData nn = w.Net.Nodes[ld.Node];
                _out.WriteLine(Fmt($"  stuck on connector at node ({nn.Position.X:F0},{nn.Position.Z:F0}) control={nn.Control}"));
            }
        }
    }

    private void PrintLine()
    {
        int dayTicks = _sim.CityDayTicks;
        long t = _sim.Tick;
        int day = (int)(t / dayTicks);
        int hh = (int)(t % dayTicks * 24 / dayTicks);
        long outJobs = _oi.JobsTotal;
        SimCounters c = _sim.Counters;
        _out.WriteLine(Fmt($"d{day} {hh:00} | {_cs.Population,5} {_arrivals,5} {_oi.ArrivalsInTransit,3} | {_oi.JobsTaken,4}/{outJobs,-4} {_cs.Vacancies,4} {_cs.Employed,4} | {_bud.Balance,8} {_cs.DemandR,4} {_cs.DemandC,4} {_cs.DemandI,4} | {_cs.BuildingsR,3} {_cs.BuildingsC,3} {_cs.BuildingsI,3} {_cs.Constructing,3} | {_segmentsBuilt,3} L{_sim.LotCount,-4} {_free,3}/{_lr,2}/{_lc,2}/{_li,2} | {c.StuckVehicles} {RejectTotal()}"));
    }

    private int RejectTotal()
    {
        int s = 0;
        foreach (int v in _rejects.Values)
        {
            s += v;
        }

        return s;
    }

    private static string Min(long tickFromStart) => tickFromStart < 0 ? "-" : (tickFromStart / 20.0 / 60.0).ToString("F1", CultureInfo.InvariantCulture);

    private void PrintSummary(long start)
    {
        SimCounters c = _sim.Counters;
        _out.WriteLine(Fmt($"summary seed={_d.Seed}: first_building_min={Min(_firstBuildingTick)} first_active_building_min={Min(_firstActiveTick)} first_arrival_min={Min(_firstArrivalTick)} first_resident_min={Min(_firstPopTick)}  (sim minutes from the start tick = real minutes at x1)"));
        foreach ((int index, long tick, int pop) in _milestones)
        {
            _out.WriteLine(Fmt($"  milestone m{index + 1} (pop>={pop}) granted at min={Min(tick - start)}"));
        }

        _out.WriteLine(Fmt($"  pop_end={_cs.Population} arrivals={_arrivals} min_treasury={_minBalance} end_treasury={_bud.Balance} road_spend~{_spent} segments={_segmentsBuilt} bldgs R/C/I={_cs.BuildingsR}/{_cs.BuildingsC}/{_cs.BuildingsI}"));
        string rej = _rejects.Count == 0 ? "none" : string.Join(",", FormatRejects());
        _out.WriteLine(Fmt($"  stuck_now={c.StuckVehicles} max_stuck={_maxStuck} stuck_events={c.StuckEventsTotal} res_stuck={c.ResStuck} insolvent={_bud.Insolvent} debt_days={_bud.DebtDays} rejects={rej}"));
        string pops = "";
        for (int i = 0; i < _popDayEnd.Length && i < 12; i++)
        {
            if (_popDayEnd[i] > 0 || i < 3)
            {
                pops += Fmt($" d{i}={_popDayEnd[i]}");
            }
        }

        _out.WriteLine("  pop_at_day_end:" + pops);
    }

    private IEnumerable<string> FormatRejects()
    {
        foreach (KeyValuePair<RejectReason, int> kv in _rejects)
        {
            yield return Fmt($"{kv.Key}={kv.Value}");
        }
    }
}
