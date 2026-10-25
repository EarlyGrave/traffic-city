namespace Sim.Core.Api;

public enum LodMode : int
{
    Off = 0,       // LOD нет: всё micro, ядро побитово как без секции (ADR-0013 п.9)
    Auto = 1,      // уровни ячеек выбирает контроллер (L3)
    AllMeso = 2,   // все ячейки meso (диагностика цены meso, тест эквивалентности)
}

public enum LodFocusKind : byte
{
    Clear = 0,
    Focus = 1,     // слоты 0..3 (камера)
    Pin = 2,       // слоты 4..7 (закрепление)
}

/// <summary>Круг на карте (начальный слот фокуса/закрепления): центр и радиус в метрах; ядро квантует в LOD-ячейки.</summary>
public struct LodPlaceDesc
{
    public float X, Z, RadiusM;
}

/// <summary>Обход фокуса по точкам пути (только хост/bench: ядро поле не читает; сдвиг шлют команды <c>SetLodFocus</c>).</summary>
public struct LodTourDesc
{
    public int PeriodTicks;                // 0 = тура нет
    public float RadiusM;
    public float[]? PathX, PathZ;          // параллельные массивы точек пути
}

/// <summary>
/// Уровни детализации симуляции (ADR-0013, <c>sim-lod.md</c> §4, <c>scenario-format.md</c> §2.1 "lod"). Нулевая структура = <see cref="LodMode.Off"/>.
/// При Mode != Off нулевые числовые поля заменяются дефолтами в <c>Create</c> (CellM 1000, MicroBudget 15000, EvalTicks 100, HoldTicks 600,
/// FocusMarginCells 1, CongestionShare 0.1, CongestionMin 10, MesoHeadwayScale = MesoAccelScale = 1); булевы без дефолта (загрузчик ставит true).
/// </summary>
public struct LodDesc
{
    public LodMode Mode;
    public float CellM;                    // сторона LOD-ячейки, м
    public int MicroBudget;                // машин в micro-ячейках (мягкий предел контроллера)
    public int EvalTicks;                  // период оценки уровней, тиков
    public int HoldTicks;                  // micro-ячейка не понижается раньше, тиков
    public int FocusMarginCells;           // кольцо ячеек вокруг фокуса
    public bool FollowSelected;            // ячейка выбранной машины - фокус
    public bool FocusCommands;             // false: SetLodFocus(Focus) отклоняется
    public float CongestionShare;          // доля стоящих >= 30 с в ячейке для «пробки»
    public int CongestionMin;              // и не меньше стольких машин
    public float MesoHeadwayScale;         // множитель T в meso
    public float MesoAccelScale;           // множитель a в meso
    public LodPlaceDesc[]? Focus;          // начальные слоты фокуса 0..3 (<= 4)
    public LodPlaceDesc[]? Pinned;         // начальные закрепления 4..7 (<= 4)
    public LodTourDesc Tour;               // только хост
}
