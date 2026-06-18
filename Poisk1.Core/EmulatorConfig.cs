namespace Poisk1.Core;

/// <summary>
/// Emulator configuration (Data/config.json): RAM size, font, BIOS list,
/// and an optional glyph remap (to work around a font code-page mismatch).
/// </summary>
public sealed class EmulatorConfig
{
    public int RamSizeKb { get; set; } = 128;
    public string Font { get; set; } = "poisk_cga.dat";
    public string DefaultBios { get; set; } = "poisk.bin";
    public List<BiosEntry> Bioses { get; set; } = new();

    /// <summary>Contents of the 4 expansion slots: card id or "" (empty). E.g. ["rom","","",""].</summary>
    public string[] Slots { get; set; } = { "", "", "", "" };

    /// <summary>B942 hard-disk size in MB. Geometry is derived as heads=4, 17 sectors/track.</summary>
    public int HddSizeMb { get; set; } = 20;

    /// <summary>Hard-disk image file in Data/hdd_disk (created zeroed on first use if missing).</summary>
    public string HddImage { get; set; } = "hdd.img";

    /// <summary>CHS geometry for the configured HDD size (heads=4, 17 sectors/track, ≤1024 cyl).</summary>
    public (int Cyl, int Heads, int Spt) HddGeometry()
    {
        const int heads = 4, spt = 17;
        long sectors = (long)Math.Max(1, HddSizeMb) * 1024 * 1024 / 512;
        int cyl = (int)(sectors / (heads * spt));
        cyl = Math.Clamp(cyl, 1, 1024);
        return (cyl, heads, spt);
    }

    /// <summary>
    /// Character-code remap applied during rendering: key→value (hex strings, e.g. "0xF6":"0x10").
    /// Needed because in the available CGAN.DAT font the arrows live at 0x10/0x11,
    /// whereas the poisk.bin BIOS uses codes 0xF6/0xF7 for them.
    /// </summary>
    public Dictionary<string, string> GlyphRemap { get; set; } = new();

    /// <summary>Builds a 256→256 table from GlyphRemap, or null if the remap is empty.</summary>
    public int[]? BuildGlyphMap()
    {
        if (GlyphRemap.Count == 0) return null;
        var map = new int[256];
        for (int i = 0; i < 256; i++) map[i] = i;
        foreach (var kv in GlyphRemap)
        {
            int from = ParseByte(kv.Key), to = ParseByte(kv.Value);
            if (from is >= 0 and < 256 && to is >= 0 and < 256) map[from] = to;
        }
        return map;
    }

    private static int ParseByte(string s)
    {
        s = s.Trim();
        return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToInt32(s, 16)
            : int.Parse(s);
    }
}
