using Poisk1.Core.Cpu;
using Poisk1.Core.Devices;
using Poisk1.Core.Expansion;
using Poisk1.Core.Io;
using Poisk1.Core.Video;

namespace Poisk1.Core;

/// <summary>
/// Assembly of the "Poisk-1" machine: CPU + memory + port bus + devices.
/// Responsible for reset and for running the CPU for a given cycle budget.
/// </summary>
public sealed class Machine
{
    public MachineConfig Config { get; }
    public MemoryBus Memory { get; }
    public IoBus Io { get; }
    public CgaAdapter Video { get; }
    public Pic8259 Pic { get; }
    public Pit8253 Pit { get; }
    public Keyboard8255 Keyboard { get; }
    public Cassette Cassette { get; }
    public Speaker Speaker { get; }
    public ICpu Cpu { get; }

    /// <summary>Counter of executed CPU cycles (for cassette synchronization, etc.).</summary>
    public long Cycles { get; private set; }

    /// <summary>The four expansion slots of the Poisk. null = empty slot.</summary>
    public IExpansionCard?[] Slots { get; } = new IExpansionCard?[4];

    /// <summary>Trace hook (reset, fetch, unknown ports).</summary>
    public Action<string>? Trace { get; }

    // CPU-cycle divisor → PIT input cycles. On the Poisk the 8253 is clocked from CPU/2
    // (5 MHz → 2.5 MHz), not from 1.19 MHz as on the IBM PC. This is critical for cassette
    // decoding: the BIOS measures the pulse period by the difference in PIT counter readings
    // and requires for the leader > 888 ticks per half-period of ~486 us — which is exactly
    // what a PIT @ ~2.5 MHz gives (1215 ticks).
    public int PitDivisor { get; set; } = 2;
    private int _pitAcc;

    // --- Speaker audio: sample the speaker level at ~44.1 kHz in RunCycles ---
    private const int CpuHz = 5_000_000;
    public const int AudioRate = 44_100;
    private const short AudioAmp = 7000;       // square-wave amplitude (16-bit, -32768..32767)
    private const double CyclesPerSample = (double)CpuHz / AudioRate; // ≈113.4
    private double _audioAcc;
    private readonly short[] _audioRing = new short[AudioRate]; // ~1 s buffer
    private int _audioHead, _audioCount;
    /// <summary>Speaker sound enabled (generate samples).</summary>
    public bool SoundEnabled { get; set; } = true;

    /// <summary>Temporary diagnostics toggle (PIT clocking / timer interrupt).</summary>
    public bool TickPitEnabled { get; set; } = true;

    private readonly byte[] _font;

    public Machine(MachineConfig config, Action<string>? trace = null)
    {
        Config = config;
        Trace = trace;

        Memory = new MemoryBus(config.RamSize);
        Io = new IoBus { Trace = trace };

        // --- ROM ---
        if (!string.IsNullOrEmpty(config.BiosPath))
            Memory.LoadBios(File.ReadAllBytes(config.BiosPath));

        _font = !string.IsNullOrEmpty(config.FontPath)
            ? File.ReadAllBytes(config.FontPath)
            : Array.Empty<byte>();

        // --- Devices and port map ---
        var videoControl = new VideoControl();
        Video = new CgaAdapter(Memory.Vram, _font, videoControl, config.GlyphMap, Memory);
        Pic = new Pic8259();
        Pit = new Pit8253(Pic);
        Keyboard = new Keyboard8255(videoControl);

        Cassette = new Cassette(() => Cycles);
        Speaker = new Speaker();

        Io.RegisterRange(new PortStub(0x00), 0x00, 0x0F); // 8237 DMA controller (stub)
        Io.Register(Pic, 0x20, 0x21);
        Io.RegisterRange(Pit, 0x40, 0x43);
        Io.RegisterRange(Keyboard, 0x60, 0x6B);
        Io.Register(Speaker, Speaker.Port);       // 0x61 — PPI port B (speaker), overrides the PPI
        Io.Register(Cassette, Cassette.DataPort); // 0x62 — cassette input (overrides the PPI)
        Io.RegisterRange(new NullDevice(), 0x28, 0x2A); // video trap / NMI latch
        Io.Register(new PortStub(0x3F), 0x0202); // platform-detector port (cassette loaders)
        Io.RegisterRange(Video, 0x3D0, 0x3DF);

        // --- CPU: self-contained 8088 interpreter ---
        Cpu = new Cpu8088(Memory, Io, Pic, trace);
    }

    public bool BiosLoaded => !string.IsNullOrEmpty(Config.BiosPath);

    /// <summary>Install (or remove, if card=null) a card into slot 0..3.</summary>
    public void InstallCard(int slot, IExpansionCard? card)
    {
        if ((uint)slot >= Slots.Length) return;
        Slots[slot]?.Remove(this);
        Slots[slot] = card;
        card?.Install(this);
    }

    public void Reset() => Cpu.Reset();

    /// <summary>
    /// NON-AUTHENTIC cassette loading: places the program data directly into memory at seg:ofs
    /// and transfers control to it (as the BIOS would after reading the tape).
    /// The machine must already be booted (BIOS has passed POST, the IVT is ready).
    /// </summary>
    public void LoadProgram(byte[] data, ushort seg = 0x0060, ushort ofs = 0x081E)
    {
        uint baseAddr = (uint)(seg << 4) + ofs;
        for (int i = 0; i < data.Length; i++)
            Memory.WriteByte(baseAddr + (uint)i, data[i]);

        var s = Cpu.State;
        s.CS = seg; s.IP = ofs;
        s.DS = s.ES = s.SS = seg;
        s.SP = 0xFFFE;
        s.AX = s.BX = s.CX = s.DX = s.SI = s.DI = s.BP = 0;
        s.Flags = 0x0202; // IF=1
    }

    /// <summary>
    /// AUTHENTIC cassette loading: programmatically invokes INT 15h AH=4 (FILE_READ) of the real
    /// BIOS, which itself decodes the WAV signal, checks the CRC, and places the data at
    /// loadSeg:0000. The machine must be idle (menu / waiting for a key). Returns true if the file
    /// was loaded (CF=0). If <paramref name="run"/>, it transfers control to the game (loadSeg:0000),
    /// as "Run" does.
    /// </summary>
    public bool LoadCassetteAuthentic(byte[] samples, int rate, string name, bool run = true, ushort loadSeg = 0x0060)
    {
        var cpu = (Cpu8088)Cpu;

        // File name (8 characters, UPPERCASE, space-padded) into the BIOS buffer at 0040:00B2.
        string nm = (name.ToUpperInvariant() + "        ")[..8];
        for (int i = 0; i < 8; i++) Memory.WriteByte((uint)(0x4B2 + i), (byte)nm[i]);
        // Load area [0040:00B0] = loadSeg.
        Memory.WriteByte(0x4B0, (byte)loadSeg);
        Memory.WriteByte(0x4B1, (byte)(loadSeg >> 8));

        Cassette.InsertWav(samples, rate, name); // "arm" the tape (starts on the first read of 0x62)

        const ushort retSeg = 0x0070, retOfs = 0x0000; // scratch area for the HLT return
        Memory.WriteByte((uint)((retSeg << 4) + retOfs), 0xF4); // HLT

        var st = Cpu.State;
        st.AH = 4; st.ES = loadSeg; st.DS = 0x0040; st.BX = 0x00B2;
        cpu.InvokeInterrupt(0x15, retSeg, retOfs);

        // Run the CPU until the handler returns control to the HLT (the read is in real time).
        long limit = 3_000_000_000; long spent = 0;
        while (!Cpu.Halted && spent < limit) { RunCycles(4_000_000); spent += 4_000_000; }

        bool ok = !Cpu.State.GetFlag(CpuState.CF);
        if (ok && run)
        {
            // "Run": PUSH 0; RETF → jump to loadSeg:0000 (the game relocates itself).
            var g = Cpu.State;
            g.CS = loadSeg; g.IP = 0x0000;
            g.SS = 0x0000; g.SP = 0x03FF;
            cpu.ClearHalt();
        }
        return ok;
    }

    /// <summary>
    /// Execute approximately <paramref name="cycleBudget"/> CPU cycles
    /// (one frame ≈ clock frequency / frame rate).
    /// </summary>
    public void RunCycles(long cycleBudget)
    {
        long spent = 0;
        // Run through the whole budget even in the HLT state — so the PIT can raise an
        // interrupt that "wakes up" the CPU.
        while (spent < cycleBudget)
        {
            int cyc = Cpu.Step();
            spent += cyc;
            Cycles += cyc;

            if (TickPitEnabled)
            {
                _pitAcc += cyc;
                int ticks = _pitAcc / PitDivisor;
                if (ticks > 0)
                {
                    _pitAcc -= ticks * PitDivisor;
                    Pit.Tick(ticks);
                }
            }

            // Sample the speaker level at AudioRate (port 0x61 × channel-2 output).
            if (SoundEnabled)
            {
                _audioAcc += cyc;
                while (_audioAcc >= CyclesPerSample)
                {
                    _audioAcc -= CyclesPerSample;
                    bool on = Speaker.Data && (!Speaker.Gate || Pit.Channel2Level());
                    PushAudio(on ? AudioAmp : (short)0);
                }
            }
        }
    }

    private void PushAudio(short s)
    {
        if (_audioCount >= _audioRing.Length) return; // overflow (e.g. turbo) — drop it
        _audioRing[(_audioHead + _audioCount) % _audioRing.Length] = s;
        _audioCount++;
    }

    /// <summary>Drain the accumulated audio samples (16-bit mono @ AudioRate). Returns the count.</summary>
    public int DrainAudio(short[] dest)
    {
        int n = Math.Min(dest.Length, _audioCount);
        for (int i = 0; i < n; i++)
        {
            dest[i] = _audioRing[_audioHead];
            _audioHead = (_audioHead + 1) % _audioRing.Length;
        }
        _audioCount -= n;
        return n;
    }
}
