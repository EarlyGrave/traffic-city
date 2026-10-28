using System;
using Godot;
using TrafficCity.Ui;

namespace TrafficCity;

/// <summary>
/// RTS-камера: панорама (WASD, края экрана, ПКМ-drag), зум колесом, вращение (Q/E, СКМ-drag).
/// Узел-pivot на земле; дочерняя Camera3D смотрит на него с расстояния <see cref="_distance"/>.
/// </summary>
public partial class RtsCamera : Node3D
{
    private Camera3D _camera = null!;
    private Vector3 _targetPos;
    private float _targetDistance = 600f;
    private float _distance = 600f;
    private float _yaw;
    private float _pitch = Mathf.DegToRad(55f);
    private bool _panning;
    private bool _rotating;

    /// <summary>Скорость панорамы, доля расстояния до земли в секунду.</summary>
    [Export]
    public float PanSpeed { get; set; } = 1.0f;

    /// <summary>Ширина зоны у края окна (px), в которой камера едет; 0 - выключено.</summary>
    [Export]
    public float EdgeScrollMargin { get; set; } = 4f;

    /// <summary>Скорость вращения Q/E, рад/с.</summary>
    [Export]
    public float RotateSpeed { get; set; } = 1.6f;

    [Export]
    public float MinDistance { get; set; } = 15f;

    [Export]
    public float MaxDistance { get; set; } = 8000f;

    public Camera3D Camera => _camera;

    /// <summary>Замеры (<c>--pose</c>): ввод и прокрутка у края окна отключены, камера не уезжает от заданной позы.</summary>
    public bool Locked { get; set; }

    /// <summary>Текущее расстояние камеры до точки на земле, м.</summary>
    public float Distance => _distance;

    /// <summary>Поставить камеру точно (аргумент запуска <c>--cam</c>): точка на земле, расстояние, наклон и поворот в градусах.</summary>
    public void SetPose(Vector3 center, float distance, float pitchDeg, float yawDeg)
    {
        _targetPos = center;
        _targetDistance = Math.Clamp(distance, MinDistance, MaxDistance);
        _distance = _targetDistance;
        _pitch = Mathf.DegToRad(Math.Clamp(pitchDeg, 15f, 89f));
        _yaw = Mathf.DegToRad(yawDeg);
        Apply();
    }

    /// <summary>Текущая цель позы (для сейва): точка на земле, расстояние, наклон и поворот в градусах - то, что принимает <see cref="SetPose"/>.</summary>
    public void GetPose(out Vector3 center, out float distance, out float pitchDeg, out float yawDeg)
    {
        center = _targetPos;
        distance = _targetDistance;
        pitchDeg = Mathf.RadToDeg(_pitch);
        yawDeg = Mathf.RadToDeg(_yaw);
    }

    public override void _Ready()
    {
        _camera = GetNode<Camera3D>("Camera3D");
        _camera.Near = 1f;
        _camera.Far = 40000f;
        _camera.Current = true;
        Apply();
    }

    /// <summary>Поставить камеру над точкой; <paramref name="extent"/> - характерный размер области, м.</summary>
    public void Focus(Vector3 center, float extent)
    {
        _targetPos = center;
        _targetDistance = Math.Clamp(extent * 1.1f, MinDistance, MaxDistance);
        _distance = _targetDistance;
        Apply();
    }

    public override void _Process(double delta)
    {
        if (Locked)
        {
            return;
        }

        float dt = (float)delta;
        Vector2 move = Vector2.Zero;
        if (Input.IsActionPressed(UiActions.CamForward))
        {
            move.Y -= 1;
        }

        if (Input.IsActionPressed(UiActions.CamBack))
        {
            move.Y += 1;
        }

        if (Input.IsActionPressed(UiActions.CamLeft))
        {
            move.X -= 1;
        }

        if (Input.IsActionPressed(UiActions.CamRight))
        {
            move.X += 1;
        }

        if (EdgeScrollMargin > 0f && !_panning && !_rotating)
        {
            move += EdgeScroll();
        }

        if (move != Vector2.Zero)
        {
            move = move.Normalized();
            float fast = Input.IsActionPressed(UiActions.CamFast) ? 3f : 1f;
            PanGround(move.X, move.Y, PanSpeed * _distance * fast * dt);
        }

        float rot = 0f;
        if (Input.IsActionPressed(UiActions.CamRotateLeft))
        {
            rot -= 1f;
        }

        if (Input.IsActionPressed(UiActions.CamRotateRight))
        {
            rot += 1f;
        }

        _yaw += rot * RotateSpeed * dt;

        // плавный зум
        float k = 1f - MathF.Exp(-12f * dt);
        _distance += (_targetDistance - _distance) * k;
        Apply();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (Locked)
        {
            return;
        }

        switch (@event)
        {
            case InputEventMouseButton mb:
                if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed)
                {
                    _targetDistance = Math.Clamp(_targetDistance * 0.88f, MinDistance, MaxDistance);
                }
                else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed)
                {
                    _targetDistance = Math.Clamp(_targetDistance / 0.88f, MinDistance, MaxDistance);
                }
                else if (mb.ButtonIndex == MouseButton.Right)
                {
                    _panning = mb.Pressed;
                }
                else if (mb.ButtonIndex == MouseButton.Middle)
                {
                    _rotating = mb.Pressed;
                }

                break;
            case InputEventMouseMotion mm:
                if (_panning)
                {
                    PanGround(-mm.Relative.X, -mm.Relative.Y, _distance * 0.0016f);
                }
                else if (_rotating)
                {
                    _yaw -= mm.Relative.X * 0.005f;
                    _pitch = Math.Clamp(_pitch + (mm.Relative.Y * 0.005f), Mathf.DegToRad(15f), Mathf.DegToRad(89f));
                }

                break;
            default:
                break;
        }
    }

    private Vector2 EdgeScroll()
    {
        Viewport vp = GetViewport();
        Vector2 mp = vp.GetMousePosition();
        Vector2 size = vp.GetVisibleRect().Size;
        var move = Vector2.Zero;
        if (mp.X < 0 || mp.Y < 0 || mp.X > size.X || mp.Y > size.Y)
        {
            return move;
        }

        if (mp.X < EdgeScrollMargin)
        {
            move.X -= 1;
        }
        else if (mp.X > size.X - EdgeScrollMargin)
        {
            move.X += 1;
        }

        if (mp.Y < EdgeScrollMargin)
        {
            move.Y -= 1;
        }
        else if (mp.Y > size.Y - EdgeScrollMargin)
        {
            move.Y += 1;
        }

        return move;
    }

    /// <summary>Сдвиг по земле: x - вправо, y - назад (экранные оси относительно yaw камеры).</summary>
    private void PanGround(float x, float y, float amount)
    {
        float sin = MathF.Sin(_yaw);
        float cos = MathF.Cos(_yaw);
        // вправо = (cos, 0, -sin); назад = (sin, 0, cos)
        _targetPos += new Vector3((x * cos) + (y * sin), 0f, (-x * sin) + (y * cos)) * amount;
    }

    private void Apply()
    {
        Position = _targetPos;
        Rotation = new Vector3(-_pitch, _yaw, 0f);
        _camera.Position = new Vector3(0f, 0f, _distance);
    }
}
