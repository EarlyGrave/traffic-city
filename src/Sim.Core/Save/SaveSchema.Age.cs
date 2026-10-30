using Sim.Core.Api;

namespace Sim.Core.Save;

// world_version 19 (age-groups.md §5, задача RA1): возрастные группы жителей.
//   PARM 316-323 (секция city.age; без режима все 0), RESD 51 (Age: 0 Adult, 1 Child, 2 Elderly; без режима всегда 0). Миграции нет (режим прототипа): MigrationV19.
//   Отклонение от спеки (§2.2): биты 6-7 Flags заняты (64 - CareRefused SR3), поэтому группа - отдельный столбец, а не биты Flags.
internal static partial class SaveSchema
{
    // ---- PARM 316-323 ----
    private static ColumnDef[] AgeParams() => new[]
    {
        Col.Flag(316, "AgeEnabled", w => w.Town.Age.Enabled, (c, v) => c.Desc.Town.Age.Enabled = v),
        Col.Scalar<int>(317, "AgeChildQ16", w => w.Town.Age.ChildQ16, (c, v) => c.Desc.Town.Age.ChildQ16 = v),
        Col.Scalar<int>(318, "AgeElderlyQ16", w => w.Town.Age.ElderlyQ16, (c, v) => c.Desc.Town.Age.ElderlyQ16 = v),
        Col.Scalar<int>(319, "AgeElderlyCarPct", w => w.Town.Age.ElderlyCarPct, (c, v) => c.Desc.Town.Age.ElderlyCarPct = v),
        Col.Scalar<int>(320, "AgeElderlyAscFreqDs", w => w.Town.Age.ElderlyAscFreqDs, (c, v) => c.Desc.Town.Age.ElderlyAscFreqDs = v),
        Col.Scalar<int>(321, "AgeElderlySlot", w => w.Town.Age.ElderlySlot, (c, v) => c.Desc.Town.Age.ElderlySlot = v),
        Col.Scalar<int>(322, "AgeElderlyStepSlots", w => w.Town.Age.ElderlyStepSlots, (c, v) => c.Desc.Town.Age.ElderlyStepSlots = v),
        Col.Scalar<int>(323, "AgePensionDay", w => w.Town.Age.PensionDay, (c, v) => c.Desc.Town.Age.PensionDay = v),
    };

    // ---- RESD 51 (RA1): возрастная группа жителя (ResidentAge; 0 - взрослый и всегда 0 без city.age) ----
    private static ColumnDef[] AgeResidentColumns() => new[]
    {
        Col.Array<byte>(51, "Age", w => w.City!.Residents.Age, _resdHw, "HighWater", "0", _resdPool),
    };
}
