using System.Drawing.Imaging;
using Poisk1.Core;
using Poisk1.Core.Devices;
using Poisk1.Core.Expansion;
using Poisk1.Core.Video;

namespace Poisk1.WinForms;

/// <summary>
/// Emulator main window: menu (BIOS, Reset, Slots), the Poisk video screen, and the trace log.
/// Control layout is in MainForm.Designer.cs; this holds the machine logic, rendering, and dynamic menus.
/// </summary>
public sealed partial class MainForm : Form
{
    private const int NominalHz = 5_000_000;   // real Poisk clock frequency (~5 MHz)
    private const int Fps = 60;
    private const int PixelScale = 2;
    private const int ScreenW = 320, ScreenH = 200; // 40×25 text, 8×8 glyphs

    // Emulation frequency (CPU cycles per wall-clock second). 5 MHz = real speed; higher is
    // faster (in particular it speeds up cassette loading, since the tape is cycle-bound).
    private int _cpuHz = NominalHz;
    private int CyclesPerFrame => _cpuHz / Fps;

    private readonly string _dataDir;
    private readonly EmulatorConfig _config;
    private readonly int[]? _glyphMap;

    private Machine? _machine;
    private string? _currentBios;

    private int[] _frame = new int[ScreenW * ScreenH];
    private Bitmap _bmp = new(ScreenW, ScreenH, PixelFormat.Format32bppArgb); // native frame
    // 4:3 presentation (as on the Poisk's TV/monitor): the 200 scan lines are stretched vertically,
    // so 40- and 80-column text fill the same screen. The native frame is stretched into here.
    private const int DispW = 640, DispH = 480;
    private readonly Bitmap _display = new(DispW, DispH, PixelFormat.Format32bppArgb);

    private bool _pendingTapeVideo; // after inserting a tape: switch video when the game starts

    private readonly SpeakerAudio _audio = new();
    private readonly short[] _audioBuf = new short[8192];

    private readonly List<ToolStripMenuItem> _biosItems = new();
    private readonly List<ToolStripMenuItem> _ramItems = new();
    private readonly List<ToolStripMenuItem> _freqItems = new();
    private readonly List<ToolStripMenuItem>[] _slotItems = { new(), new(), new(), new() };
    private readonly ToolStripMenuItem[] _slotMenus = new ToolStripMenuItem[4];
    private readonly ToolStripMenuItem _videoMenu = new("&Видео");

    private readonly string?[] _fddPath = new string?[2];          // drive A/B images
    private readonly ToolStripMenuItem[] _fddItems = new ToolStripMenuItem[2];
    private readonly bool[] _fddPresent = { true, true };          // which drives are connected (1 or 2)
    private string? _hddImage;                                     // chosen HDD image file (under Data/hdd_disk); null = config default
    private ToolStripMenuItem? _hddItem;                           // "Hard disk" menu entry (shows current image)
    private bool _soundOn = true;                                  // sound state (preserved across languages)

    private const string WebsiteUrl = "https://koshovyi.com";
    private const string GitHubUrl = "https://github.com/koshovyi";   // TODO: confirm the repository

    public MainForm()
    {
        InitializeComponent();

        string baseDir = AppContext.BaseDirectory;
        _dataDir = Roms.FindDataDir(baseDir) ?? baseDir;
        L.Init(_dataDir);
        _config = ConfigStore.Load(_dataDir);
        _glyphMap = _config.BuildGlyphMap();

        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* keep the default */ }

        _screen.Image = _display; // show the 4:3 presentation buffer
        BuildMenus();

        _timer.Interval = 1000 / Fps;
        _timer.Tick += OnTick;
        FormClosed += (_, _) => _audio.Dispose();
        L.Changed += OnLanguageChanged;

        AppendLog($"Data: {_dataDir}");
        StartMachine(_config.DefaultBios);
        _timer.Start();
    }

    // ====================== Dynamic menus ======================

    private void BuildMenus()
    {
        // Idempotency (rebuild on language change): clear the menu and the trackers.
        _menu.Items.Clear();
        _biosItems.Clear(); _ramItems.Clear(); _freqItems.Clear();
        foreach (var l in _slotItems) l.Clear();
        _videoMenu.DropDownItems.Clear();

        // --- Computer: BIOS, RAM, frequency, sound, reset, exit ---
        var computerMenu = new ToolStripMenuItem(L.MenuComputer);
        foreach (var entry in _config.Bioses)
        {
            var item = new ToolStripMenuItem(entry.Name) { Tag = entry.File, Checked = entry.File == _config.DefaultBios };
            item.Click += (_, _) =>
            {
                _config.DefaultBios = (string)item.Tag;
                ConfigStore.Save(_dataDir, _config);
                StartMachine((string)item.Tag);
                UpdateComputerChecks();
            };
            _biosItems.Add(item);
            computerMenu.DropDownItems.Add(item);
        }
        computerMenu.DropDownItems.Add(new ToolStripSeparator());

        foreach (int kb in new[] { 128, 512 })
        {
            var it = new ToolStripMenuItem(L.Ram(kb)) { Tag = kb, Checked = _config.RamSizeKb == kb };
            it.Click += (_, _) =>
            {
                _config.RamSizeKb = (int)it.Tag;
                ConfigStore.Save(_dataDir, _config);
                if (_currentBios is not null) StartMachine(_currentBios);
                UpdateComputerChecks();
            };
            _ramItems.Add(it);
            computerMenu.DropDownItems.Add(it);
        }
        computerMenu.DropDownItems.Add(new ToolStripSeparator());

        var freqMenu = new ToolStripMenuItem(L.CpuFreq);
        foreach (var (hz, note) in new (int, string)[]
        {
            (1_000_000, "slow"), (2_500_000, ""), (5_000_000, "real"),
            (10_000_000, ""), (25_000_000, ""), (50_000_000, ""), (100_000_000, "max"),
        })
        {
            var it = new ToolStripMenuItem(L.Mhz(hz / 1_000_000.0, note)) { Tag = hz, Checked = hz == _cpuHz };
            it.Click += (_, _) => { _cpuHz = (int)it.Tag; UpdateComputerChecks(); };
            _freqItems.Add(it);
            freqMenu.DropDownItems.Add(it);
        }
        computerMenu.DropDownItems.Add(freqMenu);

        // Speaker sound — between frequency and Reset (no separator).
        var soundItem = new ToolStripMenuItem(L.SoundDevice) { CheckOnClick = true, Checked = _soundOn };
        soundItem.Click += (_, _) => { _soundOn = soundItem.Checked; if (_machine is not null) _machine.SoundEnabled = _soundOn; };
        computerMenu.DropDownItems.Add(soundItem);

        var resetItem = new ToolStripMenuItem(L.Reset) { ShortcutKeys = Keys.Control | Keys.R };
        resetItem.Click += (_, _) => ResetMachine();
        computerMenu.DropDownItems.Add(resetItem);

        computerMenu.DropDownItems.Add(new ToolStripSeparator());
        var exitItem = new ToolStripMenuItem(L.Exit);
        exitItem.Click += (_, _) => Close();
        computerMenu.DropDownItems.Add(exitItem);

        // --- Slots ---
        var slotsMenu = new ToolStripMenuItem(L.MenuSlots);
        for (int slot = 0; slot < 4; slot++)
        {
            var slotMenu = new ToolStripMenuItem(L.Slot(slot + 1));
            _slotMenus[slot] = slotMenu;
            int s = slot;
            ToolStripMenuItem MakeOption(string id, string name)
            {
                var it = new ToolStripMenuItem(name) { Tag = id };
                it.Click += (_, _) => SetSlot(s, id);
                _slotItems[s].Add(it);
                return it;
            }
            slotMenu.DropDownItems.Add(MakeOption("", L.Empty));
            foreach (var t in CardCatalog.Types)
            {
                if (t.Id == "rom")
                {
                    var romMenu = new ToolStripMenuItem(t.DisplayName);
                    romMenu.DropDownItems.Add(MakeOption("rom", L.AllModules));
                    romMenu.DropDownItems.Add(new ToolStripSeparator());
                    foreach (var name in RomCard.ListAvailable(_dataDir))
                        romMenu.DropDownItems.Add(MakeOption("rom:" + name, name));
                    slotMenu.DropDownItems.Add(romMenu);
                }
                else if (t.Id == "fdd")
                {
                    var fddBiosMenu = new ToolStripMenuItem(t.DisplayName);
                    fddBiosMenu.DropDownItems.Add(MakeOption("fdd", L.DefaultBios));
                    fddBiosMenu.DropDownItems.Add(new ToolStripSeparator());
                    foreach (var name in Poisk1.Core.Expansion.FddController.ListBioses(_dataDir))
                        fddBiosMenu.DropDownItems.Add(MakeOption("fdd:" + name, name));
                    slotMenu.DropDownItems.Add(fddBiosMenu);
                }
                else slotMenu.DropDownItems.Add(MakeOption(t.Id, t.DisplayName));
            }
            slotsMenu.DropDownItems.Add(slotMenu);
        }

        // --- Cassette ---
        var tapeMenu = new ToolStripMenuItem(L.MenuCassette);
        var insertItem = new ToolStripMenuItem(L.InsertCassette);
        insertItem.Click += (_, _) => InsertCassette();
        var ejectItem = new ToolStripMenuItem(L.Eject);
        ejectItem.Click += (_, _) => { _machine?.Cassette.Eject(); AppendLog(L.Cur == Language.En ? "Cassette ejected." : "Кассету вийнято."); };
        tapeMenu.DropDownItems.Add(insertItem);
        tapeMenu.DropDownItems.Add(ejectItem);

        // --- Video ---
        _videoMenu.Text = L.MenuVideo;
        foreach (var (mode, name) in new[]
        {
            (CgaAdapter.ModeOverride.Auto, L.VidAuto),
            (CgaAdapter.ModeOverride.Text, L.VidText),
            (CgaAdapter.ModeOverride.GfxAuto, L.VidGfxAuto),
            (CgaAdapter.ModeOverride.Gfx320, L.VidGfx320),
            (CgaAdapter.ModeOverride.Gfx640, L.VidGfx640),
        })
        {
            var cur = _machine?.Video.Override ?? CgaAdapter.ModeOverride.Auto;
            var it = new ToolStripMenuItem(name) { Tag = mode, Checked = mode == cur };
            it.Click += (_, _) => SetVideo(mode);
            _videoMenu.DropDownItems.Add(it);
        }

        // --- Disk drive: all drive A functions, separator, all drive B functions ---
        var fddMenu = new ToolStripMenuItem(L.MenuFloppy);
        for (int drive = 0; drive < 2; drive++)
        {
            int d = drive; char ch = (char)('A' + d);
            if (d > 0) fddMenu.DropDownItems.Add(new ToolStripSeparator());

            // 1) connect the drive
            var en = new ToolStripMenuItem(L.DriveConnected(ch)) { CheckOnClick = true, Checked = _fddPresent[d] };
            en.Click += (_, _) =>
            {
                _fddPresent[d] = en.Checked; ApplyFddPresence();
                AppendLog(L.Cur == Language.En
                    ? $"Drive {ch} {(en.Checked ? "connected" : "disabled")}. Reset (Ctrl+R)."
                    : $"Привід {ch} {(en.Checked ? "підключено" : "вимкнено")}. Скинь (Ctrl+R).");
            };
            fddMenu.DropDownItems.Add(en);

            // 2) choose the image
            var item = new ToolStripMenuItem(_fddPath[d] is null ? L.DriveEmpty(ch)
                : L.DriveSlot(ch, Path.GetFileName(_fddPath[d]!)));
            item.Click += (_, _) => AttachFloppyDialog(d);
            _fddItems[d] = item;
            fddMenu.DropDownItems.Add(item);

            // 3) eject the diskette
            var eject = new ToolStripMenuItem(L.EjectDrive(ch));
            eject.Click += (_, _) => EjectFloppy(d);
            fddMenu.DropDownItems.Add(eject);
        }

        // --- Hard disk (B942 НЖМД) ---
        var hddMenu = new ToolStripMenuItem(L.MenuHdd);
        _hddItem = new ToolStripMenuItem(_hddImage is null ? L.HddNone : L.HddImageSlot(_hddImage, 0));
        var hddSelect = new ToolStripMenuItem(L.HddSelect);
        hddSelect.Click += (_, _) => AttachHddDialog();
        hddMenu.DropDownItems.Add(_hddItem);
        hddMenu.DropDownItems.Add(hddSelect);

        // --- About (right-aligned) ---
        var aboutMenu = new ToolStripMenuItem(L.MenuAbout) { Alignment = ToolStripItemAlignment.Right };
        var langMenu = new ToolStripMenuItem(L.MenuLanguage);
        var enItem = new ToolStripMenuItem("English") { Checked = L.Cur == Language.En };
        enItem.Click += (_, _) => L.Set(Language.En);
        var ukItem = new ToolStripMenuItem("Українська") { Checked = L.Cur == Language.Uk };
        ukItem.Click += (_, _) => L.Set(Language.Uk);
        langMenu.DropDownItems.Add(enItem);
        langMenu.DropDownItems.Add(ukItem);
        aboutMenu.DropDownItems.Add(langMenu);
        aboutMenu.DropDownItems.Add(new ToolStripSeparator());
        aboutMenu.DropDownItems.Add(new ToolStripMenuItem($"{L.AboutAuthor}: Dmytro Koshovyi") { Enabled = false });
        var siteItem = new ToolStripMenuItem($"{L.AboutWebsite}: koshovyi.com");
        siteItem.Click += (_, _) => OpenUrl(WebsiteUrl);
        var ghItem = new ToolStripMenuItem($"{L.AboutGitHub}: {GitHubUrl}");
        ghItem.Click += (_, _) => OpenUrl(GitHubUrl);
        aboutMenu.DropDownItems.Add(siteItem);
        aboutMenu.DropDownItems.Add(ghItem);

        _menu.Items.Add(computerMenu);
        _menu.Items.Add(slotsMenu);
        _menu.Items.Add(tapeMenu);
        _menu.Items.Add(fddMenu);
        _menu.Items.Add(hddMenu);
        _menu.Items.Add(_videoMenu);
        _menu.Items.Add(aboutMenu);
    }

    private void OnLanguageChanged()
    {
        BuildMenus();
        UpdateChecks();          // slot labels + check marks
        UpdateComputerChecks();
        if (_currentBios is not null) Text = L.Title(_currentBios);
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser — ignore */ }
    }

    private Poisk1.Core.Expansion.FddController? FindFdd()
        => _machine?.Slots.OfType<Poisk1.Core.Expansion.FddController>().FirstOrDefault();

    /// <summary>Attach a diskette image to drive d (0=A, 1=B) via a dialog.</summary>
    private void AttachFloppyDialog(int d)
    {
        if (FindFdd() is null)
        {
            AppendLog("Спершу встанови контролер B504 (НГМД) у слот (меню «Слоти»).");
            return;
        }
        using var dlg = new OpenFileDialog
        {
            Title = L.DlgFloppyTitle((char)('A' + d)),
            Filter = L.FilterFloppy,
            InitialDirectory = Path.Combine(_dataDir, "disk"),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _fddPath[d] = dlg.FileName;
        AttachFloppy(d);
    }

    /// <summary>Load the saved image of drive d into the current controller.</summary>
    private void AttachFloppy(int d)
    {
        var fdd = FindFdd();
        if (fdd is null || _fddPath[d] is null) return;
        try
        {
            var data = File.ReadAllBytes(_fddPath[d]!);
            var disk = new Poisk1.Core.Devices.FloppyDisk(data, Path.GetFileName(_fddPath[d]!));
            fdd.Insert(d, disk);
            _fddItems[d].Text = L.DriveSlot((char)('A' + d), $"{disk.Name} ({disk.Cylinders}×{disk.Heads}×{disk.Sectors})") + "…";
            AppendLog($"Привід {(char)('A' + d)}: {disk.Name} ({data.Length / 1024} КБ). Скинь машину (Ctrl+R) для завантаження з дискети.");
            if (_machine?.Slots.OfType<Poisk1.Core.Expansion.RamCard>().Any() != true)
                AppendLog("⚠ Для MS-DOS додай розширення ОЗП у слот (B107/B109) — інакше «internal stack overflow».");
        }
        catch (Exception ex) { AppendLog($"Помилка образу: {ex.Message}"); }
    }

    private void EjectFloppy(int d)
    {
        _fddPath[d] = null;
        FindFdd()?.Insert(d, null);
        _fddItems[d].Text = L.DriveEmpty((char)('A' + d));
        AppendLog($"Привід {(char)('A' + d)} спорожнено.");
    }

    /// <summary>Apply drive connectivity to the controller (1 or 2 drives).</summary>
    private void ApplyFddPresence()
    {
        var fdd = FindFdd();
        if (fdd is null) return;
        fdd.Fdc.Present[0] = _fddPresent[0];
        fdd.Fdc.Present[1] = _fddPresent[1];
    }

    private Poisk1.Core.Expansion.HddController? FindHdd()
        => _machine?.Slots.OfType<Poisk1.Core.Expansion.HddController>().FirstOrDefault();

    private string HddDir() => Path.Combine(_dataDir, "hdd_disk");

    /// <summary>Load (or create, zeroed) the configured HDD image and attach it to the B942 (drive 0).</summary>
    private void AttachHdd()
    {
        var hdd = FindHdd();
        if (hdd is null) return;
        try
        {
            Directory.CreateDirectory(HddDir());
            string file = _hddImage ?? _config.HddImage;
            string path = Path.Combine(HddDir(), file);
            var (cyl, heads, spt) = _config.HddGeometry();
            long size = (long)cyl * heads * spt * Poisk1.Core.Devices.HardDisk.SectorSize;
            byte[] data;
            if (File.Exists(path)) data = File.ReadAllBytes(path);
            else { data = new byte[size]; File.WriteAllBytes(path, data); AppendLog($"HDD: створено порожній образ {file} ({size / 1024 / 1024} МБ)."); }
            var disk = new Poisk1.Core.Devices.HardDisk(data, cyl, heads, spt, file, path);
            hdd.Attach(0, disk);
            if (_hddItem is not null) _hddItem.Text = L.HddImageSlot(file, (int)(disk.SizeBytes / 1024 / 1024));
            AppendLog($"HDD: {file} {cyl}×{heads}×{spt} = {disk.SizeBytes / 1024 / 1024} МБ. Скинь (Ctrl+R).");
            if (_machine?.Slots.OfType<Poisk1.Core.Expansion.RamCard>().Any() != true)
                AppendLog("⚠ Для MS-DOS додай розширення ОЗП у слот (B107/B109).");
        }
        catch (Exception ex) { AppendLog($"Помилка образу HDD: {ex.Message}"); }
    }

    /// <summary>Choose a different HDD image file from Data/hdd_disk (then Reset to use it).</summary>
    private void AttachHddDialog()
    {
        if (FindHdd() is null)
        {
            AppendLog("Спершу встанови контролер B942 (НЖМД) у слот (меню «Слоти»).");
            return;
        }
        Directory.CreateDirectory(HddDir());
        using var dlg = new OpenFileDialog
        {
            Title = L.DlgHddTitle,
            Filter = L.FilterHdd,
            InitialDirectory = HddDir(),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _hddImage = Path.GetFileName(dlg.FileName);
        AttachHdd();
    }

    /// <summary>Number of connected floppy (NGMD) drives (0..2), or -1 if no controller is present.</summary>
    private int FddDriveCount() => FindFdd() is null ? -1 : (_fddPresent[0] ? 1 : 0) + (_fddPresent[1] ? 1 : 0);

    /// <summary>Update the check marks for the selected BIOS, RAM, and frequency in the Computer menu.</summary>
    private void UpdateComputerChecks()
    {
        foreach (var it in _biosItems) it.Checked = (string)it.Tag == _currentBios;
        foreach (var it in _ramItems) it.Checked = (int)it.Tag == _config.RamSizeKb;
        foreach (var it in _freqItems) it.Checked = (int)it.Tag == _cpuHz;
    }

    private void SetVideo(CgaAdapter.ModeOverride mode)
    {
        if (_machine is not null) _machine.Video.Override = mode;
        foreach (ToolStripMenuItem mi in _videoMenu.DropDownItems)
            mi.Checked = (CgaAdapter.ModeOverride)mi.Tag! == mode;
    }

    // ====================== Machine control ======================

    /// <summary>Cold start with the selected BIOS (a new machine: fresh RAM/VRAM).</summary>
    private void StartMachine(string biosFile)
    {
        _timer.Stop();
        try
        {
            string biosPath = Path.Combine(_dataDir, biosFile);
            if (!File.Exists(biosPath))
            {
                AppendLog($"BIOS не знайдено: {biosPath}");
                _machine = null;
                return;
            }

            string fontPath = Path.Combine(_dataDir, _config.Font);
            string? font = File.Exists(fontPath) ? fontPath : null;
            if (font is null)
                AppendLog($"УВАГА: шрифт {_config.Font} не знайдено — символи не малюватимуться.");

            _machine = new Machine(
                new MachineConfig
                {
                    RamSize = _config.RamSizeKb * 1024,
                    BiosPath = biosPath,
                    FontPath = font,
                    GlyphMap = _glyphMap,
                },
                AppendLog);

            // Install cards from the config slots (before Reset — the BIOS scans them at startup).
            for (int slot = 0; slot < 4; slot++)
            {
                var card = CardCatalog.Create(_config.Slots[slot], _dataDir);
                if (card is not null)
                {
                    _machine.InstallCard(slot, card);
                    AppendLog($"Слот {slot + 1}: {card.DisplayName}");
                }
            }

            // Re-attach the diskette images (the card was recreated) — BEFORE Reset.
            if (FindFdd() is not null)
            {
                ApplyFddPresence();
                for (int d = 0; d < 2; d++) if (_fddPath[d] is not null) AttachFloppy(d);
            }

            // Re-attach the hard-disk image (the B942 card was recreated) — BEFORE Reset.
            if (FindHdd() is not null) AttachHdd();

            _machine.Reset();

            _currentBios = biosFile;
            Text = L.Title(biosFile);
            AppendLog($"Запущено: {biosFile} (RAM {_config.RamSizeKb} КБ)");

            string biosName = _config.Bioses.FirstOrDefault(b => b.File == biosFile)?.Name ?? biosFile;
            _stMachine.Text = L.MachineLabel + biosName;
            uint progKb = _machine.Memory.RamCeiling() / 1024; // program RAM (base−32 + cards)
            uint baseProgKb = (uint)_config.RamSizeKb - 32;
            _stRam.Text = progKb > baseProgKb
                ? L.StRamFull(progKb, progKb - baseProgKb)
                : L.StRamSimple(progKb);
        }
        catch (Exception ex)
        {
            AppendLog($"ПОМИЛКА: {ex.Message}");
            _machine = null;
        }
        finally
        {
            UpdateChecks();
            _timer.Start();
        }
    }

    private void ResetMachine()
    {
        if (_currentBios is null)
        {
            AppendLog("Нема активного BIOS для скидання.");
            return;
        }
        AppendLog("--- Reset ---");
        StartMachine(_currentBios);
    }

    private void UpdateChecks()
    {
        foreach (var item in _biosItems)
            item.Checked = (string?)item.Tag == _currentBios;
        for (int s = 0; s < 4; s++)
        {
            string id = _config.Slots[s];
            // Slot label = name of the installed card (or "Slot N" if empty).
            if (_slotMenus[s] is { } m)
                m.Text = string.IsNullOrEmpty(id) ? L.Slot(s + 1) : L.SlotWith(s + 1, CardCatalog.DisplayName(id));
            foreach (var item in _slotItems[s])
                item.Checked = (string?)item.Tag == id;
        }
    }

    /// <summary>Plug card id into slot s (id="" — remove), save the config, and restart.</summary>
    private void SetSlot(int slot, string id)
    {
        _config.Slots[slot] = id;
        ConfigStore.Save(_dataDir, _config);
        AppendLog($"Слот {slot + 1} -> {CardCatalog.DisplayName(id)} (перезапуск)");
        if (_currentBios is not null) StartMachine(_currentBios);
    }

    /// <summary>Insert a .cas and start "playback". Then: F1 → file name → Enter.</summary>
    private void InsertCassette()
    {
        if (_machine is null) return;
        using var dlg = new OpenFileDialog
        {
            Title = L.DlgCassetteTitle,
            Filter = L.FilterCassette,
            InitialDirectory = _dataDir,
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _pendingTapeVideo = true; // switch to graphics when the game takes over control
            string name = Path.GetFileNameWithoutExtension(dlg.FileName);
            if (dlg.FileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            {
                var (samples, rate) = WavCassette.LoadSamples(dlg.FileName);
                // Take the name to type from the tape HEADER (matched exactly), not from the file name.
                string tapeName = name;
                try { tapeName = WavCassette.Decode(dlg.FileName).Name; } catch { /* keep the file name */ }
                _machine.Cassette.InsertWav(samples, rate, tapeName);
                AppendLog($"Стрічку вставлено: {Path.GetFileName(dlg.FileName)} ({samples.Length} семплів @ {rate} Гц).");
                AppendLog($"▶ Натисніть F1, введіть ІМ'Я «{tapeName.ToUpperInvariant()}», Enter, потім ще Enter (Вкл. магнитофон).");
                AppendLog("  Якщо після запуску екран «кашею» — Видео → Графіка (авто).");
            }
            else
            {
                _machine.Cassette.Insert(File.ReadAllBytes(dlg.FileName), name);
                AppendLog($"Стрічку (.cas) вставлено: {Path.GetFileName(dlg.FileName)}.");
                AppendLog($"▶ Натисніть F1, введіть ІМ'Я «{name.ToUpperInvariant()}», Enter, потім ще Enter.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Помилка кассети: {ex.Message}");
        }
    }

    // ====================== Keyboard ======================

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_machine is not null && KeyboardMap.Apply(_machine.Keyboard, e.KeyCode, true))
            e.Handled = e.SuppressKeyPress = true;
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_machine is not null && KeyboardMap.Apply(_machine.Keyboard, e.KeyCode, false))
            e.Handled = e.SuppressKeyPress = true;
        base.OnKeyUp(e);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        _machine?.Keyboard.ReleaseAll(); // don't leave "stuck" keys when focus is lost
        base.OnDeactivate(e);
    }

    // ====================== Rendering ======================

    private void OnTick(object? sender, EventArgs e)
    {
        if (_machine is not null)
        {
            // Cycles per frame at the selected CPU frequency (5 MHz = real speed).
            _machine.RunCycles(CyclesPerFrame);

            // Enforce the floppy count in the equipment word (0040:0010, bit0 + bits6-7),
            // because the FDD BIOS hard-codes 2 drives; this way DOS sees the chosen configuration.
            int fc = FddDriveCount();
            if (fc >= 0)
            {
                byte eq = (byte)(_machine.Memory.ReadByte(0x0410) & ~0xC1);
                if (fc > 0) eq |= (byte)(0x01 | ((fc - 1) << 6));
                _machine.Memory.WriteByte(0x0410, eq);
            }

            // When a loaded game takes over control (CS has left the BIOS) — auto-graphics.
            if (_pendingTapeVideo && _machine.Cpu.State.CS != 0xF000)
            {
                _pendingTapeVideo = false;
                SetVideo(CgaAdapter.ModeOverride.GfxAuto);
                AppendLog("Гра запущена. (Якщо текстова — Видео → Авто/Текст.)");
            }

            int n = _machine.DrainAudio(_audioBuf); // speaker sound → driver
            if (n > 0) _audio.Feed(_audioBuf, n);

            EnsureBuffers(_machine.Video.Width, _machine.Video.Height);
            _machine.Video.Render(_frame);
        }
        else
        {
            Array.Clear(_frame);
        }
        BlitFrame();
        _screen.Invalidate();

        if (++_statusTick >= 6) { _statusTick = 0; UpdateStatus(); } // ~10 Hz
    }

    private int _statusTick;

    /// <summary>Update the status bar (dynamic fields).</summary>
    private void UpdateStatus()
    {
        if (_machine is null)
        {
            _stVideo.Text = L.StVideo + ": —"; _stCassette.Text = L.StCassette + ": —";
            _stCpu.Text = "----:----"; _stFreq.Text = ""; _stState.Text = L.StStopped;
            return;
        }
        var st = _machine.Cpu.State;
        _stVideo.Text = L.StVideo + ": " + _machine.Video.Override switch
        {
            CgaAdapter.ModeOverride.Text => L.VidTextCols(40),
            CgaAdapter.ModeOverride.Gfx320 => L.VidGfxRes(320),
            CgaAdapter.ModeOverride.Gfx640 => L.VidGfxRes(640),
            CgaAdapter.ModeOverride.GfxAuto => L.VidGfxRes(_machine.Video.Width),
            _ => L.VidAutoRes(_machine.Video.Width, _machine.Video.Height),
        };
        var c = _machine.Cassette;
        _stCassette.Text = L.StCassette + ": " + (c.Inserted ? $"{c.Name}{(c.Playing ? " ▶" : " ⏸")}" : "—");
        _stCpu.Text = $"{st.CS:X4}:{st.IP:X4}";
        _stFreq.Text = L.Mhz(_cpuHz);
        _stState.Text = _machine.Cpu.Halted ? L.StHlt
            : st.CS == 0xF000 ? L.StBios : L.StProgram;
    }

    /// <summary>Recreate the frame/Bitmap when the resolution changes (320 text ↔ 640 graphics).</summary>
    private void EnsureBuffers(int w, int h)
    {
        if (_bmp.Width == w && _bmp.Height == h) return;
        var old = _bmp;
        _bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        _frame = new int[w * h];
        old.Dispose();
    }

    private void BlitFrame()
    {
        var rect = new Rectangle(0, 0, _bmp.Width, _bmp.Height);
        BitmapData data = _bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(_frame, 0, data.Scan0, _frame.Length);
        }
        finally
        {
            _bmp.UnlockBits(data);
        }
        // Stretch the native frame (320/640 × 200) into the 4:3 presentation (640×480), with chunky pixels.
        using var g = Graphics.FromImage(_display);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        g.DrawImage(_bmp, 0, 0, DispW, DispH);
    }

    private void AppendLog(string line)
    {
        if (_log.IsDisposed) return;
        _log.AppendText(line + Environment.NewLine);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        FindHdd()?.Flush(); // persist pending hard-disk writes
        base.OnFormClosed(e);
    }
}
