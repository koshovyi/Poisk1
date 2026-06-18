namespace Poisk1.Core.Expansion;

/// <summary>
/// ROM adapter: a board with several "soldered-in" ROMs (each a separate program/game).
/// The ROM modules are standard option-ROMs (0x55AA signature), mapped into upper
/// memory in consecutive 32 KB windows starting at 0xC0000. The Poisk BIOS (F2 — the
/// on-screen "Работа с ПЗУ" / "ROM access" menu) scans this range for the signature and lets you pick a module.
/// </summary>
public sealed class RomCard : IExpansionCard
{
    public const uint BaseAddress = 0xC_0000;
    public const int WindowSize = 0x8000; // 32 KB per module

    private readonly List<(string Name, byte[] Data)> _modules;
    private readonly List<uint> _mapped = new();

    public RomCard(IEnumerable<(string Name, byte[] Data)> modules)
        => _modules = modules.ToList();

    public string Id => "rom";
    public string DisplayName => "ПЗУ-адаптер";

    /// <summary>Module names (for display/diagnostics).</summary>
    public IReadOnlyList<string> ModuleNames => _modules.ConvertAll(m => m.Name);

    // BIOS scans for option-ROMs from 0xC0000 up to (but not into) the B942 HDD window @ 0xD0000.
    private const uint ScanCeiling = 0xD_0000;

    public void Install(Machine machine)
    {
        uint addr = BaseAddress;
        foreach (var (name, data) in _modules)
        {
            // Advance by the module's actual size rounded up to a 32 KB window, so a 64 KB
            // module occupies two windows and the next module starts past it (no overlap).
            uint span = ((uint)data.Length + WindowSize - 1) / WindowSize * WindowSize;
            if (addr + span > ScanCeiling)
            {
                machine.Trace?.Invoke($"ROM adapter: '{name}' skipped — no room above 0x{addr:X5} (scan ceiling 0x{ScanCeiling:X5}).");
                continue;
            }
            machine.Memory.MapRegion(addr, data, writable: false);
            _mapped.Add(addr);
            addr += span;
        }
    }

    public void Remove(Machine machine)
    {
        foreach (var a in _mapped)
            machine.Memory.UnmapRegion(a);
        _mapped.Clear();
    }

    private static string RomsDir(string dataDir) => Path.Combine(dataDir, "roms");

    /// <summary>List of available modules (*.bin names without extension) in Data/roms.</summary>
    public static IReadOnlyList<string> ListAvailable(string dataDir)
    {
        string dir = RomsDir(dataDir);
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.GetFiles(dir, "*.bin")
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)!
            .ToList()!;
    }

    /// <summary>A ROM adapter with all *.bin files from Data/roms (alphabetically).</summary>
    public static RomCard FromRomsFolder(string dataDir)
    {
        var modules = new List<(string, byte[])>();
        foreach (var name in ListAvailable(dataDir))
            modules.Add((name, File.ReadAllBytes(Path.Combine(RomsDir(dataDir), name + ".bin"))));
        return new RomCard(modules);
    }

    /// <summary>A ROM adapter with a single "soldered-in" module (by name, without extension).</summary>
    public static RomCard FromSingle(string dataDir, string name)
    {
        string path = Path.Combine(RomsDir(dataDir), name + ".bin");
        var modules = new List<(string, byte[])>();
        if (File.Exists(path)) modules.Add((name, File.ReadAllBytes(path)));
        return new RomCard(modules);
    }
}
