using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Scenarios.Mods;

/// <summary>Тип ТС каталога: id, ревизия, параметры ядра и ключ визуала (modding.md §3.1).</summary>
public sealed record VehicleEntry(AssetId Id, int Rev, VehicleTypeDesc Desc, string? VisualModel);

/// <summary>Тип дороги каталога (modding.md §3.2); ядру не передаётся, хост разворачивает его в поля команды.</summary>
public sealed record RoadEntry(
    AssetId Id, int Rev, RoadKind Kind, int LanesAB, int LanesBA, float SpeedLimit, string? VisualProfile);

/// <summary>Тип здания каталога (modding.md §3.3, zoning-demand.md §17): id, ревизия, параметры ядра и ключ визуала.</summary>
public sealed record BuildingEntry(AssetId Id, int Rev, BuildingTypeDesc Desc, string? VisualModel);

/// <summary>
/// Каталог контента набора пакетов: упорядоченные списки типов ТС и дорог в порядке загрузки пакетов и порядке
/// файлов/элементов (modding.md §5.1). Индекс типа ТС = <c>VehicleType</c> в <c>ScenarioDesc.VehicleTypes</c> (§5.2).
/// </summary>
public sealed class ContentCatalog
{
    /// <summary>Предел типов ТС в наборе (= <c>WorldBuilder.MaxVehicleTypes</c> ядра).</summary>
    public const int MaxVehicleTypes = 256;

    private readonly Dictionary<AssetId, int> _vehicleIndex = [];
    private readonly Dictionary<AssetId, int> _roadIndex = [];
    private readonly List<VehicleEntry> _vehicles = [];
    private readonly List<RoadEntry> _roads = [];
    private readonly Dictionary<AssetId, int> _buildingIndex = [];
    private readonly List<BuildingEntry> _buildings = [];
    private readonly Dictionary<AssetId, int> _serviceIndex = [];
    private readonly List<ServiceEntry> _services = [];

    public IReadOnlyList<VehicleEntry> Vehicles => _vehicles;

    public IReadOnlyList<RoadEntry> Roads => _roads;

    /// <summary>Типы зданий в порядке загрузки; индекс = <c>Building.Type</c> (строки только дописываются, zoning-demand.md §2.2).</summary>
    public IReadOnlyList<BuildingEntry> Buildings => _buildings;

    /// <summary>Записи служб в порядке загрузки (services.md §2.2); индекс = <c>Svc</c> ядра, если сценарий не выключил записи (<c>services_off</c>).</summary>
    public IReadOnlyList<ServiceEntry> Services => _services;

    /// <summary>Таблицы записей служб ядра (<see cref="CityServicesDesc"/>) из каталога; <paramref name="off"/> - выключенные сценарием записи.</summary>
    public void FillServices(ISet<AssetId>? off, ref CityServicesDesc s) => ServiceContent.Flatten(_services, off, ref s);

    /// <summary>Индекс записи службы или -1.</summary>
    public int FindService(AssetId id) => _serviceIndex.TryGetValue(id, out int i) ? i : -1;

    /// <summary>Индекс типа здания или -1.</summary>
    public int FindBuilding(AssetId id) => _buildingIndex.TryGetValue(id, out int i) ? i : -1;

    /// <summary>Индекс типа ТС или -1.</summary>
    public int FindVehicle(AssetId id) => _vehicleIndex.TryGetValue(id, out int i) ? i : -1;

    /// <summary>Индекс типа дороги или -1.</summary>
    public int FindRoad(AssetId id) => _roadIndex.TryGetValue(id, out int i) ? i : -1;

    /// <summary>
    /// Собрать каталог. Любая ошибка — в <paramref name="issues"/>; вызывающий проверяет, что их не прибавилось.
    /// Проверяет: <c>DuplicateId</c>, ассетов ≤ 4096 на пакет (<c>TooLarge</c>), типов ТС ≤ 256 (<c>CapacityExceeded</c>),
    /// первый тип набора — категории <c>car</c>.
    /// </summary>
    public static ContentCatalog Build(PackageSet set, List<ModIssue> issues)
    {
        var cat = new ContentCatalog();
        foreach (LoadedPackage pkg in set.Packages)
        {
            int assets = 0;
            foreach (string file in pkg.Manifest.Content.Vehicles)
            {
                foreach (VehicleEntry e in VehicleContent.Load(pkg, file, issues))
                {
                    assets++;
                    if (cat._vehicleIndex.TryAdd(e.Id, cat._vehicles.Count))
                    {
                        cat._vehicles.Add(e);
                    }
                    else
                    {
                        issues.Add(new ModIssue(pkg.Id, file, string.Empty, ModError.DuplicateId, e.Id.ToString()));
                    }
                }
            }

            foreach (string file in pkg.Manifest.Content.Roads)
            {
                foreach (RoadEntry e in RoadContent.Load(pkg, file, issues))
                {
                    assets++;
                    if (cat._roadIndex.TryAdd(e.Id, cat._roads.Count))
                    {
                        cat._roads.Add(e);
                    }
                    else
                    {
                        issues.Add(new ModIssue(pkg.Id, file, string.Empty, ModError.DuplicateId, e.Id.ToString()));
                    }
                }
            }

            foreach (string file in pkg.Manifest.Content.Buildings)
            {
                foreach (BuildingEntry e in BuildingContent.Load(pkg, file, issues, cat.FindVehicle))
                {
                    assets++;
                    BuildingTypeDesc desc = e.Desc;
                    desc.VisualRow = cat._buildings.Count;   // строка визуала = индекс в каталоге (строки только дописываются)
                    if (cat._buildingIndex.TryAdd(e.Id, cat._buildings.Count))
                    {
                        cat._buildings.Add(e with { Desc = desc });
                    }
                    else
                    {
                        issues.Add(new ModIssue(pkg.Id, file, string.Empty, ModError.DuplicateId, e.Id.ToString()));
                    }
                }
            }

            foreach (string file in pkg.Manifest.Content.Services)
            {
                foreach (ServiceEntry e in ServiceContent.Load(pkg, file, issues, cat))
                {
                    assets++;
                    if (cat._serviceIndex.TryAdd(e.Id, cat._services.Count))
                    {
                        cat._services.Add(e);
                    }
                    else
                    {
                        issues.Add(new ModIssue(pkg.Id, file, string.Empty, ModError.DuplicateId, e.Id.ToString()));
                    }
                }
            }

            if (assets > ModLimits.MaxAssetsPerPackage)
            {
                issues.Add(new ModIssue(pkg.Id, string.Empty, string.Empty, ModError.TooLarge,
                    $"{assets.ToString(CultureInfo.InvariantCulture)} assets, limit {ModLimits.MaxAssetsPerPackage.ToString(CultureInfo.InvariantCulture)}"));
            }
        }

        if (cat._vehicles.Count > MaxVehicleTypes)
        {
            issues.Add(new ModIssue(string.Empty, string.Empty, "$.types", ModError.CapacityExceeded,
                $"{cat._vehicles.Count.ToString(CultureInfo.InvariantCulture)} vehicle types, limit {MaxVehicleTypes.ToString(CultureInfo.InvariantCulture)}"));
        }

        if (cat._buildings.Count > ModLimits.MaxBuildingTypes)
        {
            issues.Add(new ModIssue(string.Empty, string.Empty, "$.buildings", ModError.CapacityExceeded,
                $"{cat._buildings.Count.ToString(CultureInfo.InvariantCulture)} building types, limit {ModLimits.MaxBuildingTypes.ToString(CultureInfo.InvariantCulture)}"));
        }

        if (cat._services.Count > ModLimits.MaxServices)
        {
            issues.Add(new ModIssue(string.Empty, string.Empty, "$.services", ModError.CapacityExceeded,
                $"{cat._services.Count.ToString(CultureInfo.InvariantCulture)} services, limit {ModLimits.MaxServices.ToString(CultureInfo.InvariantCulture)}"));
        }

        if (cat._vehicles.Count > 0 && cat._vehicles[0].Desc.Category != VehicleCategory.Car)
        {
            issues.Add(new ModIssue(cat._vehicles[0].Id.Package, string.Empty, "$.types[0]", ModError.BadValue,
                "the first vehicle type of the set must be of category car (type 0 is the core default)"));
        }

        return cat;
    }

    /// <summary>Прочитать и разобрать JSON-файл пакета; ошибки чтения/синтаксиса — в <paramref name="issues"/>.</summary>
    internal static JsonDocument? OpenFile(LoadedPackage pkg, string file, List<ModIssue> issues)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(pkg.Directory, file));
            return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = ModLimits.MaxJsonDepth });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            issues.Add(new ModIssue(pkg.Id, file, "$", ModError.BadValue, e.GetType().Name));
            return null;
        }
    }

    /// <summary>Перевод <see cref="ScenarioException"/> разбора поля в <see cref="ModIssue"/> (путь — до первого «: »).</summary>
    internal static ModIssue FromException(LoadedPackage pkg, string file, ScenarioException e) =>
        FromException(pkg.Id, file, e);

    internal static ModIssue FromException(string package, string file, ScenarioException e)
    {
        string msg = e.Message;
        int sep = msg.IndexOf(": ", StringComparison.Ordinal);
        string path = sep > 0 ? msg[..sep] : "$";
        string detail = sep > 0 ? msg[(sep + 2)..] : msg;
        ModError code =
            detail.StartsWith("unknown key", StringComparison.Ordinal) ? ModError.UnknownKey :
            detail.StartsWith("required key", StringComparison.Ordinal) ? ModError.MissingKey :
            detail.StartsWith("expected", StringComparison.Ordinal) && !detail.Contains(" or ", StringComparison.Ordinal) ? ModError.BadType :
            detail.Contains("out of range", StringComparison.Ordinal) ? ModError.OutOfRange :
            ModError.BadValue;
        return new ModIssue(package, file, path, code, detail);
    }
}
