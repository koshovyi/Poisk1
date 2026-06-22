using Poisk1.Core.Devices;
using Poisk1.Core.Io;

namespace Poisk1.Core.Cpu;

/// <summary>
/// A self-contained Intel 8088 (= KR1810VM88) interpreter on top of IMemoryBus and IoBus.
/// Real mode, the 8086/8088 instruction set (no 186+ and no FPU — ESC is treated as NOP).
/// The goal is to run the real Poisk BIOS.
/// </summary>
public sealed class Cpu8088 : ICpu
{
    private readonly IMemoryBus _mem;
    private readonly IoBus _io;
    private readonly IInterruptController? _pic;
    private readonly Action<string>? _trace;

    private readonly CpuState s = new();
    private static readonly bool[] ParityEven = BuildParity();

    // Decoding state of the current instruction.
    private int _segOverride = -1;   // segment-override value, or -1
    private int _rep;                // 0, 0xF2 (REPNE) or 0xF3 (REP/REPE)
    private bool _rmIsReg;
    private int _rmReg;              // register index if the rm operand is a register
    private ushort _rmSeg, _rmOff;   // segment:offset if the rm operand is memory

    public Cpu8088(IMemoryBus mem, IoBus io, IInterruptController? pic = null, Action<string>? trace = null)
    {
        _mem = mem;
        _io = io;
        _pic = pic;
        _trace = trace;
    }

    public CpuState State => s;
    public bool Halted { get; private set; }
    public int TraceN { get; set; } // [DEBUG] trace the next N instructions via _trace
    public ushort WatchIp { get; set; } = 0xFFFF; // [DEBUG] log registers when IP is reached
    public ushort WatchCs { get; set; } = 0xF000; // [DEBUG] CS for WatchIp
    public int WatchN { get; set; }
    public bool TrapCs0 { get; set; } // [DEBUG] dump history when CS first becomes 0
    public bool TrapEnabled { get; set; } // [DEBUG] trap at TrapCs:TrapIp
    public ushort TrapCs, TrapIp;
    public bool TrapBxEnabled { get; set; } public ushort TrapBx; // [DEBUG] additional BX condition
    public bool RecordHistory { get; set; } // [DEBUG] always record history
    public ushort RecordOnlyCs { get; set; } = 0xFFFF; // [DEBUG] record only this CS (0xFFFF=all)
    private readonly uint[] _hist = new uint[64];
    private int _histPos;

    /// <summary>[DEBUG] The recent history (CS:IP), from oldest to newest.</summary>
    public uint[] GetHistory()
    {
        var o = new uint[_hist.Length];
        for (int k = 0; k < _hist.Length; k++) o[k] = _hist[(_histPos + k) % _hist.Length];
        return o;
    }
    private bool _trapped;
    private int _instrCycles = 4;    // cycles of the current instruction (refined for IN/LOOP — cassette timing)
    private bool _ssLoaded;          // 8088: after loading SS, interrupts are blocked for 1 instruction
    private int _unknownOpLog;       // counter of unknown-opcode log entries

    public void Reset()
    {
        s.AX = s.BX = s.CX = s.DX = 0;
        s.SP = s.BP = s.SI = s.DI = 0;
        s.DS = s.ES = s.SS = 0;
        s.CS = 0xF000;
        s.IP = 0xFFF0;
        s.Flags = 0x0002; // bit 1 is always 1
        Halted = false;
        _trace?.Invoke($"RESET -> {s.CS:X4}:{s.IP:X4} (phys. 0x{s.LinearIp:X5})");
    }

    // ====================== Memory access ======================

    private static uint Phys(ushort seg, ushort off) => (uint)((seg << 4) + off);

    private byte ReadMem8(ushort seg, ushort off) => _mem.ReadByte(Phys(seg, off));
    private void WriteMem8(ushort seg, ushort off, byte v) => _mem.WriteByte(Phys(seg, off), v);

    private ushort ReadMem16(ushort seg, ushort off)
        => (ushort)(ReadMem8(seg, off) | (ReadMem8(seg, (ushort)(off + 1)) << 8));

    private void WriteMem16(ushort seg, ushort off, ushort v)
    {
        WriteMem8(seg, off, (byte)v);
        WriteMem8(seg, (ushort)(off + 1), (byte)(v >> 8));
    }

    private byte Fetch8()
    {
        byte b = ReadMem8(s.CS, s.IP);
        s.IP++;
        return b;
    }

    private ushort Fetch16()
    {
        ushort v = (ushort)(Fetch8() | (Fetch8() << 8));
        return v;
    }

    // ====================== Registers by index ======================

    private byte GetReg8(int i) => i switch
    {
        0 => s.AL, 1 => s.CL, 2 => s.DL, 3 => s.BL,
        4 => s.AH, 5 => s.CH, 6 => s.DH, _ => s.BH,
    };

    private void SetReg8(int i, byte v)
    {
        switch (i)
        {
            case 0: s.AL = v; break; case 1: s.CL = v; break;
            case 2: s.DL = v; break; case 3: s.BL = v; break;
            case 4: s.AH = v; break; case 5: s.CH = v; break;
            case 6: s.DH = v; break; default: s.BH = v; break;
        }
    }

    private ushort GetReg16(int i) => i switch
    {
        0 => s.AX, 1 => s.CX, 2 => s.DX, 3 => s.BX,
        4 => s.SP, 5 => s.BP, 6 => s.SI, _ => s.DI,
    };

    private void SetReg16(int i, ushort v)
    {
        switch (i)
        {
            case 0: s.AX = v; break; case 1: s.CX = v; break;
            case 2: s.DX = v; break; case 3: s.BX = v; break;
            case 4: s.SP = v; break; case 5: s.BP = v; break;
            case 6: s.SI = v; break; default: s.DI = v; break;
        }
    }

    private ushort GetSeg(int i) => i switch { 0 => s.ES, 1 => s.CS, 2 => s.SS, _ => s.DS };

    private void SetSeg(int i, ushort v)
    {
        switch (i)
        {
            case 0: s.ES = v; break; case 1: s.CS = v; break;
            case 2: s.SS = v; break; default: s.DS = v; break;
        }
    }

    // ====================== ModR/M decoding ======================

    /// <summary>Reads the ModR/M byte, prepares the rm operand (the _rm* fields), returns the reg field.</summary>
    private int DecodeModRM()
    {
        byte modrm = Fetch8();
        int mod = modrm >> 6;
        int reg = (modrm >> 3) & 7;
        int rm = modrm & 7;
        _lastReg = reg;

        if (mod == 3)
        {
            _rmIsReg = true;
            _rmReg = rm;
            return reg;
        }

        _rmIsReg = false;
        ushort baseAddr;
        bool ss; // whether the default segment is SS
        switch (rm)
        {
            case 0: baseAddr = (ushort)(s.BX + s.SI); ss = false; break;
            case 1: baseAddr = (ushort)(s.BX + s.DI); ss = false; break;
            case 2: baseAddr = (ushort)(s.BP + s.SI); ss = true; break;
            case 3: baseAddr = (ushort)(s.BP + s.DI); ss = true; break;
            case 4: baseAddr = s.SI; ss = false; break;
            case 5: baseAddr = s.DI; ss = false; break;
            case 6:
                if (mod == 0) { baseAddr = 0; ss = false; } // direct address disp16
                else { baseAddr = s.BP; ss = true; }
                break;
            default: baseAddr = s.BX; ss = false; break;
        }

        int disp = 0;
        if (mod == 1) disp = (sbyte)Fetch8();
        else if (mod == 2) disp = (short)Fetch16();
        else if (mod == 0 && rm == 6) disp = (short)Fetch16();

        _rmOff = (ushort)(baseAddr + disp);
        ushort defaultSeg = ss ? s.SS : s.DS;
        _rmSeg = _segOverride >= 0 ? (ushort)_segOverride : defaultSeg;
        return reg;
    }

    private byte GetRM8() => _rmIsReg ? GetReg8(_rmReg) : ReadMem8(_rmSeg, _rmOff);
    private void SetRM8(byte v) { if (_rmIsReg) SetReg8(_rmReg, v); else WriteMem8(_rmSeg, _rmOff, v); }
    private ushort GetRM16() => _rmIsReg ? GetReg16(_rmReg) : ReadMem16(_rmSeg, _rmOff);
    private void SetRM16(ushort v) { if (_rmIsReg) SetReg16(_rmReg, v); else WriteMem16(_rmSeg, _rmOff, v); }

    // ====================== Stack ======================

    private void Push16(ushort v) { s.SP -= 2; WriteMem16(s.SS, s.SP, v); }
    private ushort Pop16() { ushort v = ReadMem16(s.SS, s.SP); s.SP += 2; return v; }

    // ====================== Flags ======================

    private static bool[] BuildParity()
    {
        var t = new bool[256];
        for (int i = 0; i < 256; i++)
        {
            int b = i, c = 0;
            while (b != 0) { c += b & 1; b >>= 1; }
            t[i] = (c & 1) == 0; // even parity
        }
        return t;
    }

    private void SetSZP8(byte v)
    {
        s.SetFlag(CpuState.ZF, v == 0);
        s.SetFlag(CpuState.SF, (v & 0x80) != 0);
        s.SetFlag(CpuState.PF, ParityEven[v]);
    }

    private void SetSZP16(ushort v)
    {
        s.SetFlag(CpuState.ZF, v == 0);
        s.SetFlag(CpuState.SF, (v & 0x8000) != 0);
        s.SetFlag(CpuState.PF, ParityEven[(byte)v]);
    }

    private byte Add8(int a, int b, int c)
    {
        int r = a + b + c;
        s.SetFlag(CpuState.CF, (r & 0x100) != 0);
        s.SetFlag(CpuState.AF, ((a ^ b ^ r) & 0x10) != 0);
        s.SetFlag(CpuState.OF, ((~(a ^ b) & (a ^ r)) & 0x80) != 0);
        SetSZP8((byte)r);
        return (byte)r;
    }

    private ushort Add16(int a, int b, int c)
    {
        int r = a + b + c;
        s.SetFlag(CpuState.CF, (r & 0x10000) != 0);
        s.SetFlag(CpuState.AF, ((a ^ b ^ r) & 0x10) != 0);
        s.SetFlag(CpuState.OF, ((~(a ^ b) & (a ^ r)) & 0x8000) != 0);
        SetSZP16((ushort)r);
        return (ushort)r;
    }

    private byte Sub8(int a, int b, int c)
    {
        int r = a - b - c;
        s.SetFlag(CpuState.CF, (r & 0x100) != 0);
        s.SetFlag(CpuState.AF, ((a ^ b ^ r) & 0x10) != 0);
        s.SetFlag(CpuState.OF, (((a ^ b) & (a ^ r)) & 0x80) != 0);
        SetSZP8((byte)r);
        return (byte)r;
    }

    private ushort Sub16(int a, int b, int c)
    {
        int r = a - b - c;
        s.SetFlag(CpuState.CF, (r & 0x10000) != 0);
        s.SetFlag(CpuState.AF, ((a ^ b ^ r) & 0x10) != 0);
        s.SetFlag(CpuState.OF, (((a ^ b) & (a ^ r)) & 0x8000) != 0);
        SetSZP16((ushort)r);
        return (ushort)r;
    }

    private byte Logic8(int r) { s.SetFlag(CpuState.CF, false); s.SetFlag(CpuState.OF, false); s.SetFlag(CpuState.AF, false); SetSZP8((byte)r); return (byte)r; }
    private ushort Logic16(int r) { s.SetFlag(CpuState.CF, false); s.SetFlag(CpuState.OF, false); s.SetFlag(CpuState.AF, false); SetSZP16((ushort)r); return (ushort)r; }

    private byte Inc8(byte v) { bool cf = s.GetFlag(CpuState.CF); byte r = Add8(v, 1, 0); s.SetFlag(CpuState.CF, cf); return r; }
    private byte Dec8(byte v) { bool cf = s.GetFlag(CpuState.CF); byte r = Sub8(v, 1, 0); s.SetFlag(CpuState.CF, cf); return r; }
    private ushort Inc16(ushort v) { bool cf = s.GetFlag(CpuState.CF); ushort r = Add16(v, 1, 0); s.SetFlag(CpuState.CF, cf); return r; }
    private ushort Dec16(ushort v) { bool cf = s.GetFlag(CpuState.CF); ushort r = Sub16(v, 1, 0); s.SetFlag(CpuState.CF, cf); return r; }

    // Generic ALU operation (op: 0=ADD,1=OR,2=ADC,3=SBB,4=AND,5=SUB,6=XOR,7=CMP).
    private byte Alu8(int op, byte a, byte b) => op switch
    {
        0 => Add8(a, b, 0),
        1 => Logic8(a | b),
        2 => Add8(a, b, s.GetFlag(CpuState.CF) ? 1 : 0),
        3 => Sub8(a, b, s.GetFlag(CpuState.CF) ? 1 : 0),
        4 => Logic8(a & b),
        5 => Sub8(a, b, 0),
        6 => Logic8(a ^ b),
        _ => Sub8(a, b, 0), // CMP — like SUB, but the result is discarded
    };

    private ushort Alu16(int op, ushort a, ushort b) => op switch
    {
        0 => Add16(a, b, 0),
        1 => Logic16(a | b),
        2 => Add16(a, b, s.GetFlag(CpuState.CF) ? 1 : 0),
        3 => Sub16(a, b, s.GetFlag(CpuState.CF) ? 1 : 0),
        4 => Logic16(a & b),
        5 => Sub16(a, b, 0),
        6 => Logic16(a ^ b),
        _ => Sub16(a, b, 0),
    };

    // ====================== Interrupts ======================

    private void DoInterrupt(int n)
    {
        ushort newIp = ReadMem16(0, (ushort)(n * 4));
        ushort newCs = ReadMem16(0, (ushort)(n * 4 + 2));
        Push16(s.Flags);
        s.SetFlag(CpuState.IF, false);
        s.SetFlag(CpuState.TF, false);
        Push16(s.CS);
        Push16(s.IP);
        s.IP = newIp;
        s.CS = newCs;
    }

    /// <summary>
    /// [Authentic loading] Programmatically raise interrupt <paramref name="vec"/>,
    /// as if INT vec had been executed, with a return frame at retSeg:retOfs (where the caller
    /// places HLT 0xF4). The BIOS handler runs on the real CPU and returns control to the HLT.
    /// </summary>
    public void InvokeInterrupt(byte vec, ushort retSeg, ushort retOfs)
    {
        Halted = false;
        Push16(s.Flags);
        Push16(retSeg);
        Push16(retOfs);
        s.SetFlag(CpuState.IF, false);
        s.IP = ReadMem16(0, (ushort)(vec * 4));
        s.CS = ReadMem16(0, (ushort)(vec * 4 + 2));
    }

    /// <summary>[Authentic loading] Clear the HLT state (to transfer control to the game).</summary>
    public void ClearHalt() => Halted = false;

    // ====================== Main step ======================

    public int Step()
    {
        // 8088: loading SS blocks interrupts until the end of the next instruction
        // (so that MOV SS / MOV SP execute atomically). Consume the flag here.
        bool inhibitInt = _ssLoaded;
        _ssLoaded = false;

        // Service a hardware interrupt if allowed by the IF flag.
        if (!inhibitInt && _pic is not null && s.GetFlag(CpuState.IF))
        {
            int vec = _pic.Poll();
            if (vec >= 0)
            {
                Halted = false;
                DoInterrupt(vec);
                return 8;
            }
        }

        if (Halted)
            return 4; // idle, waiting for an interrupt

        if (TraceN > 0)
        {
            TraceN--;
            uint p = s.LinearIp;
            _trace?.Invoke($"  T {s.CS:X4}:{s.IP:X4} op={_mem.ReadByte(p):X2} {_mem.ReadByte(p + 1):X2} {_mem.ReadByte(p + 2):X2} AX={s.AX:X4} BX={s.BX:X4} CX={s.CX:X4} DX={s.DX:X4}");
        }
        if ((TrapCs0 || TrapEnabled || RecordHistory) && (RecordOnlyCs == 0xFFFF || s.CS == RecordOnlyCs))
        {
            _hist[_histPos] = ((uint)s.CS << 16) | s.IP;
            _histPos = (_histPos + 1) % _hist.Length;
            bool hit = (TrapCs0 && s.CS == 0x0000) || (TrapEnabled && s.CS == TrapCs && s.IP == TrapIp && (!TrapBxEnabled || s.BX == TrapBx));
            if (!_trapped && hit)
            {
                _trapped = true;
                _trace?.Invoke($"TRAP @ {s.CS:X4}:{s.IP:X4} AX={s.AX:X4} BX={s.BX:X4} CX={s.CX:X4} DX={s.DX:X4} SP={s.SP:X4} SS={s.SS:X4} DS={s.DS:X4}; history:");
                for (int k = 0; k < _hist.Length; k++)
                {
                    uint a = _hist[(_histPos + k) % _hist.Length];
                    ushort cs = (ushort)(a >> 16), ip = (ushort)a;
                    uint ph = (uint)((cs << 4) + ip) & MemoryBus.AddressMask;
                    _trace?.Invoke($"   {cs:X4}:{ip:X4}  {_mem.ReadByte(ph):X2} {_mem.ReadByte(ph + 1):X2} {_mem.ReadByte(ph + 2):X2}");
                }
            }
        }
        if (WatchN > 0 && s.CS == WatchCs && s.IP == WatchIp)
        {
            WatchN--;
            _trace?.Invoke($"  W @{s.CS:X4}:{s.IP:X4} AX={s.AX:X4} BX={s.BX:X4}({s.BX}) CX={s.CX:X4} DX={s.DX:X4} SI={s.SI:X4} DI={s.DI:X4}");
        }

        _segOverride = -1;
        _rep = 0;
        _instrCycles = 4; // typical estimate; some instructions refine it (important for cassette timing)
        ushort ipStart = s.IP; // for wait-state: re-run the IN instruction if the device isn't ready

        // Prefixes.
        byte op;
        while (true)
        {
            op = Fetch8();
            switch (op)
            {
                case 0x26: _segOverride = s.ES; continue;
                case 0x2E: _segOverride = s.CS; continue;
                case 0x36: _segOverride = s.SS; continue;
                case 0x3E: _segOverride = s.DS; continue;
                case 0xF0: continue;            // LOCK — ignored
                case 0xF2: _rep = 0xF2; continue;
                case 0xF3: _rep = 0xF3; continue;
            }
            break;
        }

        Execute(op);
        // The device requested a wait-state (e.g. the FDC is waiting for DRQ/INTRQ) — re-run the IN instruction.
        if (_io.PendingStall) { _io.PendingStall = false; s.IP = ipStart; }
        return _instrCycles;
    }

    private void Execute(byte op)
    {
        // --- Generic ALU group 0x00..0x3F (except the special segment/DAA opcodes, etc.) ---
        if (op < 0x40 && (op & 0x07) < 6 && (op & 0xC0) != 0xC0)
        {
            int aluOp = (op >> 3) & 7;
            int form = op & 7;
            switch (form)
            {
                case 0: { DecodeModRM(); byte r = Alu8(aluOp, GetRM8(), GetReg8(_lastReg)); if (aluOp != 7) SetRM8(r); break; }
                case 1: { DecodeModRM(); ushort r = Alu16(aluOp, GetRM16(), GetReg16(_lastReg)); if (aluOp != 7) SetRM16(r); break; }
                case 2: { DecodeModRM(); byte r = Alu8(aluOp, GetReg8(_lastReg), GetRM8()); if (aluOp != 7) SetReg8(_lastReg, r); break; }
                case 3: { DecodeModRM(); ushort r = Alu16(aluOp, GetReg16(_lastReg), GetRM16()); if (aluOp != 7) SetReg16(_lastReg, r); break; }
                case 4: { byte r = Alu8(aluOp, s.AL, Fetch8()); if (aluOp != 7) s.AL = r; break; }
                case 5: { ushort r = Alu16(aluOp, s.AX, Fetch16()); if (aluOp != 7) s.AX = r; break; }
            }
            return;
        }

        switch (op)
        {
            // PUSH/POP of segment registers
            case 0x06: Push16(s.ES); break;
            case 0x07: s.ES = Pop16(); break;
            case 0x0E: Push16(s.CS); break;
            case 0x0F: s.CS = Pop16(); break; // 8088: POP CS
            case 0x16: Push16(s.SS); break;
            case 0x17: s.SS = Pop16(); _ssLoaded = true; break;
            case 0x1E: Push16(s.DS); break;
            case 0x1F: s.DS = Pop16(); break;

            // Decimal adjustments
            case 0x27: Daa(); break;
            case 0x2F: Das(); break;
            case 0x37: Aaa(); break;
            case 0x3F: Aas(); break;

            // INC/DEC reg16
            case 0x40: case 0x41: case 0x42: case 0x43:
            case 0x44: case 0x45: case 0x46: case 0x47:
                SetReg16(op & 7, Inc16(GetReg16(op & 7))); break;
            case 0x48: case 0x49: case 0x4A: case 0x4B:
            case 0x4C: case 0x4D: case 0x4E: case 0x4F:
                SetReg16(op & 7, Dec16(GetReg16(op & 7))); break;

            // PUSH/POP reg16
            case 0x50: case 0x51: case 0x52: case 0x53:
            case 0x54: case 0x55: case 0x56: case 0x57:
                Push16(GetReg16(op & 7)); break;
            case 0x58: case 0x59: case 0x5A: case 0x5B:
            case 0x5C: case 0x5D: case 0x5E: case 0x5F:
                SetReg16(op & 7, Pop16()); break;

            // --- 80186/V20 instructions (the Poisk's cassette programs use them) ---
            case 0x60: { ushort sp = s.SP; Push16(s.AX); Push16(s.CX); Push16(s.DX); Push16(s.BX); Push16(sp); Push16(s.BP); Push16(s.SI); Push16(s.DI); break; } // PUSHA
            case 0x61: { s.DI = Pop16(); s.SI = Pop16(); s.BP = Pop16(); Pop16(); s.BX = Pop16(); s.DX = Pop16(); s.CX = Pop16(); s.AX = Pop16(); break; } // POPA
            case 0x62: { DecodeModRM(); short idx = (short)GetReg16(_lastReg); short lo = (short)ReadMem16(_rmSeg, _rmOff); short hi = (short)ReadMem16(_rmSeg, (ushort)(_rmOff + 2)); if (idx < lo || idx > hi) DoInterrupt(5); break; } // BOUND
            case 0x68: Push16(Fetch16()); break; // PUSH imm16
            case 0x69: { DecodeModRM(); int a = (short)GetRM16(); int imm = (short)Fetch16(); int r = a * imm; SetReg16(_lastReg, (ushort)r); bool c = r is < -32768 or > 32767; s.SetFlag(CpuState.CF, c); s.SetFlag(CpuState.OF, c); break; } // IMUL r16,rm16,imm16
            case 0x6A: Push16((ushort)(sbyte)Fetch8()); break; // PUSH imm8 (sign-extended)
            case 0x6B: { DecodeModRM(); int a = (short)GetRM16(); int imm = (sbyte)Fetch8(); int r = a * imm; SetReg16(_lastReg, (ushort)r); bool c = r is < -32768 or > 32767; s.SetFlag(CpuState.CF, c); s.SetFlag(CpuState.OF, c); break; } // IMUL r16,rm16,imm8
            case 0x6C: StringOp(() => { WriteMem8(s.ES, s.DI, _io.ReadByte(s.DX)); s.DI = (ushort)(s.DI + Dir); }); break; // INSB
            case 0x6D: StringOp(() => { WriteMem16(s.ES, s.DI, _io.ReadWord(s.DX)); s.DI = (ushort)(s.DI + 2 * Dir); }); break; // INSW
            case 0x6E: StringOp(() => { _io.WriteByte(s.DX, ReadMem8(SegDS(), s.SI)); s.SI = (ushort)(s.SI + Dir); }); break; // OUTSB
            case 0x6F: StringOp(() => { _io.WriteWord(s.DX, ReadMem16(SegDS(), s.SI)); s.SI = (ushort)(s.SI + 2 * Dir); }); break; // OUTSW

            // Conditional short jumps
            case 0x70: Jcc(Cond(0x0)); break; // JO
            case 0x71: Jcc(!Cond(0x0)); break;
            case 0x72: Jcc(s.GetFlag(CpuState.CF)); break;        // JB/JC
            case 0x73: Jcc(!s.GetFlag(CpuState.CF)); break;
            case 0x74: Jcc(s.GetFlag(CpuState.ZF)); break;        // JZ
            case 0x75: Jcc(!s.GetFlag(CpuState.ZF)); break;
            case 0x76: Jcc(s.GetFlag(CpuState.CF) || s.GetFlag(CpuState.ZF)); break; // JBE
            case 0x77: Jcc(!(s.GetFlag(CpuState.CF) || s.GetFlag(CpuState.ZF))); break;
            case 0x78: Jcc(s.GetFlag(CpuState.SF)); break;        // JS
            case 0x79: Jcc(!s.GetFlag(CpuState.SF)); break;
            case 0x7A: Jcc(s.GetFlag(CpuState.PF)); break;        // JP
            case 0x7B: Jcc(!s.GetFlag(CpuState.PF)); break;
            case 0x7C: Jcc(s.GetFlag(CpuState.SF) != s.GetFlag(CpuState.OF)); break; // JL
            case 0x7D: Jcc(s.GetFlag(CpuState.SF) == s.GetFlag(CpuState.OF)); break;
            case 0x7E: Jcc(s.GetFlag(CpuState.ZF) || (s.GetFlag(CpuState.SF) != s.GetFlag(CpuState.OF))); break; // JLE
            case 0x7F: Jcc(!(s.GetFlag(CpuState.ZF) || (s.GetFlag(CpuState.SF) != s.GetFlag(CpuState.OF)))); break;

            // Group 1: ALU rm, imm
            case 0x80: Grp1_8(false); break;
            case 0x81: Grp1_16(false); break;
            case 0x82: Grp1_8(false); break;      // same as 0x80
            case 0x83: Grp1_16(true); break;       // imm8 sign-extended

            // TEST / XCHG
            case 0x84: { DecodeModRM(); Logic8(GetRM8() & GetReg8(_lastReg)); break; }
            case 0x85: { DecodeModRM(); Logic16(GetRM16() & GetReg16(_lastReg)); break; }
            case 0x86: { DecodeModRM(); byte t = GetRM8(); SetRM8(GetReg8(_lastReg)); SetReg8(_lastReg, t); break; }
            case 0x87: { DecodeModRM(); ushort t = GetRM16(); SetRM16(GetReg16(_lastReg)); SetReg16(_lastReg, t); break; }

            // MOV
            case 0x88: { DecodeModRM(); SetRM8(GetReg8(_lastReg)); break; }
            case 0x89: { DecodeModRM(); SetRM16(GetReg16(_lastReg)); break; }
            case 0x8A: { DecodeModRM(); SetReg8(_lastReg, GetRM8()); break; }
            case 0x8B: { DecodeModRM(); SetReg16(_lastReg, GetRM16()); break; }
            case 0x8C: { DecodeModRM(); SetRM16(GetSeg(_lastReg & 3)); break; }
            case 0x8D: { DecodeModRM(); SetReg16(_lastReg, _rmOff); break; } // LEA
            case 0x8E: { DecodeModRM(); int sr = _lastReg & 3; SetSeg(sr, GetRM16()); if (sr == 2) _ssLoaded = true; break; }
            case 0x8F: { DecodeModRM(); SetRM16(Pop16()); break; } // POP rm16

            // XCHG AX,reg
            case 0x90: break; // NOP
            case 0x91: case 0x92: case 0x93:
            case 0x94: case 0x95: case 0x96: case 0x97:
                { ushort t = s.AX; s.AX = GetReg16(op & 7); SetReg16(op & 7, t); break; }

            case 0x98: s.AX = (ushort)(sbyte)s.AL; break; // CBW
            case 0x99: s.DX = (s.AX & 0x8000) != 0 ? (ushort)0xFFFF : (ushort)0; break; // CWD
            case 0x9A: { ushort noff = Fetch16(); ushort nseg = Fetch16(); Push16(s.CS); Push16(s.IP); s.CS = nseg; s.IP = noff; break; } // CALL far
            case 0x9B: break; // WAIT
            case 0x9C: Push16(s.Flags); break; // PUSHF
            case 0x9D: s.Flags = (ushort)((Pop16() & 0x0FD5) | 0x0002); break; // POPF
            case 0x9E: s.Flags = (ushort)((s.Flags & 0xFF00) | (s.AH & 0xD5) | 0x02); break; // SAHF
            case 0x9F: s.AH = (byte)(s.Flags & 0xD5); break; // LAHF

            // MOV AL/AX <-> [moffs]
            case 0xA0: { ushort o = Fetch16(); s.AL = ReadMem8(SegDS(), o); break; }
            case 0xA1: { ushort o = Fetch16(); s.AX = ReadMem16(SegDS(), o); break; }
            case 0xA2: { ushort o = Fetch16(); WriteMem8(SegDS(), o, s.AL); break; }
            case 0xA3: { ushort o = Fetch16(); WriteMem16(SegDS(), o, s.AX); break; }

            // String operations
            case 0xA4: StringOp(() => Movs8()); break;
            case 0xA5: StringOp(() => Movs16()); break;
            case 0xA6: StringOpCmp(() => Cmps8()); break;
            case 0xA7: StringOpCmp(() => Cmps16()); break;
            case 0xA8: Logic8(s.AL & Fetch8()); break; // TEST AL,imm8
            case 0xA9: Logic16(s.AX & Fetch16()); break; // TEST AX,imm16
            case 0xAA: StringOp(() => Stos8()); break;
            case 0xAB: StringOp(() => Stos16()); break;
            case 0xAC: StringOp(() => Lods8()); break;
            case 0xAD: StringOp(() => Lods16()); break;
            case 0xAE: StringOpCmp(() => Scas8()); break;
            case 0xAF: StringOpCmp(() => Scas16()); break;

            // MOV reg,imm
            case 0xB0: case 0xB1: case 0xB2: case 0xB3:
            case 0xB4: case 0xB5: case 0xB6: case 0xB7:
                SetReg8(op & 7, Fetch8()); break;
            case 0xB8: case 0xB9: case 0xBA: case 0xBB:
            case 0xBC: case 0xBD: case 0xBE: case 0xBF:
                SetReg16(op & 7, Fetch16()); break;

            // ENTER / LEAVE (80186)
            case 0xC8: { ushort sz = Fetch16(); byte lvl = (byte)(Fetch8() & 0x1F); Push16(s.BP); ushort fp = s.SP; for (int i = 1; i < lvl; i++) { s.BP -= 2; Push16(ReadMem16(s.SS, s.BP)); } if (lvl > 0) Push16(fp); s.BP = fp; s.SP -= sz; break; }
            case 0xC9: { s.SP = s.BP; s.BP = Pop16(); break; } // LEAVE

            // Shifts r/m, imm8 (80186)
            case 0xC0: { DecodeModRM(); byte v = GetRM8(); byte cnt = Fetch8(); SetRM8(Shift8(_lastReg, v, cnt)); break; }
            case 0xC1: { DecodeModRM(); ushort v = GetRM16(); byte cnt = Fetch8(); SetRM16(Shift16(_lastReg, v, cnt)); break; }

            // RET / RETF
            case 0xC2: { ushort n = Fetch16(); s.IP = Pop16(); s.SP += n; break; }
            case 0xC3: s.IP = Pop16(); break;
            case 0xC4: { DecodeModRM(); SetReg16(_lastReg, ReadMem16(_rmSeg, _rmOff)); s.ES = ReadMem16(_rmSeg, (ushort)(_rmOff + 2)); break; } // LES
            case 0xC5: { DecodeModRM(); SetReg16(_lastReg, ReadMem16(_rmSeg, _rmOff)); s.DS = ReadMem16(_rmSeg, (ushort)(_rmOff + 2)); break; } // LDS
            case 0xC6: { DecodeModRM(); SetRM8(Fetch8()); break; }
            case 0xC7: { DecodeModRM(); SetRM16(Fetch16()); break; }
            case 0xCA: { ushort n = Fetch16(); s.IP = Pop16(); s.CS = Pop16(); s.SP += n; break; }
            case 0xCB: { s.IP = Pop16(); s.CS = Pop16(); break; }
            case 0xCC: DoInterrupt(3); break;
            case 0xCD: DoInterrupt(Fetch8()); break;
            case 0xCE: if (s.GetFlag(CpuState.OF)) DoInterrupt(4); break; // INTO
            case 0xCF: { s.IP = Pop16(); s.CS = Pop16(); s.Flags = (ushort)((Pop16() & 0x0FD5) | 0x0002); break; } // IRET

            // Shifts/rotates
            case 0xD0: { DecodeModRM(); SetRM8(Shift8(_lastReg, GetRM8(), 1)); break; }
            case 0xD1: { DecodeModRM(); SetRM16(Shift16(_lastReg, GetRM16(), 1)); break; }
            case 0xD2: { DecodeModRM(); SetRM8(Shift8(_lastReg, GetRM8(), s.CL)); break; }
            case 0xD3: { DecodeModRM(); SetRM16(Shift16(_lastReg, GetRM16(), s.CL)); break; }
            case 0xD4: Aam(Fetch8()); break;
            case 0xD5: Aad(Fetch8()); break;
            case 0xD6: s.AL = s.GetFlag(CpuState.CF) ? (byte)0xFF : (byte)0x00; break; // SALC
            case 0xD7: s.AL = ReadMem8(SegDS(), (ushort)(s.BX + s.AL)); break; // XLAT

            // ESC (FPU) — consume the operand
            case 0xD8: case 0xD9: case 0xDA: case 0xDB:
            case 0xDC: case 0xDD: case 0xDE: case 0xDF:
                DecodeModRM(); break;

            // Loops / JCXZ
            case 0xE0: { sbyte d = (sbyte)Fetch8(); s.CX--; if (s.CX != 0 && !s.GetFlag(CpuState.ZF)) s.IP = (ushort)(s.IP + d); _instrCycles = 17; break; } // LOOPNE
            case 0xE1: { sbyte d = (sbyte)Fetch8(); s.CX--; if (s.CX != 0 && s.GetFlag(CpuState.ZF)) s.IP = (ushort)(s.IP + d); _instrCycles = 17; break; }  // LOOPE
            case 0xE2: { sbyte d = (sbyte)Fetch8(); s.CX--; if (s.CX != 0) s.IP = (ushort)(s.IP + d); _instrCycles = 17; break; } // LOOP
            case 0xE3: { sbyte d = (sbyte)Fetch8(); if (s.CX == 0) s.IP = (ushort)(s.IP + d); _instrCycles = 6; break; } // JCXZ

            // IN / OUT (realistic cycle counts matter for cassette timing via the PIT)
            case 0xE4: s.AL = _io.ReadByte(Fetch8()); _instrCycles = 14; break;
            case 0xE5: s.AX = _io.ReadWord(Fetch8()); _instrCycles = 14; break;
            case 0xE6: _io.WriteByte(Fetch8(), s.AL); _instrCycles = 14; break;
            case 0xE7: _io.WriteWord(Fetch8(), s.AX); _instrCycles = 14; break;

            // CALL / JMP
            case 0xE8: { short d = (short)Fetch16(); Push16(s.IP); s.IP = (ushort)(s.IP + d); break; } // CALL near
            case 0xE9: { short d = (short)Fetch16(); s.IP = (ushort)(s.IP + d); break; } // JMP near
            case 0xEA: { ushort noff = Fetch16(); ushort nseg = Fetch16(); s.CS = nseg; s.IP = noff; break; } // JMP far
            case 0xEB: { sbyte d = (sbyte)Fetch8(); s.IP = (ushort)(s.IP + d); break; } // JMP short

            case 0xEC: s.AL = _io.ReadByte(s.DX); _instrCycles = 14; break;
            case 0xED: s.AX = _io.ReadWord(s.DX); _instrCycles = 14; break;
            case 0xEE: _io.WriteByte(s.DX, s.AL); _instrCycles = 14; break;
            case 0xEF: _io.WriteWord(s.DX, s.AX); _instrCycles = 14; break;

            case 0xF4: Halted = true; _trace?.Invoke($"HLT @ {s.CS:X4}:{s.IP:X4}"); break;
            case 0xF5: s.SetFlag(CpuState.CF, !s.GetFlag(CpuState.CF)); break; // CMC
            case 0xF6: Grp3_8(); break;
            case 0xF7: Grp3_16(); break;
            case 0xF8: s.SetFlag(CpuState.CF, false); break;
            case 0xF9: s.SetFlag(CpuState.CF, true); break;
            case 0xFA: s.SetFlag(CpuState.IF, false); break;
            case 0xFB: s.SetFlag(CpuState.IF, true); break;
            case 0xFC: s.SetFlag(CpuState.DF, false); break;
            case 0xFD: s.SetFlag(CpuState.DF, true); break;
            case 0xFE: Grp4(); break;
            case 0xFF: Grp5(); break;

            default:
                // The 8088 doesn't halt on an unknown opcode — treat it as NOP and continue
                // (otherwise a game crash would "hang" the whole CPU). Log only the first few.
                if (_unknownOpLog < 16)
                {
                    _trace?.Invoke($"unknown opcode 0x{op:X2} @ {s.CS:X4}:{(ushort)(s.IP - 1):X4} — skipped (NOP)");
                    _unknownOpLog++;
                }
                break;
        }
    }

    // _lastReg: the reg field of the last DecodeModRM (handy for concise cases).
    private int _lastReg;

    private ushort SegDS() => _segOverride >= 0 ? (ushort)_segOverride : s.DS;

    private void Jcc(bool cond)
    {
        sbyte d = (sbyte)Fetch8();
        if (cond) s.IP = (ushort)(s.IP + d);
    }

    private bool Cond(int _) => s.GetFlag(CpuState.OF); // for JO/JNO

    // ====================== Groups ======================

    private void Grp1_8(bool _)
    {
        int op2 = DecodeModRM();
        byte a = GetRM8();
        byte imm = Fetch8();
        byte r = Alu8(op2, a, imm);
        if (op2 != 7) SetRM8(r);
    }

    private void Grp1_16(bool signExt)
    {
        int op2 = DecodeModRM();
        ushort a = GetRM16();
        ushort imm = signExt ? (ushort)(sbyte)Fetch8() : Fetch16();
        ushort r = Alu16(op2, a, imm);
        if (op2 != 7) SetRM16(r);
    }

    private void Grp3_8()
    {
        int op2 = DecodeModRM();
        switch (op2)
        {
            case 0: case 1: Logic8(GetRM8() & Fetch8()); break; // TEST
            case 2: SetRM8((byte)~GetRM8()); break;             // NOT
            case 3: SetRM8(Sub8(0, GetRM8(), 0)); break;        // NEG
            case 4: { int r = s.AL * GetRM8(); s.AX = (ushort)r; bool c = s.AH != 0; s.SetFlag(CpuState.CF, c); s.SetFlag(CpuState.OF, c); break; } // MUL
            case 5: { int r = (sbyte)s.AL * (sbyte)GetRM8(); s.AX = (ushort)r; bool c = (short)r is < -128 or > 127; s.SetFlag(CpuState.CF, c); s.SetFlag(CpuState.OF, c); break; } // IMUL
            case 6: { int d = GetRM8(); if (d == 0) { DoInterrupt(0); break; } int q = s.AX / d, rem = s.AX % d; if (q > 0xFF) { DoInterrupt(0); break; } s.AL = (byte)q; s.AH = (byte)rem; break; } // DIV
            case 7: { int d = (sbyte)GetRM8(); if (d == 0) { DoInterrupt(0); break; } int dividend = (short)s.AX; int q = dividend / d, rem = dividend % d; if (q is < -128 or > 127) { DoInterrupt(0); break; } s.AL = (byte)q; s.AH = (byte)rem; break; } // IDIV
        }
    }

    private void Grp3_16()
    {
        int op2 = DecodeModRM();
        switch (op2)
        {
            case 0: case 1: Logic16(GetRM16() & Fetch16()); break;
            case 2: SetRM16((ushort)~GetRM16()); break;
            case 3: SetRM16(Sub16(0, GetRM16(), 0)); break;
            case 4: { uint r = (uint)s.AX * GetRM16(); s.DX = (ushort)(r >> 16); s.AX = (ushort)r; bool c = s.DX != 0; s.SetFlag(CpuState.CF, c); s.SetFlag(CpuState.OF, c); break; }
            case 5: { int r = (short)s.AX * (short)GetRM16(); s.DX = (ushort)(r >> 16); s.AX = (ushort)r; bool c = r is < -32768 or > 32767; s.SetFlag(CpuState.CF, c); s.SetFlag(CpuState.OF, c); break; }
            case 6: { uint d = GetRM16(); if (d == 0) { DoInterrupt(0); break; } uint dividend = (uint)((s.DX << 16) | s.AX); uint q = dividend / d, rem = dividend % d; if (q > 0xFFFF) { DoInterrupt(0); break; } s.AX = (ushort)q; s.DX = (ushort)rem; break; }
            case 7: { int d = (short)GetRM16(); if (d == 0) { DoInterrupt(0); break; } int dividend = (s.DX << 16) | s.AX; int q = dividend / d, rem = dividend % d; if (q is < -32768 or > 32767) { DoInterrupt(0); break; } s.AX = (ushort)q; s.DX = (ushort)rem; break; }
        }
    }

    private void Grp4()
    {
        int op2 = DecodeModRM();
        if (op2 == 0) SetRM8(Inc8(GetRM8()));
        else SetRM8(Dec8(GetRM8()));
    }

    private void Grp5()
    {
        int op2 = DecodeModRM();
        switch (op2)
        {
            case 0: SetRM16(Inc16(GetRM16())); break;
            case 1: SetRM16(Dec16(GetRM16())); break;
            case 2: { ushort t = GetRM16(); Push16(s.IP); s.IP = t; break; } // CALL near indirect
            case 3: { ushort noff = ReadMem16(_rmSeg, _rmOff); ushort nseg = ReadMem16(_rmSeg, (ushort)(_rmOff + 2)); Push16(s.CS); Push16(s.IP); s.CS = nseg; s.IP = noff; break; } // CALL far
            case 4: s.IP = GetRM16(); break; // JMP near indirect
            case 5: { ushort noff = ReadMem16(_rmSeg, _rmOff); ushort nseg = ReadMem16(_rmSeg, (ushort)(_rmOff + 2)); s.CS = nseg; s.IP = noff; break; } // JMP far
            case 6: Push16(GetRM16()); break; // PUSH rm16
        }
    }

    // ====================== Shifts/rotates ======================

    private byte Shift8(int kind, byte v, int count)
    {
        if (count == 0) return v;
        byte v0 = v; // original (for OF on SHR)
        bool cf = false;
        for (int i = 0; i < count; i++)
        {
            switch (kind)
            {
                case 0: cf = (v & 0x80) != 0; v = (byte)((v << 1) | (cf ? 1 : 0)); s.SetFlag(CpuState.CF, cf); break; // ROL
                case 1: cf = (v & 1) != 0; v = (byte)((v >> 1) | (cf ? 0x80 : 0)); s.SetFlag(CpuState.CF, cf); break; // ROR
                case 2: { bool oc = s.GetFlag(CpuState.CF); cf = (v & 0x80) != 0; v = (byte)((v << 1) | (oc ? 1 : 0)); s.SetFlag(CpuState.CF, cf); break; } // RCL
                case 3: { bool oc = s.GetFlag(CpuState.CF); cf = (v & 1) != 0; v = (byte)((v >> 1) | (oc ? 0x80 : 0)); s.SetFlag(CpuState.CF, cf); break; } // RCR
                case 4: case 6: cf = (v & 0x80) != 0; v = (byte)(v << 1); s.SetFlag(CpuState.CF, cf); SetSZP8(v); break; // SHL/SAL
                case 5: cf = (v & 1) != 0; v = (byte)(v >> 1); s.SetFlag(CpuState.CF, cf); SetSZP8(v); break; // SHR
                case 7: cf = (v & 1) != 0; v = (byte)((sbyte)v >> 1); s.SetFlag(CpuState.CF, cf); SetSZP8(v); break; // SAR
            }
        }
        // OF is defined only for count==1: left (ROL/RCL/SHL) = CF⊕MSB; right ROR/RCR = the two top
        // bits of the result; SHR = the top bit of the original; SAR = 0.
        bool msb = (v & 0x80) != 0;
        s.SetFlag(CpuState.OF, kind switch
        {
            0 or 2 or 4 or 6 => msb ^ cf,
            1 or 3 => msb ^ ((v & 0x40) != 0),
            5 => (v0 & 0x80) != 0,
            _ => false, // SAR
        });
        return v;
    }

    private ushort Shift16(int kind, ushort v, int count)
    {
        if (count == 0) return v;
        ushort v0 = v; // original (for OF on SHR)
        bool cf = false;
        for (int i = 0; i < count; i++)
        {
            switch (kind)
            {
                case 0: cf = (v & 0x8000) != 0; v = (ushort)((v << 1) | (cf ? 1 : 0)); s.SetFlag(CpuState.CF, cf); break;
                case 1: cf = (v & 1) != 0; v = (ushort)((v >> 1) | (cf ? 0x8000 : 0)); s.SetFlag(CpuState.CF, cf); break;
                case 2: { bool oc = s.GetFlag(CpuState.CF); cf = (v & 0x8000) != 0; v = (ushort)((v << 1) | (oc ? 1 : 0)); s.SetFlag(CpuState.CF, cf); break; }
                case 3: { bool oc = s.GetFlag(CpuState.CF); cf = (v & 1) != 0; v = (ushort)((v >> 1) | (oc ? 0x8000 : 0)); s.SetFlag(CpuState.CF, cf); break; }
                case 4: case 6: cf = (v & 0x8000) != 0; v = (ushort)(v << 1); s.SetFlag(CpuState.CF, cf); SetSZP16(v); break;
                case 5: cf = (v & 1) != 0; v = (ushort)(v >> 1); s.SetFlag(CpuState.CF, cf); SetSZP16(v); break;
                case 7: cf = (v & 1) != 0; v = (ushort)((short)v >> 1); s.SetFlag(CpuState.CF, cf); SetSZP16(v); break;
            }
        }
        bool msb = (v & 0x8000) != 0;
        s.SetFlag(CpuState.OF, kind switch
        {
            0 or 2 or 4 or 6 => msb ^ cf,
            1 or 3 => msb ^ ((v & 0x4000) != 0),
            5 => (v0 & 0x8000) != 0,
            _ => false, // SAR
        });
        return v;
    }

    // ====================== Decimal adjustments ======================

    private void Daa()
    {
        byte al = s.AL; bool cf = s.GetFlag(CpuState.CF);
        if ((al & 0x0F) > 9 || s.GetFlag(CpuState.AF)) { s.AL += 6; s.SetFlag(CpuState.AF, true); }
        else s.SetFlag(CpuState.AF, false);
        if (al > 0x99 || cf) { s.AL += 0x60; s.SetFlag(CpuState.CF, true); }
        else s.SetFlag(CpuState.CF, false);
        SetSZP8(s.AL);
    }

    private void Das()
    {
        byte al = s.AL; bool cf = s.GetFlag(CpuState.CF);
        if ((al & 0x0F) > 9 || s.GetFlag(CpuState.AF)) { s.AL -= 6; s.SetFlag(CpuState.AF, true); }
        else s.SetFlag(CpuState.AF, false);
        if (al > 0x99 || cf) { s.AL -= 0x60; s.SetFlag(CpuState.CF, true); }
        else s.SetFlag(CpuState.CF, false);
        SetSZP8(s.AL);
    }

    private void Aaa()
    {
        if ((s.AL & 0x0F) > 9 || s.GetFlag(CpuState.AF))
        { s.AL += 6; s.AH++; s.SetFlag(CpuState.AF, true); s.SetFlag(CpuState.CF, true); }
        else { s.SetFlag(CpuState.AF, false); s.SetFlag(CpuState.CF, false); }
        s.AL &= 0x0F;
    }

    private void Aas()
    {
        if ((s.AL & 0x0F) > 9 || s.GetFlag(CpuState.AF))
        { s.AL -= 6; s.AH--; s.SetFlag(CpuState.AF, true); s.SetFlag(CpuState.CF, true); }
        else { s.SetFlag(CpuState.AF, false); s.SetFlag(CpuState.CF, false); }
        s.AL &= 0x0F;
    }

    private void Aam(byte b)
    {
        if (b == 0) { DoInterrupt(0); return; }
        s.AH = (byte)(s.AL / b);
        s.AL = (byte)(s.AL % b);
        SetSZP8(s.AL);
    }

    private void Aad(byte b)
    {
        s.AL = (byte)((s.AL + s.AH * b) & 0xFF);
        s.AH = 0;
        SetSZP8(s.AL);
    }

    // ====================== String operations ======================

    private void StringOp(Action one)
    {
        if (_rep == 0) { one(); return; }
        while (s.CX != 0) { one(); s.CX--; }
    }

    private void StringOpCmp(Action one)
    {
        if (_rep == 0) { one(); return; }
        while (s.CX != 0)
        {
            one();
            s.CX--;
            bool zf = s.GetFlag(CpuState.ZF);
            if (_rep == 0xF3 && !zf) break; // REPE: while ZF=1
            if (_rep == 0xF2 && zf) break;  // REPNE: while ZF=0
        }
    }

    private int Dir => s.GetFlag(CpuState.DF) ? -1 : 1;

    private void Movs8() { WriteMem8(s.ES, s.DI, ReadMem8(SegDS(), s.SI)); s.SI = (ushort)(s.SI + Dir); s.DI = (ushort)(s.DI + Dir); }
    private void Movs16() { WriteMem16(s.ES, s.DI, ReadMem16(SegDS(), s.SI)); s.SI = (ushort)(s.SI + 2 * Dir); s.DI = (ushort)(s.DI + 2 * Dir); }
    private void Stos8() { WriteMem8(s.ES, s.DI, s.AL); s.DI = (ushort)(s.DI + Dir); }
    private void Stos16() { WriteMem16(s.ES, s.DI, s.AX); s.DI = (ushort)(s.DI + 2 * Dir); }
    private void Lods8() { s.AL = ReadMem8(SegDS(), s.SI); s.SI = (ushort)(s.SI + Dir); }
    private void Lods16() { s.AX = ReadMem16(SegDS(), s.SI); s.SI = (ushort)(s.SI + 2 * Dir); }
    private void Cmps8() { Sub8(ReadMem8(SegDS(), s.SI), ReadMem8(s.ES, s.DI), 0); s.SI = (ushort)(s.SI + Dir); s.DI = (ushort)(s.DI + Dir); }
    private void Cmps16() { Sub16(ReadMem16(SegDS(), s.SI), ReadMem16(s.ES, s.DI), 0); s.SI = (ushort)(s.SI + 2 * Dir); s.DI = (ushort)(s.DI + 2 * Dir); }
    private void Scas8() { Sub8(s.AL, ReadMem8(s.ES, s.DI), 0); s.DI = (ushort)(s.DI + Dir); }
    private void Scas16() { Sub16(s.AX, ReadMem16(s.ES, s.DI), 0); s.DI = (ushort)(s.DI + 2 * Dir); }
}
