using System;
using Sim.Headless.Render;

namespace Sim.Headless.Tests.Render;

public class RenderOptionsTests
{
    private static bool Parse(string line, out RenderOptions o, out string err) =>
        RenderOptions.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries), out o, out err);

    [Fact]
    public void MinimalArgs_UseDefaults()
    {
        Assert.True(Parse("render --scenario demo-grid --seconds 10 --out a.mp4", out RenderOptions o, out string err), err);
        Assert.Equal("demo-grid", o.Scenario);
        Assert.Equal(10.0, o.Seconds);
        Assert.Equal(20, o.Fps);
        Assert.Equal(1.0, o.Speed);
        Assert.Equal(1280, o.Width);
        Assert.Equal(1280, o.Height);
        Assert.Null(o.View);
        Assert.Equal(0.0, o.WarmupSeconds);
        Assert.Equal("a.mp4", o.Out);
        Assert.Equal(200, o.FrameCount);
    }

    [Fact]
    public void AllOptions_AreParsed()
    {
        Assert.True(
            Parse("render --scenario s1k --seconds 30.5 --fps 30 --speed 2 --width 640 --height 480 --view -10,-20.5,300,400 --warmup-s 60 --frames-dir d", out RenderOptions o, out string err),
            err);
        Assert.Equal(30, o.Fps);
        Assert.Equal(2.0, o.Speed);
        Assert.Equal(640, o.Width);
        Assert.Equal(480, o.Height);
        Assert.Equal(new[] { -10f, -20.5f, 300f, 400f }, o.View);
        Assert.Equal(60.0, o.WarmupSeconds);
        Assert.Equal("d", o.FramesDir);
        Assert.Equal((int)(30.5 * 30 / 2), o.FrameCount);
    }

    [Theory]
    [InlineData("render --seconds 10 --out a.mp4", "--scenario")]
    [InlineData("render --scenario x --out a.mp4", "--seconds")]
    [InlineData("render --scenario x --seconds 10", "--out")]
    [InlineData("render --scenario x --seconds abc --out a.mp4", "not a number")]
    [InlineData("render --scenario x --seconds 0 --out a.mp4", "> 0")]
    [InlineData("render --scenario x --seconds 5 --out a.mp4 --fps 0", "--fps")]
    [InlineData("render --scenario x --seconds 5 --out a.mp4 --width 641", "even")]
    [InlineData("render --scenario x --seconds 5 --out a.mp4 --view 1,2,3", "--view")]
    [InlineData("render --scenario x --seconds 5 --out a.mp4 --view 5,0,1,10", "x1 > x0")]
    [InlineData("render --scenario x --seconds 5 --out a.mp4 --bogus 1", "unknown option")]
    [InlineData("render --scenario x --seconds 5 --out", "requires a value")]
    [InlineData("render --scenario x --seconds 5 --speed -1 --out a.mp4", "--speed")]
    public void InvalidArgs_AreRejectedWithMessage(string line, string expected)
    {
        Assert.False(Parse(line, out _, out string err));
        Assert.Contains(expected, err);
    }
}
