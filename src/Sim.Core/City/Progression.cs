using System;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>
/// Состояние прогрессии мира (progression.md §4.4, ADR-0034): определения вех из сценария (POD, без строк) и что уже открыто. Существует только при <c>progression.enabled</c>
/// и режиме жителей (<c>World.Progression</c>, иначе <c>null</c> - всё открыто, ни одна ветка не исполняется). PG1 - данные, сейв (<c>PROG</c>) и хэш; проверки вех и замков - PG3.
/// </summary>
internal sealed class ProgressionState
{
    public const int MaxMilestones = 32;
    public const int MaxAreas = 8;
    public const int FeatureCount = 11;
    public const ulong AllFeatures = (1UL << FeatureCount) - 1;

    // ---- определения (сценарий; в сейве целиком, в хэш входят) ----
    public readonly MilestoneDesc[] Defs;
    public readonly int[] ProfileIdx;
    public readonly AreaRect[] Areas;
    public int MinLegs;
    public ulong LockedFeatures;   // закрытые на старте (UI: какая веха откроет)
    public int ProfileCount;       // размер каталога профилей при создании (длина битсета в битах)

    // ---- состояние ----
    public int Reached;
    public ulong Features;         // открытые признаки
    public readonly ulong[] Profiles;   // открытые профили (бит = индекс каталога)
    public int CreditDaysBonus;
    public int AreaStage;
    public ulong GatedNotified;    // вехи, о которых уже было MilestoneGated
    public readonly long[] ReachedTick;

    public ProgressionState(int milestones, int profileIdx, int areas, int profileWords)
    {
        Defs = new MilestoneDesc[milestones];
        ProfileIdx = new int[profileIdx];
        Areas = new AreaRect[areas];
        Profiles = new ulong[profileWords];
        ReachedTick = new long[milestones];
        Array.Fill(ReachedTick, -1L);
    }

    public static int WordsFor(int profiles) => Math.Max(1, (profiles + 63) / 64);

    /// <summary>Состояние старта по описанию (проверенному <see cref="Validate"/>): открыто всё, кроме закрытого сценарием.</summary>
    public static ProgressionState FromDesc(in ProgressionDesc d, int profileCount)
    {
        MilestoneDesc[] ms = d.Milestones ?? Array.Empty<MilestoneDesc>();
        int[] idx = d.ProfileIdx ?? Array.Empty<int>();
        AreaRect[] areas = d.Areas ?? Array.Empty<AreaRect>();
        var s = new ProgressionState(ms.Length, idx.Length, areas.Length, WordsFor(profileCount));
        ms.AsSpan().CopyTo(s.Defs);
        idx.AsSpan().CopyTo(s.ProfileIdx);
        areas.AsSpan().CopyTo(s.Areas);
        s.MinLegs = d.MinLegs;
        s.LockedFeatures = d.LockedFeatures;
        s.ProfileCount = profileCount;
        s.Features = AllFeatures & ~d.LockedFeatures;
        for (int p = 0; p < profileCount; p++)
        {
            s.Profiles[p >> 6] |= 1UL << (p & 63);
        }

        int[] locked = d.LockedProfiles ?? Array.Empty<int>();
        foreach (int p in locked)
        {
            s.Profiles[p >> 6] &= ~(1UL << (p & 63));
        }

        return s;
    }

    public bool IsFeatureUnlocked(Feature f) => (Features & (1UL << (int)f)) != 0;

    public bool IsProfileUnlocked(int profile) => (uint)profile < (uint)ProfileCount && (Profiles[profile >> 6] & (1UL << (profile & 63))) != 0;

    /// <summary>
    /// Проверка описания (одна для Create и загрузки сейва, Rust - то же): вехи <= 32 с растущим порогом населения, ворота известных показателей, признаки - только закрытые,
    /// профили - в каталоге, области <= 8 с <c>Min &lt;= Max</c>, <c>Area</c> в диапазоне. false - описание испорчено.
    /// </summary>
    public static bool Validate(ReadOnlySpan<MilestoneDesc> ms, ReadOnlySpan<int> profileIdx, ReadOnlySpan<AreaRect> areas, int minLegs, ulong locked, int profileCount)
    {
        if (ms.Length > MaxMilestones || areas.Length > MaxAreas || minLegs < 0 || (locked & ~AllFeatures) != 0 || (locked & 1UL) != 0)
        {
            return false;
        }

        for (int i = 0; i < profileIdx.Length; i++)
        {
            if ((uint)profileIdx[i] >= (uint)profileCount)
            {
                return false;
            }
        }

        for (int i = 0; i < areas.Length; i++)
        {
            ref readonly AreaRect a = ref areas[i];
            if (!(a.MinX <= a.MaxX && a.MinZ <= a.MaxZ))
            {
                return false;
            }
        }

        int prev = 0;
        for (int i = 0; i < ms.Length; i++)
        {
            ref readonly MilestoneDesc m = ref ms[i];
            bool ok = m.Population > prev && m.Grant >= 0 && m.CreditDays >= 0 && m.Area >= -1 && m.Area < areas.Length && (m.Flags & ~MilestoneFlags.GatesAny) == 0 &&
                (m.Features & ~locked) == 0 && m.ProfileFirst >= 0 && m.ProfileCount >= 0 && (long)m.ProfileFirst + m.ProfileCount <= profileIdx.Length &&
                GateOk(in m.G0) && GateOk(in m.G1) && (m.G0.Kpi != KpiKind.None || m.G1.Kpi == KpiKind.None);
            if (!ok)
            {
                return false;
            }

            prev = m.Population;
        }

        return true;
    }

    private static bool GateOk(in KpiGate g) => g.Kpi <= KpiKind.WorkMin && g.IsMin <= 1 && g.R == 0 && (g.Kpi != KpiKind.None || (g.IsMin == 0 && g.Value == 0));

    /// <summary>Индекс вехи, которая откроет признак (первая после взятых), или -1 (не откроется / уже открыт).</summary>
    public int MilestoneForFeature(Feature f)
    {
        ulong bit = 1UL << (int)f;
        for (int i = Reached; i < Defs.Length; i++)
        {
            if ((Defs[i].Features & bit) != 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Индекс вехи, которая откроет профиль, или -1.</summary>
    public int MilestoneForProfile(int profile)
    {
        for (int i = Reached; i < Defs.Length; i++)
        {
            ref readonly MilestoneDesc m = ref Defs[i];
            for (int k = 0; k < m.ProfileCount; k++)
            {
                if (ProfileIdx[m.ProfileFirst + k] == profile)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>Раздел хэша прогрессии (§4.4): определения и состояние.</summary>
    public void AddToHash(ref Fnv1a64 h)
    {
        h.Add(Defs.Length);
        for (int i = 0; i < Defs.Length; i++)
        {
            ref readonly MilestoneDesc m = ref Defs[i];
            h.Add(m.Population);
            h.Add(m.Grant);
            h.Add(m.CreditDays);
            h.Add(m.Area);
            h.Add(m.Flags);
            AddGate(ref h, in m.G0);
            AddGate(ref h, in m.G1);
            h.Add(m.Features);
            h.Add(m.ProfileFirst);
            h.Add(m.ProfileCount);
            h.Add(ReachedTick[i]);
        }

        h.Add(ProfileIdx.Length);
        for (int i = 0; i < ProfileIdx.Length; i++)
        {
            h.Add(ProfileIdx[i]);
        }

        h.Add(Areas.Length);
        for (int i = 0; i < Areas.Length; i++)
        {
            h.Add(Areas[i].MinX);
            h.Add(Areas[i].MinZ);
            h.Add(Areas[i].MaxX);
            h.Add(Areas[i].MaxZ);
        }

        h.Add(MinLegs);
        h.Add(LockedFeatures);
        h.Add(ProfileCount);
        h.Add(Reached);
        h.Add(Features);
        for (int i = 0; i < Profiles.Length; i++)
        {
            h.Add(Profiles[i]);
        }

        h.Add(CreditDaysBonus);
        h.Add(AreaStage);
        h.Add(GatedNotified);
    }

    private static void AddGate(ref Fnv1a64 h, in KpiGate g)
    {
        h.Add((byte)g.Kpi);
        h.Add(g.IsMin);
        h.Add(g.Value);
    }
}
