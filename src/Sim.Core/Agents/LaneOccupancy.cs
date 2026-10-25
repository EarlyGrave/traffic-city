using System;
using System.Diagnostics;
using Sim.Core.Lod;

namespace Sim.Core.Agents;

/// <summary>
/// Порядок машин на полосах (core-api 6.4): intrusive двусвязные списки поверх <see cref="VehicleStore"/>.
/// Полоса - просто индекс 0..maxLanes-1. Список идёт от <see cref="LaneTail"/> (наименьший S) к
/// <see cref="LaneHead"/> (наибольший S): <c>Ahead</c> ведёт к голове, <c>Behind</c> - к хвосту,
/// инвариант: <c>S[Ahead[i]] >= S[i]</c> (от головы к хвосту S не возрастает).
/// Машина состоит в списке своей полосы <c>Lane[i]</c> по ссылкам <c>Ahead/Behind</c> и, пока идёт смена полосы,
/// ещё и в списке <c>GhostLane[i]</c> по ссылкам <c>GhostAhead/GhostBehind</c> (ghost). Ссылка соседа на машину
/// выбирается по роли в данной полосе, поэтому ghost виден идущим сзади на исходной полосе как препятствие.
/// <see cref="LaneCount"/> считает всех членов списка, ghost включительно. Все операции O(1), кроме
/// <see cref="FindNeighbours"/>/<see cref="InsertSorted"/> (O(k) по числу машин на полосе). Ничего не аллоцирует.
/// </summary>
internal sealed class LaneOccupancy
{
    private readonly VehicleStore _v;
    private readonly ulong[] _linkedBits;

    public LaneOccupancy(VehicleStore vehicles, int maxLanes)
    {
        Debug.Assert(maxLanes >= 0);
        _v = vehicles;
        LaneHead = new int[maxLanes];
        LaneTail = new int[maxLanes];
        LaneCount = new int[maxLanes];
        OccMask = new ulong[(maxLanes + 63) >> 6];
        int maxVehicles = vehicles.Lane.Length;
        Linked = new int[maxVehicles];
        _linkedBits = new ulong[(maxVehicles + 63) >> 6];
        Array.Fill(LaneHead, -1);
        Array.Fill(LaneTail, -1);
    }

    /// <summary>Машина с наибольшим S (ближе к концу полосы) или -1.</summary>
    public int[] LaneHead { get; }

    /// <summary>Машина с наименьшим S или -1.</summary>
    public int[] LaneTail { get; }

    /// <summary>Длина списка полосы (primary + ghost).</summary>
    public int[] LaneCount { get; }

    public int MaxLanes => LaneHead.Length;

    /// <summary>
    /// Маска занятых полос (бит <c>lane</c> = <c>LaneCount[lane] &gt; 0</c>, ghost-члены включительно); ведётся при переходе пустая &lt;-&gt; непустая
    /// в <see cref="Insert"/>/<see cref="Remove"/> (ADR-0019, A2). Производное, вне хэша и сейва; обход по возрастанию индекса полосы.
    /// </summary>
    public ulong[] OccMask { get; }

    /// <summary>
    /// Машины, которых связали со списком полосы с прошлой очистки (<see cref="ClearLinked"/>): въезд, вставка, смена полосы, спавн; без повторов.
    /// Читает и очищает фаза 4 (метка <c>StoppedBeforeSignal</c> для машин вне прохода от головы).
    /// </summary>
    public int[] Linked { get; }

    /// <summary>Число машин в <see cref="Linked"/>.</summary>
    public int LinkedCount { get; private set; }

    /// <summary>Очищает <see cref="Linked"/> (после того как фаза 4 его обработала).</summary>
    public void ClearLinked()
    {
        for (int k = 0; k < LinkedCount; k++)
        {
            int i = Linked[k];
            _linkedBits[i >> 6] &= ~(1UL << (i & 63));
        }

        LinkedCount = 0;
    }

    /// <summary>
    /// LOD-ячейки (ADR-0013); не null только при <c>Lod.Mode != Off</c>. При въезде на полосу (<see cref="PushTail"/>, <see cref="PushHead"/>,
    /// <see cref="InsertBetween"/>) уровень машины берётся из ячейки полосы (sim-lod.md §2.5).
    /// </summary>
    public LodCells? LodCells { get; set; }

    /// <summary>
    /// Q-meso (ADR-0019, <c>Lod/QueueMeso.cs</c>); не null только при <c>lod.meso_model = "queue"</c>. Получает каждое добавление/снятие члена списка
    /// (счётчики полосы, освобождение ведомого очереди) и въезд на обычную полосу (переход в очередь). У машин очереди <c>S</c> - позиция въезда, поэтому
    /// порядок <c>S</c> в списке не проверяется (FIFO держит сам список).
    /// </summary>
    public QueueMeso? Queue { get; set; }

    /// <summary>Добавляет машину в хвост полосы (въезд с начала; S должен быть не больше S хвоста). Lane[i] = lane.</summary>
    public void PushTail(int lane, int i)
    {
        Debug.Assert(_v.Lane[i] == -1, "PushTail: машина уже на полосе");
        Debug.Assert(LaneTail[lane] == -1 || _v.S[i] <= _v.S[LaneTail[lane]] || Queue != null, "PushTail: нарушен порядок S");
        _v.Lane[i] = lane;
        _v.MarkRouteDirty(i);
        Insert(lane, i, ahead: LaneTail[lane], behind: -1);
        EnterLevel(lane, i);
    }

    /// <summary>Добавляет машину в голову полосы (S не меньше S головы). Lane[i] = lane.</summary>
    public void PushHead(int lane, int i)
    {
        Debug.Assert(_v.Lane[i] == -1, "PushHead: машина уже на полосе");
        Debug.Assert(LaneHead[lane] == -1 || _v.S[i] >= _v.S[LaneHead[lane]] || Queue != null, "PushHead: нарушен порядок S");
        _v.Lane[i] = lane;
        _v.MarkRouteDirty(i);
        MarkFollower(LaneHead[lane]);
        Insert(lane, i, ahead: -1, behind: LaneHead[lane]);
        EnterLevel(lane, i);
    }

    /// <summary>Вставка между известными соседями (из <see cref="FindNeighbours"/>); -1 = край. Lane[i] = lane.</summary>
    public void InsertBetween(int lane, int i, int ahead, int behind)
    {
        Debug.Assert(_v.Lane[i] == -1, "InsertBetween: машина уже на полосе");
        _v.Lane[i] = lane;
        _v.MarkRouteDirty(i);
        MarkFollower(behind);
        Insert(lane, i, ahead, behind);
        EnterLevel(lane, i);
    }

    /// <summary>Вставка по текущему S[i] (поиск от хвоста). При равных S новая встаёт позади существующих.</summary>
    public void InsertSorted(int lane, int i)
    {
        FindNeighbours(lane, _v.S[i], out int ahead, out int behind);
        InsertBetween(lane, i, ahead, behind);
    }

    /// <summary>
    /// Соседи для позиции s на полосе: behind - последняя с S &lt; s, ahead - первая с S &gt;= s (идём от хвоста). -1 = нет.
    /// Ghost-члены участвуют наравне с остальными.
    /// </summary>
    public void FindNeighbours(int lane, float s, out int ahead, out int behind)
    {
        int b = -1;
        int cur = LaneTail[lane];
        while (cur != -1 && _v.S[cur] < s)
        {
            b = cur;
            cur = AheadRef(cur, lane);
        }

        ahead = cur;
        behind = b;
    }

    /// <summary>Снимает машину с её полосы Lane[i] (ghost-членство не трогает). Lane[i] = -1. Нет на полосе - no-op.</summary>
    public void Unlink(int i)
    {
        int lane = _v.Lane[i];
        if (lane == -1)
        {
            return;
        }

        Remove(lane, i);
        _v.Lane[i] = -1;
    }

    /// <summary>
    /// Начало смены полосы: текущее членство Lane[i] становится ghost (машина физически остаётся в списке исходной
    /// полосы), а в целевой полосе она встаёт между <paramref name="ahead"/> и <paramref name="behind"/>.
    /// После вызова Lane[i] = target, GhostLane[i] = исходная.
    /// </summary>
    public void BeginLaneChange(int i, int target, int ahead, int behind)
    {
        int src = _v.Lane[i];
        Debug.Assert(src != -1 && src != target, "BeginLaneChange: нет полосы или цель = источник");
        Debug.Assert(_v.GhostLane[i] == -1, "BeginLaneChange: ghost уже есть");
        _v.GhostLane[i] = src;
        _v.GhostAhead[i] = _v.Ahead[i];
        _v.GhostBehind[i] = _v.Behind[i];
        _v.Lane[i] = target;
        _v.MarkRouteDirty(i);
        MarkFollower(behind);
        Insert(target, i, ahead, behind);
    }

    /// <summary>Конец манёвра: снимает ghost-членство. Нет ghost - no-op.</summary>
    public void GhostUnlink(int i)
    {
        int g = _v.GhostLane[i];
        if (g == -1)
        {
            return;
        }

        Remove(g, i);
        _v.GhostLane[i] = -1;
    }

    /// <summary>Отмена манёвра: целевое членство снимается, ghost становится основным (Lane[i] = прежняя полоса).</summary>
    public void CancelLaneChange(int i)
    {
        int g = _v.GhostLane[i];
        Debug.Assert(g != -1, "CancelLaneChange: нет ghost");
        Remove(_v.Lane[i], i);
        _v.Lane[i] = g;
        _v.MarkRouteDirty(i);
        _v.Ahead[i] = _v.GhostAhead[i];
        _v.Behind[i] = _v.GhostBehind[i];
        _v.GhostLane[i] = -1;
        _v.GhostAhead[i] = -1;
        _v.GhostBehind[i] = -1;
    }

    /// <summary>Снимает машину со всех списков (деспаун): GhostUnlink + Unlink.</summary>
    public void UnlinkAll(int i)
    {
        GhostUnlink(i);
        Unlink(i);
    }

    /// <summary>Вставка перед машиной <paramref name="behind"/> даёт ей нового, более близкого лидера: meso-ведомый пересчитывает поведение в ближайшей фазе 5.</summary>
    private void MarkFollower(int behind)
    {
        if (behind != -1 && LodCells != null && (_v.Lod[behind] & LodBits.Meso) != 0)
        {
            _v.Lod[behind] |= LodBits.Fresh;
        }
    }

    private void EnterLevel(int lane, int i)
    {
        LodCells? lc = LodCells;
        if (lc != null)
        {
            byte level = lc.CellLevel[lc.LaneCell[lane]];
            LodTransitions.SetLevel(_v, i, level);
            if (level == LodCells.LevelMeso)
            {
                // Новая полоса - другой лидер и, возможно, другой цвет ячейки (другой такт): без пересчёта бюджет пути прежнего решения мог
                // кончиться до такта новой ячейки (до 7 тиков) и обнулить скорость. Пересчёт в ближайшей фазе 5 гарантирует решение не позже чем за 4 тика до такта.
                _v.Lod[i] |= LodBits.Fresh;
            }

            Queue?.OnEnterLane(lane, i);
        }
    }

    // Роль машины в списке lane: основная (Lane) или ghost. Возврат по ref - без копий и аллокаций.
    private ref int AheadRef(int v, int lane)
    {
        if (_v.Lane[v] == lane)
        {
            return ref _v.Ahead[v];
        }

        Debug.Assert(_v.GhostLane[v] == lane, "машина не состоит в списке этой полосы");
        return ref _v.GhostAhead[v];
    }

    private ref int BehindRef(int v, int lane)
    {
        if (_v.Lane[v] == lane)
        {
            return ref _v.Behind[v];
        }

        Debug.Assert(_v.GhostLane[v] == lane, "машина не состоит в списке этой полосы");
        return ref _v.GhostBehind[v];
    }

    // Роль i на lane уже назначена (Lane[i] == lane или GhostLane[i] == lane).
    private void Insert(int lane, int i, int ahead, int behind)
    {
        AheadRef(i, lane) = ahead;
        BehindRef(i, lane) = behind;
        if (ahead != -1)
        {
            BehindRef(ahead, lane) = i;
        }
        else
        {
            LaneHead[lane] = i;
        }

        if (behind != -1)
        {
            AheadRef(behind, lane) = i;
        }
        else
        {
            LaneTail[lane] = i;
        }

        if (LaneCount[lane]++ == 0)
        {
            OccMask[lane >> 6] |= 1UL << (lane & 63);
        }

        MarkLinked(i);
        Queue?.OnInsert(lane, i);
    }

    /// <summary>Заносит машину в <see cref="Linked"/> (без повторов): её класс в фазе 4 изменился не движением по списку (вставка, снятие допуска).</summary>
    public void MarkLinked(int i)
    {
        ulong bit = 1UL << (i & 63);
        if ((_linkedBits[i >> 6] & bit) == 0)
        {
            _linkedBits[i >> 6] |= bit;
            Linked[LinkedCount++] = i;
        }
    }

    private void Remove(int lane, int i)
    {
        int a = AheadRef(i, lane);
        int b = BehindRef(i, lane);
        if (a != -1)
        {
            BehindRef(a, lane) = b;
        }
        else
        {
            LaneHead[lane] = b;
        }

        if (b != -1)
        {
            AheadRef(b, lane) = a;
        }
        else
        {
            LaneTail[lane] = a;
        }

        AheadRef(i, lane) = -1;
        BehindRef(i, lane) = -1;
        if (--LaneCount[lane] == 0)
        {
            OccMask[lane >> 6] &= ~(1UL << (lane & 63));
        }

        Queue?.OnRemove(lane, i, b);
    }
}
