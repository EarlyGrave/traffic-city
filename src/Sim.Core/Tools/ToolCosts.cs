using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;

namespace Sim.Core.Tools;

/// <summary>
/// Цена инструментов в казне города (city-economy.md §8, E4). Только при <c>city_budget</c> (<c>CityWorld.Budget != null</c>); иначе всё бесплатно и поведение прежнее.
/// <see cref="Resolve"/> зовёт общий валидатор (<see cref="ToolValidator.Check"/>) - превью и применение видят одну цену и один <c>InsufficientFunds</c>;
/// <see cref="Settle"/> зовёт обработчик команды в точке «применена». Цена из таблицы <c>tools.costs</c> считается по текущей геометрии (§8.2), не из сохранённой цены постройки.
/// Undo (§8.3): прямая платная команда пишет запись в <c>UndoCredits</c> (цель, <c>Amount = cost</c>, срок), обратная (<see cref="CommandFlags.Inverse"/>) со своей живой записью
/// стоит <c>-Amount</c> (возврат 100% либо возврат полученного за снос) и гасит запись. Без записи (истекла, чужая, подделка бита) - обычная цена.
/// </summary>
internal static class ToolCosts
{
    private const int NoSlot = -1;

    // T9: цели остановок и линий в UndoCredits - индекс со старшим тегом вида (индексы сегментов, узлов, остановок и линий пересекаются; поколение у генератора у всех 1).
    private const int StopTag = 1 << 24;
    private const int RouteTag = 2 << 24;

    /// <summary>Индекс цели undo-кредита для остановки <paramref name="stop"/>.</summary>
    public static int StopTarget(int stop) => StopTag | stop;

    /// <summary>Индекс цели undo-кредита для линии <paramref name="route"/>.</summary>
    public static int RouteTarget(int route) => RouteTag | route;

    /// <summary>Цель команды в терминах handle'ов: то, что команда создаёт, меняет или сносит; индекс -1 - не известна (прямая <c>BuildRoad</c>: сегмент появится после применения).</summary>
    public static void TargetOf(World w, in Command cmd, out int index, out int gen)
    {
        index = -1;
        gen = 0;
        switch (cmd.Kind)
        {
            case CommandKind.BuildRoad:
                if (cmd.BuildRoad.RestoreOf.Generation != 0)
                {
                    index = cmd.BuildRoad.RestoreOf.Index;
                    gen = cmd.BuildRoad.RestoreOf.Generation;
                }

                break;
            case CommandKind.RemoveRoad:
                index = cmd.RemoveRoad.Seg.Index;
                gen = cmd.RemoveRoad.Seg.Generation;
                break;
            case CommandKind.UpgradeRoad:
                index = cmd.UpgradeRoad.Seg.Index;
                gen = cmd.UpgradeRoad.Seg.Generation;
                break;
            case CommandKind.SetArmControl:
                index = cmd.SetArmControl.Node.Index;
                gen = cmd.SetArmControl.Node.Generation;
                break;
            case CommandKind.SetTurnBan:
                index = cmd.SetTurnBan.Node.Index;
                gen = cmd.SetTurnBan.Node.Generation;
                break;
            case CommandKind.SetNodeControl:
                NodeTarget(w, cmd.SetNodeControl.Node, cmd.SetNodeControl.NodeGen, out index, out gen);
                break;
            case CommandKind.SetSignalPlan:
                NodeTarget(w, cmd.SetSignalPlan.Node, cmd.SetSignalPlan.NodeGen, out index, out gen);
                break;
            case CommandKind.CreateBusRoute:
                index = RouteTarget(cmd.CreateBusRoute.RestoreOf.Index);
                gen = cmd.CreateBusRoute.RestoreOf.Index >= 0 ? cmd.CreateBusRoute.RestoreOf.Generation : 0;
                break;
            case CommandKind.InsertBusStop:
                index = StopTarget(cmd.InsertBusStop.RestoreOf.Index);
                gen = cmd.InsertBusStop.RestoreOf.Index >= 0 ? cmd.InsertBusStop.RestoreOf.Generation : 0;
                break;
            case CommandKind.RemoveBusStop:
                index = StopTarget(cmd.RemoveBusStop.Stop.Index);
                gen = (uint)cmd.RemoveBusStop.Stop.Index < (uint)w.Net.BusStopCount
                    ? (cmd.RemoveBusStop.Stop.Generation != 0 ? cmd.RemoveBusStop.Stop.Generation : w.Net.BusStops[cmd.RemoveBusStop.Stop.Index].Generation) : 0;
                break;
            case CommandKind.RemoveBusRoute:
                index = RouteTarget(cmd.RemoveBusRoute.Route.Index);
                gen = (uint)cmd.RemoveBusRoute.Route.Index < (uint)w.Net.BusRouteCount
                    ? (cmd.RemoveBusRoute.Route.Generation != 0 ? cmd.RemoveBusRoute.Route.Generation : w.Net.BusRoutes[cmd.RemoveBusRoute.Route.Index].Generation) : 0;
                break;
        }

        if (gen == 0)
        {
            index = -1;
        }
    }

    private static void NodeTarget(World w, int node, int nodeGen, out int index, out int gen)
    {
        index = node;
        gen = nodeGen != 0 ? nodeGen : ((uint)node < (uint)w.Net.NodeCount ? w.Net.Nodes[node].Generation : 0);
    }

    /// <summary>
    /// Эффективная цена и проверка казны после того, как валидатор принял команду. <c>c.Cost</c> становится ценой с учётом undo-кредита; отказ <see cref="RejectReason.InsufficientFunds"/>,
    /// если <c>City - cost &lt; -CreditLimit</c>. Без бюджета - ничего не меняет.
    /// </summary>
    public static RejectReason Resolve(World w, in Command cmd, ref ToolCheck c)
    {
        c.CreditSlot = NoSlot;
        CityWorld? city = w.City;
        CityBudget? b = city?.Budget;
        if (city == null || b == null)
        {
            return RejectReason.None;
        }

        if (c.Reopen)
        {
            // Отмена сноса: без кредита это обычная стройка (снос уже вернул долю цены), цена - как нового сегмента той же геометрии.
            ref readonly SegmentData sd = ref w.Net.Segments[c.Seg];
            c.Cost = ToolValidator.RoadCost(in w.ToolsCfg.Costs, sd.Kind, w.Builder.FullAxisLength(c.Seg), sd.LanesAB + sd.LanesBA, (sd.Flags & SegmentFlags.Curve) != 0);
        }

        if ((cmd.Flags & CommandFlags.Inverse) != 0)
        {
            TargetOf(w, in cmd, out int ti, out int tg);
            int slot = FindCredit(b, ti, tg, w.Tick);
            if (slot >= 0)
            {
                c.CreditSlot = slot;
                c.Cost = -b.UndoCredits[slot].Amount;
            }
        }

        if (c.Cost > 0 && city.Ledger.City - c.Cost < -b.CreditLimit(in city.Cfg.Economy))
        {
            return RejectReason.InsufficientFunds;
        }

        return RejectReason.None;
    }

    private static int FindCredit(CityBudget b, int index, int gen, long tick)
    {
        if (gen == 0)
        {
            return NoSlot;
        }

        for (int i = 0; i < b.UndoCredits.Length; i++)
        {
            ref readonly UndoCredit u = ref b.UndoCredits[i];
            if (u.TargetGeneration == gen && u.TargetIndex == index && tick <= u.ExpireTick)
            {
                return i;
            }
        }

        return NoSlot;
    }

    /// <summary>
    /// Проводка применённой команды: <c>Construction</c> (трата) либо <c>Refund</c> (возврат), погашение использованной записи, запись нового кредита для прямой платной команды.
    /// <paramref name="builtIndex"/>/<paramref name="builtGen"/> - цель, созданная прямой командой (сегмент <c>BuildRoad</c>, линия или остановка с тегом вида; T9); <c>builtGen == 0</c> - цель из команды.
    /// </summary>
    public static void Settle(World w, in Command cmd, in ToolCheck c, int builtIndex, int builtGen)
    {
        CityWorld? city = w.City;
        CityBudget? b = city?.Budget;
        if (city == null || b == null)
        {
            return;
        }

        Ledger lg = city.Ledger;
        if (c.CreditSlot >= 0)
        {
            b.UndoCredits[c.CreditSlot] = default;
        }

        long cost = c.Cost;
        if (cost > 0)
        {
            lg.Transfer(lg.CityAccount, lg.ExternalAccount, cost, FlowKind.Construction);
        }
        else if (cost < 0)
        {
            lg.Transfer(lg.ExternalAccount, lg.CityAccount, -cost, FlowKind.Refund);
        }

        if (cost == 0 || c.CreditSlot >= 0 || (cmd.Flags & CommandFlags.Inverse) != 0)
        {
            return;
        }

        int window = city.Cfg.Economy.UndoRefundS * Simulation.TicksPerSecond;
        if (window <= 0)
        {
            return;
        }

        int ti;
        int tg;
        if (builtGen != 0 && builtIndex >= 0)
        {
            ti = builtIndex;
            tg = builtGen;
        }
        else
        {
            TargetOf(w, in cmd, out ti, out tg);
        }

        if (tg != 0 && ti >= 0)
        {
            Record(b, ti, tg, cost, w.Tick + window);
        }
    }

    private static void Record(CityBudget b, int index, int gen, long amount, long expire)
    {
        UndoCredit[] t = b.UndoCredits;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i].TargetGeneration != 0 && t[i].ExpireTick != expire)
            {
                System.Array.Clear(t);   // новая группа (другой тик): возврат 100% - только за последнее действие
                break;
            }
        }

        int slot = 0;   // кольцо полно (одна группа, все сроки равны): вытесняется первая запись
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i].TargetGeneration == 0)
            {
                slot = i;
                break;
            }
        }

        t[slot] = new UndoCredit { TargetIndex = index, TargetGeneration = gen, Amount = amount, ExpireTick = expire };
    }

    /// <summary>Гасит записи, срок которых вышел к концу фазы 1 (после неё <c>Tick++</c>): сейв не должен хранить просроченное (city-economy.md §12, правило 7).</summary>
    public static void Purge(World w)
    {
        CityBudget? b = w.City?.Budget;
        if (b == null)
        {
            return;
        }

        UndoCredit[] t = b.UndoCredits;
        for (int i = 0; i < t.Length; i++)
        {
            if (t[i].TargetGeneration != 0 && t[i].ExpireTick <= w.Tick)
            {
                t[i] = default;
            }
        }
    }
}
