using System;
using System.Collections.Generic;
using Godot;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace TrafficCity;

/// <summary>
/// Дороги чанками (docs/design/render.md §5.2, §4.3; visual-style.md §4, урезанная VS3a): ближние чанки 250 м - LOD0 (до <see cref="LodConfig.RoadLod0"/>) и LOD1
/// (до <see cref="LodConfig.RoadLod1"/>), дальние чанки 1 000 м (RoadFarCell) - дальняя лента (клиент строит сам, видна от камеры и без верхней границы:
/// <see cref="LodConfig.RoadFar"/> - бюджет, на общем плане s10k+ камера дальше). Штатные <c>visibility_range</c> и frustum culling движка.
/// На каждом уровне - сплошной асфальт: одна лента на сегмент на всю ширину проезжей части (оба направления) и сплошная площадка перекрёстка - многоугольник
/// из торцов лент рукавов и внешней кромки правых поворотов (connector'ы lane0 -> lane0, смещённые наружу на полполосы). Ширины, смещения и концы полос берутся
/// из данных сети (полосы сегмента, <see cref="NodeData"/>, connector'ы), констант ширины нет: профили дорог (RP6) поменяют только источник ширин.
/// LOD0 добавляет слабые линии полос (в шейдере, постоянной толщины в пикселях, гаснут с дистанцией) и направляющие connector'ов на площадках.
/// Цвет heatmap берёт шейдер <c>road.gdshader</c> из текстуры <see cref="HeatTexture"/> по <c>CUSTOM0.x</c> (сегмент; у площадок -1 - нейтрально); меши не пересобираются.
/// </summary>
#pragma warning disable CA1001 // ресурсы Godot принадлежат узлам сцены и освобождаются вместе с ними
public sealed partial class RoadChunks
{
    public const float NearCell = 250f;
    public const float FarCell = 2000f;

    /// <summary>
    /// Полудиагональ ближней ячейки: <c>visibility_range</c> считается до центра AABB чанка, а пороги LOD - до самой дороги (по ближнему краю чанка),
    /// поэтому к порогу добавляется полудиагональ; иначе у края чанка под камерой включался бы LOD1.
    /// </summary>
    public const float NearHalfDiag = 177f;

    /// <summary>
    /// Ячейка дальней ленты дорог и тротуаров, м. Меньше <see cref="FarCell"/> (его делят землепользование и участки): полудиагональ 707 м позволяет
    /// включать ленту только там, где все её точки уже за пределами Road0, - иначе скошенные углы ленты (FarY над землёй) торчат из-под скруглений LOD0.
    /// </summary>
    public const float RoadFarCell = 1000f;

    /// <summary>Полудиагональ ячейки дальней ленты.</summary>
    public const float FarHalfDiag = 707f;

    /// <summary>Запас на сегменты, выходящие за свою ячейку (центр AABB чанка не равен центру ячейки).</summary>
    public const float RangeSlack = 100f;

    /// <summary>
    /// Дальняя лента видна от этой дистанции (до центра её чанка): все точки её ячейки не ближе <c>Lod0End - NearHalfDiag</c> = порога LOD0 от камеры,
    /// и не раньше начала LOD1 по данным (<see cref="LodConfig.RoadLod1"/>).
    /// </summary>
    public static float FarBegin(LodConfig lod) => MathF.Max(lod.RoadLod1, Lod0End(lod) + FarHalfDiag);

    /// <summary>Конец диапазона Road0 (до центра чанка): порог LOD0 + полудиагональ ячейки; с него начинается Road1.</summary>
    public static float Lod0End(LodConfig lod) => lod.RoadLod0 + NearHalfDiag;

    /// <summary>
    /// Конец диапазона Road1: пока лента скрыта (центр её чанка ближе <see cref="FarBegin"/>), её точки могут быть дальше на полудиагональ,
    /// а центр ближнего чанка с такой точкой - ещё на полудиагональ ближней ячейки.
    /// </summary>
    public static float Lod1End(LodConfig lod) => FarBegin(lod) + FarHalfDiag + NearHalfDiag + RangeSlack;

    /// <summary>
    /// Страж щели: Road0 [0, Lod0End], Road1 [Lod0End, Lod1End] и RoadFar [FarBegin, inf) покрывают [0, inf) без дыр, а ближний слой перекрывает ленту.
    /// Margin узлов обязан быть 0 (<see cref="ChunkGrid.Emit"/>), иначе на каждой границе мёртвая зона (lessons.md, агент L34).
    /// </summary>
    public static bool CheckRanges(LodConfig lod, out string error)
    {
        float e0 = Lod0End(lod);
        float e1 = Lod1End(lod);
        float fb = FarBegin(lod);
        if (lod.RoadLod0 <= 0f || e0 > e1)
        {
            error = $"Road0 [0, {e0:0.#}], Road1 [{e0:0.#}, {e1:0.#}]: Road1 не стыкуется с Road0";
        }
        else if (fb <= 0f || fb > e1 - FarHalfDiag - NearHalfDiag)
        {
            error = $"RoadFar [{fb:0.#}, inf) не перекрывает Road1 (до {e1:0.#}) с запасом на полудиагонали ячеек";
        }
        else if (fb - FarHalfDiag < lod.RoadLod0)
        {
            error = $"RoadFar от {fb:0.#}: её ближние точки (на {fb - FarHalfDiag:0.#} м) заходят в зону Road0 (до {lod.RoadLod0:0.#} м)";
        }
        else
        {
            error = string.Empty;
        }

        return error.Length == 0;
    }

    private const float Lod1Tolerance = 0.4f;
    private const float FarTolerance = 6f;
    private const float FarY = -0.10f; // под дорогами ближних чанков (y >= 0), над землёй (-0.15)
    private const int Lod1CornerPoints = 2; // точек угловой кривой площадки на LOD1; на дальней ленте - прямой срез, LOD0 - все точки connector'а
    private const float GuideLift = 0.03f; // направляющие коннекторов над площадкой

    // Линии полос: доли дистанции LOD0, на которых они гаснут (в шейдере - метры). Цвет и прозрачность - Palette.
    private const float LineFadeBegin = 0.35f;
    private const float LineFadeEnd = 0.70f;
    private const float LinePx = 1.2f;

    // Ширина полосы, если в сети нет ни одного сегмента с двумя и более полосами (иначе её не измерить): значение NetworkOptions.LaneWidth по умолчанию.
    private const float FallbackLaneWidth = 3.5f;


    private readonly ShaderMaterial _material;
    private readonly List<int> _keep = new();
    private readonly List<Arm> _arms = new();
    private readonly List<Vec2> _ring = new();
    private readonly List<Vec2> _corner = new();
    private readonly HashSet<(int, int, int, int, int, int)> _seen = new();
    private Vec2[] _scratch = new Vec2[256];

    /// <summary>Версия сети (<c>NetworkView.Version</c>), по которой построены меши; при расхождении <see cref="SimHost"/> пересобирает дороги целиком.</summary>
    public int Version { get; }

    public RoadChunks(NetworkView net, HeatTexture heat, LodConfig lod)
    {
        Version = net.Version;
        _material = HeatPalette.CreateMaterial(GD.Load<Shader>("res://Shaders/road.gdshader"), heat.Texture, false, lod.PointRange);
        _material.SetShaderParameter("line_color", Palette.LaneLine);
        _material.SetShaderParameter("line_alpha", Palette.LaneLineAlpha);
        _material.SetShaderParameter("line_center_alpha", Palette.LaneLineCenterAlpha);
        _material.SetShaderParameter("line_px", LinePx);
        _material.SetShaderParameter("stop_alpha", Palette.StopLineAlpha);
        _material.SetShaderParameter("line_fade_begin", lod.RoadLod0 * LineFadeBegin);
        _material.SetShaderParameter("line_fade_end", lod.RoadLod0 * LineFadeEnd);
        Root = new Node3D { Name = "Roads" };
        Build(net, lod);
    }

    public Node3D Root { get; }

    /// <summary>Асфальт одного цвета на всех LOD и у всех классов; закрывающийся сегмент (4.3, T4: Closing, дренаж) - тон <see cref="Palette.Closing"/> поверх (меши пересобираются при смене Version сети).</summary>
    private static Color KindColor(NetworkView net, int segment) =>
        (uint)segment < (uint)net.Segments.Length && (net.Segments[segment].Flags & SegmentFlags.Closing) != 0 ? Palette.Closing : Palette.AsphaltVertex;

    private static float KindY(NetworkView net, int segment) =>
        segment >= 0 && segment < net.Segments.Length && net.Segments[segment].Kind == RoadKind.Arterial ? 0.02f : 0f;

    private static Vec2 NodePos(NetworkView net, int node, Vec2 fallback) => (uint)node < (uint)net.Nodes.Length ? net.Nodes[node].Position : fallback;

    /// <summary>
    /// Измерение проезжей части сегмента по данным полос. Базовая полоса - крайняя правая (lane 0) направления A→B, а если его нет - B→A.
    /// Полосы идут от базовой в одну сторону на <c>d</c> (знак - сторона их роста по нормали базовой полилинии), поэтому по крайней полосе сегмента
    /// <c>d = смещение / (n - 1)</c>: ширина полосы <c>w = |d|</c>, ось проезжей части сдвинута от базовой полосы на <c>(n - 1) d / 2</c>.
    /// </summary>
    private readonly struct SegGeom
    {
        public SegGeom(bool ok, float half, float shift, float sgn, bool baseAB, float vPlus, float code, float w, bool measured, RoadProfileLayouts.Layout? lay = null)
        {
            Lay = lay;
            Ok = ok;
            Half = half;
            Shift = shift;
            Sgn = sgn;
            BaseAB = baseAB;
            VPlus = vPlus;
            Code = code;
            W = w;
            Measured = measured;
        }

        public bool Ok { get; }

        /// <summary>Полуширина проезжей части (n * w / 2).</summary>
        public float Half { get; }

        /// <summary>Сдвиг оси проезжей части от базовой полилинии вдоль её нормали (-dz, dx), в канонической ориентации A→B.</summary>
        public float Shift { get; }

        /// <summary>Знак нормали (-dz, dx) базовой полосы, в сторону которой растёт номер полосы (знак <c>d</c>); правая сторона - противоположная.</summary>
        public float Sgn { get; }

        /// <summary>Базовая полилиния идёт A→B (иначе её переворачиваем).</summary>
        public bool BaseAB { get; }

        /// <summary>Поперечная координата в полосах (0..n) у вершины на стороне +N ленты; на стороне -N - n минус это значение.</summary>
        public float VPlus { get; }

        /// <summary><c>CUSTOM0.y</c> ленты: n + 16 * (число полос базового направления, если есть встречное; иначе 0); 0 - без линий.</summary>
        public float Code { get; }

        public float W { get; }

        /// <summary>Ширина и сторона измерены по полосам (а не взяты по умолчанию).</summary>
        public bool Measured { get; }

        /// <summary>Сечение профиля (RP6): сегмент строится по элементам профиля, а не по ширине полосы; null - legacy.</summary>
        public RoadProfileLayouts.Layout? Lay { get; }

        public bool Profiled => Lay != null;
    }

    /// <summary>Рукав узла: торец ленты сегмента, обращённый к узлу.</summary>
    private struct Arm
    {
        public int Seg;
        public bool AtA;
        public Vec2 ROut;   // правая кромка для движения от узла
        public Vec2 RIn;    // правая кромка для движения к узлу
        public Vec2 Mid;
        public float Angle; // угол середины торца от центра узла
        public float Key;   // угол, умноженный на сторону обхода
    }

    private static SegGeom Measure(NetworkView net, int s, float defW, float defSgn)
    {
        SegmentData sd = net.Segments[s];
        int n1 = sd.LanesAB;
        int n2 = sd.LanesBA;
        bool baseAB = n1 > 0;
        int baseLane = baseAB ? sd.FirstLaneAB : sd.FirstLaneBA;
        int nTot = n1 + n2;
        if (nTot <= 0 || !net.IsLaneAlive(baseLane) || net.Lanes[baseLane].PointCount < 2)
        {
            return default;
        }

        RoadProfileLayouts.Layout? lay = RoadProfileLayouts.ForSegment(in sd);
        if (lay != null)
        {
            // профиль: ленты строятся от оси по элементам сечения; система A→B сегмента, правая сторона - +N (Sgn = -1)
            float cb = baseAB ? lay.CBaseAB : lay.CBaseBA;
            if (float.IsNaN(cb))
            {
                return default;
            }

            return new SegGeom(true, (lay.CurbL + lay.CurbR) * 0.5f, (lay.CurbR - lay.CurbL) * 0.5f, -1f, baseAB, 0f, 0f, baseAB ? lay.WFwd : lay.WBwd, false, lay);
        }

        LaneData bl = net.Lanes[baseLane];
        float w = defW;
        float sgn = defSgn;
        float off = 0f;
        bool measured = false;
        if (nTot > 1)
        {
            bool opposite = baseAB && n2 > 0;
            // крайняя полоса сегмента: у встречного направления - его lane 0 (правая для него, дальняя для нас), иначе - левая полоса своего
            int far = opposite ? sd.FirstLaneBA : baseAB ? sd.FirstLaneAB + n1 - 1 : sd.FirstLaneBA + n2 - 1;
            if (net.IsLaneAlive(far) && net.Lanes[far].PointCount >= 1)
            {
                LaneData fl = net.Lanes[far];
                Vec2 q = net.LanePoints[opposite ? fl.FirstPoint + fl.PointCount - 1 : fl.FirstPoint];
                ReadOnlySpan<Vec2> bp = net.LanePoints.Slice(bl.FirstPoint, bl.PointCount);
                MeshBuilder.EdgeNormal(bp, 0, out float ox, out float oz);
                float o = (ox * (q.X - bp[0].X)) + (oz * (q.Z - bp[0].Z));
                if (MathF.Abs(o) > 0.1f)
                {
                    off = o;
                    sgn = o > 0f ? 1f : -1f;
                    w = MathF.Abs(o) / (nTot - 1);
                    measured = true;
                }
            }
        }

        int nBase = baseAB ? n1 : n2;
        float vPlus = sgn * (baseAB ? 1f : -1f) > 0f ? nTot : 0f;
        float code = nTot >= 2 ? nTot + (16 * (nTot > nBase ? nBase : 0)) : 0f;
        float shift = baseAB ? off * 0.5f : -off * 0.5f;
        return new SegGeom(true, nTot * w * 0.5f, shift, sgn, baseAB, vPlus, code, w, measured);
    }

    private void Build(NetworkView net, LodConfig lod)
    {
        var lod0 = new Dictionary<long, MeshBuilder>();
        var lod1 = new Dictionary<long, MeshBuilder>();
        var far = new Dictionary<long, MeshBuilder>();

        // позиция сегмента для привязки к чанку (середина между узлами): все представления сегмента - в одном чанке, иначе при смене LOD бывают дыры
        int segCount = net.Segments.Length;
        var segCx = new float[segCount];
        var segCz = new float[segCount];
        for (int s = 0; s < segCount; s++)
        {
            if (!net.IsSegmentAlive(s))
            {
                continue;
            }

            SegmentData sd = net.Segments[s];
            int lane = sd.LanesAB > 0 ? sd.FirstLaneAB : sd.FirstLaneBA;
            Vec2 fb = net.IsLaneAlive(lane) && net.Lanes[lane].PointCount > 0 ? net.LanePoints[net.Lanes[lane].FirstPoint] : default;
            Vec2 a = NodePos(net, sd.NodeA, fb);
            Vec2 b = NodePos(net, sd.NodeB, fb);
            segCx[s] = (a.X + b.X) * 0.5f;
            segCz[s] = (a.Z + b.Z) * 0.5f;
        }

        // ширина полосы и сторона роста номеров: по умолчанию - первое измеренное в сети значение (для сегментов с одной полосой)
        float defW = FallbackLaneWidth;
        float defSgn = -1f; // правостороннее движение в системе Godot: правая сторона - +N
        for (int s = 0; s < segCount; s++)
        {
            if (net.IsSegmentAlive(s))
            {
                SegGeom m = Measure(net, s, defW, defSgn);
                if (m.Measured)
                {
                    defW = m.W;
                    defSgn = m.Sgn;
                    break;
                }
            }
        }

        var geo = new SegGeom[segCount];
        for (int s = 0; s < segCount; s++)
        {
            if (net.IsSegmentAlive(s))
            {
                geo[s] = Measure(net, s, defW, defSgn);
            }
        }

        // рукава узлов: сегменты по обоим концам
        var armCount = new int[net.Nodes.Length + 1];
        for (int s = 0; s < segCount; s++)
        {
            if (geo[s].Ok)
            {
                armCount[net.Segments[s].NodeA]++;
                armCount[net.Segments[s].NodeB]++;
            }
        }

        var armStart = new int[net.Nodes.Length + 1];
        for (int n = 0; n < net.Nodes.Length; n++)
        {
            armStart[n + 1] = armStart[n] + armCount[n];
        }

        var armFill = new int[net.Nodes.Length];
        var armSeg = new int[armStart[net.Nodes.Length]]; // seg * 2 + (на конце A ? 0 : 1)
        for (int s = 0; s < segCount; s++)
        {
            if (!geo[s].Ok)
            {
                continue;
            }

            int na = net.Segments[s].NodeA;
            int nb = net.Segments[s].NodeB;
            armSeg[armStart[na] + armFill[na]++] = s * 2;
            armSeg[armStart[nb] + armFill[nb]++] = (s * 2) + 1;
        }

        var ends0 = new Vec2[segCount * 4];
        var ends1 = new Vec2[segCount * 4];
        var endsF = new Vec2[segCount * 4];
        int profiled = 0;
        for (int s = 0; s < segCount; s++)
        {
            if (!geo[s].Ok)
            {
                continue;
            }

            Color col = KindColor(net, s);
            float y = KindY(net, s);
            MeshBuilder b0 = ChunkGrid.Get(lod0, ChunkGrid.Key(segCx[s], segCz[s], NearCell), 4, true);
            MeshBuilder b1 = ChunkGrid.Get(lod1, ChunkGrid.Key(segCx[s], segCz[s], NearCell), 4);
            MeshBuilder bf = ChunkGrid.Get(far, ChunkGrid.Key(segCx[s], segCz[s], RoadFarCell), 4);
            if (geo[s].Profiled)
            {
                AddProfileRoad(net, s, geo[s], b0, 0f, y, col, Detail.Full, true, ends0);
                AddProfileRoad(net, s, geo[s], b1, Lod1Tolerance, y, col, Detail.Walk, false, ends1);
                AddProfileRoad(net, s, geo[s], bf, FarTolerance, FarY, col, Detail.Asphalt, false, endsF);
                profiled++;
                continue;
            }

            AddRoad(net, s, geo[s], b0, 0f, y, col, true, ends0);
            AddRoad(net, s, geo[s], b1, Lod1Tolerance, y, col, false, ends1);
            AddRoad(net, s, geo[s], bf, FarTolerance, FarY, col, false, endsF);
        }

        for (int n = 0; n < net.Nodes.Length; n++)
        {
            NodeData nd = net.Nodes[n];
            if (!net.IsNodeAlive(n) || armStart[n + 1] == armStart[n])
            {
                continue;
            }

            ReadOnlySpan<int> arms = armSeg.AsSpan(armStart[n], armStart[n + 1] - armStart[n]);
            float py = NodeY(net, arms);
            Color pc = Palette.AsphaltVertex;
            AddJunction(net, nd, arms, geo, ends0, ChunkGrid.Get(lod0, ChunkGrid.Key(nd.Position.X, nd.Position.Z, NearCell), 4, true), py, pc, true, int.MaxValue, Detail.Full);
            AddJunction(net, nd, arms, geo, ends1, ChunkGrid.Get(lod1, ChunkGrid.Key(nd.Position.X, nd.Position.Z, NearCell), 4), py, pc, false, Lod1CornerPoints, Detail.Walk);
            AddJunction(net, nd, arms, geo, endsF, ChunkGrid.Get(far, ChunkGrid.Key(nd.Position.X, nd.Position.Z, RoadFarCell), 4), FarY, pc, false, 0, Detail.Asphalt);
        }

        GD.Print(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"[roads] chunks: lod0 {lod0.Count}, lod1 {lod1.Count}, far {far.Count} (ячейки {NearCell:0}/{RoadFarCell:0} м, LOD0 до {lod.RoadLod0:0} м, лента от {FarBegin(lod):0} м без верхней границы дальности, LOD1 до {Lod1End(lod):0} м; полоса {defW:0.##} м, сегментов с профилем {profiled})"));
        if (!CheckRanges(lod, out string rangeError))
        {
            GD.PushError("[roads] щель в диапазонах видимости LOD: " + rangeError);
        }

        ChunkGrid.Emit(Root, lod0, "Road0", _material, 0f, Lod0End(lod));
        ChunkGrid.Emit(Root, lod1, "Road1", _material, Lod0End(lod), Lod1End(lod));
        // дальняя лента от FarBegin; ближний LOD1 держится до Lod1End (см. CheckRanges), чтобы не было дыр (центры чанков разных сеток)
        // верхней границы нет: на общем плане s10k/s1m камера дальше 6 000 м (render-budgets road.far_distance_m), а лента - единственное, что видно
        ChunkGrid.Emit(Root, far, "RoadFar", _material, FarBegin(lod), 0f);
    }

    private static float NodeY(NetworkView net, ReadOnlySpan<int> arms)
    {
        float y = 0f;
        foreach (int a in arms)
        {
            y = MathF.Max(y, KindY(net, a >> 1));
        }

        return y;
    }

    /// <summary>
    /// Лента сегмента на всю проезжую часть: полилиния базовой полосы (упрощённая при <paramref name="tol"/> &gt; 0), развёрнутая в A→B, сдвинутая к оси проезжей части.
    /// В <paramref name="ends"/> пишутся кромки (+N, -N) в первой и последней точке: площадка перекрёстка стыкуется с ними без зазора.
    /// </summary>
    private void AddRoad(NetworkView net, int seg, in SegGeom g, MeshBuilder b, float tol, float y, Color col, bool lines, Vec2[] ends)
    {
        LaneData bl = net.Lanes[g.BaseAB ? net.Segments[seg].FirstLaneAB : net.Segments[seg].FirstLaneBA];
        ReadOnlySpan<Vec2> pts = net.LanePoints.Slice(bl.FirstPoint, bl.PointCount);
        int m;
        if (tol > 0f)
        {
            ChunkGrid.Simplify(pts, tol, _keep);
            m = _keep.Count;
            EnsureScratch(m);
            for (int k = 0; k < m; k++)
            {
                _scratch[k] = pts[_keep[k]];
            }
        }
        else
        {
            m = pts.Length;
            EnsureScratch(m);
            pts.CopyTo(_scratch);
        }

        if (!g.BaseAB)
        {
            Array.Reverse(_scratch, 0, m);
        }

        if (m < 2)
        {
            return;
        }

        var span = new ReadOnlySpan<Vec2>(_scratch, 0, m);
        float code = lines ? g.Code : 0f;
        float nTot = g.Half * 2f / g.W;
        float vMin = nTot - g.VPlus;
        int first = b.VertexCount;
        for (int k = 0; k < m; k++)
        {
            MeshBuilder.EdgeNormal(span, k, out float ox, out float oz);
            float cx = span[k].X + (ox * g.Shift);
            float cz = span[k].Z + (oz * g.Shift);
            var plus = new Vec2(cx + (ox * g.Half), cz + (oz * g.Half));
            var minus = new Vec2(cx - (ox * g.Half), cz - (oz * g.Half));
            b.Add(new Vector3(plus.X, y, plus.Z), col, seg, code, 0f, 0f, new Vector2(0f, g.VPlus));
            b.Add(new Vector3(minus.X, y, minus.Z), col, seg, code, 0f, 0f, new Vector2(0f, vMin));
            if (k == 0)
            {
                ends[(seg * 4) + 0] = plus;
                ends[(seg * 4) + 1] = minus;
            }

            if (k == m - 1)
            {
                ends[(seg * 4) + 2] = plus;
                ends[(seg * 4) + 3] = minus;
            }
        }

        for (int k = 0; k + 1 < m; k++)
        {
            int a = first + (k * 2);
            b.Quad(a, a + 1, a + 2, a + 3);
        }
    }

    private void EnsureScratch(int n)
    {
        if (_scratch.Length < n)
        {
            _scratch = new Vec2[Math.Max(n, _scratch.Length * 2)];
        }
    }

    /// <summary>
    /// Площадка перекрёстка: центр узла + кольцо из торцов рукавов (правая кромка движения от узла, затем к узлу) и угловых кривых между соседними рукавами -
    /// внешней кромки правого поворота lane0 -> lane0 (connector, смещённый вправо на полполосы); нет такого connector'а - прямой отрезок.
    /// Порядок рукавов и сторона обхода - по геометрии торцов. LOD0 дополнительно рисует направляющие всех connector'ов узла.
    /// </summary>
    private void AddJunction(NetworkView net, in NodeData nd, ReadOnlySpan<int> armRefs, SegGeom[] geo, Vec2[] ends, MeshBuilder b, float y, Color col, bool guides, int cornerKeep, Detail detail)
    {
        _arms.Clear();
        Vec2 c = nd.Position;
        float turn = 0f;
        foreach (int r in armRefs)
        {
            int s = r >> 1;
            bool atA = (r & 1) == 0;
            SegGeom g = geo[s];
            Vec2 plus = ends[(s * 4) + (atA ? 0 : 2)];
            Vec2 minus = ends[(s * 4) + (atA ? 1 : 3)];
            Vec2 right = g.Sgn < 0f ? plus : minus; // правая кромка движения A→B
            Vec2 left = g.Sgn < 0f ? minus : plus;
            // от узла - по направлению A→B на конце A, к узлу - на конце B
            Vec2 rOut = atA ? right : left;
            Vec2 rIn = atA ? left : right;
            var mid = new Vec2((rOut.X + rIn.X) * 0.5f, (rOut.Z + rIn.Z) * 0.5f);
            float dx = mid.X - c.X;
            float dz = mid.Z - c.Z;
            turn += MathF.Sign((dx * (rIn.Z - mid.Z)) - (dz * (rIn.X - mid.X)));
            _arms.Add(new Arm { Seg = s, AtA = atA, ROut = rOut, RIn = rIn, Mid = mid, Angle = MathF.Atan2(dz, dx) });
        }

        float rho = turn >= 0f ? 1f : -1f;
        for (int i = 0; i < _arms.Count; i++)
        {
            Arm a = _arms[i];
            a.Key = a.Angle * rho;
            _arms[i] = a;
        }

        _arms.Sort(static (p, q) => p.Key.CompareTo(q.Key));

        _ring.Clear();
        for (int i = 0; i < _arms.Count; i++)
        {
            Arm a = _arms[i];
            Arm nx = _arms[(i + 1) % _arms.Count];
            _ring.Add(a.ROut);
            _ring.Add(a.RIn);
            Corner(net, nd, a, nx, geo[a.Seg], cornerKeep);
            AddCornerStrips(nd, a, nx, geo, ends, b, detail);
            foreach (Vec2 p in _corner)
            {
                _ring.Add(p);
            }
        }

        int centerIdx = b.Add(new Vector3(c.X, y, c.Z), col, -1f);
        int firstRing = b.VertexCount;
        foreach (Vec2 p in _ring)
        {
            b.Add(new Vector3(p.X, y, p.Z), col, -1f);
        }

        for (int k = 0; k < _ring.Count; k++)
        {
            b.Tri(centerIdx, firstRing + k, firstRing + ((k + 1) % _ring.Count));
        }

        if (guides)
        {
            AddBoundaries(net, nd, geo, ends, b, y + GuideLift, col);
        }
    }

    /// <summary>Граница номер <paramref name="bnd"/> (0 - внешняя правая кромка направления, n - левая) на торце рукава: точка на отрезке между кромками торца, как целые значения <c>UV.y</c> ленты.</summary>
    private static Vec2 BoundaryPoint(NetworkView net, SegGeom[] geo, Vec2[] ends, int seg, bool atA, int dir, int bnd)
    {
        SegGeom g = geo[seg];
        Vec2 plus = ends[(seg * 4) + (atA ? 0 : 2)];
        Vec2 minus = ends[(seg * 4) + (atA ? 1 : 3)];
        Vec2 right = g.Sgn < 0f ? plus : minus; // правая кромка движения A→B
        Vec2 left = g.Sgn < 0f ? minus : plus;
        Vec2 r = dir == 0 ? right : left;
        Vec2 l = dir == 0 ? left : right;
        float t = bnd / (float)(net.Segments[seg].LanesAB + net.Segments[seg].LanesBA);
        if (g.Profiled)
        {
            // граница полосы номер bnd - от внешней кромки своего направления на bnd ширин полосы (между направлениями может быть разделитель)
            float len = MathF.Sqrt(((l.X - r.X) * (l.X - r.X)) + ((l.Z - r.Z) * (l.Z - r.Z)));
            t = len > 1e-4f ? bnd * (dir == 0 ? g.Lay!.WFwd : g.Lay!.WBwd) / len : 0f;
        }

        return new Vec2(r.X + ((l.X - r.X) * t), r.Z + ((l.Z - r.Z) * t));
    }

    /// <summary>
    /// Линии внутри площадки - продолжение разделителей полос сегментов: для каждого connector'а его правая и левая границы (ось +- w/2), начало и конец -
    /// точно на границах торцов лент рукавов. Граница, общая у соседних параллельных connector'ов (те же границы входа и выхода), рисуется один раз.
    /// Внешние кромки (граница 0 и крайняя левая направления: край дороги и осевая) не рисуются: край - это кромка площадки, осевая обрывается у площадки,
    /// как в Cities: Skylines (внутри перекрёстка нет сквозной линии, которую пересекали бы встречные левые повороты).
    /// </summary>
    private void AddBoundaries(NetworkView net, in NodeData nd, SegGeom[] geo, Vec2[] ends, MeshBuilder b, float y, Color col)
    {
        _seen.Clear();
        for (int ci = 0; ci < nd.ConnectorCount; ci++)
        {
            int li = nd.FirstConnector + ci;
            if (!net.IsLaneAlive(li) || net.Lanes[li].PointCount < 3)
            {
                continue;
            }

            LaneData cn = net.Lanes[li];
            if (!net.IsLaneAlive(cn.FromLane) || !net.IsLaneAlive(cn.ToLane))
            {
                continue;
            }

            LaneData from = net.Lanes[cn.FromLane];
            LaneData to = net.Lanes[cn.ToLane];
            if ((uint)from.Segment >= (uint)geo.Length || (uint)to.Segment >= (uint)geo.Length || !geo[from.Segment].Ok || !geo[to.Segment].Ok)
            {
                continue;
            }

            SegmentData sf = net.Segments[from.Segment];
            SegmentData st = net.Segments[to.Segment];
            int nf = from.Direction == 0 ? sf.LanesAB : sf.LanesBA;
            int nt = to.Direction == 0 ? st.LanesAB : st.LanesBA;
            bool fromA = sf.NodeA == cn.Node;
            bool toA = st.NodeA == cn.Node;
            ReadOnlySpan<Vec2> pts = net.LanePoints.Slice(cn.FirstPoint, cn.PointCount);
            for (int side = 0; side < 2; side++)
            {
                int bs = from.LaneNo + side;
                int be = to.LaneNo + side;
                if (bs <= 0 || bs >= nf || be <= 0 || be >= nt || !_seen.Add((from.Segment, from.Direction, bs, to.Segment, to.Direction, be)))
                {
                    continue;
                }

                float lw = geo[from.Segment].Profiled && from.Width > 0f ? from.Width : geo[from.Segment].W;
                float off = (side == 0 ? -geo[from.Segment].Sgn : geo[from.Segment].Sgn) * lw * 0.5f; // вправо / влево по ходу connector'а
                EnsureScratch(pts.Length);
                _scratch[0] = BoundaryPoint(net, geo, ends, from.Segment, fromA, from.Direction, bs);
                for (int k = 1; k < pts.Length - 1; k++)
                {
                    MeshBuilder.EdgeNormal(pts, k, out float ox, out float oz);
                    _scratch[k] = new Vec2(pts[k].X + (ox * off), pts[k].Z + (oz * off));
                }

                _scratch[pts.Length - 1] = BoundaryPoint(net, geo, ends, to.Segment, toA, to.Direction, be);
                AddGuide(b, new ReadOnlySpan<Vec2>(_scratch, 0, pts.Length), y, col);
            }
        }
    }

    /// <summary>Внутренние точки угловой кривой от правой кромки входа рукава <paramref name="a"/> до правой кромки выхода рукава <paramref name="b"/> (в <see cref="_corner"/>).</summary>
    private void Corner(NetworkView net, in NodeData nd, in Arm a, in Arm b, in SegGeom ga, int keep)
    {
        _corner.Clear();
        int inDir = a.AtA ? 1 : 0;
        int outDir = b.AtA ? 0 : 1;
        for (int ci = 0; ci < nd.ConnectorCount; ci++)
        {
            int li = nd.FirstConnector + ci;
            if (!net.IsLaneAlive(li))
            {
                continue;
            }

            LaneData cn = net.Lanes[li];
            if (!net.IsLaneAlive(cn.FromLane) || !net.IsLaneAlive(cn.ToLane) || cn.PointCount < 3)
            {
                continue;
            }

            LaneData from = net.Lanes[cn.FromLane];
            LaneData to = net.Lanes[cn.ToLane];
            if (from.LaneNo != 0 || to.LaneNo != 0 || from.Segment != a.Seg || to.Segment != b.Seg || from.Direction != inDir || to.Direction != outDir)
            {
                continue;
            }

            ReadOnlySpan<Vec2> pts = net.LanePoints.Slice(cn.FirstPoint, cn.PointCount);
            float side = -ga.Sgn * (ga.Profiled && from.Width > 0f ? from.Width : ga.W) * 0.5f; // вправо по ходу connector'а
            int inner = pts.Length - 2;
            int take = Math.Min(inner, keep);
            for (int t = 0; t < take; t++)
            {
                int k = 1 + (int)(((t + 0.5f) * inner / take));
                MeshBuilder.EdgeNormal(pts, k, out float ox, out float oz);
                _corner.Add(new Vec2(pts[k].X + (ox * side), pts[k].Z + (oz * side)));
            }

            return;
        }
    }

    /// <summary>Направляющая: лента нулевой ширины, кромки раздвигает вершинный шейдер до постоянной толщины в пикселях (<c>CUSTOM0.y = -1</c>, <c>zw</c> - смещение кромки).</summary>
    private static void AddGuide(MeshBuilder b, ReadOnlySpan<Vec2> pts, float y, Color col)
    {
        int first = b.VertexCount;
        for (int k = 0; k < pts.Length; k++)
        {
            MeshBuilder.EdgeNormal(pts, k, out float ox, out float oz);
            b.Add(new Vector3(pts[k].X, y, pts[k].Z), col, -1f, -1f, ox, oz, new Vector2(1f, 0f));
            b.Add(new Vector3(pts[k].X, y, pts[k].Z), col, -1f, -1f, -ox, -oz, new Vector2(-1f, 0f));
        }

        for (int k = 0; k + 1 < pts.Length; k++)
        {
            int a = first + (k * 2);
            b.Quad(a, a + 1, a + 2, a + 3);
        }
    }
}
