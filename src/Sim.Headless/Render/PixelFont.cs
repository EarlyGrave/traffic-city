using System;

namespace Sim.Headless.Render;

/// <summary>Пиксельный шрифт 5x7 (латиница без учёта регистра, цифры, немного пунктуации) для HUD.</summary>
public static class PixelFont
{
    public const int GlyphWidth = 5;
    public const int GlyphHeight = 7;

    private static readonly byte[] _blank = new byte[GlyphHeight];

    private static readonly byte[]?[] _table = Build();

    /// <summary>7 строк глифа, младшие 5 бит; бит 4 - левый столбец. Неизвестный символ - пусто.</summary>
    public static ReadOnlySpan<byte> Glyph(char c)
    {
        char u = char.ToUpperInvariant(c);
        return u < _table.Length && _table[u] is { } g ? g : _blank;
    }

    private static byte[]?[] Build()
    {
        var t = new byte[]?[128];
        t['0'] = new byte[] { 14, 17, 19, 21, 25, 17, 14 };
        t['1'] = new byte[] { 4, 12, 4, 4, 4, 4, 14 };
        t['2'] = new byte[] { 14, 17, 1, 2, 4, 8, 31 };
        t['3'] = new byte[] { 30, 1, 1, 14, 1, 1, 30 };
        t['4'] = new byte[] { 2, 6, 10, 18, 31, 2, 2 };
        t['5'] = new byte[] { 31, 16, 30, 1, 1, 17, 14 };
        t['6'] = new byte[] { 6, 8, 16, 30, 17, 17, 14 };
        t['7'] = new byte[] { 31, 1, 2, 4, 8, 8, 8 };
        t['8'] = new byte[] { 14, 17, 17, 14, 17, 17, 14 };
        t['9'] = new byte[] { 14, 17, 17, 15, 1, 2, 12 };
        t['A'] = new byte[] { 14, 17, 17, 31, 17, 17, 17 };
        t['B'] = new byte[] { 30, 17, 17, 30, 17, 17, 30 };
        t['C'] = new byte[] { 14, 17, 16, 16, 16, 17, 14 };
        t['D'] = new byte[] { 30, 17, 17, 17, 17, 17, 30 };
        t['E'] = new byte[] { 31, 16, 16, 30, 16, 16, 31 };
        t['F'] = new byte[] { 31, 16, 16, 30, 16, 16, 16 };
        t['G'] = new byte[] { 14, 17, 16, 23, 17, 17, 15 };
        t['H'] = new byte[] { 17, 17, 17, 31, 17, 17, 17 };
        t['I'] = new byte[] { 14, 4, 4, 4, 4, 4, 14 };
        t['J'] = new byte[] { 7, 2, 2, 2, 2, 18, 12 };
        t['K'] = new byte[] { 17, 18, 20, 24, 20, 18, 17 };
        t['L'] = new byte[] { 16, 16, 16, 16, 16, 16, 31 };
        t['M'] = new byte[] { 17, 27, 21, 21, 17, 17, 17 };
        t['N'] = new byte[] { 17, 25, 21, 19, 17, 17, 17 };
        t['O'] = new byte[] { 14, 17, 17, 17, 17, 17, 14 };
        t['P'] = new byte[] { 30, 17, 17, 30, 16, 16, 16 };
        t['Q'] = new byte[] { 14, 17, 17, 17, 21, 18, 13 };
        t['R'] = new byte[] { 30, 17, 17, 30, 20, 18, 17 };
        t['S'] = new byte[] { 15, 16, 16, 14, 1, 1, 30 };
        t['T'] = new byte[] { 31, 4, 4, 4, 4, 4, 4 };
        t['U'] = new byte[] { 17, 17, 17, 17, 17, 17, 14 };
        t['V'] = new byte[] { 17, 17, 17, 17, 17, 10, 4 };
        t['W'] = new byte[] { 17, 17, 17, 21, 21, 21, 10 };
        t['X'] = new byte[] { 17, 17, 10, 4, 10, 17, 17 };
        t['Y'] = new byte[] { 17, 17, 10, 4, 4, 4, 4 };
        t['Z'] = new byte[] { 31, 1, 2, 4, 8, 16, 31 };
        t['.'] = new byte[] { 0, 0, 0, 0, 0, 12, 12 };
        t[':'] = new byte[] { 0, 12, 12, 0, 12, 12, 0 };
        t['-'] = new byte[] { 0, 0, 0, 31, 0, 0, 0 };
        t['/'] = new byte[] { 1, 1, 2, 4, 8, 16, 16 };
        t['%'] = new byte[] { 25, 26, 2, 4, 8, 11, 19 };
        t['='] = new byte[] { 0, 0, 31, 0, 31, 0, 0 };

        return t;
    }
}
