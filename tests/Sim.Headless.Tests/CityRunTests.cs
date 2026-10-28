using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Headless.Bench;

namespace Sim.Headless.Tests;

/// <summary>Долгие прогоны города (Z5, zoning-demand.md §9.3, §11): <c>run --days/--years</c>, JSON <c>city</c>, <c>bench --warmup-days --window am_peak</c>, отказ <c>compare</c> от разных окон.</summary>
public sealed class CityRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tc-cityrun-" + Guid.NewGuid().ToString("N"));

    public CityRunTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static string ScenariosDir()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            string candidate = Path.Combine(dir, "scenarios");
            if (File.Exists(Path.Combine(candidate, "s1k-res.json")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new DirectoryNotFoundException("scenarios dir not found");
    }

    /// <summary>Копия s1k-res с короткими сутками (576 с = 11 520 тиков) во временной папке: в Debug помещается в десятки секунд.</summary>
    private string ShortDayScenarios()
    {
        string src = ScenariosDir();
        string text = File.ReadAllText(Path.Combine(src, "s1k-res.json"));
        Assert.Contains("\"day_s\": 3600", text);
        File.WriteAllText(Path.Combine(_dir, "s1k-res.json"), text.Replace("\"day_s\": 3600", "\"day_s\": 576"));
        File.Copy(Path.Combine(src, "vehicle-types.json"), Path.Combine(_dir, "vehicle-types.json"));
        return _dir;
    }

    [Theory]
    [InlineData(false, 0, 0, 0, TripExecution.Agents)]
    [InlineData(false, 5, 1, 3, TripExecution.Agents)]
    [InlineData(true, 0, 1, 0, TripExecution.Agents)]
    [InlineData(true, 1, 1, 0, TripExecution.Aggregate)]
    [InlineData(true, 4, 1, 4, TripExecution.Agents)]
    [InlineData(true, 5, 1, 4, TripExecution.Aggregate)]
    [InlineData(true, 8, 2, 4, TripExecution.Agents)]
    [InlineData(true, 1, 2, 0, TripExecution.Agents)]
    public void ModeFor_CalibratesThenSamples(bool aggregate, int day, int calibrateDays, int sampleEvery, TripExecution expected) =>
        Assert.Equal(expected, CityRunCommand.ModeFor(day, aggregate, calibrateDays, sampleEvery));

    [Fact]
    public void Run_Days_Aggregate_WritesCityBlock()
    {
        string dir = ShortDayScenarios();
        string outPath = Path.Combine(_dir, "city.json");
        var o = new StringWriter();
        var e = new StringWriter();
        int code = RunCommand.Run(
            new[] { "run", "--scenario", "s1k-res", "--days", "3", "--exec", "aggregate", "--calibrate-days", "1", "--out", outPath, "--scenarios-dir", dir },
            o,
            e);
        Assert.True(code == 0, e.ToString());
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(outPath));
        JsonElement root = doc.RootElement;
        Assert.Equal("aggregate", root.GetProperty("exec").GetString());
        Assert.Equal(1, root.GetProperty("micro_days").GetInt64());
        Assert.Equal(2, root.GetProperty("aggregate_days").GetInt64());
        JsonElement city = root.GetProperty("city");
        Assert.Equal(0, city.GetProperty("money_sum").GetInt64());
        Assert.Equal(0, city.GetProperty("res_stuck").GetInt64());
        Assert.True(city.GetProperty("population").GetInt32() > 0);
        JsonElement hashes = city.GetProperty("day_hashes");
        Assert.Equal(3, hashes.GetArrayLength());
        Assert.Equal(3, hashes.EnumerateArray().Select(h => h.GetString()).Distinct().Count());
        JsonElement last = city.GetProperty("series").EnumerateArray().Last();
        Assert.Equal(3, last.GetProperty("day").GetInt64());
        Assert.Equal("aggregate", last.GetProperty("exec").GetString());
        Assert.Equal(13, last.GetProperty("flows_per_resident_day").GetArrayLength());

        // та же команда ещё раз: хэши суток совпадают (детерминизм прогона)
        string again = Path.Combine(_dir, "city2.json");
        Assert.Equal(
            0,
            RunCommand.Run(
                new[] { "run", "--scenario", "s1k-res", "--days", "3", "--exec", "aggregate", "--calibrate-days", "1", "--out", again, "--scenarios-dir", dir },
                new StringWriter(),
                new StringWriter()));
        using JsonDocument doc2 = JsonDocument.Parse(File.ReadAllText(again));
        Assert.Equal(
            hashes.EnumerateArray().Select(h => h.GetString()),
            doc2.RootElement.GetProperty("city").GetProperty("day_hashes").EnumerateArray().Select(h => h.GetString()));
    }

    [Fact]
    public void Run_DaysAndYears_AreExclusive_AndNeedACity()
    {
        var e = new StringWriter();
        Assert.Equal(2, RunCommand.Run(new[] { "run", "--scenario", "s1k", "--days", "1", "--years", "1", "--scenarios-dir", ScenariosDir() }, new StringWriter(), e));
        Assert.Contains("mutually exclusive", e.ToString());
        var e2 = new StringWriter();
        Assert.Equal(2, RunCommand.Run(new[] { "run", "--scenario", "s1k", "--days", "1", "--scenarios-dir", ScenariosDir() }, new StringWriter(), e2));
        Assert.Contains("residents", e2.ToString());
        Assert.Equal(2, RunCommand.Run(new[] { "run", "--scenario", "s1k-res", "--days", "1", "--exec", "bogus", "--scenarios-dir", ScenariosDir() }, new StringWriter(), new StringWriter()));
    }

    [Fact]
    public void Bench_WarmupDays_Window_WritesFields_AndCompareRefusesMixedWindows()
    {
        string dir = ShortDayScenarios();
        string withWindow = Path.Combine(_dir, "peak.json");
        var e = new StringWriter();
        int code = BenchCommand.Run(
            new[] { "bench", "--scenario", "s1k-res", "--ticks", "100", "--warmup-days", "1", "--window", "am_peak", "--out", withWindow, "--scenarios-dir", dir },
            new StringWriter(),
            e);
        Assert.True(code == 0, e.ToString());
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(withWindow));
        Assert.Equal("am_peak", doc.RootElement.GetProperty("window").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("warmup_days").GetInt32());
        Assert.Equal(100, doc.RootElement.GetProperty("ticks").GetInt32());
        Assert.True(doc.RootElement.GetProperty("perf").GetProperty("warmup_ticks_actual").GetInt32() > 11_520);

        string plain = Path.Combine(_dir, "plain.json");
        File.WriteAllText(plain, File.ReadAllText(withWindow).Replace("\"window\": \"am_peak\",", string.Empty));
        var o = new StringWriter();
        var err = new StringWriter();
        Assert.Equal(CompareCommand.ExitUsage, CompareCommand.Run(new[] { "compare", "--result", withWindow, "--baseline", plain }, o, err));
        Assert.Contains("measurement windows", err.ToString());
        Assert.NotEqual(CompareCommand.ExitUsage, CompareCommand.Run(new[] { "compare", "--result", withWindow, "--baseline", withWindow, "--tolerance", "10" }, o, err));
    }

    [Fact]
    public void Bench_UnknownWindow_IsAUsageError()
    {
        var e = new StringWriter();
        Assert.Equal(2, BenchCommand.Run(new[] { "bench", "--scenario", "s1k-res", "--window", "pm_peak", "--out", Path.Combine(_dir, "x.json"), "--scenarios-dir", ScenariosDir() }, new StringWriter(), e));
        Assert.Contains("am_peak", e.ToString());
    }
}
