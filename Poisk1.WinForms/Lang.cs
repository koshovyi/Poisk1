using System.Text.Json;

namespace Poisk1.WinForms;

/// <summary>
/// EN/UK localization with strings in Data/langs/{en,uk}.json. English is the default.
/// Changing the language → fires the <see cref="Changed"/> event. A missing key → falls back to English, then to the key itself.
/// </summary>
public static class L
{
    public static Language Cur = Language.En;
    public static event Action? Changed;

    private static readonly Dictionary<Language, Dictionary<string, string>> Maps = new();
    private static readonly Dictionary<Language, string> Files = new()
    {
        [Language.En] = "en.json",
        [Language.Uk] = "uk.json",
    };

    /// <summary>Load the dictionaries from Data/langs. Call once at startup.</summary>
    public static void Init(string dataDir)
    {
        string dir = Path.Combine(dataDir, "langs");
        foreach (var (lang, file) in Files)
        {
            try
            {
                string path = Path.Combine(dir, file);
                if (File.Exists(path))
                {
                    var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
                    if (d is not null) { Maps[lang] = d; continue; }
                }
            }
            catch { /* corrupted file — leave it empty (falls back to key/English) */ }
            Maps[lang] = new();
        }
    }

    public static void Set(Language lang) { if (Cur != lang) { Cur = lang; Changed?.Invoke(); } }

    private static string T(string key)
    {
        if (Maps.TryGetValue(Cur, out var m) && m.TryGetValue(key, out var v)) return v;
        if (Maps.TryGetValue(Language.En, out var e) && e.TryGetValue(key, out var ev)) return ev;
        return key;
    }
    private static string T(string key, params object[] args) => string.Format(T(key), args);

    private static string Num(double m) => m.ToString("0.##");

    // --- Title / menu ---
    public static string Title(string bios) => T("title", bios);
    public static string MenuComputer => T("menu.computer");
    public static string MenuSlots => T("menu.slots");
    public static string MenuCassette => T("menu.cassette");
    public static string MenuFloppy => T("menu.floppy");
    public static string MenuVideo => T("menu.video");
    public static string MenuAbout => T("menu.about");
    public static string MenuLanguage => T("menu.language");

    // --- Computer ---
    public static string Ram(int kb) => T("ram", kb);
    public static string CpuFreq => T("cpuFreq");
    public static string Mhz(double m, string noteKey) => noteKey.Length == 0
        ? T("mhz", Num(m))
        : T("mhz.note", Num(m), T("note." + noteKey));
    public static string Mhz(int hz) => T("mhz", Num(hz / 1_000_000.0));
    public static string SoundDevice => T("soundDevice");
    public static string Reset => T("reset");
    public static string Exit => T("exit");

    // --- Slots ---
    public static string Slot(int n) => T("slot", n);
    public static string SlotWith(int n, string card) => T("slotWith", n, card);
    public static string Empty => T("empty");
    public static string AllModules => T("allModules");
    public static string DefaultBios => T("defaultBios");

    // --- Cassette ---
    public static string InsertCassette => T("insertCassette");
    public static string Eject => T("eject");

    // --- Disk drive ---
    public static string DriveConnected(char d) => T("drive.connected", d);
    public static string DriveSlot(char d, string disk) => T("drive.slot", d, disk);
    public static string DriveEmpty(char d) => T("drive.empty", d);
    public static string EjectDrive(char d) => T("drive.eject", d);

    // --- Hard disk (B942) ---
    public static string MenuHdd => T("menu.hdd");
    public static string HddSelect => T("hdd.select");
    public static string HddImageSlot(string file, int mb) => T("hdd.slot", file, mb);
    public static string HddNone => T("hdd.none");

    // --- Video ---
    public static string VidAuto => T("vid.auto");
    public static string VidText => T("vid.text");
    public static string VidGfxAuto => T("vid.gfxAuto");
    public static string VidGfx320 => T("vid.gfx320");
    public static string VidGfx640 => T("vid.gfx640");

    // --- Status bar ---
    public static string StStopped => T("st.stopped");
    public static string StVideo => T("st.video");
    public static string StCassette => T("st.cassette");
    public static string StRamFull(uint prog, uint cards) => T("st.ramFull", prog, cards);
    public static string StRamSimple(uint prog) => T("st.ramSimple", prog);
    public static string VidTextCols(int cols) => T("vid.textCols", cols);
    public static string VidGfxRes(int w) => T("vid.gfxRes", w);
    public static string VidAutoRes(int w, int h) => T("vid.autoRes", w, h);
    public static string StBios => T("st.bios");
    public static string StProgram => T("st.program");
    public static string StHlt => T("st.hlt");
    public static string MachineLabel => T("machineLabel");

    // --- About ---
    public static string AboutAuthor => T("about.author");
    public static string AboutWebsite => T("about.website");
    public static string AboutGitHub => T("about.github");

    // --- Dialogs ---
    public static string DlgCassetteTitle => T("dlg.cassetteTitle");
    public static string DlgFloppyTitle(char d) => T("dlg.floppyTitle", d);
    public static string DlgHddTitle => T("dlg.hddTitle");
    public static string FilterCassette => T("filter.cassette");
    public static string FilterFloppy => T("filter.floppy");
    public static string FilterHdd => T("filter.hdd");
}
