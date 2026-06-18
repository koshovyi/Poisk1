using Poisk1.Core.Io;

namespace Poisk1.Core.Video;

// IMemoryBus — in Poisk1.Core (MemoryBus.cs).

/// <summary>
/// Poisk video adapter (VRAM @ 0xB8000, 32 KB; ports 0x3D0–0x3DF — status).
/// The mode is chosen via <see cref="VideoControl"/> (port 0x68):
///   text 40×25 (character/attribute + character generator) — the BIOS default;
///   graphics 320×200, 2 bpp (4 colors);
///   graphics 640×200, 1 bpp (mono) — the HiRes bit.
/// The graphics framebuffer is interlaced: even rows from +0x0000, odd rows from +0x2000.
/// </summary>
public sealed class CgaAdapter : IIoDevice
{
    public const int Columns = 40, Rows = 25;
    public const int CharW = 8, CharH = 8;
    private const int FontBytesPerChar = 16; // 8 rows × 2 bytes (data in the even ones)
    private const int InterlaceBank = 0x2000;
    private const int BytesPerRow = 80;

    private readonly byte[] _vram;
    private readonly byte[] _font;
    private readonly int[]? _glyphMap;
    private readonly VideoControl _video;

    private byte _statusRegister; // 0x3DA

    // 6845 CRTC registers (0x3D4 index / 0x3D5 data). R1 = number of visible columns (40 or 80).
    private readonly byte[] _crtc = new byte[32];
    private int _crtcIndex;

    /// <summary>Manual video-mode override (Auto = take it from VideoControl/port 0x68).</summary>
    public enum ModeOverride { Auto, Text, Gfx320, Gfx640, GfxAuto }
    public ModeOverride Override { get; set; } = ModeOverride.Auto;

    private bool EffGraphics => Override switch
    {
        ModeOverride.Text => false,
        ModeOverride.Gfx320 or ModeOverride.Gfx640 or ModeOverride.GfxAuto => true,
        _ => _video.Graphics,
    };

    private bool EffHiRes => Override switch
    {
        ModeOverride.Gfx640 => true,
        ModeOverride.Gfx320 or ModeOverride.Text => false,
        ModeOverride.GfxAuto => _video.HiRes, // resolution from bit 7 of port 0x68
        _ => _video.HiRes,
    };

    private readonly IMemoryBus? _mem; // for reading the column count from [0040:004A]

    public CgaAdapter(byte[] vram, byte[] font, VideoControl video, int[]? glyphMap = null, IMemoryBus? mem = null)
    {
        _vram = vram;
        _font = font;
        _video = video;
        _glyphMap = glyphMap;
        _mem = mem;
    }

    /// <summary>Visible text columns (40 or 80). The Poisk doesn't program the 6845 for this,
    /// so we take the count from the BIOS variable [0040:004A]; fallback is 6845 R1, or 40 by default.</summary>
    private int EffColumns
    {
        get
        {
            int c = _mem?.ReadByte(0x044A) ?? 0;
            if (c is 40 or 80) return c;
            if (_crtc[1] is >= 20 and <= 132) return _crtc[1];
            return Columns;
        }
    }

    /// <summary>Current frame width in pixels (depends on the mode).</summary>
    public int Width => EffGraphics ? (EffHiRes ? 640 : 320) : EffColumns * CharW;

    /// <summary>Current frame height in pixels.</summary>
    public int Height => 200;

    // 16-color CGA palette (ARGB).
    private static readonly int[] Palette =
    {
        C(0x00,0x00,0x00), C(0x00,0x00,0xAA), C(0x00,0xAA,0x00), C(0x00,0xAA,0xAA),
        C(0xAA,0x00,0x00), C(0xAA,0x00,0xAA), C(0xAA,0x55,0x00), C(0xAA,0xAA,0xAA),
        C(0x55,0x55,0x55), C(0x55,0x55,0xFF), C(0x55,0xFF,0x55), C(0x55,0xFF,0xFF),
        C(0xFF,0x55,0x55), C(0xFF,0x55,0xFF), C(0xFF,0xFF,0x55), C(0xFF,0xFF,0xFF),
    };

    // 320×200 palette (CGA palette 1, bright): black/cyan/magenta/white.
    private static readonly int[] Palette4 = { Palette[0], Palette[11], Palette[13], Palette[15] };

    private static int C(int r, int g, int b)
        => unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));

    /// <summary>[DEBUG] current video-control state.</summary>
    public string VideoDebug => $"68={_video.Mode68:X2} 6A={_video.Color6A:X2} cols={EffColumns} gfx={_video.Graphics} hires={_video.HiRes} bank={_video.DisplayBank}";

    public readonly long[] PortReads = new long[16]; // [DEBUG] reads of 0x3D0..0x3DF
    public byte ReadByte(ushort port)
    {
        PortReads[port & 0x0F]++;
        if ((port & 0x0F) == 0x0A) // 0x3DA — status
        {
            _statusRegister ^= 0x09; // toggle the retrace bits so poll loops don't hang
            return _statusRegister;
        }
        if ((port & 0x0F) == 0x05) // 0x3D5 — CRTC data
            return (uint)_crtcIndex < 32 ? _crtc[_crtcIndex] : (byte)0xFF;
        return 0xFF;
    }

    public void WriteByte(ushort port, byte value)
    {
        switch (port & 0x0F)
        {
            case 0x04: _crtcIndex = value; break;                                   // 0x3D4 — CRTC index
            case 0x05: if ((uint)_crtcIndex < 32) _crtc[_crtcIndex] = value; break; // 0x3D5 — data (R1 = columns)
        }
    }

    /// <summary>Renders the current screen into an ARGB buffer of size Width*Height.</summary>
    public void Render(int[] argb)
    {
        if (!EffGraphics) RenderText(argb);
        else if (EffHiRes) RenderGfx640(argb);
        else RenderGfx320(argb);
    }

    private int Bank => _video.DisplayBank ? 0x4000 : 0;

    private void RenderText(int[] argb)
    {
        int cols = EffColumns;       // 40 or 80 (from 6845 R1)
        int w = cols * CharW;        // 320 or 640
        bool haveFont = _font.Length >= 256 * FontBytesPerChar;
        int bank = Bank;
        for (int row = 0; row < Rows; row++)
            for (int col = 0; col < cols; col++)
            {
                int cellOff = bank + (row * cols + col) * 2;
                int ch = _vram[cellOff];
                if (_glyphMap is not null) ch = _glyphMap[ch];
                byte attr = _vram[cellOff + 1];
                int fg = Palette[attr & 0x0F];
                int bg = Palette[(attr >> 4) & 0x07];

                int px0 = col * CharW, py0 = row * CharH;
                for (int gy = 0; gy < CharH; gy++)
                {
                    byte bits = haveFont ? _font[ch * FontBytesPerChar + gy * 2] : (byte)0;
                    int o = (py0 + gy) * w + px0;
                    for (int gx = 0; gx < CharW; gx++)
                        argb[o + gx] = (bits & (1 << gx)) != 0 ? fg : bg; // bit 0 = leftmost pixel
                }
            }
    }

    // 320×200, 2 bpp (4 pixels per byte, high bits on the left), interlaced.
    private void RenderGfx320(int[] argb)
    {
        const int w = 320;
        int bank = Bank;
        for (int y = 0; y < 200; y++)
        {
            int rowBase = bank + (y & 1) * InterlaceBank + (y >> 1) * BytesPerRow;
            int o = y * w;
            for (int bx = 0; bx < BytesPerRow; bx++)
            {
                byte b = _vram[rowBase + bx];
                int x = bx * 4;
                argb[o + x] = Palette4[(b >> 6) & 3];
                argb[o + x + 1] = Palette4[(b >> 4) & 3];
                argb[o + x + 2] = Palette4[(b >> 2) & 3];
                argb[o + x + 3] = Palette4[b & 3];
            }
        }
    }

    // 640×200, 1 bpp (8 pixels per byte, high bit on the left), interlaced.
    private void RenderGfx640(int[] argb)
    {
        const int w = 640;
        int fg = Palette[15], bg = Palette[0];
        int bank = Bank;
        for (int y = 0; y < 200; y++)
        {
            int rowBase = bank + (y & 1) * InterlaceBank + (y >> 1) * BytesPerRow;
            int o = y * w;
            for (int bx = 0; bx < BytesPerRow; bx++)
            {
                byte b = _vram[rowBase + bx];
                int x = bx * 8;
                for (int bit = 0; bit < 8; bit++)
                    argb[o + x + bit] = (b & (0x80 >> bit)) != 0 ? fg : bg;
            }
        }
    }
}
