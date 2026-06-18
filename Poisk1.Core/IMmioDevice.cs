namespace Poisk1.Core;

/// <summary>
/// A memory-mapped I/O device occupying a physical window (e.g. the B942 HDD controller
/// at 0xD0000): reads/writes have side effects, unlike a plain RAM/ROM overlay.
/// </summary>
public interface IMmioDevice
{
    /// <summary>Window start (inclusive) and end (exclusive), physical addresses.</summary>
    uint MmioStart { get; }
    uint MmioEnd { get; }
    byte MmioRead(uint address);
    void MmioWrite(uint address, byte value);
}
