using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.Network;

/// <summary>
/// Каталог профилей дорог в SoA (road-profiles.md §3.1, ADR-0030): создаётся один раз в <c>Create</c> из проверенного <see cref="RoadProfilesDesc"/>,
/// в тике только читается. Все ширины - целые мм из JSON; метры (float) получаются одной формулой <c>(OffsetMm + WidthMm/2) * 0.001f</c>.
/// Профиль задан в направлении рисования (A→B); сегмент, построенный «против» профиля, несёт <see cref="SegmentFlags.Mirrored"/>:
/// элементы читаются в обратном порядке, <c>forward</c> и <c>backward</c> меняются местами (см. <c>*Mirrored</c>-аксессоры).
/// </summary>
internal sealed class RoadProfileStore
{
    /// <summary>Число профилей.</summary>
    public readonly int Count;

    public readonly int ModuleMm;

    /// <summary>Идентификаторы (диагностика и клиент; в хэш не входят).</summary>
    public readonly string[] Ids;

    // ---- по профилю ----
    public readonly int[] FirstElem;
    public readonly int[] ElemCount;
    public readonly int[] WidthMm;
    public readonly RoadKind[] Kind;
    public readonly byte[] LanesFwd;
    public readonly byte[] LanesBwd;

    /// <summary>Индекс первой полосы профиля в <see cref="LaneOffsetMm"/>: сначала forward (LaneNo 0 = правая), затем backward (LaneNo 0 = правая по ходу своего движения).</summary>
    public readonly int[] FirstLane;

    /// <summary>Ось → внешний край крайней левой / правой полосы (внутренний край бордюра), мм; положительные - по свою сторону оси.</summary>
    public readonly int[] CurbLMm;
    public readonly int[] CurbRMm;

    /// <summary>Ширина тротуара слева / справа, 0 - тротуара нет.</summary>
    public readonly int[] WalkLMm;
    public readonly int[] WalkRMm;

    /// <summary>Ось → край профиля, мм (<c>WidthMm / 2</c>).</summary>
    public readonly int[] EdgeMm;

    /// <summary>Предел уклона профиля, ‰ (TR3, <c>max_grade_pct</c>, terrain.md §4.3); 0 - по виду дороги из <c>terrain.max_grade_pct</c>. Не в хэше; сейв - <c>TERR</c> 20 (только с рельефом).</summary>
    public readonly int[] MaxGradePm;

    /// <summary>Те же данные плоскими blittable-записями для <c>NetworkView</c> (клиент, FFI); заполняются в конструкторе, дальше только читаются.</summary>
    public readonly RoadProfileData[] ViewProfiles;
    public readonly RoadElemData[] ViewElems;

    // ---- по элементу ----
    public readonly RoadElemKind[] ElemKind;
    public readonly byte[] ElemDir;
    public readonly int[] ElemOffsetMm;     // левый край элемента от оси, мм (+ вправо по A→B)
    public readonly int[] ElemWidthMm;

    // ---- по полосе профиля ----
    public readonly int[] LaneOffsetMm;     // левый край полосы от оси, мм
    public readonly int[] LaneWidthMm;

    /// <summary>Каталог должен быть проверен <see cref="RoadProfileValidator.Validate"/>.</summary>
    public RoadProfileStore(in RoadProfilesDesc d)
    {
        RoadProfileDesc[] ps = d.Profiles;
        Count = ps.Length;
        ModuleMm = d.ModuleMm;
        int elems = 0;
        int lanes = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            elems += ps[i].Elements.Length;
            for (int e = 0; e < ps[i].Elements.Length; e++)
            {
                if (ps[i].Elements[e].Kind == RoadElemKind.Lane)
                {
                    lanes++;
                }
            }
        }

        Ids = new string[Count];
        FirstElem = new int[Count];
        ElemCount = new int[Count];
        WidthMm = new int[Count];
        Kind = new RoadKind[Count];
        LanesFwd = new byte[Count];
        LanesBwd = new byte[Count];
        FirstLane = new int[Count];
        CurbLMm = new int[Count];
        CurbRMm = new int[Count];
        WalkLMm = new int[Count];
        WalkRMm = new int[Count];
        EdgeMm = new int[Count];
        MaxGradePm = new int[Count];
        ElemKind = new RoadElemKind[elems];
        ElemDir = new byte[elems];
        ElemOffsetMm = new int[elems];
        ElemWidthMm = new int[elems];
        ViewProfiles = new RoadProfileData[Count];
        ViewElems = new RoadElemData[elems];
        LaneOffsetMm = new int[lanes];
        LaneWidthMm = new int[lanes];

        int ei = 0;
        int li = 0;
        for (int i = 0; i < ps.Length; i++)
        {
            RoadElemDesc[] el = ps[i].Elements;
            Ids[i] = ps[i].Id;
            Kind[i] = ps[i].Kind;
            WidthMm[i] = ps[i].WidthMm;
            EdgeMm[i] = ps[i].WidthMm / 2;
            MaxGradePm[i] = ps[i].MaxGradePm;
            FirstElem[i] = ei;
            ElemCount[i] = el.Length;
            FirstLane[i] = li;

            int fwd = 0;
            int bwd = 0;
            for (int e = 0; e < el.Length; e++)
            {
                if (el[e].Kind == RoadElemKind.Lane)
                {
                    if (el[e].Dir == RoadLaneDir.Forward)
                    {
                        fwd++;
                    }
                    else
                    {
                        bwd++;
                    }
                }
            }

            LanesFwd[i] = (byte)fwd;
            LanesBwd[i] = (byte)bwd;

            int x = -EdgeMm[i];
            int leftEdge = int.MaxValue;
            int rightEdge = int.MinValue;
            int fSeen = 0;
            int bSeen = 0;
            for (int e = 0; e < el.Length; e++)
            {
                ElemKind[ei + e] = el[e].Kind;
                ElemDir[ei + e] = (byte)el[e].Dir;
                ElemOffsetMm[ei + e] = x;
                ElemWidthMm[ei + e] = el[e].WidthMm;
                ViewElems[ei + e] = new RoadElemData { Kind = (byte)el[e].Kind, Dir = (byte)el[e].Dir, OffsetMm = x, WidthMm = el[e].WidthMm };
                if (el[e].Kind == RoadElemKind.Lane)
                {
                    // Слева направо: сначала backward (LaneNo растёт слева направо), затем forward (LaneNo растёт справа налево).
                    int slot = el[e].Dir == RoadLaneDir.Forward ? fwd - 1 - fSeen++ : fwd + bSeen++;
                    LaneOffsetMm[li + slot] = x;
                    LaneWidthMm[li + slot] = el[e].WidthMm;
                    if (x < leftEdge)
                    {
                        leftEdge = x;
                    }

                    if (x + el[e].WidthMm > rightEdge)
                    {
                        rightEdge = x + el[e].WidthMm;
                    }
                }

                x += el[e].WidthMm;
            }

            ViewProfiles[i] = new RoadProfileData { FirstElem = ei, WidthMm = WidthMm[i], ElemCount = (byte)el.Length, Kind = (byte)Kind[i], LanesFwd = LanesFwd[i], LanesBwd = LanesBwd[i] };
            CurbLMm[i] = -leftEdge;
            CurbRMm[i] = rightEdge;
            WalkLMm[i] = el[0].Kind == RoadElemKind.Sidewalk ? el[0].WidthMm : 0;
            WalkRMm[i] = el[^1].Kind == RoadElemKind.Sidewalk ? el[^1].WidthMm : 0;
            ei += el.Length;
            li += fwd + bwd;
        }
    }

    // ---- аксессоры (учитывают Mirrored) ----

    /// <summary>Число полос A→B сегмента с профилем <paramref name="p"/>.</summary>
    public int LanesAB(int p, bool mirrored) => mirrored ? LanesBwd[p] : LanesFwd[p];

    /// <summary>Число полос B→A.</summary>
    public int LanesBA(int p, bool mirrored) => mirrored ? LanesFwd[p] : LanesBwd[p];

    private int Slot(int p, bool mirrored, int dir, int laneNo)
    {
        int d = mirrored ? 1 - dir : dir;
        return FirstLane[p] + (d == 0 ? laneNo : LanesFwd[p] + laneNo);
    }

    /// <summary>Смещение оси полосы от оси сегмента, м (+ вправо по A→B сегмента). Одна формула в C# и Rust: <c>(OffsetMm + WidthMm/2) * 0.001f</c>.</summary>
    public float LaneCenterM(int p, bool mirrored, int dir, int laneNo)
    {
        int s = Slot(p, mirrored, dir, laneNo);
        float c = (LaneOffsetMm[s] + (LaneWidthMm[s] * 0.5f)) * 0.001f;
        return mirrored ? -c : c;
    }

    /// <summary>Ширина полосы, м.</summary>
    public float LaneWidthM(int p, bool mirrored, int dir, int laneNo) => LaneWidthMm[Slot(p, mirrored, dir, laneNo)] * 0.001f;

    /// <summary>Ось → кромка проезжей части слева / справа по A→B сегмента, мм.</summary>
    public int CurbMm(int p, bool mirrored, bool left) => left != mirrored ? CurbLMm[p] : CurbRMm[p];

    /// <summary>Ширина тротуара слева / справа по A→B сегмента, мм; 0 - тротуара нет.</summary>
    public int WalkMm(int p, bool mirrored, bool left) => left != mirrored ? WalkLMm[p] : WalkRMm[p];

    /// <summary>
    /// Полуширина проезжей части <c>h</c> для отступа рукава (ADR-0028): <c>max(CurbL, CurbR)</c>, м. У асимметричного профиля оценка консервативная;
    /// от <c>Mirrored</c> не зависит.
    /// </summary>
    public float HalfRoadM(int p) => Math.Max(CurbLMm[p], CurbRMm[p]) * 0.001f;

    /// <summary>Находит профиль по id или -1 (построение, не тик).</summary>
    public int IndexOf(string id)
    {
        for (int i = 0; i < Count; i++)
        {
            if (string.Equals(Ids[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Каталог в хэш (только режим профилей): модуль, затем профили и их элементы - целые, порядок фиксирован.</summary>
    public void AddToHash(ref Fnv1a64 h)
    {
        h.Add(Count);
        h.Add(ModuleMm);
        for (int i = 0; i < Count; i++)
        {
            h.Add((byte)Kind[i]);
            h.Add(WidthMm[i]);
            h.Add(ElemCount[i]);
            for (int e = FirstElem[i]; e < FirstElem[i] + ElemCount[i]; e++)
            {
                h.Add((byte)ElemKind[e]);
                h.Add(ElemDir[e]);
                h.Add(ElemWidthMm[e]);
            }
        }
    }
}
