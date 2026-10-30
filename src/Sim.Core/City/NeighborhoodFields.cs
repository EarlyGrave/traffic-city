using System;

namespace Sim.Core.City;

/// <summary>
/// Поля квартала (building-lifecycle.md §2.4, §2.6): <c>Heritage</c>, <c>Blight</c>, <c>Disorder</c>, <c>FireRisk</c> на своей сетке <c>lifecycle.cell_m</c> (128 м) поверх прямоугольника <see cref="CityGrid"/>.
/// Двойной буфер: стадия «здания» копит вклады в <see cref="Next"/>, <see cref="Swap"/> в <c>Growth.Daily</c> переносит их в <see cref="Cur"/> (O(клеток), не O(зданий)); читают только <see cref="Cur"/>.
/// Хранение - плоский <c>int[4 · клеток]</c> (клетка · 4 + поле). Отклонение от спеки: «обмен» - копирование и очистка, а не смена ссылок, чтобы сейв писал два фиксированных массива.
/// Пишется только в последовательных фазах главного потока, 0 Б/тик.
/// </summary>
internal sealed class NeighborhoodFields
{
    public const int Heritage = 0;
    public const int Blight = 1;
    public const int Disorder = 2;
    public const int FireRisk = 3;
    public const int Stride = 4;

    /// <summary>Лимит величины клетки (насыщение, чтобы не переполнить <c>int</c> суммой окна 3×3).</summary>
    private const int Cap = 1 << 24;

    private readonly float _originX;
    private readonly float _originZ;
    private readonly float _invCell;

    public readonly int Nx;
    public readonly int Nz;

    /// <summary>Действующие значения (читает всё остальное).</summary>
    public readonly int[] Cur;

    /// <summary>Накопитель текущего прохода по зданиям.</summary>
    public readonly int[] Next;

    public NeighborhoodFields(in CityGrid grid, int cellM)
    {
        _originX = grid.OriginX;
        _originZ = grid.OriginZ;
        _invCell = 1f / cellM;
        Nx = Math.Max(1, (int)Math.Ceiling(grid.CellsX * (double)grid.CellM / cellM));
        Nz = Math.Max(1, (int)Math.Ceiling(grid.CellsZ * (double)grid.CellM / cellM));
        Cur = new int[Nx * Nz * Stride];
        Next = new int[Nx * Nz * Stride];
    }

    public int Cells => Nx * Nz;

    /// <summary>Клетка поля для точки мира (зажим к краю сетки).</summary>
    public int CellOf(float x, float z)
    {
        int cx = Math.Clamp((int)MathF.Floor((x - _originX) * _invCell), 0, Nx - 1);
        int cz = Math.Clamp((int)MathF.Floor((z - _originZ) * _invCell), 0, Nz - 1);
        return (cz * Nx) + cx;
    }

    /// <summary>Вклад здания в накопитель прохода.</summary>
    public void Add(int cell, int field, int points) => AddTo(Next, cell, field, points);

    /// <summary>Вклад в произвольный массив той же раскладки (накопитель или пересчёт с нуля в тесте); насыщение <see cref="Cap"/>.</summary>
    public static void AddTo(int[] target, int cell, int field, int points)
    {
        int i = (cell * Stride) + field;
        target[i] = Math.Min(Cap, target[i] + points);
    }

    /// <summary>Конец прохода по зданиям: накопитель становится действующим значением и обнуляется.</summary>
    public void Swap()
    {
        Array.Copy(Next, Cur, Next.Length);
        Array.Clear(Next);
    }

    public int Value(int cell, int field) => Cur[(cell * Stride) + field];

    /// <summary>Окно 3×3 с весом 2 у своей клетки (§2.6): <c>2·F[own] + Σ₈ F</c>; за краем сетки клетки нет.</summary>
    public int Window(int cell, int field)
    {
        int cx = cell % Nx;
        int cz = cell / Nx;
        long sum = 2L * Cur[(cell * Stride) + field];
        for (int dz = -1; dz <= 1; dz++)
        {
            int z = cz + dz;
            if ((uint)z >= (uint)Nz)
            {
                continue;
            }

            for (int dx = -1; dx <= 1; dx++)
            {
                int x = cx + dx;
                if ((uint)x >= (uint)Nx || (dx == 0 && dz == 0))
                {
                    continue;
                }

                sum += Cur[(((z * Nx) + x) * Stride) + field];
            }
        }

        return (int)Math.Min(sum, int.MaxValue);
    }
}
