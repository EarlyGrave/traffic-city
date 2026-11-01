using Sim.Core.Api;

namespace Sim.Core.Agents;

/// <summary>
/// Биты доступа класса ТС (vehicle-classes.md §2.1, ADR-0039). Права машины = <c>TypeAccess[Type] | (OnCall ? Emergency : 0)</c>.
/// В VC1 только данные; маски запрета полос и улиц и проверки по ним - VC2, приоритет на вызове - VC3.
/// </summary>
internal static class VehicleAccess
{
    public const byte Car = 0x01;
    public const byte Bus = 0x02;
    public const byte Van = 0x04;
    public const byte Truck = 0x08;
    public const byte Special = 0x10;
    public const byte Emergency = 0x20;

    /// <summary>Бит доступа класса; неизвестная категория (отсечена валидатором типов) - 0.</summary>
    public static byte OfCategory(VehicleCategory c) => c switch
    {
        VehicleCategory.Car => Car,
        VehicleCategory.Bus => Bus,
        VehicleCategory.Van => Van,
        VehicleCategory.Truck => Truck,
        VehicleCategory.Special => Special,
        _ => 0,
    };

    /// <summary>Права «без ограничений» для запросов без машины (тесты, инструменты): проходят любую маску запрета.</summary>
    public const byte All = 0xFF;

    /// <summary>Разрешена ли полоса или улица с маской <paramref name="deny"/> правам <paramref name="access"/>: хватает одного неразрешённого запретом бита.</summary>
    public static bool Allowed(byte access, byte deny) => (access & ~deny) != 0;

    /// <summary>Таблица <c>TypeAccess</c> по набору типов: 256 элементов (номер типа - байт), за набором - 0.</summary>
    public static byte[] ForTypes(VehicleTypeDesc[] types)
    {
        var a = new byte[256];
        for (int i = 0; i < types.Length && i < a.Length; i++)
        {
            a[i] = OfCategory(types[i].Category);
        }

        return a;
    }
}
