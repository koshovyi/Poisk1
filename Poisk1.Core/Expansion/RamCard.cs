namespace Poisk1.Core.Expansion;

/// <summary>
/// RAM expansion board for the Poisk (B107 — 256 KB, B109 — 512 KB). On install it
/// maps a block of writable memory RIGHT above the current RAM ceiling (base + previously
/// installed RAM cards) — so cards stack in slot order. The total amount is capped at
/// <see cref="MaxRamTotal"/> (≈768 KB); the last card is truncated if needed.
/// </summary>
public sealed class RamCard : IExpansionCard
{
    /// <summary>Maximum contiguous RAM for the Poisk — 736 KB (exactly up to VRAM @ 0xB8000):
    /// any higher and RAM would overlap video memory.</summary>
    public const uint MaxRamTotal = MemoryBus.VramBase; // 0xB8000 = 736 KB

    private readonly int _sizeKb;
    private uint _mappedAt = uint.MaxValue;

    public RamCard(string id, string displayName, int sizeKb)
    {
        Id = id;
        DisplayName = displayName;
        _sizeKb = sizeKb;
    }

    public string Id { get; }
    public string DisplayName { get; }

    public void Install(Machine machine)
    {
        uint start = machine.Memory.RamCeiling();
        if (start >= MaxRamTotal) return; // no room — the card has no effect
        long size = _sizeKb * 1024L;
        if (start + size > MaxRamTotal) size = MaxRamTotal - start; // trim to the limit
        machine.Memory.MapRegion(start, new byte[size], writable: true);
        _mappedAt = start;
    }

    public void Remove(Machine machine)
    {
        if (_mappedAt == uint.MaxValue) return;
        machine.Memory.UnmapRegion(_mappedAt);
        _mappedAt = uint.MaxValue;
    }
}
