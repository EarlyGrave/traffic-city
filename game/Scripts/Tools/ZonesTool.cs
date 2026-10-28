using System;
using Sim.Core.Api;

namespace TrafficCity.Tools;

/// <summary>Вид зоны в строке опций (ui-spec §5.2 п. 1): значения равны <see cref="LotZone"/>, «Стереть» = 0 (<c>ZoneKind 0</c>).</summary>
public enum ZoneChoice : byte
{
    Erase = 0,
    Residential = 1,
    Commercial = 2,
    Industrial = 3,
}

/// <summary>Как участок встречает прямоугольник (оценка клиента по <c>GetLots</c>, превью <c>PaintZone</c> в ядре нет - запрос UI-C1).</summary>
public enum ZoneMark : byte
{
    /// <summary>Будет зонирован (зелёный).</summary>
    Paint = 0,

    /// <summary>Будет зонирован, здание другого вида уйдёт в <c>Abandoned</c> (оранжевый).</summary>
    Abandon = 1,

    /// <summary>Недоступен: дорога снесена (серый).</summary>
    Lost = 2,

    /// <summary>Уже такой вид и плотность - команда ничего не изменит (светло-серый).</summary>
    Same = 3,
}

/// <summary>Что инструменту «Зоны» нужно от мира (реализует хост клиента): участки, сетка, предел площади. Тонкая граница, чтобы инструмент оставался чистым C#.</summary>
public interface IZonesWorld
{
    /// <summary>Есть жители (<c>demand.model = residents</c>): без них у города нет участков и <c>PaintZone</c> не поддержан.</summary>
    bool HasCity { get; }

    /// <summary>Сторона клетки сетки, м (<c>module_m</c>); 0 - сетки нет (legacy), клетка 8 м.</summary>
    float CellM { get; }

    float GridOriginX { get; }

    float GridOriginZ { get; }

    /// <summary>Предел площади одной кисти, м2 (<c>zoning.max_paint_m2</c>).</summary>
    long MaxPaintM2 { get; }

    /// <summary>Участки (копия, обновляется при смене <see cref="LotsVersion"/>); false - данных ещё нет.</summary>
    bool TryGetLots(out ReadOnlySpan<LotRenderData> lots, out int lotsVersion);

    bool Paused { get; }
}

/// <summary>
/// Инструмент «Зоны» (UI9, ui-spec §5.2): прямоугольная кисть. Drag тянет прямоугольник по клеткам сетки, отпускание шлёт одну команду <c>PaintZone</c> (одна протяжка = одно
/// действие игрока = один пункт undo, когда ядро запишет обратную). Оценка участков (зелёные, оранжевые, серые) считается на клиенте по <see cref="IZonesWorld.TryGetLots"/>
/// только при смене прямоугольника или версии участков; в стационаре 0 аллокаций. Чистый C# без Godot.
/// </summary>
public sealed class ZonesTool : ToolBase
{
    /// <summary>Сколько участков в прямоугольнике подсвечиваем по одному (остальные только считаются).</summary>
    public const int MaxMarks = 4096;

    private const float DefaultCellM = 8f;
    private const float LegacyLotM = 36f;

    private readonly IZonesWorld _world;
    private readonly float[] _markRect = new float[MaxMarks * 4];
    private readonly ZoneMark[] _markKind = new ZoneMark[MaxMarks];
    private int _sx;
    private int _sz;
    private int _cx;
    private int _cz;
    private bool _cursorValid;
    private int _lotsVersion = -1;
    private bool _dirty = true;

    public ZonesTool(ToolCommandService service, IZonesWorld world)
        : base(service)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
    }

    public override string Id => "zones";

    public override string DisplayName => "Зоны";

    public ZoneChoice Kind { get; private set; } = ZoneChoice.Residential;

    /// <summary>Плотность 0/1 (низкая / высокая).</summary>
    public byte Density { get; private set; }

    /// <summary>Растёт при любой смене вида: прямоугольника, выбора, оценки. Вид перерисовывается по ней.</summary>
    public int StateVersion { get; private set; }

    public bool CursorValid => _cursorValid;

    /// <summary>Идёт протяжка (кнопка нажата).</summary>
    public bool IsDragging => Dragging;

    /// <summary>Прямоугольник кисти, м (при наведении без протяжки - одна клетка под курсором).</summary>
    public float X0 { get; private set; }

    public float Z0 { get; private set; }

    public float X1 { get; private set; }

    public float Z1 { get; private set; }

    public float AreaM2 => (X1 - X0) * (Z1 - Z0);

    public long MaxPaintM2 => _world.MaxPaintM2;

    /// <summary>Площадь больше <c>zoning.max_paint_m2</c>: прямоугольник красный, команда не уйдёт.</summary>
    public bool OverLimit => AreaM2 > _world.MaxPaintM2;

    public int Paint { get; private set; }

    public int Abandon { get; private set; }

    public int Lost { get; private set; }

    public int Same { get; private set; }

    /// <summary>Подсвечиваемые участки (первые <see cref="MaxMarks"/>).</summary>
    public int MarkCount { get; private set; }

    public ZoneMark MarkKind(int i) => _markKind[i];

    /// <summary>Контур i-й подсвеченной клетки: x0, z0, x1, z1.</summary>
    public void MarkRect(int i, out float x0, out float z0, out float x1, out float z1)
    {
        int o = i * 4;
        x0 = _markRect[o];
        z0 = _markRect[o + 1];
        x1 = _markRect[o + 2];
        z1 = _markRect[o + 3];
    }

    /// <summary>Лента событий: тост (текст, ошибка?).</summary>
    public event Action<string, bool>? Message;

    public event Action<string>? Logged;

    public event Action? OptionsChanged;

    /// <summary>Инструмент включён / выключен роутером: хост включает и возвращает слой «Зоны» (ui-spec §5.2 п. 2).</summary>
    public event Action? Entered;

    public event Action? Exited;

    /// <summary>Макет протяжки для скриншотов (<c>--zones-rect</c>): курсор не двигает прямоугольник.</summary>
    public bool Demo { get; private set; }

    /// <summary>Показать протяжку прямоугольника <c>(x0,z0)-(x1,z1)</c> без мыши (скриншоты).</summary>
    public void DemoDrag(float x0, float z0, float x1, float z1)
    {
        float cell = CellSize();
        _sx = (int)MathF.Floor((MathF.Min(x0, x1) - _world.GridOriginX) / cell);
        _sz = (int)MathF.Floor((MathF.Min(z0, z1) - _world.GridOriginZ) / cell);
        _cx = (int)MathF.Floor(((MathF.Max(x0, x1) - _world.GridOriginX) - 0.01f) / cell);
        _cz = (int)MathF.Floor(((MathF.Max(z0, z1) - _world.GridOriginZ) - 0.01f) / cell);
        _cursorValid = true;
        Demo = true;
        Dragging = true;
        _dirty = true;
        Touch();
        Tick();
    }

    public void SetKind(ZoneChoice kind)
    {
        if (Kind == kind)
        {
            return;
        }

        Kind = kind;
        Touch();
        OptionsChanged?.Invoke();
    }

    public void SetDensity(byte density)
    {
        byte d = density > 0 ? (byte)1 : (byte)0;
        if (Density == d)
        {
            return;
        }

        Density = d;
        Touch();
        OptionsChanged?.Invoke();
    }

    public override void OnEnter()
    {
        _dirty = true;
        Touch();
        OptionsChanged?.Invoke();
        Entered?.Invoke();
    }

    public override void OnExit()
    {
        base.OnExit();
        Demo = false;
        _cursorValid = false;
        Touch();
        Exited?.Invoke();
    }

    public override void OnHover(in ToolPointer at)
    {
        if (!at.OnGround)
        {
            if (_cursorValid)
            {
                _cursorValid = false;
                Touch();
            }

            return;
        }

        float cell = CellSize();
        int cx = (int)MathF.Floor((at.X - _world.GridOriginX) / cell);
        int cz = (int)MathF.Floor((at.Z - _world.GridOriginZ) / cell);
        if (!_cursorValid || cx != _cx || cz != _cz)
        {
            _cursorValid = true;
            _cx = cx;
            _cz = cz;
            _dirty = true;
            Touch();
        }

        Tick();
    }

    public override void OnPress(in ToolPointer at)
    {
        if (at.Button != ToolButton.Primary || !at.OnGround)
        {
            return;
        }

        OnHover(in at);
        Dragging = true;
        _sx = _cx;
        _sz = _cz;
        _dirty = true;
        Touch();
        Tick();
    }

    public override void OnRelease(in ToolPointer at)
    {
        if (at.Button == ToolButton.Secondary)
        {
            OnCancel(); // ПКМ-клик без сдвига - отмена протяжки
            return;
        }

        if (!Dragging)
        {
            return;
        }

        if (at.OnGround)
        {
            OnHover(in at);
        }

        Dragging = false;
        Tick();
        Commit();
        _dirty = true;
        Touch();
    }

    public override void OnCancel()
    {
        if (Dragging)
        {
            Dragging = false;
            _dirty = true;
            Touch();
        }
    }

    /// <summary>Раз в кадр: пересчитать оценку, если сменились прямоугольник или участки в ядре.</summary>
    public void Tick()
    {
        if (!_cursorValid)
        {
            return;
        }

        bool have = _world.TryGetLots(out ReadOnlySpan<LotRenderData> lots, out int ver);
        if (!_dirty && ver == _lotsVersion)
        {
            return;
        }

        _dirty = false;
        _lotsVersion = ver;
        Recount(have ? lots : default);
        Touch();
    }

    /// <summary>Число с пробелом между тысячами (без культуры ru: в инвариантном режиме её нет).</summary>
    public static string Grouped(long v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ');

    private float CellSize() => _world.CellM > 0f ? _world.CellM : DefaultCellM;

    private void Touch() => StateVersion++;

    private void Recount(ReadOnlySpan<LotRenderData> lots)
    {
        float cell = CellSize();
        int ax = Dragging ? Math.Min(_sx, _cx) : _cx;
        int az = Dragging ? Math.Min(_sz, _cz) : _cz;
        int bx = Dragging ? Math.Max(_sx, _cx) : _cx;
        int bz = Dragging ? Math.Max(_sz, _cz) : _cz;
        X0 = _world.GridOriginX + (ax * cell);
        Z0 = _world.GridOriginZ + (az * cell);
        X1 = _world.GridOriginX + ((bx + 1) * cell);
        Z1 = _world.GridOriginZ + ((bz + 1) * cell);

        Paint = 0;
        Abandon = 0;
        Lost = 0;
        Same = 0;
        MarkCount = 0;
        byte kind = (byte)Kind;
        float half = LegacyLotM * 0.5f;
        for (int i = 0; i < lots.Length; i++)
        {
            ref readonly LotRenderData l = ref lots[i];
            // центр внутри, границы включены - как в ядре (Zoning.Apply)
            if (l.X < X0 || l.X > X1 || l.Z < Z0 || l.Z > Z1)
            {
                continue;
            }

            ZoneMark m;
            if ((l.Flags & LotRenderFlags.Lost) != 0)
            {
                m = ZoneMark.Lost;
                Lost++;
            }
            else if (l.Zone == kind && (kind == 0 || l.Density == Density))
            {
                m = ZoneMark.Same;
                Same++;
            }
            else if ((l.Flags & LotRenderFlags.HasBuilding) != 0 && l.Zone != kind)
            {
                // здание стоит на участке своего вида (l.Zone): другой вид (в том числе «стереть») отправит его в Abandoned
                m = ZoneMark.Abandon;
                Abandon++;
            }
            else
            {
                m = ZoneMark.Paint;
                Paint++;
            }

            if (MarkCount < MaxMarks)
            {
                int o = MarkCount * 4;
                if (_world.CellM > 0f && l.TryGetCellBounds(out int c0, out int d0, out int c1, out int d1))
                {
                    _markRect[o] = _world.GridOriginX + (c0 * cell);
                    _markRect[o + 1] = _world.GridOriginZ + (d0 * cell);
                    _markRect[o + 2] = _world.GridOriginX + ((c1 + 1) * cell);
                    _markRect[o + 3] = _world.GridOriginZ + ((d1 + 1) * cell);
                }
                else
                {
                    _markRect[o] = l.X - half;
                    _markRect[o + 1] = l.Z - half;
                    _markRect[o + 2] = l.X + half;
                    _markRect[o + 3] = l.Z + half;
                }

                _markKind[MarkCount++] = m;
            }
        }
    }

    private void Commit()
    {
        if (!_cursorValid)
        {
            return;
        }

        if (OverLimit)
        {
            Message?.Invoke("Не больше " + Grouped(MaxPaintM2) + " м²", true);
            Logged?.Invoke("[zones] rejected: area " + ((long)AreaM2).ToString(System.Globalization.CultureInfo.InvariantCulture) + " > " + MaxPaintM2.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return;
        }

        if (Paint + Abandon == 0)
        {
            Message?.Invoke("Здесь нечего менять: нет участков или они уже такого вида", false);
            return;
        }

        var cmd = Command.MakePaintZone(new PaintZoneCmd { X0 = X0, Z0 = Z0, X1 = X1, Z1 = Z1, ZoneKind = (byte)Kind, Density = Density });
        uint id = Service.Submit(in cmd);
        Logged?.Invoke(string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"[zones] paint {Kind} d{Density} rect {X0:0.#},{Z0:0.#}..{X1:0.#},{Z1:0.#} paint={Paint} abandon={Abandon} -> action #{id}"));
        if (id == 0)
        {
            Message?.Invoke("Команда не принята: очередь занята", true);
        }
    }
}
