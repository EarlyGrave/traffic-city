using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>
/// Потоки и политика казны (city-economy.md §3, §6, §11). E1 заморозил поверхность и сделал команду <see cref="CommandKind.SetBudgetPolicy"/> с применением на границе суток;
/// проводки налогов, сборов, содержания, процентов, дивидендов и утечки, долг, лимит и события <c>Budget*</c> добавляет E2 в <see cref="OnDayBoundary"/> и в события жителей и фирм.
/// </summary>
internal static class BudgetFlows
{
    /// <summary>
    /// Обработчик <see cref="CommandKind.SetBudgetPolicy"/> (§11): проверки и запись в ожидающую политику; <see cref="RejectReason.None"/> - принята. Без города - <c>NotSupported</c>;
    /// у города без бюджета запрос <c>Enable</c> - <c>NotSupported</c> (тело <see cref="Enable"/> - E2), политика без <c>Enable</c> - <c>BudgetRule</c> (нечего задавать);
    /// у города с бюджетом - <see cref="CityBudget.CheckPolicy"/>. Политика, равная действующей, снимает ожидающую («последняя команда суток побеждает»).
    /// </summary>
    public static RejectReason SetPolicy(World w, in Command cmd)
    {
        CityWorld? c = w.City;
        if (c == null)
        {
            return RejectReason.NotSupported;
        }

        BudgetPolicy p = cmd.SetBudgetPolicy.Policy;
        CityBudget? b = c.Budget;
        if (b == null)
        {
            if ((p.Flags & ~BudgetPolicyFlags.Enable) != 0 || p.R0 != 0 || p.R1 != 0)
            {
                return RejectReason.InvalidArgument;
            }

            return (p.Flags & BudgetPolicyFlags.Enable) != 0 ? Enable(w, c) : RejectReason.BudgetRule;
        }

        RejectReason why = CityBudget.CheckPolicy(in c.Cfg.Economy, in p);
        if (why != RejectReason.None)
        {
            return why;
        }

        if (CityBudget.Same(in p, in b.Policy))
        {
            b.PendingPolicy = default;
            b.HasPending = 0;
        }
        else
        {
            b.PendingPolicy = p;
            b.HasPending = 1;
        }

        return RejectReason.None;
    }

    /// <summary>
    /// Включение бюджета на идущем городе (§11, EC8): создаёт <see cref="CityBudget"/>, вклад в казну, <c>LV = T</c>, <c>PayrollEwma</c>, <c>IncomeEwma</c>. E1 - заглушка:
    /// <see cref="RejectReason.NotSupported"/>; тело - E2.
    /// </summary>
    public static RejectReason Enable(World w, CityWorld c) => RejectReason.NotSupported;

    /// <summary>
    /// Граница суток (фаза 9r, после <c>Day++</c>, до <c>Ledger.RollDay</c>; §6.2). E1: применяет ожидающую политику (событие <see cref="SimEventKind.PolicyApplied"/>).
    /// E2 вставляет перед применением остальные шаги в порядке §6.2: цена земли, содержание, проценты, сглаженные доход и расход, лимит и <c>Insolvent</c>.
    /// </summary>
    public static void OnDayBoundary(World w, CityWorld c)
    {
        CityBudget? b = c.Budget;
        if (b == null)
        {
            return;
        }

        if (b.HasPending != 0)
        {
            b.Policy = b.PendingPolicy;
            b.PendingPolicy = default;
            b.HasPending = 0;
            w.Emit(SimEventKind.PolicyApplied, 0, c.Day, b.Policy.Fare);
        }
    }

    /// <summary>Пересчёт содержания дорог и автобусов (<c>MaintRoadDay</c>, <c>MaintBusDay</c>, §6.1) при <c>NetDirty</c> и правке маршрутов. E1 - заглушка (нули), тело - E2.</summary>
    public static void RecomputeMaintenance(World w, CityWorld c)
    {
    }
}
