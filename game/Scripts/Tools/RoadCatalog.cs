using System;
using System.Collections.Generic;
using System.Globalization;
using Sim.Core.Api;
using Sim.Core.Network;

namespace TrafficCity.Tools;

/// <summary>
/// Вариант дороги в выпадающем списке инструмента «Дороги»: профиль каталога ядра (<c>Profile &gt;= 0</c>) либо встроенная пара «улица / магистраль»
/// для сценария без каталога (legacy, <c>Profile = -1</c>). В команде профиль передаётся как <c>Profile + 1</c> (0 - без профиля).
/// </summary>
public readonly record struct RoadChoice(int Profile, string Name, RoadKind Kind, byte LanesFwd, byte LanesBwd, float WidthM, float SpeedMps)
{
    /// <summary>Полосы по сторонам разные: имеет смысл зеркало (<c>M</c>).</summary>
    public bool Asymmetric => LanesFwd != LanesBwd;

    /// <summary>Значение поля <c>Profile</c> команд: индекс в каталоге + 1, 0 - без профиля.</summary>
    public ushort CommandProfile => (ushort)(Profile + 1);
}

/// <summary>
/// Каталог профилей для клиента (ui-spec §5.1 п. 2): берётся из ядра (<c>NetworkView.RoadProfiles</c>), а не из констант. В записи каталога нет строкового id,
/// поэтому имя собирается из данных: тип, полосы по направлениям, ширина. Без каталога - две встроенные позиции (прежняя геометрия).
/// Чистый C# без Godot.
/// </summary>
public sealed class RoadCatalog
{
    /// <summary>Скорость улицы по умолчанию, м/с (40 км/ч, как у генератора города).</summary>
    public const float StreetSpeed = 40f / 3.6f;

    /// <summary>Скорость магистрали по умолчанию, м/с (60 км/ч).</summary>
    public const float ArterialSpeed = 60f / 3.6f;

    private readonly RoadChoice[] _choices;

    private RoadCatalog(RoadChoice[] choices, bool fromProfiles)
    {
        _choices = choices;
        FromProfiles = fromProfiles;
    }

    public ReadOnlySpan<RoadChoice> Choices => _choices;

    public int Count => _choices.Length;

    /// <summary>Каталог пришёл из ядра (режим профилей); иначе встроенные варианты.</summary>
    public bool FromProfiles { get; }

    public RoadChoice this[int i] => _choices[i];

    /// <summary>Каталог по вью сети; пустой каталог ядра - <see cref="Legacy"/>.</summary>
    public static RoadCatalog FromView(in NetworkView v)
    {
        int n = v.RoadProfiles.Length;
        if (n == 0)
        {
            return Legacy();
        }

        var list = new List<RoadChoice>(n);
        for (int i = 0; i < n; i++)
        {
            RoadProfileData p = v.RoadProfiles[i];
            var kind = (RoadKind)p.Kind;
            float width = p.WidthMm / 1000f;
            list.Add(new RoadChoice(i, NameOf(kind, p.LanesFwd, p.LanesBwd, width), kind, p.LanesFwd, p.LanesBwd, width, kind == RoadKind.Arterial ? ArterialSpeed : StreetSpeed));
        }

        return new RoadCatalog(list.ToArray(), true);
    }

    public static RoadCatalog Legacy() => new(
        new[]
        {
            new RoadChoice(-1, NameOf(RoadKind.Street, 1, 1, 10f), RoadKind.Street, 1, 1, 10f, StreetSpeed),
            new RoadChoice(-1, NameOf(RoadKind.Arterial, 2, 2, 17f), RoadKind.Arterial, 2, 2, 17f, ArterialSpeed),
        },
        false);

    /// <summary>Номер варианта по индексу профиля ядра; -1, если такого нет (сегмент без профиля).</summary>
    public int IndexOfProfile(int profile)
    {
        for (int i = 0; i < _choices.Length; i++)
        {
            if (_choices[i].Profile == profile && profile >= 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>«Улица 1+1 · 16 м», «Улица, 2 в одну сторону · 16 м», «Магистраль 2+2 · 24 м».</summary>
    public static string NameOf(RoadKind kind, int fwd, int bwd, float widthM)
    {
        string k = kind == RoadKind.Arterial ? "Магистраль" : "Улица";
        string lanes = fwd > 0 && bwd > 0
            ? string.Create(CultureInfo.InvariantCulture, $" {fwd}+{bwd}")
            : string.Create(CultureInfo.InvariantCulture, $", {Math.Max(fwd, bwd)} в одну сторону");
        return string.Create(CultureInfo.InvariantCulture, $"{k}{lanes} · {widthM:0.#} м");
    }
}
