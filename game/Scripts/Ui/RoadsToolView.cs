using System;
using Godot;
using Sim.Core.Api;
using Sim.Core.Util;
using TrafficCity.Tools;

namespace TrafficCity.Ui;

/// <summary>
/// Отрисовка инструмента «Дороги»: призрак - одна лента профиля по оси превью (белая при OK, жёлтая с предупреждением, красная с ошибкой; ui-spec §6.1) плюс маркеры привязки
/// и проблемных мест - один <see cref="ImmediateMesh"/>, один draw call; метка у курсора (длина, цена, причина отказа) - панель в слое курсора UI.
/// Геометрия и текст пересобираются только при смене состояния инструмента или ответа превью, не каждый кадр. Цвета из <see cref="UiPalette"/>.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class RoadsToolView
{
    private const float GhostY = 0.55f;

    private readonly MeshInstance3D _ghost;
    private readonly ImmediateMesh _mesh = new();
    private readonly PanelContainer _panel;
    private readonly Label _text;
    private readonly StyleBoxFlat[] _styles = new StyleBoxFlat[3];
    private readonly Color[] _textColors = { UiPalette.Text, UiPalette.Warning, UiPalette.Error };
    private int _toolVersion = -1;
    private long _previewId = -1;
    private bool _wasMatch;
    private bool _wasPaused;
    private int _verts;

    public RoadsToolView(Node parent3d, Control cursorLayer)
    {
        _ghost = new MeshInstance3D
        {
            Name = "RoadsGhost",
            Mesh = _mesh,
            Visible = false,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                VertexColorUseAsAlbedo = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                NoDepthTest = true, // призрак поверх зданий: иначе его не видно за домами
                RenderPriority = 5,
            },
        };
        parent3d.AddChild(_ghost);

        for (int i = 0; i < 3; i++)
        {
            Color border = i == 0 ? UiPalette.PanelBorder : _textColors[i];
            _styles[i] = new StyleBoxFlat
            {
                BgColor = UiPalette.PanelBg(UiPalette.DefaultPanelAlpha),
                BorderColor = border,
                ContentMarginLeft = 8f,
                ContentMarginRight = 8f,
                ContentMarginTop = 4f,
                ContentMarginBottom = 4f,
            };
            _styles[i].SetBorderWidthAll(i == 0 ? 1 : 2);
            _styles[i].SetCornerRadiusAll((int)UiMetrics.Radius);
        }

        _panel = new PanelContainer { Name = "ToolLabel", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _panel.AddThemeStyleboxOverride("panel", _styles[0]);
        _text = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddChild(_text);
        cursorLayer.AddChild(_panel);
    }

    /// <summary>Видимая метка у курсора (для проверок и журнала).</summary>
    public string LabelText => _panel.Visible ? _text.Text : string.Empty;

    public bool GhostVisible => _ghost.Visible;

    public void Hide()
    {
        _ghost.Visible = false;
        _panel.Visible = false;
        _toolVersion = -1;
    }

    /// <summary>Раз в кадр, пока инструмент активен. <paramref name="overUi"/> - курсор над панелью: призрака и метки нет.</summary>
    public void Update(RoadsTool tool, ToolCommandService svc, Camera3D cam, Vector2 mouse, Vector2 viewport, bool overUi)
    {
        bool show = !overUi && tool.CursorValid;
        if (!show)
        {
            _ghost.Visible = false;
            _panel.Visible = false;
            _toolVersion = -1; // курсор вернётся - пересоберём
            return;
        }

        bool match = tool.HasCommand && svc.PreviewMatches(in tool.WantedCommand);
        long pid = match ? svc.Preview.Id : -1;
        bool paused = tool.World.Paused;
        if (tool.StateVersion != _toolVersion || pid != _previewId || match != _wasMatch || paused != _wasPaused)
        {
            _toolVersion = tool.StateVersion;
            _previewId = pid;
            _wasMatch = match;
            _wasPaused = paused;
            Rebuild(tool, svc, cam, match);
        }

        _ghost.Visible = true;
        bool hasText = _text.Text.Length > 0;
        _panel.Visible = hasText;
        if (hasText)
        {
            Vector2 size = _panel.GetCombinedMinimumSize();
            var pos = new Vector2(mouse.X + 22f, mouse.Y + 26f);
            pos.X = Math.Clamp(pos.X, 4f, Math.Max(4f, viewport.X - size.X - 4f));
            if (pos.Y + size.Y > viewport.Y - UiMetrics.ToolbarHeight - UiMetrics.OptionsHeight - 4f)
            {
                pos.Y = mouse.Y - size.Y - 12f; // не лезем под тулбар: метка уходит выше курсора
            }

            _panel.Position = pos;
        }
    }

    private void Rebuild(RoadsTool tool, ToolCommandService svc, Camera3D cam, bool match)
    {
        LabelContent label = RoadsLabel.Compose(tool, svc);
        _text.Text = label.Text;
        _panel.AddThemeStyleboxOverride("panel", _styles[(int)label.Severity]);
        _text.AddThemeColorOverride("font_color", _textColors[(int)label.Severity]);

        _mesh.ClearSurfaces();
        var cursor = new Vector3(tool.Cursor.X, 0f, tool.Cursor.Z);
        float dist = cam.GlobalPosition.DistanceTo(cursor);
        float marker = Math.Clamp(dist * 0.012f, 3f, 40f);
        float edge = Math.Clamp(dist * 0.0025f, 0.35f, 4f);
        _verts = 0;
        _mesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);

        (Color fill, Color line) = Colors(tool, label.Severity, match);
        switch (tool.Mode)
        {
            case RoadsMode.Build:
                if (tool.HasAnchor)
                {
                    Marker(tool.Anchor.X, tool.Anchor.Z, marker, line);
                    if (tool.HasCommand)
                    {
                        Axis(tool, svc, match, tool.Choice.WidthM, edge, fill, line);
                    }
                }

                Marker(tool.Cursor.X, tool.Cursor.Z, tool.CursorSnap == PointSnap.Node ? marker * 1.6f : tool.CursorSnap == PointSnap.Grid ? marker : marker * 0.7f, line);
                break;
            case RoadsMode.Upgrade when tool.HoverValid:
                Ribbon(tool.Hover.A, tool.Hover.B, tool.Choice.WidthM, edge, fill, line);
                break;
            case RoadsMode.Remove when tool.HoverValid:
                Ribbon(tool.Hover.A, tool.Hover.B, tool.Hover.WidthM, edge, fill, line);
                break;
            default:
                break;
        }

        if (match)
        {
            foreach (PreviewIssue i in svc.Preview.IssueSpan)
            {
                if (i.Severity >= IssueSeverity.Warning && (i.X != 0f || i.Z != 0f))
                {
                    Marker(i.X, i.Z, marker, i.Severity == IssueSeverity.Error ? UiPalette.Error : UiPalette.Warning);
                }
            }
        }

        if (_verts == 0)
        {
            Tri(Vector3.Zero, Vector3.Zero, Vector3.Zero, new Color(0f, 0f, 0f, 0f)); // пустая поверхность - ошибка Godot: вырожденный треугольник
        }

        _mesh.SurfaceEnd();
    }

    private static (Color Fill, Color Line) Colors(RoadsTool tool, LabelSeverity sev, bool match)
    {
        Color baseCol = sev switch
        {
            LabelSeverity.Error => UiPalette.Error,
            LabelSeverity.Warning => UiPalette.Warning,
            _ => tool.Mode == RoadsMode.Remove ? UiPalette.Error : tool.Mode == RoadsMode.Upgrade ? UiPalette.Accent : UiPalette.TextOnAccent,
        };
        bool ok = sev == LabelSeverity.Ok && tool.Mode == RoadsMode.Build;
        Color line = ok ? UiPalette.Text : baseCol;
        float a = match ? 0.5f : 0.28f; // ответа ещё нет - бледнее
        if (tool.Mode == RoadsMode.Upgrade && tool.HoverSame)
        {
            baseCol = UiPalette.TextSecondary;
            line = UiPalette.TextSecondary;
        }

        return (baseCol with { A = a }, line with { A = match ? 0.95f : 0.6f });
    }

    private void Axis(RoadsTool tool, ToolCommandService svc, bool match, float width, float edge, Color fill, Color line)
    {
        if (match && svc.Preview.AxisCount >= 2)
        {
            ReadOnlySpan<Vec2> ax = svc.Preview.AxisSpan;
            for (int i = 0; i + 1 < ax.Length; i++)
            {
                Ribbon(ax[i], ax[i + 1], width, edge, fill, line);
            }

            return;
        }

        Ribbon(tool.Anchor, tool.Cursor, width, edge, fill, line);
    }

    // Лента между двумя точками оси: заливка по ширине профиля и тонкие кромки по бокам (один отрезок = 3 прямоугольника)
    private void Ribbon(Vec2 a, Vec2 b, float width, float edge, Color fill, Color line)
    {
        float dx = b.X - a.X;
        float dz = b.Z - a.Z;
        float len = MathF.Sqrt((dx * dx) + (dz * dz));
        if (len < 0.01f)
        {
            return;
        }

        float nx = -dz / len;
        float nz = dx / len;
        float hw = width * 0.5f;
        Strip(a, b, nx, nz, -hw, hw, fill);
        Strip(a, b, nx, nz, hw - edge, hw, line);
        Strip(a, b, nx, nz, -hw, -hw + edge, line);
    }

    private void Strip(Vec2 a, Vec2 b, float nx, float nz, float o0, float o1, Color c)
    {
        c = Palette.ToWorld(c);
        Vector3 p0 = new(a.X + (nx * o0), GhostY, a.Z + (nz * o0));
        Vector3 p1 = new(a.X + (nx * o1), GhostY, a.Z + (nz * o1));
        Vector3 p2 = new(b.X + (nx * o1), GhostY, b.Z + (nz * o1));
        Vector3 p3 = new(b.X + (nx * o0), GhostY, b.Z + (nz * o0));
        Tri(p0, p1, p2, c);
        Tri(p0, p2, p3, c);
    }

    private void Marker(float x, float z, float size, Color c)
    {
        float h = size * 0.5f;
        Color w = Palette.ToWorld(c with { A = 0.9f });
        float y = GhostY + 0.05f;
        Tri(new Vector3(x - h, y, z - h), new Vector3(x + h, y, z - h), new Vector3(x + h, y, z + h), w);
        Tri(new Vector3(x - h, y, z - h), new Vector3(x + h, y, z + h), new Vector3(x - h, y, z + h), w);
    }

    private void Tri(Vector3 a, Vector3 b, Vector3 c, Color col)
    {
        _verts += 3;
        _mesh.SurfaceSetColor(col);
        _mesh.SurfaceAddVertex(a);
        _mesh.SurfaceSetColor(col);
        _mesh.SurfaceAddVertex(b);
        _mesh.SurfaceSetColor(col);
        _mesh.SurfaceAddVertex(c);
    }
}
