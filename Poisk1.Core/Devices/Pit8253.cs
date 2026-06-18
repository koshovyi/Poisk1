using Poisk1.Core.Io;

namespace Poisk1.Core.Devices;

/// <summary>
/// 8253 timer (ports 0x40–0x43). The counters count down from reload; at terminal count
/// they emit a pulse on the output:
///   channel 0 -> IRQ0 (system timer),
///   channel 1 -> IRQ6 ("Poisk" keyboard polling).
/// The modes are simplified down to periodic reloading.
/// </summary>
public sealed class Pit8253 : IIoDevice
{
    private sealed class Channel
    {
        public int Reload = 0x10000;
        public int Count = 0x10000;
        public int Access = 3;     // 1=lo, 2=hi, 3=lo/hi
        public int Mode;
        public bool HiNext;
        public int Latch = -1;
    }

    private readonly Channel[] _ch = { new(), new(), new() };
    private readonly Pic8259 _pic;

    /// <summary>[DEBUG] enable interrupts from channels 0 (timer) and 1 (keyboard).</summary>
    public bool Ch0Irq = true, Ch1Irq = true;

    public Pit8253(Pic8259 pic) => _pic = pic;

    public void WriteByte(ushort port, byte v)
    {
        int p = port & 3;
        if (p == 3) // control word
        {
            int c = v >> 6;
            if (c == 3) return; // read-back (8254) — not supported
            int access = (v >> 4) & 3;
            if (access == 0) { _ch[c].Latch = _ch[c].Count; _ch[c].HiNext = false; } // latch (read lo→hi)
            else { _ch[c].Access = access; _ch[c].Mode = (v >> 1) & 7; _ch[c].HiNext = false; }
            return;
        }

        var x = _ch[p];
        switch (x.Access)
        {
            case 1: x.Reload = (x.Reload & 0xFF00) | v; Load(x); break;
            case 2: x.Reload = (x.Reload & 0x00FF) | (v << 8); Load(x); break;
            default:
                if (!x.HiNext) { x.Reload = (x.Reload & 0xFF00) | v; x.HiNext = true; }
                else { x.Reload = (x.Reload & 0x00FF) | (v << 8); x.HiNext = false; Load(x); }
                break;
        }
    }

    private static void Load(Channel x) => x.Count = x.Reload == 0 ? 0x10000 : x.Reload;

    public byte ReadByte(ushort port)
    {
        int p = port & 3;
        if (p == 3) return 0;
        var x = _ch[p];
        int val = x.Latch >= 0 ? x.Latch : x.Count;
        byte b;
        if (x.Access == 1) b = (byte)val;
        else if (x.Access == 2) b = (byte)(val >> 8);
        else
        {
            if (!x.HiNext) { b = (byte)val; x.HiNext = true; }
            else { b = (byte)(val >> 8); x.HiNext = false; x.Latch = -1; }
        }
        return b;
    }

    /// <summary>Advance the counters by <paramref name="ticks"/> input clocks.</summary>
    public void Tick(int ticks)
    {
        for (int i = 0; i < 3; i++)
        {
            var x = _ch[i];
            x.Count -= ticks;
            while (x.Count <= 0)
            {
                x.Count += x.Reload == 0 ? 0x10000 : x.Reload;
                if (i == 0 && Ch0Irq) _pic.RaiseIrq(0);
                else if (i == 1 && Ch1Irq) _pic.RaiseIrq(6);
            }
        }
    }

    /// <summary>Output level of channel 2 (mode 3, square wave) — for speaker emulation.
    /// High in the first half of the count period → frequency = input_clock / Reload.</summary>
    public bool Channel2Level()
    {
        var x = _ch[2];
        int r = x.Reload == 0 ? 0x10000 : x.Reload;
        return x.Count > r / 2;
    }
}
