using System.Text.Encodings.Web;
using System.Text.Json;

namespace Poisk1.Core;

/// <summary>Loading/creating Data/config.json.</summary>
public static class ConfigStore
{
    public const string FileName = "config.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // readable Cyrillic
    };

    /// <summary>Reads config.json from the Data folder; if missing, creates a default one.</summary>
    public static EmulatorConfig Load(string dataDir)
    {
        string path = Path.Combine(dataDir, FileName);
        if (File.Exists(path))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<EmulatorConfig>(File.ReadAllText(path), Options);
                if (cfg is not null && cfg.Bioses.Count > 0)
                {
                    if (cfg.Slots is null || cfg.Slots.Length != 4)
                        cfg.Slots = new[] { "", "", "", "" };
                    return cfg;
                }
            }
            catch { /* corrupted config — we'll overwrite it with the default */ }
        }

        var def = CreateDefault();
        Save(dataDir, def);
        return def;
    }

    /// <summary>Save the config to Data/config.json.</summary>
    public static void Save(string dataDir, EmulatorConfig cfg)
    {
        try { File.WriteAllText(Path.Combine(dataDir, FileName), JsonSerializer.Serialize(cfg, Options)); }
        catch { }
    }

    private static EmulatorConfig CreateDefault() => new()
    {
        RamSizeKb = 128,
        Font = "poisk_cga.dat",
        DefaultBios = "poisk.bin",
        Bioses = new()
        {
            new BiosEntry { Name = "Поиск 1989 (poisk.bin)", File = "poisk.bin" },
            // Add your own BIOS files here (8 KB, placed in Data):
            // new BiosEntry { Name = "Поиск 1991", File = "p_bios_nm.bin" },
        },
        GlyphRemap = new() { { "0xF6", "0x10" }, { "0xF7", "0x11" } },
    };
}
