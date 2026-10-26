using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Сетка 1-км ячеек города (zoning-demand.md §5.1): квадратная сетка по bounding box узлов сети на момент <c>Create</c> (та же формула, что у LOD-ячеек, sim-lod.md §1),
/// считается и при <c>lod.mode = off</c>. После <c>BuildRoad</c> сетка не меняется (вне неё - зажим к краю). Параметры лежат в <c>PARM</c>.
/// </summary>
internal readonly struct CityGrid
{
    public CityGrid(float cellM, float originX, float originZ, int cellsX, int cellsZ)
    {
        CellM = cellM;
        OriginX = originX;
        OriginZ = originZ;
        CellsX = cellsX;
        CellsZ = cellsZ;
    }

    public float CellM { get; }

    public float OriginX { get; }

    public float OriginZ { get; }

    public int CellsX { get; }

    public int CellsZ { get; }

    public int Cells => CellsX * CellsZ;

    /// <summary>Предел числа ячеек (как у LOD).</summary>
    public const int MaxCells = 1 << 20;

    /// <summary>Сетка по узлам готовой сети; false - ячеек больше <see cref="MaxCells"/> или <paramref name="cellM"/> не положителен.</summary>
    public static bool TryCreate(NetworkStore net, float cellM, out CityGrid grid)
    {
        grid = default;
        if (!float.IsFinite(cellM) || cellM <= 0f)
        {
            return false;
        }

        float minX = 0f, minZ = 0f, maxX = 0f, maxZ = 0f;
        for (int n = 0; n < net.NodeCount; n++)
        {
            Vec2 p = net.Nodes[n].Position;
            if (n == 0)
            {
                minX = maxX = p.X;
                minZ = maxZ = p.Z;
                continue;
            }

            minX = MathF.Min(minX, p.X);
            maxX = MathF.Max(maxX, p.X);
            minZ = MathF.Min(minZ, p.Z);
            maxZ = MathF.Max(maxZ, p.Z);
        }

        double nx = Math.Max(1.0, Math.Ceiling((maxX - minX) / cellM));
        double nz = Math.Max(1.0, Math.Ceiling((maxZ - minZ) / cellM));
        if (nx * nz > MaxCells)
        {
            return false;
        }

        grid = new CityGrid(cellM, minX, minZ, DetMath.ToIntSat(nx), DetMath.ToIntSat(nz));
        return true;
    }

    public int CellXOf(float x) => Clamp(MathF.Floor((x - OriginX) / CellM), CellsX);

    public int CellZOf(float z) => Clamp(MathF.Floor((z - OriginZ) / CellM), CellsZ);

    public int CellOf(float x, float z) => (CellZOf(z) * CellsX) + CellXOf(x);

    /// <summary>Центр ячейки, м.</summary>
    public void CenterOf(int cell, out float x, out float z)
    {
        x = OriginX + (((cell % CellsX) + 0.5f) * CellM);
        z = OriginZ + (((cell / CellsX) + 0.5f) * CellM);
    }

    private static int Clamp(float v, int n) => !(v > 0f) ? 0 : (v >= n ? n - 1 : (int)v);
}
