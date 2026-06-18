using Poisk1.Core.Io;

namespace Poisk1.Core.Devices;

/// <summary>
/// A simple port that returns a fixed value on read (for hardware "detector" ports
/// that software polls). For example, 0x202 → 0x3F: the "Poisk" cassette loaders
/// (the S.Kovalenko packer) check IN 0x202 == 0x3F as a platform marker.
/// </summary>
public sealed class PortStub : IIoDevice
{
    private readonly byte _value;
    public PortStub(byte value) => _value = value;

    public byte ReadByte(ushort port) => _value;
    public void WriteByte(ushort port, byte value) { }
}
