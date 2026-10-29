using Godot;
using Sim.Core.Api;

namespace TrafficCity.Ui;

/// <summary>
/// Панель «Город» (UI16) в правой док-панели и замки инструментов в тулбаре (progression.md §4.2): кнопка «Город  C», клавиша C, Esc закрывает.
/// Данные - <see cref="SimHost.CityPanel"/> (1 Гц); замки пересчитываются только при новой версии данных.
/// </summary>
public sealed partial class UiRoot
{
    private static readonly (string Id, Feature A, Feature B)[] _toolLocks =
    {
        ("zones", Feature.Zoning, Feature.Zoning), ("transit", Feature.BusLines, Feature.BusLanes), ("nodes", Feature.SignalsFixed, Feature.TurnRules),
    };

    private UiCityPanel _city = null!;
    private Button _cityButton = null!;
    private int _lockVersion = -1;
    private int _cityBottomFrames;   // --ui-city-bottom: через несколько кадров прокрутить к вехам
    private bool _cityAtStart;   // --ui-city: открыть панель, как только город готов (скриншоты)

    /// <summary>Панель «Город» видна.</summary>
    public bool CityOpen => _city.Visible;

    private void BuildCity()
    {
        _city = new UiCityPanel(_rightDock);
        _city.CloseRequested += () => SetCityOpen(false);
        _cityAtStart = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--ui-city") >= 0;
        _cityBottomFrames = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--ui-city-bottom") >= 0 ? 20 : 0;
        if (_toolbar.GetChildCount() > 0 && _toolbar.GetChild(0) is HBoxContainer row)
        {
            _cityButton = MakeButton("Город  C", toggle: true, disabled: true, 96f);
            _cityButton.TooltipText = "Показатели города и вехи (C): нужно: сценарий с жителями (residents)";
            _cityButton.Toggled += SetCityOpen;
            row.AddChild(_cityButton);
            row.MoveChild(_cityButton, _infoButton.GetIndex() + 1);
        }
    }

    private void ToggleCity() => SetCityOpen(!_city.Visible);

    /// <summary>Закрыть панель (Esc); true - была открыта.</summary>
    private bool CloseCity()
    {
        if (!_city.Visible)
        {
            return false;
        }

        SetCityOpen(false);
        return true;
    }

    private void SetCityOpen(bool on)
    {
        _city.Visible = on;
        _cityButton?.SetPressedNoSignal(on);
        if (_host != null)
        {
            _host.CityPanelWanted = on;
        }
    }

    private void UpdateCity()
    {
        if (_host is not { IsReady: true } h || !h.HasCity)
        {
            return;
        }

        if (_cityButton is { Disabled: true })
        {
            _cityButton.Disabled = false;
            _cityButton.TooltipText = "Показатели города и вехи (C)";
        }

        if (_cityAtStart)
        {
            _cityAtStart = false;
            SetCityOpen(true);
        }

        if (_cityBottomFrames > 0 && --_cityBottomFrames == 0)
        {
            _city.ScrollToEnd();
        }

        CityPanelData d = h.CityPanel;
        _city.Update(d, h.DayTicks);
        if (d.Version != _lockVersion && d.Version > 0)
        {
            _lockVersion = d.Version;
            ApplyToolLocks(d);
        }
    }

    /// <summary>Кнопки категорий с закрытыми признаками - серые, в подсказке - веха, которая откроет (принцип 2 ui-spec.md: замок виден до клика).</summary>
    private void ApplyToolLocks(CityPanelData d)
    {
        if (!d.HasProgression)
        {
            return;
        }

        foreach ((string id, Feature a, Feature b) in _toolLocks)
        {
            if (!_toolButtons.TryGetValue(id, out Button? btn))
            {
                continue;
            }

            bool open = d.FeatureOpen(a) || d.FeatureOpen(b);
            string title = id switch
            {
                "zones" => "Зоны (Z)",
                "transit" => "Транспорт (T)",
                _ => "Узлы (J)",
            };
            if (open == !btn.Disabled)
            {
                continue;
            }

            btn.Disabled = !open;
            if (open)
            {
                btn.TooltipText = title;
                continue;
            }

            int m = d.MilestoneFor(a);
            if (m < 0 && b != a)
            {
                m = d.MilestoneFor(b);
            }

            btn.TooltipText = m >= 0
                ? title + ": ещё не открыто — откроется на вехе " + (m + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + " (" + UiCityPanel.FeatureName(a) + "). Вехи — панель «Город» (C)"
                : title + ": ещё не открыто в этом сценарии";
            if (_router.Tool?.Id == id)
            {
                _router.SetTool(null);
            }
        }
    }
}
