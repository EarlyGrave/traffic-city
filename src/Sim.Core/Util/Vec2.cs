namespace Sim.Core.Util;

/// <summary>Точка/вектор в плоскости земли (X, Z); Y — высота — хранится отдельно.</summary>
public readonly struct Vec2
{
    public readonly float X;
    public readonly float Z;

    public Vec2(float x, float z)
    {
        X = x;
        Z = z;
    }

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Z + b.Z);

    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Z - b.Z);

    public static Vec2 operator *(Vec2 a, float k) => new(a.X * k, a.Z * k);
}
