using System;
using Godot;

namespace TrafficCity;

/// <summary>
/// Пакет инстансов одного меша на одном MultiMeshInstance3D: поворот только вокруг Y, цвет на инстанс.
/// Буфер пишется целиком одним вызовом (<c>MultiMesh.Buffer</c>): на инстанс 12 float трансформа
/// (3 строки по 4: базис по столбцам + origin) и 4 float цвета.
/// Размер буфера и <c>InstanceCount</c> привязаны к числу записанных инстансов (render.md §5.1): ёмкость = ближайшее
/// кратное <see cref="Granule"/> с запасом 25%, растёт по требованию, сжимается, только когда занято меньше половины
/// (без дрожания); в стационаре 0 управляемых аллокаций.
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлу сцены и освобождаются вместе с ним
public sealed class BoxBatch
{
    private const int Stride = 16;
    private const int Granule = 256;

    private readonly MultiMesh _mm;
    private float[] _buf = Array.Empty<float>();
    private int _capacity;
    private int _count;
    private int _hint;

    public BoxBatch(string name, Vector3 size, bool unshaded)
        : this(name, new BoxMesh { Size = size }, unshaded)
    {
    }

    public BoxBatch(string name, Mesh mesh, bool unshaded)
        : this(name, mesh, new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            ShadingMode = unshaded ? BaseMaterial3D.ShadingModeEnum.Unshaded : BaseMaterial3D.ShadingModeEnum.PerPixel,
            Roughness = 0.7f,
        })
    {
    }

    public BoxBatch(string name, Mesh mesh, Material material)
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
            MaterialOverride = material,
        };
        Resize(Granule); // минимальная ёмкость сразу: первый инстанс не должен аллоцировать посреди кадра
    }

    public MultiMeshInstance3D Node { get; }

    /// <summary>Число инстансов, записанных в этом кадре.</summary>
    public int Count => _count;

    /// <summary>Подготовка кадра: сбрасывает счётчик; <paramref name="needed"/> - подсказка (ёмкость под ожидаемое число, чтобы не расти по одному).</summary>
    public void Begin(int needed)
    {
        _hint = needed;
        if (needed > _capacity)
        {
            Resize(RoundUp(needed + (needed / 4)));
        }

        _count = 0;
    }

    /// <summary>Инстанс без масштаба.</summary>
    public void Add(float x, float y, float z, float heading, Color color) => Add(x, y, z, heading, color, 1f, 1f, 1f);

    /// <summary>Инстанс с равномерным масштабом.</summary>
    public void Add(float x, float y, float z, float heading, Color color, float scale) => Add(x, y, z, heading, color, scale, scale, scale);

    /// <summary>Добавить инстанс: центр (x, y, z), курс <paramref name="heading"/> (atan2(dz, dx), рад), цвет, масштаб по осям меша (вдоль курса, вверх, поперёк).</summary>
    public void Add(float x, float y, float z, float heading, Color color, float sx, float sy, float sz)
    {
        if (_count >= _capacity)
        {
            Resize(RoundUp(_capacity + (_capacity / 2) + 1));
        }

        float c = MathF.Cos(heading);
        float s = MathF.Sin(heading);
        int o = _count * Stride;
        float[] b = _buf;
        // вращение вокруг Y на -heading: локальная +X смотрит вдоль (cos h, 0, sin h)
        b[o] = c * sx;
        b[o + 1] = 0f;
        b[o + 2] = -s * sz;
        b[o + 3] = x;
        b[o + 4] = 0f;
        b[o + 5] = sy;
        b[o + 6] = 0f;
        b[o + 7] = y;
        b[o + 8] = s * sx;
        b[o + 9] = 0f;
        b[o + 10] = c * sz;
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
        if (_count > _capacity || Math.Max(_count, _hint) * 2 < _capacity)
        {
            Resize(RoundUp(Math.Max(_count, _hint) + (Math.Max(_count, _hint) / 4)));
        }

        if (_capacity > 0)
        {
            _mm.Buffer = _buf;
        }

        _mm.VisibleInstanceCount = _count;
    }

    private static int RoundUp(int n) => Math.Max(Granule, (n + Granule - 1) / Granule * Granule);

    private void Resize(int capacity)
    {
        if (capacity == _capacity)
        {
            return;
        }

        Array.Resize(ref _buf, capacity * Stride);
        _capacity = capacity;
        _mm.InstanceCount = capacity;
    }
}
