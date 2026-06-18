namespace Poisk1.Core;

/// <summary>
/// The memory-access abstraction expected by the CPU core.
/// Addresses are physical (20-bit on the 8088).
/// </summary>
public interface IMemoryBus
{
    byte ReadByte(uint address);
    void WriteByte(uint address, byte value);
    ushort ReadWord(uint address);
    void WriteWord(uint address, ushort value);
}
