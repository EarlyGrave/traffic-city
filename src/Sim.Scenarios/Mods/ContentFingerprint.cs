using System;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using Sim.Core.Api;

namespace Sim.Scenarios.Mods;

/// <summary>
/// Хэши контента и отпечаток набора пакетов (modding.md §5.3). Отпечаток - функция записей пакетов в <c>MANI</c>
/// (вид 4), отдельно в сейве не хранится; <c>StateHash</c> ядра он не расширяет.
/// </summary>
public static class ContentFingerprint
{
    /// <summary>Отпечаток без пакетов (сценарии v2): xxh64 пустой последовательности.</summary>
    public static ulong Empty => new XxHash64().GetCurrentHashAsUInt64();

    /// <summary>Версия пакета в <c>MANI.Version</c>: <c>MAJOR &lt;&lt; 20 | MINOR &lt;&lt; 10 | PATCH</c>; часть ≥ 1024 - <c>OutOfRange</c>.</summary>
    public static uint PackVersion(SemVer v, string packageId)
    {
        if (v.Major is < 0 or >= 1024 || v.Minor is < 0 or >= 1024 || v.Patch is < 0 or >= 1024)
        {
            throw new ScenarioException(
                $"package '{packageId}': version part >= 1024 cannot be stored in the manifest", ModError.OutOfRange);
        }

        return ((uint)v.Major << 20) | ((uint)v.Minor << 10) | (uint)v.Patch;
    }

    /// <summary>Обратное к <see cref="PackVersion"/>.</summary>
    public static SemVer UnpackVersion(uint v) => new((int)(v >> 20), (int)((v >> 10) & 1023), (int)(v & 1023));

    /// <summary>xxh64 параметров дороги: <c>kind u8, lanes_ab u8, lanes_ba u8, 0 u8, speed f32</c> (м/с, little-endian). Визуал не входит.</summary>
    public static ulong RoadHash(RoadEntry e)
    {
        Span<byte> b = stackalloc byte[8];
        b[0] = (byte)e.Kind;
        b[1] = (byte)e.LanesAB;
        b[2] = (byte)e.LanesBA;
        b[3] = 0;
        BinaryPrimitives.WriteInt32LittleEndian(b[4..], BitConverter.SingleToInt32Bits(e.SpeedLimit));
        return XxHash64.HashToUInt64(b);
    }

    /// <summary>xxh64 симуляционных полей типа здания (все, кроме <c>VisualRow</c>: он зависит от порядка каталога, а не от контента).</summary>
    public static ulong BuildingHash(BuildingTypeDesc d)
    {
        Span<byte> b = stackalloc byte[40];
        b[0] = (byte)d.Kind;
        b[1] = d.Density;
        BinaryPrimitives.WriteUInt16LittleEndian(b[2..], d.Reserved);
        BinaryPrimitives.WriteInt32LittleEndian(b[4..], d.Capacity);
        BinaryPrimitives.WriteInt32LittleEndian(b[8..], d.ShopCapacity);
        BinaryPrimitives.WriteInt32LittleEndian(b[12..], d.ShiftStartSlot);
        BinaryPrimitives.WriteInt32LittleEndian(b[16..], d.ShiftSlots);
        BinaryPrimitives.WriteInt32LittleEndian(b[20..], d.ConstructDays);
        BinaryPrimitives.WriteInt32LittleEndian(b[24..], d.AttractQ8);
        if (d.ServiceKind != ServiceKind.None || d.BuildCost != 0)
        {
            // служба (BL3, building-lifecycle.md §2.10): поля службы - в хвост; у прочих типов отпечаток прежний
            Span<byte> s = stackalloc byte[48];
            b.CopyTo(s);
            BinaryPrimitives.WriteInt32LittleEndian(s[28..], d.ParkingCapacity);
            BinaryPrimitives.WriteInt32LittleEndian(s[32..], (int)d.ParkingAccess);
            BinaryPrimitives.WriteInt32LittleEndian(s[36..], d.ParkingFee);
            BinaryPrimitives.WriteInt32LittleEndian(s[40..], d.BuildCost);
            s[44] = (byte)d.ServiceKind;
            s[45] = d.Vehicles;
            s[46] = d.VehicleType;
            s[47] = 0;
            return XxHash64.HashToUInt64(s);
        }

        if (d.ParkingCapacity == 0 && d.ParkingAccess == ParkingAccess.Private && d.ParkingFee == 0)
        {
            return XxHash64.HashToUInt64(b[..28]);   // без парковки отпечаток прежний (пакеты до world_version 10)
        }

        BinaryPrimitives.WriteInt32LittleEndian(b[28..], d.ParkingCapacity);
        BinaryPrimitives.WriteInt32LittleEndian(b[32..], (int)d.ParkingAccess);
        BinaryPrimitives.WriteInt32LittleEndian(b[36..], d.ParkingFee);
        return XxHash64.HashToUInt64(b);
    }

    /// <summary>
    /// <c>ContentHash</c> пакета: xxh64 по конкатенации <c>ContentHash</c> его ассетов в порядке каталога
    /// (типы ТС, затем дороги, затем здания; u64 LE).
    /// </summary>
    public static ulong PackageHash(string packageId, ContentCatalog cat)
    {
        var h = new XxHash64();
        Span<byte> b = stackalloc byte[8];
        foreach (VehicleEntry e in cat.Vehicles)
        {
            if (e.Id.Package == packageId)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(b, AssetManifestBuilder.ParamsHash(e.Desc));
                h.Append(b);
            }
        }

        foreach (RoadEntry e in cat.Roads)
        {
            if (e.Id.Package == packageId)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(b, RoadHash(e));
                h.Append(b);
            }
        }

        foreach (BuildingEntry e in cat.Buildings)
        {
            if (e.Id.Package == packageId)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(b, BuildingHash(e.Desc));
                h.Append(b);
            }
        }

        foreach (ServiceEntry e in cat.Services)
        {
            if (e.Id.Package == packageId)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(b, ServiceHash(e, cat));
                h.Append(b);
            }
        }

        return h.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// xxh64 симуляционных полей записи службы (services.md §2.2, SR2): фонд, уровни, апгрейды, вызовы, визиты. Ссылки на тип здания и машину выезда - <c>ContentHash</c> ассета, а не индекс
    /// каталога: отпечаток не зависит от порядка пакетов. Пакет без записей служб - отпечаток прежний.
    /// </summary>
    public static ulong ServiceHash(ServiceEntry e, ContentCatalog cat)
    {
        var h = new XxHash64();
        byte[] b = new byte[8];
        void I(int v)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b, v);
            h.Append(b.AsSpan(0, 4));
        }

        void U(ulong v)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(b, v);
            h.Append(b);
        }

        I((int)e.Fund);
        I(e.Levels.Length);
        foreach (ServiceLevelDesc l in e.Levels)
        {
            U((uint)l.Type < (uint)cat.Buildings.Count ? BuildingHash(cat.Buildings[l.Type].Desc) : 0);
            U((uint)l.DispatchVehicle < (uint)cat.Vehicles.Count ? AssetManifestBuilder.ParamsHash(cat.Vehicles[l.DispatchVehicle].Desc) : 0);
            I((int)l.Roles);
            I(l.LotMinF | (l.LotMinD << 8) | (l.Milestone << 16));
            I(l.BuildCost);
            I(l.UpkeepDay);
            I(l.CapacityDay);
            I(l.Beds);
            I(l.Icu);
            I((ushort)l.OpenSlot | ((ushort)l.CloseSlot << 16));
            I(l.StaySlots);
            I(l.Units);
            I(l.PerUnitDay);
            I(l.FirstUpgrade);
            I(l.UpgradeCount);
        }

        I(e.Upgrades.Length);
        foreach (ServiceUpgradeDesc u in e.Upgrades)
        {
            I(u.Cost);
            I(u.UpkeepDay);
            I(u.Milestone | ((int)u.Effect << 8));
            I(u.Value);
        }

        I(e.Calls.Length);
        foreach (ServiceCallDesc c in e.Calls)
        {
            I((int)c.Kind | (c.Flags << 8));
            I(c.OnsiteS);
            I(c.DeadlineS);
            I(c.PerCall);
        }

        I(e.Visits.Length);
        foreach (ServiceVisitDesc v in e.Visits)
        {
            I((int)v.Reason | ((int)v.MinRole << 8));
            I(v.PerVisit);
            I(v.MaxCostS);
        }

        return h.GetCurrentHashAsUInt64();
    }

    /// <summary>
    /// Отпечаток по манифесту: xxh64 по записям вида 4 в порядке <c>MANI</c>: <c>(id UTF-8, 0x00, Version u32 LE, ContentHash u64 LE)</c>.
    /// Манифест без пакетов даёт <see cref="Empty"/>.
    /// </summary>
    public static ulong Compute(ReadOnlySpan<AssetRef> refs, ReadOnlySpan<byte> ids)
    {
        var h = new XxHash64();
        Span<byte> tail = stackalloc byte[13];
        for (int i = 0; i < refs.Length; i++)
        {
            AssetRef r = refs[i];
            if (r.Kind != (byte)Sim.Core.Api.AssetKind.Package)
            {
                continue;
            }

            h.Append(ids.Slice((int)r.IdOffset, (int)r.IdLength));
            tail[0] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(tail[1..], r.Version);
            BinaryPrimitives.WriteUInt64LittleEndian(tail[5..], r.ContentHash);
            h.Append(tail);
        }

        return h.GetCurrentHashAsUInt64();
    }

    /// <summary>Отпечаток загруженного сценария: v3 - по манифесту каталога, v2 - <see cref="Empty"/>.</summary>
    public static ulong For(LoadedScenario sc)
    {
        if (sc.Catalog == null || sc.Packages == null)
        {
            return Empty;
        }

        AssetManifestBuilder.ForCatalog(sc.Catalog, sc.Packages, out AssetRef[] refs, out byte[] ids);
        return Compute(refs, ids);
    }

    public static string Hex(ulong fingerprint) => fingerprint.ToString("x16", CultureInfo.InvariantCulture);
}
