using System;
using System.Text;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using Sim.Host;
using TrafficCity.Tools;

namespace TrafficCity.Ui;

/// <summary>
/// Отрисовка инструмента «Транспорт» (UI11, ui-spec §5.3): остановки всех линий цветными метками (цвет линии - из палитры Окабе-Ито, различимой для дальтоников, §11),
/// у выбранной линии - ноги между остановками по порядку (прямыми; путь по графу - Позже), нога к курсору (красная, если ядро говорит <c>Unreachable</c>),
/// метка у курсора: что сделает клик, вердикт и цена превью. Пересборка - только при смене <see cref="BusTool.StateVersion"/> или ответа превью.
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class BusToolView
{
    public const int MaxMarks = 1024;
    private const float Y = 0.6f;

    private static readonly Color[] _lineColors =
    {
        new(0.902f, 0.624f, 0f), new(0.337f, 0.706f, 0.914f), new(0f, 0.620f, 0.451f), new(0.941f, 0.894f, 0.259f),
        new(0f, 0.447f, 0.698f), new(0.835f, 0.369f, 0f), new(0.800f, 0.475f, 0.655f),
    };

    private readonly MultiMeshInstance3D _marks;
    private readonly MultiMesh _multi;
    private readonly MeshInstance3D _legs;
    private readonly ImmediateMesh _legMesh;
    private readonly StandardMaterial3D _legMaterial;
    private readonly PanelContainer _panel;
    private readonly Label _text;
    private readonly StyleBoxFlat _normal;
    private readonly StyleBoxFlat _error;
    private readonly StringBuilder _sb = new();
    private int _version = -1;
    private long _previewId = -1;
    private bool _shownError;

    public BusToolView(Node parent3d, Control cursorLayer)
    {
        _multi = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = new PlaneMesh { Size = Vector2.One },
            InstanceCount = MaxMarks,
            VisibleInstanceCount = 0,
        };
        StandardMaterial3D mm = Overlay(Colors.White);
        mm.VertexColorUseAsAlbedo = true;
        _marks = new MultiMeshInstance3D { Name = "BusMarks", Multimesh = _multi, MaterialOverride = mm, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent3d.AddChild(_marks);

        _legMesh = new ImmediateMesh();
        _legMaterial = Overlay(Colors.White);
        _legMaterial.VertexColorUseAsAlbedo = true;
        _legs = new MeshInstance3D { Name = "BusLegs", Mesh = _legMesh, MaterialOverride = _legMaterial, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        parent3d.AddChild(_legs);

        _normal = Style(UiPalette.PanelBorder, 1);
        _error = Style(UiPalette.Error, 2);
        _panel = new PanelContainer { Name = "BusLabel", MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        _panel.AddThemeStyleboxOverride("panel", _normal);
        _text = new Label { MouseFilter = Control.MouseFilterEnum.Ignore };
        _panel.AddChild(_text);
        cursorLayer.AddChild(_panel);
    }

    public string LabelText => _panel.Visible ? _text.Text : string.Empty;

    public int MarksShown => _multi.VisibleInstanceCount;

    /// <summary>Цвет линии по индексу.</summary>
    public static Color LineColor(int route) => _lineColors[((route % _lineColors.Length) + _lineColors.Length) % _lineColors.Length];

    public void Hide()
    {
        _marks.Visible = false;
        _legs.Visible = false;
        _panel.Visible = false;
        _version = -1;
    }

    public void Update(BusTool tool, ToolCommandService svc, NetworkSnapshot? net, Vector2 mouse, Vector2 viewport, bool overUi)
    {
        if (net == null)
        {
            Hide();
            return;
        }

        long pid = svc.HasPreview ? svc.Preview.Id : -1;
        if (tool.StateVersion != _version || pid != _previewId)
        {
            _version = tool.StateVersion;
            _previewId = pid;
            Rebuild(tool, svc, net);
        }

        _marks.Visible = _multi.VisibleInstanceCount > 0;
        _legs.Visible = true;
        bool label = tool.CursorValid && !overUi;
        _panel.Visible = label;
        if (!label)
        {
            return;
        }

        Vector2 size = _panel.GetCombinedMinimumSize();
        var pos = new Vector2(mouse.X + 22f, mouse.Y + 26f);
        pos.X = Math.Clamp(pos.X, 4f, Math.Max(4f, viewport.X - size.X - 4f));
        if (pos.Y + size.Y > viewport.Y - UiMetrics.ToolbarHeight - UiMetrics.OptionsHeight - 4f)
        {
            pos.Y = mouse.Y - size.Y - 12f;
        }

        _panel.Position = pos;
    }

    private void Rebuild(BusTool tool, ToolCommandService svc, NetworkSnapshot net)
    {
        NetworkView v = net.View;
        int sel = tool.Route.Index;
        int n = 0;
        for (int i = 0; i < v.BusStops.Length && n < MaxMarks - 1; i++)
        {
            int r = v.BusStops[i].Route;
            if (!v.IsBusRouteAlive(r) || !BusTool.TryStopPos(v, i, out Vec2 p))
            {
                continue;
            }

            bool mine = r == sel;
            float size = mine ? 7f : 4.5f;
            if (i == tool.HoverStop)
            {
                size += 2f;
            }

            Color c = LineColor(r);
            if (sel >= 0 && !mine)
            {
                c.A = 0.45f;
            }

            _multi.SetInstanceTransform(n, new Transform3D(new Basis(Vector3.Right * size, Vector3.Up, Vector3.Back * size), new Vector3(p.X, Y, p.Z)));
            _multi.SetInstanceColor(n, c);
            n++;
        }

        bool err = svc.HasPreview && svc.Preview.Verdict != RejectReason.None && tool.Mode != BusMode.Select;
        if (tool.HoverValid && tool.Mode != BusMode.Select)
        {
            Color hc = err ? UiPalette.Error : sel >= 0 ? LineColor(sel) : Colors.White;
            hc.A = 0.8f;
            _multi.SetInstanceTransform(n, new Transform3D(new Basis(Vector3.Right * 6f, Vector3.Up, Vector3.Back * 6f), new Vector3(tool.HoverX, Y + 0.05f, tool.HoverZ)));
            _multi.SetInstanceColor(n, hc);
            n++;
        }

        _multi.VisibleInstanceCount = n;

        _legMesh.ClearSurfaces();
        Vec2 last = default;
        bool hasLast = false;
        if (sel >= 0 && v.IsBusRouteAlive(sel))
        {
            BusRouteData rd = v.BusRoutes[sel];
            Color lc = LineColor(sel);
            if (rd.Suspended)
            {
                lc.A = 0.5f;
            }

            _legMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
            int verts = 0;
            for (int k = 0; k < rd.StopCount; k++)
            {
                int slot = rd.FirstStop + k;
                if ((uint)slot >= (uint)v.BusRouteStops.Length || !BusTool.TryStopPos(v, v.BusRouteStops[slot], out Vec2 p))
                {
                    continue;
                }

                if (hasLast)
                {
                    Line(last, p, lc);
                    verts += 2;
                }

                last = p;
                hasLast = true;
            }

            if (hasLast && tool.HoverValid && (tool.Mode == BusMode.Build || tool.Mode == BusMode.Edit))
            {
                Color hl = err ? UiPalette.Error : lc with { A = 0.6f };
                Line(last, new Vec2(tool.HoverX, tool.HoverZ), hl);
                verts += 2;
            }

            if (verts == 0)
            {
                Line(last, last, lc); // пустую поверхность Godot не принимает
            }

            _legMesh.SurfaceEnd();
        }

        _text.Text = Compose(tool, svc, v, err);
        if (err != _shownError)
        {
            _shownError = err;
            _panel.AddThemeStyleboxOverride("panel", err ? _error : _normal);
            _text.AddThemeColorOverride("font_color", err ? UiPalette.Error : UiPalette.Text);
        }
    }

    private void Line(Vec2 a, Vec2 b, Color c)
    {
        _legMesh.SurfaceSetColor(c);
        _legMesh.SurfaceAddVertex(new Vector3(a.X, Y, a.Z));
        _legMesh.SurfaceSetColor(c);
        _legMesh.SurfaceAddVertex(new Vector3(b.X, Y, b.Z));
    }

    private string Compose(BusTool tool, ToolCommandService svc, NetworkView v, bool err)
    {
        _sb.Clear();
        switch (tool.Mode)
        {
            case BusMode.Select:
                _sb.Append(tool.HoverStop >= 0 ? "Клик — выбрать линию " + BusTool.LineName(v.BusStops[tool.HoverStop].Route) : "Клик по остановке — выбрать линию");
                return _sb.ToString();
            case BusMode.Build:
                if (tool.Creating)
                {
                    return "Линия создаётся…";
                }

                _sb.Append(tool.Route.Index < 0 ? "Новая линия: клик — первая остановка" : "Клик — следующая остановка\nEnter или клик по первой — готово");
                break;
            default:
                _sb.Append("Линия ").Append(BusTool.LineName(tool.Route.Index)).Append(": клик — остановка в конец, Shift — после ближайшей");
                if (tool.HoverStop >= 0 && v.BusStops[tool.HoverStop].Route == tool.Route.Index)
                {
                    _sb.Append("\nПКМ — удалить остановку");
                }

                break;
        }

        if (!tool.HoverValid)
        {
            _sb.Append("\nНаведите на дорогу");
            return _sb.ToString();
        }

        if (!svc.HasPreview)
        {
            return _sb.ToString();
        }

        PreviewReply p = svc.Preview;
        if (err)
        {
            _sb.Append('\n').Append(RejectMessages.Label(p.Verdict));
            string hint = RejectMessages.Hint(p.Verdict);
            if (hint.Length > 0)
            {
                _sb.Append(": ").Append(hint);
            }

            return _sb.ToString();
        }

        if (p.Result.Cost != 0)
        {
            _sb.Append('\n').Append(RejectMessages.Money(p.Result.Cost));
        }

        foreach (PreviewIssue i in p.IssueSpan)
        {
            _sb.Append('\n').Append(RejectMessages.Issue(i.Code, i.Value));
        }

        return _sb.ToString();
    }

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
