using Poisk1.Core.Devices;

namespace Poisk1.Core.Expansion;

/// <summary>
/// Hard-disk controller <b>B942</b> (НЖМД) built around the WD2010, as on the Poisk-1.
/// Reverse-engineered from the v1.x HDD BIOS (S. Kovalenko) and the MAME p1_hdc driver.
///
/// Memory map (all in the 4 KB window at <see cref="WinBase"/> = segment 0xD000):
///   0x000–0x1FF : 512-byte sector data buffer (CPU copies sectors to/from here).
///   0x801       : error register (read).
///   0x802       : sector count.
///   0x803       : sector number (1-based, as INT 13h).
///   0x804/0x805 : cylinder low / high (bits 0–1).
///   0x806       : SDH — head (bits 0–2), drive (bits 3–4), bit 5 = 512-byte sectors.
///   0x807       : status (read) / command (write).
///   0xC00       : control (write: drive select) / status (read: bit7 = DRQ, bits4-5 = drive type).
/// The option ROM (2 KB) is mapped at <see cref="RomBase"/> = 0xE2000.
///
/// Transfer model: a command (READ 0x20 / WRITE 0x30) is issued to 0x807; the controller signals
/// each sector via the DRQ bit (0xC00 bit7, which the BIOS polls), and raises IRQ5 once the whole
/// command completes. The buffer's last byte (0x1FF) being accessed marks one sector consumed,
/// which advances to the next sector or finishes. Sectors are 512 bytes, 17 per track.
/// </summary>
public sealed class HddController : IExpansionCard, IMmioDevice
{
    public const uint RomBase = 0xE_2000;   // 2 KB option ROM
    public const uint WinBase = 0xD_0000;   // 4 KB MMIO window (segment 0xD000)
    public const uint WinEnd = 0xD_1000;
    public const int Irq = 5;

    private const int BufSize = 0x200;      // 512-byte sector buffer at offset 0
    private const int RegBase = 0x800;      // task-file registers 0x800..0x807
    private const int CtrlOff = 0xC00;      // control/status register

    private readonly byte[] _rom;
    private readonly byte[] _buf = new byte[BufSize];
    private readonly byte[] _reg = new byte[8];   // task file at 0x800+i
    private byte _status = 0x50;                   // RDY | SC
    private bool _drq;                             // 0xC00 bit7 — data ready (read) / wanted (write)
    private Machine? _machine;

    public HardDisk?[] Disks { get; } = new HardDisk?[2];

    private enum Mode { Idle, Read, Write, Format }
    private Mode _mode = Mode.Idle;
    private int _lba, _count, _formatCyl, _formatHead, _formatDrive;

    public HddController(byte[] rom) => _rom = rom;

    public string Id => "hdd";
    public string DisplayName => "B942 (НЖМД)";

    public uint MmioStart => WinBase;
    public uint MmioEnd => WinEnd;

    public void Install(Machine machine)
    {
        _machine = machine;
        machine.Memory.MapRegion(RomBase, _rom, writable: false);
        machine.Memory.MapMmio(this);
    }

    public void Remove(Machine machine)
    {
        machine.Memory.UnmapRegion(RomBase);
        machine.Memory.UnmapMmio(this);
        foreach (var d in Disks) d?.Flush();
    }

    public void Attach(int drive, HardDisk? disk) { if ((uint)drive < 2) Disks[drive] = disk; }
    public HardDisk? DiskIn(int drive) => (uint)drive < 2 ? Disks[drive] : null;
    public void Flush() { foreach (var d in Disks) d?.Flush(); }

    // ===================== MMIO =====================

    public byte MmioRead(uint address)
    {
        int off = (int)(address - WinBase);
        if (off < BufSize)
        {
            byte v = _buf[off];
            if (off == BufSize - 1 && _mode == Mode.Read) AdvanceRead();
            return v;
        }
        if (off >= RegBase && off < RegBase + 8)
            return off - RegBase == 7 ? _status : _reg[off - RegBase];
        if (off == CtrlOff)
            return (byte)((_drq ? 0x80 : 0x00) | 0x30); // bits4-5 set → step-rate calc yields 0
        return 0xFF;
    }

    public void MmioWrite(uint address, byte value)
    {
        int off = (int)(address - WinBase);
        if (off < BufSize)
        {
            _buf[off] = value;
            if (off == BufSize - 1 && (_mode == Mode.Write || _mode == Mode.Format)) AdvanceWrite();
            return;
        }
        if (off >= RegBase && off < RegBase + 8)
        {
            int r = off - RegBase;
            _reg[r] = value;
            if (r == 7) Command(value);
            return;
        }
        // 0xC00 control writes (drive select / latch reset) — no effect in this model.
    }

    // ===================== WD2010 command engine =====================

    private void Command(byte cmd)
    {
        int op = cmd & 0xF0;
        int head = _reg[6] & 0x07;
        int drive = (_reg[6] >> 3) & 0x03;
        int sector = _reg[3];                                   // 1-based
        int cyl = _reg[4] | ((_reg[5] & 0x03) << 8);
        int count = _reg[2] == 0 ? 256 : _reg[2];
        var disk = (uint)drive < 2 ? Disks[drive] : null;
        _machine?.Trace?.Invoke($"HDD cmd={cmd:X2} drv={drive} c={cyl} h={head} s={sector} n={count} disk={(disk is null ? "none" : "ok")}");

        _drq = false;
        switch (op)
        {
            case 0x10: // RESTORE (recalibrate to cyl 0)
            case 0x70: // SEEK
                Complete(disk is not null);
                break;

            case 0x20: // READ SECTOR(S)
                if (disk is null) { Fail(0x10); break; }
                _lba = disk.Chs2Lba(cyl, head, sector);
                _count = count; _mode = Mode.Read;
                if (!disk.ReadSector(_lba, _buf, 0)) { Fail(0x10); break; }
                _status = 0x58; _drq = true;                    // RDY|SC|DRQ
                break;

            case 0x30: // WRITE SECTOR(S)
                if (disk is null) { Fail(0x10); break; }
                _lba = disk.Chs2Lba(cyl, head, sector);
                _count = count; _mode = Mode.Write;
                _status = 0x58; _drq = true;                    // request first sector's data
                break;

            case 0x50: // FORMAT TRACK (buffer holds the sector-ID table; we just clear the track)
                if (disk is null) { Fail(0x10); break; }
                _mode = Mode.Format; _formatCyl = cyl; _formatHead = head; _formatDrive = drive;
                _status = 0x58; _drq = true;
                break;

            default:   // SCAN ID (0x40), COMPUTE, SET PARAMETER, … — acknowledge.
                Complete(true);
                break;
        }
    }

    /// <summary>One read sector consumed (host read the last buffer byte): load next or finish.</summary>
    private void AdvanceRead()
    {
        var disk = CurrentReadDisk();
        if (--_count <= 0 || disk is null) { _drq = false; Complete(true); return; }
        _lba++;
        if (!disk.ReadSector(_lba, _buf, 0)) { _drq = false; Fail(0x10); return; }
        // _drq stays set: next sector ready immediately.
    }

    /// <summary>Host filled the buffer (wrote the last byte): commit the sector, request next or finish.</summary>
    private void AdvanceWrite()
    {
        if (_mode == Mode.Format)
        {
            var fdisk = (uint)_formatDrive < 2 ? Disks[_formatDrive] : null;
            if (fdisk is not null)
            {
                var zero = new byte[HardDisk.SectorSize];
                int lba0 = fdisk.Chs2Lba(_formatCyl, _formatHead, 1);
                for (int s = 0; s < fdisk.Sectors; s++) fdisk.WriteSector(lba0 + s, zero, 0);
            }
            _drq = false; Complete(true); return;
        }

        var disk = CurrentWriteDisk();
        if (disk is null || !disk.WriteSector(_lba, _buf, 0)) { _drq = false; Fail(0x10); return; }
        if (--_count <= 0) { _drq = false; Complete(true); return; }
        _lba++;
        // _drq stays set: controller wants the next sector's data.
    }

    private HardDisk? CurrentReadDisk() => DiskForSdh();
    private HardDisk? CurrentWriteDisk() => DiskForSdh();
    private HardDisk? DiskForSdh() { int d = (_reg[6] >> 3) & 3; return (uint)d < 2 ? Disks[d] : null; }

    private void Complete(bool ok)
    {
        _mode = Mode.Idle;
        _status = ok ? (byte)0x50 : (byte)0x51;  // RDY|SC, or RDY|ERR
        _machine?.Pic.RaiseIrq(Irq);
    }

    private void Fail(byte errorBits)
    {
        _mode = Mode.Idle; _drq = false;
        _reg[1] = errorBits;                      // error register @ 0x801
        _status = 0x51;                           // RDY|ERR (bit0)
        _machine?.Pic.RaiseIrq(Irq);
    }

    // ===================== Image directory =====================

    private static string HddDir(string dataDir) => Path.Combine(dataDir, "hdd");

    /// <summary>Available controller BIOS files (valid 0x55AA option-ROMs) in Data/hdd.</summary>
    public static IReadOnlyList<string> ListBioses(string dataDir)
    {
        string dir = HddDir(dataDir);
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

    /// <summary>Create a B942 with the specified (or default v1.7) BIOS from Data/hdd.</summary>
    public static HddController FromBios(string dataDir, string? biosFile = null)
    {
        var available = ListBioses(dataDir);
        string? file = biosFile is not null && available.Contains(biosFile) ? biosFile
            : available.FirstOrDefault(f => f.Contains("1.7"))
              ?? available.FirstOrDefault(f => f.Contains("Version"))
              ?? available.FirstOrDefault();
        byte[] rom = file is not null ? File.ReadAllBytes(Path.Combine(HddDir(dataDir), file)) : new byte[0x800];
        if (rom.Length < 0x800) Array.Resize(ref rom, 0x800);   // ROM window is 2 KB
        return new HddController(rom);
    }
}
