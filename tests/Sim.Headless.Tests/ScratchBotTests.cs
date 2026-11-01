using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Sim.Headless.Bench;

namespace Sim.Headless.Tests;

/// <summary>Бот калибровки старта с нуля (Y5, <c>scratch-bot</c>): детерминизм, первые жители в первые игровые сутки, казна и res_stuck.</summary>
public class ScratchBotTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static string Run(string seed, int days)
    {
        var o = new StringWriter(CultureInfo.InvariantCulture);
        var e = new StringWriter(CultureInfo.InvariantCulture);
        int rc = ScratchBotCommand.Run(new[] { "scratch-bot", "--days", days.ToString(CultureInfo.InvariantCulture), "--seeds", seed, "--summary" }, o, e);
        Assert.Equal(0, rc);
        Assert.Equal("", e.ToString());
        return o.ToString();
    }

    private static double Field(string text, string name)
    {
        Match m = Regex.Match(text, name + @"=(-?[0-9.]+)");
        Assert.True(m.Success, $"в выводе нет {name}");
        return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    [Fact]
    public void ScratchBot_IsDeterministic_FirstResidentsInFirstGameDay_TreasuryPositive()
    {
        string a = Run("default", 3);
        output.WriteLine(a);
        Assert.Equal(a, Run("default", 3));
        Assert.InRange(Field(a, "first_resident_min"), 0.1, 60.0);   // сутки = 60 мин симуляции (day_s 3600)
        Assert.True(Field(a, "min_treasury") > 0);
        Assert.Equal(0, Field(a, "res_stuck"));
        Assert.Equal(0, Field(a, "insolvent"));
        Assert.True(Field(a, "pop_end") > 100);
    }
}
