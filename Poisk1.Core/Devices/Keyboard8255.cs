using Poisk1.Core.Io;
using Poisk1.Core.Video;

namespace Poisk1.Core.Devices;

/// <summary>
/// PPI 8255 of the "Poisk" (ports 0x60–0x6B): matrix keyboard + video control.
///   write 0x60  — keyboard row polling mask (also a scan-code latch, read back);
///   write 0x68  — video mode register (→ VideoControl.Mode68);
///   write 0x6A  — video color selection (→ VideoControl.Color6A);
///   read 0x69/0x6A — keyboard matrix data (low/high byte, active 0).
///
/// Key state: 8 rows of 16 bits each, 1 = released, 0 = pressed.
/// The matrix (row, bit) follows the MAME poisk1_keyboard_v91 layout.
/// </summary>
public sealed class Keyboard8255 : IIoDevice
{
    private readonly ushort[] _rows = new ushort[8];
    private readonly VideoControl _video;
    private byte _pollMask;
    private byte _port60; // latch of port 0x60 (BIOS writes a scan-code and reads it back)

    public Keyboard8255(VideoControl video)
    {
        _video = video;
        for (int i = 0; i < 8; i++) _rows[i] = 0xFFFF;
    }

    /// <summary>Set the key state by matrix coordinates (row 0..7, bit mask).</summary>
    public void SetKey(int row, int bit, bool pressed)
    {
        if ((uint)row >= 8) return;
        if (pressed) _rows[row] &= (ushort)~bit;
        else _rows[row] |= (ushort)bit;
    }

    /// <summary>Reset all keys to "released".</summary>
    public void ReleaseAll()
    {
        for (int i = 0; i < 8; i++) _rows[i] = 0xFFFF;
    }

    public byte ReadByte(ushort port)
    {
        if (port == 0x60) return _port60;             // latch: the last written scan-code
        if (port == 0x69) return (byte)(Scan() & 0xFF);
        if (port == 0x6A) return (byte)(Scan() >> 8);
        return 0x00;
    }

    public void WriteByte(ushort port, byte value)
    {
        switch (port)
        {
            case 0x60: _pollMask = value; _port60 = value; break; // row mask + scan-code latch
            case 0x68: _video.Mode68 = value; break;              // video mode
            case 0x6A: _video.Color6A = value; break;             // color/palette
        }
    }

    private ushort Scan()
    {
        ushort key = 0xFFFF;
        for (int i = 0; i < 8; i++)
            if (((_pollMask >> i) & 1) != 0) key &= _rows[i];
        return key;
    }
}
