using Poisk1.Core.Io;

namespace Poisk1.Core.Devices;

/// <summary>
/// 8259 interrupt controller (ports 0x20–0x21). Implements IRR/ISR/IMR, priorities,
/// the ICW1–ICW4 initialization sequence and EOI. Sufficient for IRQ0 (timer) and IRQ6 (keyboard).
/// </summary>
public sealed class Pic8259 : IIoDevice, IInterruptController
{
    private byte _imr = 0xFF;  // mask (1 = masked)
    private byte _irr;         // requests
    private byte _isr;         // in service
    private byte _vectorBase = 0x08;

    private int _initStep;     // 0 = running; 1 = awaiting ICW2; 2 = ICW3; 3 = ICW4
    private bool _needIcw4, _single, _readIsr;

    /// <summary>[DEBUG] counter of how many times each IRQ was raised.</summary>
    public readonly long[] Raised = new long[8];

    /// <summary>Raise the IRQ request line n (0..7).</summary>
    public void RaiseIrq(int n) { _irr |= (byte)(1 << n); Raised[n]++; }

    public void WriteByte(ushort port, byte v)
    {

        if ((port & 1) == 0) // 0x20 — command
        {
            if ((v & 0x10) != 0) // ICW1
            {
                _needIcw4 = (v & 0x01) != 0;
                _single = (v & 0x02) != 0;
                _initStep = 1;
                _isr = 0;
                _irr = 0;
                _imr = 0; // ICW1 clears the mask (all IRQs enabled after initialization)
            }
            else if ((v & 0x08) != 0) // OCW3
            {
                if ((v & 0x03) == 0x03) _readIsr = true;
                else if ((v & 0x03) == 0x02) _readIsr = false;
            }
            else if ((v & 0x20) != 0) // OCW2 — EOI
            {
                if ((v & 0x40) != 0) _isr &= (byte)~(1 << (v & 7)); // specific EOI
                else ClearHighestIsr();                              // non-specific EOI
            }
        }
        else // 0x21 — data
        {
            switch (_initStep)
            {
                case 1: _vectorBase = (byte)(v & 0xF8); _initStep = _single ? (_needIcw4 ? 3 : 0) : 2; break;
                case 2: _initStep = _needIcw4 ? 3 : 0; break; // ICW3 — ignored
                case 3: _initStep = 0; break;                 // ICW4 — ignored
                default: _imr = v; break;                     // OCW1 — mask
            }
        }
    }

    public byte ReadByte(ushort port)
        => (port & 1) == 1 ? _imr : (_readIsr ? _isr : _irr);

    private void ClearHighestIsr()
    {
        for (int i = 0; i < 8; i++)
            if ((_isr & (1 << i)) != 0) { _isr &= (byte)~(1 << i); return; }
    }

    public int Poll()
    {
        int avail = _irr & ~_imr;
        for (int i = 0; i < 8; i++)
        {
            int bit = 1 << i;
            if ((avail & bit) == 0) continue;
            // If an equal/higher priority is already in service — it is blocked.
            if ((_isr & ((1 << (i + 1)) - 1)) != 0) return -1;
            _irr &= (byte)~bit;
            _isr |= (byte)bit;
            return _vectorBase + i;
        }
        return -1;
    }
}
