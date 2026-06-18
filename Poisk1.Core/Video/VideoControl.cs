namespace Poisk1.Core.Video;

/// <summary>
/// Shared video-control state for the Poisk, written by the PPI ports (0x68/0x6A)
/// and read by the video adapter to pick the render mode.
///
/// Port 0x68 (PPI #2 port A):
///   bit 3 (0x08) — graphics mode (otherwise text);
///   bit 7 (0x80) — high resolution 640×200 (otherwise 320×200).
/// Port 0x6A — color/palette selection.
/// </summary>
public sealed class VideoControl
{
    public byte Mode68 { get; set; }
    public byte Color6A { get; set; }

    public bool Graphics => (Mode68 & 0x08) != 0;
    public bool HiRes => (Mode68 & 0x80) != 0;
    /// <summary>Bit 6 — display bank (the second half of the 32 KB VRAM, offset 0x4000).</summary>
    public bool DisplayBank => (Mode68 & 0x40) != 0;
}
