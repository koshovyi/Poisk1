using Poisk1.Core.Io;

namespace Poisk1.Core.Devices;

/// <summary>
/// Passive stub device: absorbs writes, returns 0xFF on reads.
/// Used for ports that need to be accepted "silently" (e.g. the video-trap/NMI 0x28–0x2A).
/// </summary>
public sealed class NullDevice : IIoDevice
{
    public byte ReadByte(ushort port) => 0xFF;
    public void WriteByte(ushort port, byte value) { }
}
