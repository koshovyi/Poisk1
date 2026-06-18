namespace Poisk1.Core.Io;

/// <summary>
/// A device attached to the I/O port bus.
/// </summary>
public interface IIoDevice
{
    byte ReadByte(ushort port);
    void WriteByte(ushort port, byte value);
}
