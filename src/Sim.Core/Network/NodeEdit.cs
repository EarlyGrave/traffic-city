using System;
using Sim.Core.Api;
using Sim.Core.Rules.Intersections;

namespace Sim.Core.Network;

/// <summary>
/// Перестройка узла целиком (4.3, T3; player-tools.md §5): структура - <see cref="NetworkBuilder.PlanRebuild"/> + <see cref="NetworkBuilder.CommitRebuild"/>, затем светофор узла
/// (маски фаз заново, план сохраняется или сбрасывается) и события. Пересчёт производных мира (граф, оверлей, пешеходы, LOD) - у вызывающего, один раз на пачку правок
/// (<see cref="NetworkEdit.RefreshDerived"/>).
/// </summary>
internal static class NodeEdit
{
    /// <summary>Проверка перестройки без изменений: <see cref="NetworkStatus.Ok"/> - перестройка получится (при той же сети и составе машин).</summary>
    public static NetworkStatus Check(World w, int node, bool rejectTrap) => w.Builder.PlanRebuild(w, node, rejectTrap, out _);

    /// <summary>Перестраивает построенный узел. При отказе (<c>Busy</c>, <c>Trap</c>, ёмкость) мир не тронут.</summary>
    public static NetworkStatus Rebuild(World w, int node, bool rejectTrap, uint tag)
    {
        NetworkStore n = w.Net;
        NetworkStatus st = w.Builder.PlanRebuild(w, node, rejectTrap, out RebuildPlan rp);
        if (st != NetworkStatus.Ok)
        {
            return st;
        }

        int slot = n.Nodes[node].Signal;
        Span<int> oldSegs = stackalloc int[NetworkStore.MaxArms];
        Span<sbyte> oldPhase = stackalloc sbyte[NetworkStore.MaxArms];
        int oldCount = 0;
        if (slot >= 0)
        {
            w.Signals.ArmPhases(w, slot, node, oldSegs, oldPhase, out oldCount);
        }

        st = w.Builder.CommitRebuild(w, node, in rp, out RebuildInfo info);
        if (st != NetworkStatus.Ok)
        {
            return st;
        }

        ref NodeData nd = ref n.Nodes[node];
        if (slot >= 0)
        {
            if (nd.ArmCount == 0)
            {
                w.Signals.Remove(w, slot);
                nd.Control = NodeControl.None;
            }
            else
            {
                int r = w.Signals.Rebind(w, slot, node, oldSegs, oldPhase, oldCount);
                if (r == 1)
                {
                    w.Emit(SimEventKind.SignalPlanReset, tag, node);
                }
                else if (r == 2)
                {
                    nd.Control = nd.ArmCount >= 3 ? NodeControl.RightHandRule : NodeControl.None;
                    w.Connectors.RecomputeRel(n, node, false);
                }
            }
        }

        w.Work.NodeRebuilds++;
        w.Emit(SimEventKind.NodeRebuilt, tag, node);
        _ = info;
        return NetworkStatus.Ok;
    }
}
