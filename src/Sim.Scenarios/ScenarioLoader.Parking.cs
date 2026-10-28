using System;
using Sim.Core.Api;

namespace Sim.Scenarios;

// Секция сценария parking (docs/design/parking.md §14) и capacities.max_parking. Единицы ядра целые (ADR-0022): дробные значения переводятся в целые здесь и только здесь
// (f64 -> f32, затем округление), чтобы Rust-версия читала тот же JSON и получала те же числа.
public static partial class ScenarioLoader
{
    /// <summary>Ключ секции парковки; вне режима жителей его наличие - ошибка.</summary>
    private const string ParkingKey = "parking";

    /// <summary>Мм из метров: <c>round((float)m * 1000)</c>.</summary>
    private static int MmF(double meters) => (int)Math.Round((float)meters * 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Секция <c>parking</c>: нет секции - режима нет (нулевая структура, сценарий побитово прежний). Есть - обязателен <c>model = "resource"</c> и <c>demand.model = "residents"</c>
    /// (иначе <c>InvalidScenario</c>); остальные ключи необязательны и берут дефолты <see cref="ParkingDesc.Default"/>. Неизвестные ключи отклоняются.
    /// </summary>
    private static ParkingDesc ParseParking(JsonObjectReader? r, bool residents)
    {
        if (r == null)
        {
            return default;
        }

        if (!residents)
        {
            throw new ScenarioException($"{r.Path}: allowed only with demand.model = \"residents\"");
        }

        string model = r.Str("model");
        if (model != "resource")
        {
            throw new ScenarioException($"{r.ChildPath("model")}: expected \"resource\"");
        }

        ParkingDesc d = ParkingDesc.Default();
        string walk = r.Str("access_walk", "timer");
        d.AccessWalkAgents = walk switch
        {
            "timer" => false,
            "agents" => true,
            _ => throw new ScenarioException($"{r.ChildPath("access_walk")}: expected \"timer\" or \"agents\""),
        };
        d.WalkMaxMm = MmF(r.Num("walk_max_m", 400, min: 0, max: 10_000));
        d.HomeWalkMaxMm = MmF(r.Num("home_walk_max_m", 300, min: 0, max: 10_000));
        d.MaxCandidates = r.Int("max_candidates", 16, min: 1, max: 64);
        d.WalkSpeedMmS = MmF(r.Num("walk_speed_mps", 1.3, min: 0.1, max: 10));
        d.FeeDsPerMoney = r.Int("fee_ds_per_money", 60, min: 0, max: 100_000);
        d.RetryTicks = Math.Max(1, Ticks(r.Num("retry_s", 30, min: 0.05, max: 50_000)));
        d.SearchRetryTicks = Math.Max(1, Ticks(r.Num("search_retry_s", 10, min: 0.05, max: 50_000)));
        d.SearchMaxTicks = Math.Max(1, Ticks(r.Num("search_max_s", 300, min: 0.05, max: 500_000)));
        d.HomeRetryPerDayQ16 = Frac16(r, "home_retry_per_day", 0.02);
        JsonObjectReader? curb = r.OptObject("curb");
        if (curb != null)
        {
            d.CurbSlotMm = MmF(curb.Num("slot_m", 6, min: 1, max: 100));
            d.CurbStopClearMm = MmF(curb.Num("stop_clear_m", 15, min: 0, max: 1000));
            d.CurbStreet = CurbModeOf(curb, "street", "free");
            d.CurbArterial = CurbModeOf(curb, "arterial", "off");
            d.CurbFee = curb.Int("fee", 0, min: 0, max: 1_000_000);
            d.CurbPermits = curb.Bool("permits", true);
            curb.Finish();
        }

        JsonObjectReader? garage = r.OptObject("garage");
        if (garage != null)
        {
            d.GarageFee = garage.Int("fee", 0, min: 0, max: 1_000_000);
            d.GaragePermits = garage.Bool("permits", false);
            garage.Finish();
        }

        r.Finish();
        return d;
    }

    private static CurbMode CurbModeOf(JsonObjectReader r, string key, string def) => r.Str(key, def) switch
    {
        "off" => CurbMode.Off,
        "free" => CurbMode.Free,
        "paid" => CurbMode.Paid,
        _ => throw new ScenarioException($"{r.ChildPath(key)}: expected \"off\", \"free\" or \"paid\""),
    };
}
