using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Sim.Scenarios.Mods;

/// <summary>
/// Подъёмы форматов «vN → vN+1» как чистые функции над деревом JSON (modding.md §8): входной узел не меняется,
/// результат — новая копия. Подъём только вверх; новее текущей версии отвергает вызывающий.
/// </summary>
public static class FormatUpgrades
{
    /// <summary>Ссылка на встроенный тип ТС: <c>base:vehicle/&lt;имя&gt;</c> (modding.md §2).</summary>
    public static string BaseVehicleRef(string name) => "base:vehicle/" + name;

    /// <summary><c>vehicles.json</c> v1 → v2: новые ключи (<c>rev</c>, <c>visual</c>) необязательны, меняется только версия.</summary>
    public static JsonNode VehiclesV1ToV2(JsonNode v1)
    {
        JsonObject o = Clone(v1);
        o["format_version"] = 2;
        return o;
    }

    /// <summary>
    /// Сценарий v2 → v3 для сценариев на встроенных типах (<c>vehicle-types.json</c> по умолчанию): версия 3,
    /// <c>packages: ["base"]</c>, без <c>vehicle_types_file</c>, ссылки на типы (<c>type_mix</c>, <c>transit.bus_type</c>)
    /// переведены в полные id. Сценарий с собственным файлом типов поднять нельзя (<see cref="ScenarioException"/>).
    /// </summary>
    public static JsonNode ScenarioV2ToV3(JsonNode v2)
    {
        JsonObject o = Clone(v2);
        if (o["vehicle_types_file"] is JsonNode f && f.GetValue<string>() != "vehicle-types.json")
        {
            throw new ScenarioException("$.vehicle_types_file: custom type file cannot be upgraded to v3 automatically");
        }

        o.Remove("vehicle_types_file");
        o["format_version"] = 3;
        o["packages"] = new JsonArray("base");
        if (o["vehicles"] is JsonObject veh && veh["type_mix"] is JsonObject mix)
        {
            var renamed = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> kv in mix)
            {
                renamed[BaseVehicleRef(kv.Key)] = kv.Value?.DeepClone();
            }

            veh["type_mix"] = renamed;
        }

        if (o["transit"] is JsonObject tr && tr["bus_type"] is JsonNode bt)
        {
            tr["bus_type"] = BaseVehicleRef(bt.GetValue<string>());
        }

        return o;
    }

    private static JsonObject Clone(JsonNode n) =>
        n.DeepClone() as JsonObject ?? throw new ScenarioException("$: expected object");
}
