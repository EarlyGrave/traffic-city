using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Gen;
using Sim.Headless.Golden;
using Sim.Scenarios;

namespace Sim.Core.Tests.Network;

/// <summary>
/// Кросс-языковой golden Rust-пилота (rust-pilot.md §5): файлы <c>tests/golden/rust-pilot/*.json</c> совпадают с тем, что считает C# сейчас.
/// Сдвиг байт сети, конфликтов или разбора сценария краснит тест; обновление -
/// <c>dotnet run --project src/Sim.Headless -- golden --out tests/golden/rust-pilot</c> (отдельным коммитом, <c>golden_version++</c> при смене формата).
/// </summary>
public sealed class RustPilotGoldenTests
{
    private static readonly string _root = GenFixture.RepoRoot();
    private static readonly string _dir = Path.Combine(_root, "tests", "golden", "rust-pilot");

    public static IEnumerable<object[]> ScenarioFiles()
    {
        foreach (string p in RustPilotGolden.ScenarioPaths(_root, _dir))
        {
            yield return [p];
        }
    }

    [Fact]
    public void UtilJson_MatchesGeneratedVectors()
    {
        Assert.Equal(RustPilotGolden.UtilJson(_root), ReadGolden("util.json"));
    }

    [Fact]
    public void IdmPowJson_MatchesGeneratedVectors()
    {
        // MathF.Pow при delta не из {2, 4} - системный libm (glibc/UCRT), между ОС расходится (rust-pilot.md §6.3): строго только при TC_STRICT_HEADINGS=1.
        if (Environment.GetEnvironmentVariable("TC_STRICT_HEADINGS") != "1")
        {
            return;
        }

        Assert.Equal(RustPilotGolden.IdmPowJson(_root), ReadGolden("idm-pow.json"));
    }

    [Theory]
    [MemberData(nameof(ScenarioFiles))]
    public void ScenarioGolden_MatchesCurrentCore(string scenarioPath)
    {
        string name = RustPilotGolden.FileNameFor(scenarioPath);
        string actual = RustPilotGolden.ScenarioJson(_root, scenarioPath);
        string expected = ReadGolden(name);

        // headings_hash - системный atan2 (glibc/UCRT), между ОС расходится (rust-pilot.md §6.3): строго только при TC_STRICT_HEADINGS=1.
        if (Environment.GetEnvironmentVariable("TC_STRICT_HEADINGS") != "1")
        {
            actual = MaskHeadings(actual);
            expected = MaskHeadings(expected);
        }

        if (actual != expected)
        {
            Assert.Fail($"{name}: golden не совпадает с ядром.\n--- файл ---\n{expected}\n--- ядро ---\n{actual}\n" +
                "Если изменение осознанное: golden --out tests/golden/rust-pilot, отдельным коммитом.");
        }
    }

    [Theory]
    [MemberData(nameof(ScenarioFiles))]
    public void ScenarioGolden_PublicHashAndStatus_MatchFile(string scenarioPath)
    {
        using JsonDocument doc = JsonDocument.Parse(ReadGolden(RustPilotGolden.FileNameFor(scenarioPath)));
        JsonElement r = doc.RootElement;
        Assert.Equal(RustPilotGolden.GoldenVersion, r.GetProperty("golden_version").GetInt32());
        Assert.Equal(scenarioPath, r.GetProperty("scenario").GetString());
        if (!r.GetProperty("load_ok").GetBoolean())
        {
            Assert.ThrowsAny<ScenarioException>(() => ScenarioLoader.Load(Path.Combine(_root, scenarioPath)));
            return;
        }

        ScenarioDesc desc = ScenarioLoader.Load(Path.Combine(_root, scenarioPath));
        CreateStatus st = Simulation.TryCreate(in desc, out Simulation? sim);
        Assert.Equal(r.GetProperty("create_status").GetInt32(), (int)st);
        if (st != CreateStatus.Ok)
        {
            Assert.False(r.TryGetProperty("network_hash", out _));
            return;
        }

        using (sim)
        {
            Assert.Equal(r.GetProperty("network_hash").GetString(), sim!.ComputeNetworkHash().ToString("x16"));

            // Rust строит сеть без светофоров и зон: эти поля узлов в состоянии «после построения сети» равны -1.
            NetworkView nv = sim.Network;
            for (int i = 0; i < nv.Nodes.Length; i++)
            {
                Assert.Equal(-1, nv.Nodes[i].Signal);
                Assert.Equal(-1, nv.Nodes[i].Zone);
            }
        }
    }

    [Fact]
    public void GoldenSet_CoversRequiredScenarios()
    {
        var names = new HashSet<string>();
        foreach (string p in RustPilotGolden.ScenarioPaths(_root, _dir))
        {
            names.Add(Path.GetFileNameWithoutExtension(p));
        }

        foreach (string need in new[] { "demo-grid", "net-skew", "net-star5", "net-oneway", "net-signal", "net-big", "err-snap", "err-short", "err-caps", "err-json", "net-profiles", "err-profile-width", "err-profile-nocurb", "err-profile-lane-order", "err-profile-lane-width", "err-profile-mm", "err-profile-ref", "err-profile-lanes" })
        {
            Assert.Contains(need, names);
        }
    }

    [Fact]
    public void NetworkHash_IsStable_AndIgnoresRuntimeState()
    {
        ScenarioDesc desc = ScenarioLoader.Load(Path.Combine(_root, RustPilotGolden.DemoGrid));
        using Simulation a = Simulation.Create(in desc);
        using Simulation b = Simulation.Create(in desc);
        ulong h0 = a.ComputeNetworkHash();
        Assert.NotEqual(0UL, h0);
        Assert.Equal(h0, b.ComputeNetworkHash());
        ulong state0 = a.ComputeStateHash();
        a.Step(40);
        Assert.NotEqual(state0, a.ComputeStateHash());
        Assert.Equal(h0, a.ComputeNetworkHash());
    }

    private static string ReadGolden(string file) => File.ReadAllText(Path.Combine(_dir, file));

    private static string MaskHeadings(string json) =>
        Regex.Replace(json, "\"headings_hash\": \"[0-9a-f]+\"", "\"headings_hash\": \"*\"");
}
