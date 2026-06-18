namespace Poisk1.Core;

/// <summary>
/// Locates firmware files in the Data folder. The directory is searched upward from the
/// working/application directory (handy when running from bin/Debug/...).
/// </summary>
public static class Roms
{
    // The actual files from our set: POISK.BIN (= BIOSP1S.RF4, BIOS 1988/89, 8 KB)
    // and the character generator CGAN.DAT (4 KB, data in even bytes — needs de-interleaving).
    public const string BiosFileName = "poisk.bin";
    public const string FontFileName = "poisk_cga.dat";

    /// <summary>Find the Data folder by walking up from <paramref name="startDir"/>.</summary>
    public static string? FindDataDir(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Data");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Returns the full path to a file in Data, or null if not found.</summary>
    public static string? Resolve(string startDir, string fileName)
    {
        var data = FindDataDir(startDir);
        if (data is null)
            return null;
        string path = Path.Combine(data, fileName);
        return File.Exists(path) ? path : null;
    }
}
