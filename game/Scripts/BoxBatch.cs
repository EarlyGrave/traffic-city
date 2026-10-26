using System;
using Godot;

namespace TrafficCity;

/// <summary>
/// Пакет коробок-инстансов на одном MultiMeshInstance3D: поворот только вокруг Y, цвет на инстанс.
/// Буфер пишется целиком одним вызовом (<c>MultiMesh.Buffer</c>): на инстанс 12 float трансформа
/// (3 строки по 4: базис по столбцам + origin) и 4 float цвета.
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлу сцены и освобождаются вместе с ним
public sealed class BoxBatch
{
    private const int Stride = 16;

    private readonly MultiMesh _mm;
    private float[] _buf = Array.Empty<float>();
    private int _capacity;
    private int _count;

    public BoxBatch(string name, Vector3 size, bool unshaded)
        : this(name, new BoxMesh { Size = size }, unshaded)
    {
    }

    public BoxBatch(string name, Mesh mesh, bool unshaded)
    {
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            CustomAabb = new Aabb(new Vector3(-100000f, -50f, -100000f), new Vector3(200000f, 200f, 200000f)),
        };
        Node = new MultiMeshInstance3D
        {
            Name = name,
            Multimesh = _mm,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                VertexColorUseAsAlbedo = true,
                ShadingMode = unshaded ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
                Roughness = 0.7f,
            },
        };
    }

    public MultiMeshInstance3D Node { get; }

    /// <summary>Подготовка кадра: гарантирует ёмкость на <paramref name="needed"/> инстансов и сбрасывает счётчик.</summary>
    public void Begin(int needed)
    {
        if (needed > _capacity)
        {
            _capacity = Math.Max(needed + (needed / 2), 256);
            _buf = new float[_capacity * Stride];
            _mm.InstanceCount = _capacity;
        }

        _count = 0;
    }

    /// <summary>Добавить инстанс: центр (x, y, z), курс <paramref name="heading"/> (atan2(dz, dx), рад), цвет, равномерный масштаб (дальний план).</summary>
    public void Add(float x, float y, float z, float heading, Color color, float scale = 1f)
    {
        float c = MathF.Cos(heading) * scale;
        float s = MathF.Sin(heading) * scale;
        int o = _count * Stride;
        float[] b = _buf;
        // вращение вокруг Y на -heading: локальная +X смотрит вдоль (cos h, 0, sin h)
        b[o] = c;
        b[o + 1] = 0f;
        b[o + 2] = -s;
        b[o + 3] = x;
        b[o + 4] = 0f;
        b[o + 5] = scale;
        b[o + 6] = 0f;
        b[o + 7] = y;
        b[o + 8] = s;
        b[o + 9] = 0f;
        b[o + 10] = c;
        b[o + 11] = z;
        b[o + 12] = color.R;
        b[o + 13] = color.G;
        b[o + 14] = color.B;
        b[o + 15] = color.A;
        _count++;
    }

    /// <summary>Отправить буфер в движок и показать ровно записанные инстансы.</summary>
    public void End()
    {
        if (_capacity > 0)
        {
            _mm.Buffer = _buf;
        }

        _mm.VisibleInstanceCount = _count;
    }
}
