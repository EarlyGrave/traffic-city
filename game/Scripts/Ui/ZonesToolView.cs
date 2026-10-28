using System;
using System.Globalization;
using Godot;
using TrafficCity.Tools;

namespace TrafficCity.Ui;

/// <summary>
/// Отрисовка инструмента «Зоны» (UI9, ui-spec §5.2 п. 3, 5): плоская заливка прямоугольника кисти (цвет вида зоны; красная, если площадь больше предела) и подсветка участков внутри
/// (зелёные - будут зонированы, оранжевые - здание уйдёт в упадок, серые - недоступны) - один <see cref="MultiMesh"/>, два draw call. Метка у курсора: вид, число участков, площадь.
/// Геометрия и текст пересобираются только при смене <see cref="ZonesTool.StateVersion"/>; в стационаре кадр ничего не выделяет.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class ZonesToolView
{
    private const float FillY = 0.45f;
    private const float MarkY = 0.5f;

    private readonly MeshInstance3D _fill;
    private readonly StandardMaterial3D _fillMaterial;
    private readonly MultiMeshInstance3D _marks;
    private readonly MultiMesh _multi;
    private readonly PanelContainer _panel;
    private readonly Label _text;
    private readonly StyleBoxFlat _normal;
    private readonly StyleBoxFlat _error;
    private int _version = -1;
    private bool _shownError;

    public ZonesToolView(Node parent3d, Control cursorLayer)
    {
        var plane = new PlaneMesh { Size = Vector2.One };
        _fillMaterial = Overlay(Colors.White);
        _fill = new MeshInstance3D { Name = "ZonesFill", Mesh = plane, MaterialOverride = _fillMaterial, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent3d.AddChild(_fill);

        _multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new PlaneMesh { Size = Vector2.One },
            InstanceCount = ZonesTool.MaxMarks,
            VisibleInstanceCount = 0,
        };
        var marksMaterial = Overlay(Colors.White);
        marksMaterial.VertexColorUseAsAlbedo = true;
        _marks = new MultiMeshInstance3D { Name = "ZonesMarks", Multimesh = _multi, MaterialOverride = marksMaterial, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent3d.AddChild(_marks);

        _normal = Style(UiPalette.PanelBorder, 1);
        _error = Style(UiPalette.Error, 2);
        _panel = new PanelContainer { Name = "ZonesLabel", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _panel.AddThemeStyleboxOverride("panel", _normal);
        _text = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddChild(_text);
        cursorLayer.AddChild(_panel);
    }

    /// <summary>Видимая метка у курсора (журнал и проверки).</summary>
    public string LabelText => _panel.Visible ? _text.Text : string.Empty;

    public int MarksShown => _multi.VisibleInstanceCount;

    public void Hide()
    {
        _fill.Visible = false;
        _marks.Visible = false;
        _panel.Visible = false;
        _version = -1;
    }

    /// <summary>Раз в кадр, пока инструмент активен.</summary>
    public void Update(ZonesTool tool, Vector2 mouse, Vector2 viewport, bool overUi)
    {
        bool show = tool.CursorValid && !overUi;
        if (!show)
        {
            Hide();
            return;
        }

        if (tool.StateVersion != _version)
        {
            _version = tool.StateVersion;
            Rebuild(tool);
        }

        _fill.Visible = true;
        _marks.Visible = _multi.VisibleInstanceCount > 0;
        _panel.Visible = true;
        Vector2 size = _panel.GetCombinedMinimumSize();
        var pos = new Vector2(mouse.X + 22f, mouse.Y + 26f);
        pos.X = Math.Clamp(pos.X, 4f, Math.Max(4f, viewport.X - size.X - 4f));
        if (pos.Y + size.Y > viewport.Y - UiMetrics.ToolbarHeight - UiMetrics.OptionsHeight - 4f)
        {
            pos.Y = mouse.Y - size.Y - 12f; // не лезем под тулбар
        }

        _panel.Position = pos;
    }

    private void Rebuild(ZonesTool tool)
    {
        bool err = tool.IsDragging && tool.OverLimit;
        Color baseColor = err ? UiPalette.Error : KindColor(tool.Kind);
        _fillMaterial.AlbedoColor = baseColor with { A = tool.IsDragging ? 0.28f : 0.18f };
        float w = tool.X1 - tool.X0;
        float d = tool.Z1 - tool.Z0;
        _fill.Transform = new Transform3D(new Basis(Vector3.Right * w, Vector3.Up, Vector3.Back * d), new Vector3((tool.X0 + tool.X1) * 0.5f, FillY, (tool.Z0 + tool.Z1) * 0.5f));

        int n = tool.IsDragging && !err ? tool.MarkCount : 0;
        for (int i = 0; i < n; i++)
        {
            tool.MarkRect(i, out float x0, out float z0, out float x1, out float z1);
            // зазор 0.6 м между соседями: клетки читаются по отдельности
            var basis = new Basis(Vector3.Right * MathF.Max(1f, x1 - x0 - 0.6f), Vector3.Up, Vector3.Back * MathF.Max(1f, z1 - z0 - 0.6f));
            _multi.SetInstanceTransform(i, new Transform3D(basis, new Vector3((x0 + x1) * 0.5f, MarkY, (z0 + z1) * 0.5f)));
            _multi.SetInstanceColor(i, MarkColor(tool.MarkKind(i)));
        }

        _multi.VisibleInstanceCount = n;

        _text.Text = Compose(tool, err);
        if (err != _shownError)
        {
            _shownError = err;
            _panel.AddThemeStyleboxOverride("panel", err ? _error : _normal);
            _text.AddThemeColorOverride("font_color", err ? UiPalette.Error : UiPalette.Text);
        }
    }

    private static string Compose(ZonesTool tool, bool err)
    {
        CultureInfo ru = CultureInfo.InvariantCulture;
        string kind = KindName(tool.Kind) + (tool.Kind == ZoneChoice.Erase ? string.Empty : tool.Density == 0 ? ", малая плотность" : ", высокая плотность");
        if (!tool.IsDragging)
        {
            return kind + "\nТяните, чтобы закрасить";
        }

        if (err)
        {
            return "Не больше " + ZonesTool.Grouped(tool.MaxPaintM2) + " м²\n" + ZonesTool.Grouped((long)tool.AreaM2) + " м²";
        }

        string s = kind + "\nУчастков: " + tool.Paint.ToString(ru);
        if (tool.Abandon > 0)
        {
            s += ", здания уйдут в упадок: " + tool.Abandon.ToString(ru);
        }

        if (tool.Lost > 0)
        {
            s += ", недоступно: " + tool.Lost.ToString(ru);
        }

        return s + "\n" + ZonesTool.Grouped((long)tool.AreaM2) + " м²";
    }

    public static string KindName(ZoneChoice k) => k switch
    {
        ZoneChoice.Residential => "Жилая зона",
        ZoneChoice.Commercial => "Коммерческая зона",
        ZoneChoice.Industrial => "Промзона",
        _ => "Стереть зону",
    };

    private static Color KindColor(ZoneChoice k) => k switch
    {
        ZoneChoice.Residential => Palette.ZoneResidentialSrgb,
        ZoneChoice.Commercial => Palette.ZoneCommercialSrgb,
        ZoneChoice.Industrial => Palette.ZoneIndustrialSrgb,
        _ => UiPalette.TextSecondary,
    };

    private static Color MarkColor(ZoneMark m) => m switch
    {
        ZoneMark.Paint => UiPalette.SignalGreen with { A = 0.55f },
        ZoneMark.Abandon => UiPalette.Warning with { A = 0.65f },
        ZoneMark.Lost => UiPalette.TextSecondary with { A = 0.50f },
        _ => UiPalette.TextSecondary with { A = 0.18f },
    };

    private static StandardMaterial3D Overlay(Color c) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        AlbedoColor = c,
        NoDepthTest = true,
        RenderPriority = 4,
    };

    private static StyleBoxFlat Style(Color border, int width)
    {
        var s = new StyleBoxFlat
        {
            BgColor = UiPalette.PanelBg(UiPalette.DefaultPanelAlpha),
            BorderColor = border,
            ContentMarginLeft = 8f,
            ContentMarginRight = 8f,
            ContentMarginTop = 4f,
            ContentMarginBottom = 4f,
        };
        s.SetBorderWidthAll(width);
        s.SetCornerRadiusAll((int)UiMetrics.Radius);
        return s;
    }
}
