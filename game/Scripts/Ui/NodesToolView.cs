using System;
using System.Globalization;
using System.Text;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;
using TrafficCity.Tools;

namespace TrafficCity.Ui;

/// <summary>
/// Отрисовка инструмента «Узлы» (UI10, ui-spec §5.4): иконки рукавов (знак: авто/главная/уступи/стоп) в режиме «Рукава», стрелки манёвров над рукавами
/// в стиле TM:PE в режиме «Запреты» (запрещённый - красный ×), метка у курсора (наведённая цель, предупреждения превью, флаги полосы).
/// Пул меток создаётся один раз; текст и цвет меняются только при смене, позиции - раз в кадр (0 аллокаций в стационаре).
/// </summary>
#pragma warning disable CA1001 // узлы Godot принадлежат дереву сцены
public sealed class NodesToolView
{
    private const int MaxArrows = NodeLayout.MaxArms * (NodeLayout.MaxArms - 1);
    private const float MarkY = 0.6f;

    private static readonly string[] _armText = { "авто", "главная", "уступи", "СТОП" };
    // стрелка показывает, куда уезжает машина (направление рукава-цели на экране: X вправо, Z вниз), по 8 румбам от востока по часовой
    private static readonly string[] _arrowText = { "→", "↘", "↓", "↙", "←", "↖", "↑", "↗" };
    private static readonly string[] _arrowBanned = { "×→", "×↘", "×↓", "×↙", "×←", "×↖", "×↑", "×↗" };

    private readonly Label[] _arms = new Label[NodeLayout.MaxArms];
    private readonly Label[] _arrows = new Label[MaxArrows];
    private readonly string?[] _armShown = new string?[NodeLayout.MaxArms];
    private readonly string?[] _arrowShown = new string?[MaxArrows];
    private readonly int[] _armColor = new int[NodeLayout.MaxArms];
    private readonly int[] _arrowColor = new int[MaxArrows];
    private readonly PanelContainer _panel;
    private readonly Label _text;
    private readonly StringBuilder _sb = new(128);
    private long _labelKey = long.MinValue;

    public NodesToolView(Control cursorLayer)
    {
        for (int i = 0; i < _arms.Length; i++)
        {
            _arms[i] = Mark(cursorLayer, 15);
            _armColor[i] = -1;
        }

        for (int i = 0; i < _arrows.Length; i++)
        {
            _arrows[i] = Mark(cursorLayer, 18);
            _arrowColor[i] = -1;
        }

        var tag = new CursorTag("NodesLabel", cursorLayer);
        _panel = tag.Panel;
        _text = tag.Text;
    }

    /// <summary>Видимая метка у курсора (журнал и проверки).</summary>
    public string LabelText => _panel.Visible ? _text.Text : string.Empty;

    public void Hide()
    {
        for (int i = 0; i < _arms.Length; i++)
        {
            _arms[i].Visible = false;
        }

        for (int i = 0; i < _arrows.Length; i++)
        {
            _arrows[i].Visible = false;
        }

        _panel.Visible = false;
        _labelKey = long.MinValue;
    }

    /// <summary>Раз в кадр, пока инструмент активен.</summary>
    public void Update(NodesTool tool, ToolCommandService svc, Camera3D cam, Vector2 mouse, bool overUi)
    {
        NodeLayout l = tool.Layout;
        int arms = tool.HasSelection ? l.Count : 0;
        bool showArms = tool.Mode == NodesMode.Arms;
        bool showArrows = tool.Mode == NodesMode.Bans;
        for (int a = 0; a < _arms.Length; a++)
        {
            Label lb = _arms[a];
            if (!showArms || a >= arms)
            {
                lb.Visible = false;
                continue;
            }

            byte c = tool.ArmControl(a);
            SetText(lb, ref _armShown[a], _armText[c & 3]);
            SetColor(lb, ref _armColor[a], a == tool.HoverArm ? 3 : c == 3 ? 2 : c == 0 ? 0 : 1);
            Place(lb, cam, l.ArmIcon(a));
        }

        int k = 0;
        for (int from = 0; from < NodeLayout.MaxArms; from++)
        {
            for (int to = 0; to < NodeLayout.MaxArms; to++)
            {
                if (from == to)
                {
                    continue;
                }

                Label lb = _arrows[k];
                if (!showArrows || from >= arms || to >= arms)
                {
                    lb.Visible = false;
                    k++;
                    continue;
                }

                bool banned = l.Banned(from, to);
                int m = Octant(l.DirX(to), l.DirZ(to));
                SetText(lb, ref _arrowShown[k], banned ? _arrowBanned[m] : _arrowText[m]);
                SetColor(lb, ref _arrowColor[k], from == tool.HoverFrom && to == tool.HoverTo ? 3 : banned ? 2 : 0);
                Place(lb, cam, l.Arrow(from, to));
                k++;
            }
        }

        UpdateLabel(tool, svc, mouse, overUi);
    }

    private void UpdateLabel(NodesTool tool, ToolCommandService svc, Vector2 mouse, bool overUi)
    {
        long key;
        if (overUi)
        {
            key = -1;
        }
        else if (tool.HoverFrom >= 0)
        {
            bool match = svc.HasPreview && svc.PreviewCommand.Kind == CommandKind.SetTurnBan
                && svc.PreviewCommand.SetTurnBan.FromArm == tool.HoverFrom && svc.PreviewCommand.SetTurnBan.ToArm == tool.HoverTo;
            key = 1_000_000 + (tool.HoverFrom * 8) + tool.HoverTo + (match ? 100 + (svc.Preview.Id * 1000) : 0);
        }
        else if (tool.HoverArm >= 0)
        {
            key = 2_000_000 + (tool.HoverArm * 4) + tool.ArmControl(tool.HoverArm);
        }
        else if (tool.HoverLaneValid)
        {
            LaneHit h = tool.HoverLane;
            key = 3_000_000 + ((long)h.Seg.Index * 1024) + (h.Dir * 256) + (h.LaneNo * 16) + h.Flags;
        }
        else if (tool.HoverNode >= 0 && tool.HoverNode != tool.Layout.Node)
        {
            key = 4_000_000 + tool.HoverNode;
        }
        else
        {
            key = -1;
        }

        if (key != _labelKey)
        {
            _labelKey = key;
            if (key < 0)
            {
                _panel.Visible = false;
                return;
            }

            ComposeLabel(tool, svc);
            _text.Text = _sb.ToString(); // строка - только при смене цели или ответа превью
            _panel.Visible = true;
        }

        if (_panel.Visible)
        {
            _panel.Position = mouse + new Vector2(18f, 14f);
        }
    }

    private void ComposeLabel(NodesTool tool, ToolCommandService svc)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        _sb.Clear();
        NodeLayout l = tool.Layout;
        if (tool.HoverFrom >= 0)
        {
            bool banned = l.Banned(tool.HoverFrom, tool.HoverTo);
            _sb.Append(banned ? "Разрешить манёвр" : "Запретить манёвр");
            if (svc.HasPreview && svc.PreviewCommand.Kind == CommandKind.SetTurnBan && svc.PreviewCommand.SetTurnBan.FromArm == tool.HoverFrom
                && svc.PreviewCommand.SetTurnBan.ToArm == tool.HoverTo)
            {
                if (svc.Preview.Verdict != RejectReason.None)
                {
                    _sb.Append('\n').Append(RejectMessages.Label(svc.Preview.Verdict));
                }

                foreach (PreviewIssue i in svc.Preview.IssueSpan)
                {
                    _sb.Append('\n').Append(RejectMessages.Issue(i.Code, i.Value));
                }
            }
            else
            {
                _sb.Append(" …");
            }
        }
        else if (tool.HoverArm >= 0)
        {
            _sb.Append(inv, $"Рукав {tool.HoverArm + 1}: {_armText[tool.ArmControl(tool.HoverArm) & 3]}\nКлик - следующий знак");
        }
        else if (tool.HoverLaneValid)
        {
            LaneHit h = tool.HoverLane;
            _sb.Append(inv, $"Полоса {h.LaneNo + 1}: ");
            _sb.Append((h.Flags & LaneFlags.BusOnly) != 0 ? "только автобусы" : "для всех");
            if ((h.Flags & LaneFlags.NoLaneChangeOut) != 0)
            {
                _sb.Append(", без перестроения");
            }

            _sb.Append("\nКлик - автобусная, Shift+клик - перестроение");
        }
        else
        {
            _sb.Append(inv, $"Узел #{tool.HoverNode}: клик - выбрать");
        }
    }

    private static int Octant(float x, float z)
    {
        float a = MathF.Atan2(z, x); // 0 - восток, +π/2 - юг (Z вниз)
        int o = (int)MathF.Round(a / (MathF.PI / 4f));
        return (o + 8) & 7;
    }

    private static Label Mark(Control parent, int size)
    {
        var lb = new Label { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
        lb.AddThemeFontSizeOverride("font_size", size);
        lb.AddThemeColorOverride("font_outline_color", UiPalette.PanelSolid);
        lb.AddThemeConstantOverride("outline_size", 6);
        parent.AddChild(lb);
        return lb;
    }

    private static void SetText(Label lb, ref string? shown, string text)
    {
        if (!ReferenceEquals(shown, text))
        {
            shown = text;
            lb.Text = text;
        }
    }

    /// <summary>0 обычный, 1 акцент, 2 ошибка (стоп / запрет), 3 наведён.</summary>
    private static void SetColor(Label lb, ref int shown, int color)
    {
        if (shown == color)
        {
            return;
        }

        shown = color;
        lb.AddThemeColorOverride("font_color", color switch
        {
            1 => UiPalette.Accent,
            2 => UiPalette.Error,
            3 => UiPalette.Warning,
            _ => UiPalette.Text,
        });
    }

    private static void Place(Label lb, Camera3D cam, Vec2 p)
    {
        var w = new Vector3(p.X, MarkY, p.Z);
        if (cam.IsPositionBehind(w))
        {
            lb.Visible = false;
            return;
        }

        Vector2 s = cam.UnprojectPosition(w);
        lb.Position = s - (lb.Size * 0.5f);
        lb.Visible = true;
    }
}
