namespace Poisk1.Core;

/// <summary>
/// Configuration of the "Poisk-1" machine. Defaults to model 1.0 (128 KB RAM).
/// </summary>
public sealed class MachineConfig
{
    /// <summary>Size of main RAM in bytes. Model 1.0 = 128 KB.</summary>
    public int RamSize { get; init; } = 128 * 1024;

    /// <summary>Path to the BIOS file (8 KB), e.g. poisk_1991.bin.</summary>
    public string? BiosPath { get; init; }

    /// <summary>Path to the character generator (2 KB), e.g. poisk.cga.</summary>
    public string? FontPath { get; init; }

    /// <summary>Optional character-code remap applied during rendering (256→256), or null.</summary>
    public int[]? GlyphMap { get; init; }
}
