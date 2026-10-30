namespace Sim.Scenarios.Mods;

/// <summary>Пределы безопасности пакета (modding.md §7). Менять значения — сначала в спеке.</summary>
public static class ModLimits
{
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public const int MaxPackageFiles = 4096;
    public const long MaxJsonBytes = 8L * 1024 * 1024;
    public const int MaxJsonDepth = 32;
    public const int MaxJsonStringBytes = 1024;
    public const int MaxJsonArrayLength = 65_536;
    public const long MaxGltfBytes = 128L * 1024 * 1024;
    public const long MaxBinBytes = 128L * 1024 * 1024;
    public const long MaxTextureBytes = 64L * 1024 * 1024;
    public const int MaxTextureSide = 4096;
    public const int MaxAssetsPerPackage = 4096;
    public const int MaxPackagesInSet = 256;
    public const int MaxTimelineCommands = 100_000;
    public const int MaxTitleBytes = 128;
    public const int MaxAssetIdBytes = 128;

    /// <summary>Здания (zoning-demand.md §17): типов в наборе и вместимость одного здания.</summary>
    public const int MaxBuildingTypes = 512;
    public const int MaxBuildingCapacity = 2_000;

    /// <summary>Записи служб в наборе (services.md §2.2; = <c>CityServicesDesc.MaxRecords</c> ядра).</summary>
    public const int MaxServices = 32;

    /// <summary>Расширения, которые пакет может содержать и использовать.</summary>
    public static readonly string[] AllowedExtensions = [".json", ".gltf", ".glb", ".bin", ".png", ".jpg", ".jpeg", ".ktx2"];

    /// <summary>Расширения, которые допустимы, но игнорируются (документация).</summary>
    public static readonly string[] IgnoredExtensions = [".md", ".txt"];

    public static long MaxBytesFor(string extensionLower) => extensionLower switch
    {
        ".json" => MaxJsonBytes,
        ".gltf" or ".glb" => MaxGltfBytes,
        ".bin" => MaxBinBytes,
        ".png" or ".jpg" or ".jpeg" or ".ktx2" => MaxTextureBytes,
        _ => MaxJsonBytes,
    };
}
