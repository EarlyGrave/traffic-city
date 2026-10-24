using System.Diagnostics.CodeAnalysis;

// Публичные blittable-структуры API по спеке (core-api §2-§5, §12) состоят из публичных полей: это C-раскладка (FFI).
[assembly: SuppressMessage("Design", "CA1051:Do not declare visible instance fields", Justification = "blittable FFI-структуры с публичными полями по спеке")]

// RngStream - перечисление номеров подпотоков PRNG (core-api §8), не System.IO.Stream.
[assembly: SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "имя из спеки; это не поток ввода-вывода", Scope = "type", Target = "~T:Sim.Core.Util.RngStream")]

// Имена из спеки: VehicleRenderFlags ([Flags]-перечисление) и пара NetworkView.LanePoints / LanePointS.
[assembly: SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "имя из спеки", Scope = "type", Target = "~T:Sim.Core.Api.VehicleRenderFlags")]
[assembly: SuppressMessage("Naming", "CA1708:Identifiers should differ by more than case", Justification = "LanePoints/LanePointS - имена из спеки (core-api §5.6)")]
