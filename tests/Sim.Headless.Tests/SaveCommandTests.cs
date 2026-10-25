#pragma warning disable CA1861 // тест: литералы-массивы аргументов допустимы
using System;
using System.Globalization;
using System.IO;
using Sim.Headless.Bench;

namespace Sim.Headless.Tests;

/// <summary>T12: <c>save</c> -> <c>load --ticks N</c> печатает те же хэши, что <c>hash</c> без сейва.</summary>
public sealed class SaveCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tc-save-" + Guid.NewGuid().ToString("N"));

    public SaveCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    public void SaveThenLoad_PrintsSameHashesAsUninterruptedRun(int saveThreads, int loadThreads)
    {
        string file = Path.Combine(_dir, "s.tcsave");
        var o1 = new StringWriter();
        Assert.Equal(0, SaveCommands.RunSave(new[] { "save", "--scenario", "s1k", "--ticks", "600", "--out", file, "--threads", saveThreads.ToString(CultureInfo.InvariantCulture) }, o1, new StringWriter()));
        Assert.Contains("saved bytes=", o1.ToString(), StringComparison.Ordinal);
        Assert.True(new FileInfo(file).Length > 1000);

        var o2 = new StringWriter();
        Assert.Equal(0, SaveCommands.RunLoad(new[] { "load", "--in", file, "--ticks", "400", "--every", "200", "--threads", loadThreads.ToString(CultureInfo.InvariantCulture) }, o2, new StringWriter()));
        string[] loadLines = o2.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("loaded tick=600 ", loadLines[0], StringComparison.Ordinal);

        var o3 = new StringWriter();
        Assert.Equal(0, HashCommand.Run(new[] { "hash", "--scenario", "s1k", "--ticks", "1000", "--every", "200" }, o3, new StringWriter()));
        string[] hashLines = o3.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(hashLines[3].Trim(), loadLines[1].Trim());   // tick=800
        Assert.Equal(hashLines[4].Trim(), loadLines[2].Trim());   // tick=1000
    }

    [Fact]
    public void Load_RejectsGarbage_WithExitCode()
    {
        string file = Path.Combine(_dir, "junk.tcsave");
        File.WriteAllBytes(file, new byte[200]);
        var e = new StringWriter();
        Assert.Equal(SaveCommands.ExitFailed, SaveCommands.RunLoad(new[] { "load", "--in", file }, new StringWriter(), e));
        Assert.Contains("NotASave", e.ToString(), StringComparison.Ordinal);
    }
}
