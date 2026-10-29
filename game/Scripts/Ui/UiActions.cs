using Godot;

namespace TrafficCity.Ui;

/// <summary>
/// Действия <see cref="InputMap"/> вместо <c>Key.*</c> в коде (ui-spec.md §11, §12): ремап клавиш потом - подмена событий действия, а не правка кода.
/// Регистрируются в коде идемпотентно (<see cref="Register"/>), привязки - прежние (ui-spec.md §9). Буквы - по физической клавише,
/// чтобы WASD/Q/E работали и на русской раскладке; F-клавиши, Space, Esc, +/- - по коду символа.
/// </summary>
public static class UiActions
{
    // камера (RtsCamera: удерживаемые клавиши)
    public static readonly StringName CamForward = "cam_forward";
    public static readonly StringName CamBack = "cam_back";
    public static readonly StringName CamLeft = "cam_left";
    public static readonly StringName CamRight = "cam_right";
    public static readonly StringName CamRotateLeft = "cam_rotate_left";
    public static readonly StringName CamRotateRight = "cam_rotate_right";
    public static readonly StringName CamFast = "cam_fast";

    // режимы и слои
    public static readonly StringName InfoToggle = "info_toggle";
    public static readonly StringName InfoLayer1 = "info_layer_1";
    public static readonly StringName InfoLayer2 = "info_layer_2";
    public static readonly StringName InfoLayer3 = "info_layer_3";
    public static readonly StringName SignalsOverlay = "signals_overlay";
    public static readonly StringName PerfHud = "perf_hud";
    public static readonly StringName RenderPresetToggle = "render_preset_toggle";
    public static readonly StringName PedModeCycle = "ped_mode_cycle";

    // время
    public static readonly StringName TimePause = "time_pause";
    public static readonly StringName TimeFaster = "time_faster";
    public static readonly StringName TimeSlower = "time_slower";
    public static readonly StringName TimeOfDayNext = "time_of_day_next";
    public static readonly StringName TimeOfDayPrev = "time_of_day_prev";

    // сетка и снап (RP10)
    public static readonly StringName GridSnapToggle = "grid_snap_toggle";
    public static readonly StringName GridShowToggle = "grid_show_toggle";
    public static readonly StringName SnapOverride = "snap_override";

    // инструменты (UI4): R/X включают «Дороги» и «Снос», остальное работает внутри инструмента «Дороги»
    public static readonly StringName ToolRoads = "tool_roads";
    public static readonly StringName ToolBulldoze = "tool_bulldoze";
    public static readonly StringName ToolZones = "tool_zones";
    public static readonly StringName RoadsPrevProfile = "roads_profile_prev";
    public static readonly StringName RoadsNextProfile = "roads_profile_next";
    public static readonly StringName RoadsReplace = "roads_replace";
    public static readonly StringName RoadsMirror = "roads_mirror";
    public static readonly StringName Undo = "tool_undo";
    public static readonly StringName Redo = "tool_redo";

    // сохранение (меню, T6.1)
    public static readonly StringName QuickSave = "quick_save";
    public static readonly StringName QuickLoad = "quick_load";

    /// <summary>C: панель «Город» (UI16).</summary>
    public static readonly StringName CityPanel = "city_panel";

    /// <summary>F10: настройки.</summary>
    public static readonly StringName Settings = "open_settings";

    /// <summary>Esc: отмена протяжки -> выход из инструмента -> закрыть инспектор -> выйти из инфо -> меню (ui-spec.md §2).</summary>
    public static readonly StringName Cancel = "game_cancel";

    /// <summary>Все действия в порядке регистрации (для самопроверки и будущего экрана ремапа).</summary>
    public static readonly StringName[] All =
    {
        CamForward, CamBack, CamLeft, CamRight, CamRotateLeft, CamRotateRight, CamFast,
        InfoToggle, InfoLayer1, InfoLayer2, InfoLayer3, SignalsOverlay, PerfHud, RenderPresetToggle, PedModeCycle,
        TimePause, TimeFaster, TimeSlower, TimeOfDayNext, TimeOfDayPrev,
        GridSnapToggle, GridShowToggle, SnapOverride, ToolRoads, ToolBulldoze, ToolZones, RoadsPrevProfile, RoadsNextProfile, RoadsReplace, RoadsMirror, Undo, Redo,
        QuickSave, QuickLoad, CityPanel, Settings, Cancel,
    };

    /// <summary>Добавить действия, которых ещё нет в <see cref="InputMap"/> (повторный вызов ничего не меняет; ремап из файла настроек не затирается).</summary>
    public static void Register()
    {
        Add(CamForward, Phys(Key.W));
        Add(CamBack, Phys(Key.S));
        Add(CamLeft, Phys(Key.A));
        Add(CamRight, Phys(Key.D));
        Add(CamRotateLeft, Phys(Key.Q));
        Add(CamRotateRight, Phys(Key.E));
        Add(CamFast, Code(Key.Shift));

        Add(InfoToggle, Code(Key.F1));
        Add(InfoLayer1, Phys(Key.Key1));
        Add(InfoLayer2, Phys(Key.Key2));
        Add(InfoLayer3, Phys(Key.Key3));
        Add(SignalsOverlay, Code(Key.F2));
        Add(PerfHud, Code(Key.F3));
        Add(RenderPresetToggle, Code(Key.F4));
        Add(PedModeCycle, Phys(Key.F));

        Add(TimePause, Code(Key.Space));
        Add(TimeFaster, Code(Key.Equal), Code(Key.Plus), Code(Key.KpAdd));
        Add(TimeSlower, Code(Key.Minus), Code(Key.KpSubtract));
        Add(TimeOfDayNext, Phys(Key.T));
        Add(TimeOfDayPrev, Phys(Key.T, shift: true));

        Add(GridSnapToggle, Phys(Key.G));
        Add(GridShowToggle, Phys(Key.G, shift: true));
        Add(SnapOverride, Code(Key.Shift));

        Add(ToolRoads, Phys(Key.R));
        Add(ToolBulldoze, Phys(Key.X));
        Add(ToolZones, Phys(Key.Z));
        Add(RoadsPrevProfile, Phys(Key.Bracketleft));
        Add(RoadsNextProfile, Phys(Key.Bracketright));
        Add(RoadsReplace, Phys(Key.U));
        Add(RoadsMirror, Phys(Key.M));
        Add(Undo, Phys(Key.Z, ctrl: true));
        Add(Redo, Phys(Key.Y, ctrl: true), Phys(Key.Z, shift: true, ctrl: true));

        Add(QuickSave, Code(Key.F5));
        Add(QuickLoad, Code(Key.F9));
        Add(CityPanel, Phys(Key.C));
        Add(Settings, Code(Key.F10));

        Add(Cancel, Code(Key.Escape));
    }

    /// <summary>
    /// Нажатие пары «действие / то же с Shift» (G и Shift+G, T и Shift+T): сравнение модификаторов точное, иначе голая клавиша
    /// срабатывала бы и при Shift.
    /// </summary>
    public static bool Pressed(InputEvent e, StringName action) => e.IsActionPressed(action, false, true);

    private static InputEventKey Phys(Key code, bool shift = false, bool ctrl = false) => new() { PhysicalKeycode = code, ShiftPressed = shift, CtrlPressed = ctrl };

    private static InputEventKey Code(Key code) => new() { Keycode = code };

    private static void Add(StringName action, params InputEventKey[] events)
    {
        if (InputMap.HasAction(action))
        {
            return;
        }

        InputMap.AddAction(action);
        foreach (InputEventKey e in events)
        {
            InputMap.ActionAddEvent(action, e);
        }
    }
}
