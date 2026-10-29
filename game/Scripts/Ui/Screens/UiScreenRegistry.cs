using System;

namespace TrafficCity.Ui;

/// <summary>
/// Реестр экранов: <see cref="UiRoot"/> создаёт их в этом порядке (он же порядок отрисовки: ниже по списку - выше на экране)
/// и раздаёт ввод и Esc по <see cref="UiScreen.EscPriority"/> (при равном - в порядке списка). Новый экран - одна строка здесь.
/// </summary>
public static class UiScreenRegistry
{
    public static readonly Func<UiScreen>[] All =
    {
        () => new TopBarScreen(),
        () => new ToolbarScreen(),
        () => new ToolOptionsScreen(),
        () => new LegendScreen(),
        () => new InspectorScreen(),
        () => new CityScreen(),
        () => new NotifyScreen(),
        () => new CursorLayerScreen(),
        () => new MenuScreen(),
        () => new ConfirmScreen(),
    };
}
