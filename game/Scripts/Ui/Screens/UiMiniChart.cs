using System;
using Godot;
using TrafficCity.Ui.Kit;

namespace TrafficCity.Ui;

/// <summary>
/// Маленький линейный график панели «Город» (UI16): до <see cref="Capacity"/> точек, пропуски (нет данных) рвут линию. Точки и вершины - в заранее
/// выделенных массивах; перерисовка только по <see cref="SetValues"/>. Подписи осей - снаружи (текстом), здесь только линия, нулевая и опорная.
/// </summary>
public sealed partial class UiMiniChart : Control
{
    public const int Capacity = 32;

    private readonly double[] _v = new double[Capacity];
    private readonly bool[] _has = new bool[Capacity];
    private readonly Vector2[] _seg = new Vector2[Capacity];
    private int _count;
    private int _slots = 24;
    private double _min;
    private double _max = 1;
    private double _ref = double.NaN;

    /// <summary>Заполнить ряд: <paramref name="count"/> точек справа налево, слоты оси - <paramref name="slots"/> (24 часа / 32 суток).</summary>
    public void Begin(int slots)
    {
        _slots = Math.Clamp(slots, 2, Capacity);
        _count = 0;
    }

    public void Add(double value, bool has)
    {
        if (_count < Capacity)
        {
            _v[_count] = value;
            _has[_count] = has;
            _count++;
        }
    }

    /// <summary>Закончить ряд: шкала по данным (с нулём, если <paramref name="fromZero"/>), опорная линия (порог ворот) - NaN, если нет.</summary>
    public void SetValues(bool fromZero, double reference)
    {
        _ref = reference;
        double lo = double.MaxValue, hi = double.MinValue;
        for (int i = 0; i < _count; i++)
        {
            if (_has[i])
            {
                lo = Math.Min(lo, _v[i]);
                hi = Math.Max(hi, _v[i]);
            }
        }

        if (!double.IsNaN(reference))
        {
            lo = Math.Min(lo, reference);
            hi = Math.Max(hi, reference);
        }

        if (lo > hi)
        {
            lo = 0;
            hi = 1;
        }

        if (fromZero)
        {
            lo = Math.Min(lo, 0);
            hi = Math.Max(hi, 0);
        }

        if (hi - lo < 1e-9)
        {
            hi = lo + 1;
        }

        double pad = (hi - lo) * 0.08;
        _min = lo - (fromZero && lo >= 0 ? 0 : pad);
        _max = hi + pad;
        QueueRedraw();
    }

    public override void _Draw()
    {
        Vector2 size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), Tokens.Ink, false, 1f);
        if (_min < 0 && _max > 0)
        {
            float y0 = Y(0, size.Y);
            DrawLine(new Vector2(0, y0), new Vector2(size.X, y0), Tokens.InkSecondary, 1f);
        }

        if (!double.IsNaN(_ref))
        {
            float yr = Y(_ref, size.Y);
            DrawDashedLine(new Vector2(0, yr), new Vector2(size.X, yr), Tokens.Danger.Darkened(0.35f), 2f, 5f);
        }

        // точки прижаты вправо: последняя - у правого края
        float step = size.X / (_slots - 1);
        int start = _slots - _count;
        int n = 0;
        for (int i = 0; i < _count; i++)
        {
            if (!_has[i])
            {
                Flush(n);
                n = 0;
                continue;
            }

            _seg[n++] = new Vector2((start + i) * step, Y(_v[i], size.Y));
        }

        Flush(n);
    }

    private void Flush(int n)
    {
        if (n == 1)
        {
            DrawCircle(_seg[0], 3.5f, Tokens.Ink);
            DrawCircle(_seg[0], 2f, Tokens.Accent);
        }
        else if (n > 1)
        {
            // DrawPolyline берёт массив целиком: рисуем отрезками, чтобы не копировать хвост
            for (int k = 1; k < n; k++)
            {
                DrawLine(_seg[k - 1], _seg[k], Tokens.Ink, 2.5f, true);
            }
        }
    }

    private float Y(double v, float h) => (float)(h - 2 - ((v - _min) / (_max - _min) * (h - 4)));
}
