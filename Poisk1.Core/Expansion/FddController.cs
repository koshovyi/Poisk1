using Poisk1.Core.Devices;
using Poisk1.Core.Io;

namespace Poisk1.Core.Expansion;

/// <summary>
/// Floppy-drive controller <b>B504</b> based on the FD1793 (КР1818ВГ93) — up to two 360/720 KB drives.
/// Maps the option-ROM (B-НГМД, the floppy-drive ROM) at 0xE0000, registers the FD1793 ports (0xC0–0xC3) and the control
/// port (0xC4). Port 0xC4: bits D0/D1 — drive select, D2/D3 — motor, D4 — side,
/// D5 — double density, D6 — RESET (active low). Reading 0xC4 returns DRQ.
/// </summary>
public sealed class FddController : IExpansionCard, IIoDevice
{
    public const uint RomBase = 0xE_0000;
    public const ushort AuxBase = 0x00C4;

    private readonly byte[] _rom;
    private Poisk1.Core.Io.IoBus? _io;
    public Fd1793 Fdc { get; } = new();

    public FddController(byte[] rom) => _rom = rom;

    public string Id => "fdd";
    public string DisplayName => "B504 (НГМД)";

    public void Install(Machine machine)
    {
        _io = machine.Io;
        Fdc.Cycles = () => machine.Cycles; // for the time-based "no disk" timeout
        machine.Memory.MapRegion(RomBase, _rom, writable: false);
        machine.Io.RegisterRange(Fdc, Fd1793.Base, (ushort)(Fd1793.Base + 3)); // 0xC0–0xC3
        machine.Io.RegisterRange(this, AuxBase, (ushort)(AuxBase + 3));         // 0xC4–0xC7
    }

    public void Remove(Machine machine)
    {
        machine.Memory.UnmapRegion(RomBase);
        machine.Io.UnregisterRange(Fd1793.Base, (ushort)(Fd1793.Base + 3));
        machine.Io.UnregisterRange(AuxBase, (ushort)(AuxBase + 3));
    }

    // --- Control port 0xC4 ---
    public byte ReadByte(ushort port)
    {
        if (port - AuxBase == 0)
        {
            Fdc.PollTimer(); // the timeout may have elapsed
            // Wait-state (as on real hardware): while the FDC is busy with no DRQ/INTRQ — retry IN.
            if (_io is not null && Fdc.Busy && !Fdc.Drq && !Fdc.Intrq) _io.PendingStall = true;
            return (byte)(Fdc.Drq ? 1 : 0); // aux: DRQ
        }
        return 0; // 0xC6 motor_r → 0
    }

    public void WriteByte(ushort port, byte value)
    {
        if (port - AuxBase != 0) return;
        if ((value & 0x40) == 0) Fdc.Reset();      // D6=0 → RESET
        Fdc.SetDrive((value & 0x02) != 0 ? 1 : 0); // D1 → drive 1, otherwise 0
        Fdc.SetSide((value & 0x10) != 0 ? 1 : 0);  // D4 → side
        // D2/D3 (motor), D5 (density) — no effect in the "instant" model.
    }

    // --- Loading images into drives ---
    public void Insert(int drive, FloppyDisk? disk) => Fdc.Attach(drive, disk);
    public FloppyDisk? DiskIn(int drive) => Fdc.Disk(drive);

    private static string FddDir(string dataDir) => Path.Combine(dataDir, "fdd");

    /// <summary>Available controller BIOS files (valid 0x55AA option-ROMs) in Data/fdd.</summary>
    public static IReadOnlyList<string> ListBioses(string dataDir)
    {
        string dir = FddDir(dataDir);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        var list = new List<string>();
        foreach (var p in Directory.GetFiles(dir))
        {
            try { var b = File.ReadAllBytes(p); if (b.Length >= 2 && b[0] == 0x55 && b[1] == 0xAA) list.Add(Path.GetFileName(p)); }
            catch { /* skip */ }
        }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    /// <summary>Create a B504 with the specified (or default) BIOS file from Data/fdd.</summary>
    public static FddController FromBios(string dataDir, string? biosFile = null)
    {
        var available = ListBioses(dataDir);
        string? file = biosFile is not null && available.Contains(biosFile) ? biosFile
            : available.FirstOrDefault(f => f.Contains("ADD BIOS") && f.Contains("4.11"))
              ?? available.FirstOrDefault(f => f.Contains("ADD BIOS"))
              ?? available.FirstOrDefault();
        byte[] rom = file is not null ? File.ReadAllBytes(Path.Combine(FddDir(dataDir), file)) : new byte[0x2000];
        return new FddController(rom);
    }
}
