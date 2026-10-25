namespace Sim.Scenarios.Mods;

/// <summary>Одна ошибка (или предупреждение) пакета: пакет, файл внутри него, JSON-путь, код, пояснение.</summary>
public readonly record struct ModIssue(string Package, string File, string JsonPath, ModError Code, string Detail)
{
    public override string ToString() =>
        $"{Package}:{File}{(JsonPath.Length > 0 ? " " + JsonPath : string.Empty)}: {Code}{(Detail.Length > 0 ? " (" + Detail + ")" : string.Empty)}";
}
