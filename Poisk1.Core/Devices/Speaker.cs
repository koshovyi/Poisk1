using Poisk1.Core.Io;

namespace Poisk1.Core.Devices;

/// <summary>
/// "Poisk" speaker via PPI port B (0x61). Bit 0 — enable counting of timer channel 2
/// (gate), bit 1 — connection of the timer output to the speaker (speaker data). The BIOS
/// BEEP procedure enables both and sets the frequency via PIT channel 2 (port 0x42, mode 3).
/// The sound level itself is computed in <see cref="Machine"/> (port 0x61 × channel 2 output).
/// </summary>
public sealed class Speaker : IIoDevice
{
    public const ushort Port = 0x61;

    private byte _port61;

    public byte ReadByte(ushort port) => _port61;
    public void WriteByte(ushort port, byte value) => _port61 = value;

    /// <summary>Bit 0 (0x01) — timer channel 2 gate is enabled.</summary>
    public bool Gate => (_port61 & 0x01) != 0;

    /// <summary>Bit 1 (0x02) — the timer output is connected to the speaker.</summary>
    public bool Data => (_port61 & 0x02) != 0;
}
