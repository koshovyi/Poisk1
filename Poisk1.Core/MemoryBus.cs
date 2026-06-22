namespace Poisk1.Core;

/// <summary>
/// Memory map of the "Poisk-1" (1 MB physical address space):
///   0x00000 .. RamSize   — main RAM
///   0xB8000 .. 0xBFFFF   — video RAM (32 KB, CGA-like framebuffer)
///   0xFE000 .. 0xFFFFF   — BIOS (8 KB)
/// Everything else is an "open bus" (returns 0xFF, writes are ignored).
/// </summary>
public sealed class MemoryBus : IMemoryBus
{
    public const uint AddressMask = 0xF_FFFF; // 20 bits — the 8088 wraps around at 1 MB

    public const uint VramBase = 0xB_8000;
    public const int VramSize = 0x8000; // 32 KB

    public const uint BiosBase = 0xF_E000;
    public const int BiosSize = 0x2000; // 8 KB

    private readonly byte[] _ram;
    private readonly byte[] _vram = new byte[VramSize];
    private readonly byte[] _bios = new byte[BiosSize];

    public MemoryBus(int ramSize)
    {
        // 32 KB of the total RAM is reserved for video memory (_vram @ 0xB8000), so the
        // program RAM = model − 32 KB: 128 KB → 96 KB, 512 KB → 480 KB (as on the Poisk).
        _ram = new byte[Math.Max(VramSize, ramSize) - VramSize];
    }

    /// <summary>Direct access to video memory for rendering (does not copy).</summary>
    public byte[] Vram => _vram;

    public int RamSize => _ram.Length;

    /// <summary>
    /// A memory overlay from an expansion card (ROM/RAM in the upper address space).
    /// </summary>
    private sealed class Overlay
    {
        public uint Start;
        public byte[] Data = Array.Empty<byte>();
        public bool Writable;
    }

    private readonly List<Overlay> _overlays = new();
    private readonly List<IMmioDevice> _mmio = new();

    /// <summary>Map a card's memory block at a physical address (ROM: writable=false).</summary>
    public void MapRegion(uint start, byte[] data, bool writable)
        => _overlays.Add(new Overlay { Start = start, Data = data, Writable = writable });

    /// <summary>Unmap the block mapped at the given address.</summary>
    public void UnmapRegion(uint start)
        => _overlays.RemoveAll(o => o.Start == start);

    /// <summary>Register a memory-mapped I/O device (e.g. the B942 HDD window at 0xD0000).</summary>
    public void MapMmio(IMmioDevice dev) => _mmio.Add(dev);
    public void UnmapMmio(IMmioDevice dev) => _mmio.Remove(dev);

    /// <summary>Ceiling of contiguous RAM = base + all writable overlays (for stacking RAM cards).</summary>
    public uint RamCeiling()
    {
        uint top = (uint)_ram.Length;
        foreach (var o in _overlays)
            if (o.Writable) top = Math.Max(top, o.Start + (uint)o.Data.Length);
        return top;
    }

    public void LoadBios(byte[] data)
    {
        if (data.Length != BiosSize)
            throw new ArgumentException(
                $"BIOS must be exactly {BiosSize} bytes (8 KB), got {data.Length}.");
        Array.Copy(data, _bios, BiosSize);
    }

    public byte ReadByte(uint address)
    {
        address &= AddressMask;
        if (address < (uint)_ram.Length)
            return _ram[address];
        if (address >= VramBase && address < VramBase + VramSize)
            return _vram[address - VramBase];
        if (address >= BiosBase) // BiosBase..0xFFFFF == exactly 8 KB
            return _bios[address - BiosBase];
        // Memory-mapped I/O devices (take precedence over ROM/RAM overlays in their window).
        for (int i = 0; i < _mmio.Count; i++)
        {
            var d = _mmio[i];
            if (address >= d.MmioStart && address < d.MmioEnd) return d.MmioRead(address);
        }
        // Expansion-card overlays (ROM/RAM).
        for (int i = 0; i < _overlays.Count; i++)
        {
            var o = _overlays[i];
            if (address >= o.Start && address < o.Start + (uint)o.Data.Length)
                return o.Data[address - o.Start];
        }
        return 0xFF; // open bus
    }

    public void WriteByte(uint address, byte value)
    {
        address &= AddressMask;
        if (address < (uint)_ram.Length)
        {
            _ram[address] = value;
            return;
        }
        if (address >= VramBase && address < VramBase + VramSize)
        {
            _vram[address - VramBase] = value;
            return;
        }
        // Memory-mapped I/O devices.
        for (int i = 0; i < _mmio.Count; i++)
        {
            var d = _mmio[i];
            if (address >= d.MmioStart && address < d.MmioEnd) { d.MmioWrite(address, value); return; }
        }
        // Writes into expansion-card RAM overlays.
        for (int i = 0; i < _overlays.Count; i++)
        {
            var o = _overlays[i];
            if (o.Writable && address >= o.Start && address < o.Start + (uint)o.Data.Length)
            {
                o.Data[address - o.Start] = value;
                return;
            }
        }
        // Writes to ROM and to "holes" are ignored.
    }

    public ushort ReadWord(uint address)
        => (ushort)(ReadByte(address) | (ReadByte(address + 1) << 8));

    public void WriteWord(uint address, ushort value)
    {
        WriteByte(address, (byte)(value & 0xFF));
        WriteByte(address + 1, (byte)(value >> 8));
    }
}
