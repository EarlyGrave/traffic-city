using Sim.Headless.Render;

namespace Sim.Headless.Tests.Render;

public class CanvasTests
{
    private static readonly Rgb _bg = new(0, 0, 0);
    private static readonly Rgb _fg = new(200, 100, 50);

    private static Canvas Blank(int w = 16, int h = 16)
    {
        var c = new Canvas(w, h);
        c.Clear(_bg);
        return c;
    }

    [Fact]
    public void AxisAlignedRect_FillsExactlyPixelsWhoseCentersAreInside()
    {
        Canvas c = Blank();
        c.FillOrientedRect(5f, 4f, 1f, 0f, 6f, 4f, _fg); // x in [2,8), y in [2,6)
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                bool inside = x >= 2 && x < 8 && y >= 2 && y < 6;
                Assert.Equal(inside ? _fg : _bg, c.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void RotatedRect_NinetyDegrees_SwapsExtents()
    {
        Canvas c = Blank();
        c.FillOrientedRect(8f, 8f, 0f, 1f, 6f, 2f, _fg); // long axis vertical: x in [7,9), y in [5,11)
        Assert.Equal(_fg, c.GetPixel(7, 5));
        Assert.Equal(_fg, c.GetPixel(8, 10));
        Assert.Equal(_bg, c.GetPixel(6, 8));
        Assert.Equal(_bg, c.GetPixel(9, 8));
        Assert.Equal(_bg, c.GetPixel(8, 4));
        Assert.Equal(_bg, c.GetPixel(8, 11));
    }

    [Fact]
    public void ThickHorizontalLine_CoversExpectedRows()
    {
        Canvas c = Blank();
        c.FillThickLine(2f, 8f, 12f, 8f, 2f, _fg); // y in [7,9), x in [2,12)
        for (int x = 2; x < 12; x++)
        {
            Assert.Equal(_fg, c.GetPixel(x, 7));
            Assert.Equal(_fg, c.GetPixel(x, 8));
            Assert.Equal(_bg, c.GetPixel(x, 6));
            Assert.Equal(_bg, c.GetPixel(x, 9));
        }

        Assert.Equal(_bg, c.GetPixel(1, 8));
        Assert.Equal(_bg, c.GetPixel(12, 8));
    }

    [Fact]
    public void DiagonalThinLine_HasNoGapsPerColumn()
    {
        Canvas c = Blank(32, 32);
        c.FillThickLine(2.5f, 2.5f, 28.5f, 20.5f, 1f, _fg);
        for (int x = 4; x < 27; x++)
        {
            bool any = false;
            for (int y = 0; y < 32; y++)
            {
                any |= c.GetPixel(x, y) == _fg;
            }

            Assert.True(any, $"column {x} has no pixel");
        }
    }

    [Fact]
    public void Drawing_OutsideCanvas_IsClippedWithoutThrowing()
    {
        Canvas c = Blank();
        c.FillOrientedRect(-100f, -100f, 1f, 0f, 10f, 10f, _fg);
        c.FillThickLine(-50f, 8f, 500f, 8f, 3f, _fg);
        c.FillCircle(100f, 100f, 5f, _fg);
        Assert.Equal(_fg, c.GetPixel(0, 8));
        Assert.Equal(_fg, c.GetPixel(15, 8));
    }

    [Fact]
    public void Circle_IsSymmetricAndCoversCenter()
    {
        Canvas c = Blank();
        c.FillCircle(8f, 8f, 3f, _fg);
        Assert.Equal(_fg, c.GetPixel(8, 8));
        Assert.Equal(_fg, c.GetPixel(7, 7));
        Assert.Equal(_bg, c.GetPixel(8, 12));
        Assert.Equal(c.GetPixel(5, 8), c.GetPixel(10, 8));
    }

    [Fact]
    public void DrawText_RendersGlyphPixels()
    {
        Canvas c = Blank();
        c.DrawText(0, 0, "I", _fg); // top row of 'I' = 01110 -> x 1..3
        Assert.Equal(_bg, c.GetPixel(0, 0));
        Assert.Equal(_fg, c.GetPixel(1, 0));
        Assert.Equal(_fg, c.GetPixel(3, 0));
        Assert.Equal(_bg, c.GetPixel(4, 0));
        Assert.Equal(_fg, c.GetPixel(2, 3));
        Assert.Equal(17, Canvas.TextWidth("ABC"));
    }
}
