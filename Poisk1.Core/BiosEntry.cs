namespace Poisk1.Core;

/// <summary>A single entry in the BIOS list (display name + file name in Data).</summary>
public sealed class BiosEntry
{
    public string Name { get; set; } = "";
    public string File { get; set; } = "";
}
