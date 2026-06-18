namespace Poisk1.Core.Cpu;

/// <summary>
/// The full 8086/8088 register file and flags. This is the canonical state store
/// the interpreter works with; the UI/logs read it directly.
/// </summary>
public sealed class CpuState
{
    // 16-bit general-purpose registers.
    public ushort AX, BX, CX, DX;
    public ushort SP, BP, SI, DI;
    // Segment registers.
    public ushort CS, DS, ES, SS;
    // Instruction pointer and flags.
    public ushort IP;
    public ushort Flags;

    // --- Byte "halves" ---
    public byte AL { get => (byte)AX; set => AX = (ushort)((AX & 0xFF00) | value); }
    public byte AH { get => (byte)(AX >> 8); set => AX = (ushort)((AX & 0x00FF) | (value << 8)); }
    public byte BL { get => (byte)BX; set => BX = (ushort)((BX & 0xFF00) | value); }
    public byte BH { get => (byte)(BX >> 8); set => BX = (ushort)((BX & 0x00FF) | (value << 8)); }
    public byte CL { get => (byte)CX; set => CX = (ushort)((CX & 0xFF00) | value); }
    public byte CH { get => (byte)(CX >> 8); set => CX = (ushort)((CX & 0x00FF) | (value << 8)); }
    public byte DL { get => (byte)DX; set => DX = (ushort)((DX & 0xFF00) | value); }
    public byte DH { get => (byte)(DX >> 8); set => DX = (ushort)((DX & 0x00FF) | (value << 8)); }

    // --- Flags (bits of the FLAGS register) ---
    public const ushort CF = 0x0001;
    public const ushort PF = 0x0004;
    public const ushort AF = 0x0010;
    public const ushort ZF = 0x0040;
    public const ushort SF = 0x0080;
    public const ushort TF = 0x0100;
    public const ushort IF = 0x0200;
    public const ushort DF = 0x0400;
    public const ushort OF = 0x0800;

    public bool GetFlag(ushort mask) => (Flags & mask) != 0;

    public void SetFlag(ushort mask, bool value)
    {
        if (value) Flags |= mask;
        else Flags = (ushort)(Flags & ~mask);
    }

    /// <summary>Linear (physical) address of the next instruction.</summary>
    public uint LinearIp => (uint)(((CS << 4) + IP) & MemoryBus.AddressMask);

    public override string ToString()
        => $"{CS:X4}:{IP:X4}  AX={AX:X4} BX={BX:X4} CX={CX:X4} DX={DX:X4} " +
           $"SI={SI:X4} DI={DI:X4} BP={BP:X4} SP={SP:X4} " +
           $"DS={DS:X4} ES={ES:X4} SS={SS:X4} F={Flags:X4}";
}
