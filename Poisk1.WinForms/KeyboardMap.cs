using Poisk1.Core.Devices;

namespace Poisk1.WinForms;

/// <summary>
/// Mapping of Windows Forms keys to the "Poisk-1" keyboard matrix
/// (row 0..7 = Y1..Y8, bit mask), following the MAME poisk1_keyboard_v91 layout.
/// </summary>
public static class KeyboardMap
{
    // (row, bit) for each key.
    private static readonly Dictionary<Keys, (int row, int bit)> Map = new()
    {
        // Function keys
        [Keys.F1] = (3, 0x010), [Keys.F2] = (3, 0x001), [Keys.F3] = (3, 0x800),
        [Keys.F4] = (3, 0x400), [Keys.F5] = (3, 0x008), [Keys.F6] = (1, 0x008),
        [Keys.F7] = (6, 0x200), [Keys.F8] = (6, 0x800), [Keys.F9] = (6, 0x020),
        [Keys.F10] = (7, 0x020), [Keys.F11] = (7, 0x080), [Keys.F12] = (4, 0x080),

        // Control keys
        [Keys.Enter] = (0, 0x020), [Keys.Escape] = (3, 0x020), [Keys.Back] = (1, 0x020),
        [Keys.Space] = (5, 0x800), [Keys.Tab] = (7, 0x004),
        [Keys.ShiftKey] = (5, 0x001), [Keys.LShiftKey] = (5, 0x001), [Keys.RShiftKey] = (5, 0x080),
        [Keys.ControlKey] = (5, 0x004), [Keys.LControlKey] = (5, 0x004),
        [Keys.Menu] = (5, 0x020), [Keys.LMenu] = (5, 0x020), [Keys.RMenu] = (5, 0x040),
        [Keys.CapsLock] = (4, 0x004),

        // Arrow keys (on the matrix numeric keypad)
        [Keys.Up] = (0, 0x040), [Keys.Down] = (0, 0x080),
        [Keys.Left] = (1, 0x002), [Keys.Right] = (6, 0x080),

        // Top-row digits
        [Keys.D0] = (0, 0x100), [Keys.D1] = (7, 0x040), [Keys.D2] = (7, 0x010),
        [Keys.D3] = (7, 0x001), [Keys.D4] = (7, 0x800), [Keys.D5] = (7, 0x400),
        [Keys.D6] = (3, 0x100), [Keys.D7] = (3, 0x200), [Keys.D8] = (1, 0x100),
        [Keys.D9] = (1, 0x200),

        // Letters (Latin)
        [Keys.A] = (4, 0x040), [Keys.B] = (2, 0x400), [Keys.C] = (5, 0x008),
        [Keys.D] = (2, 0x010), [Keys.E] = (4, 0x001), [Keys.F] = (2, 0x001),
        [Keys.G] = (2, 0x800), [Keys.H] = (4, 0x008), [Keys.I] = (4, 0x200),
        [Keys.J] = (4, 0x100), [Keys.K] = (2, 0x200), [Keys.L] = (6, 0x100),
        [Keys.M] = (2, 0x100), [Keys.N] = (2, 0x008), [Keys.O] = (0, 0x200),
        [Keys.P] = (1, 0x400), [Keys.Q] = (4, 0x020), [Keys.R] = (4, 0x800),
        [Keys.S] = (2, 0x020), [Keys.T] = (4, 0x400), [Keys.U] = (7, 0x200),
        [Keys.V] = (5, 0x400), [Keys.W] = (4, 0x010), [Keys.X] = (5, 0x010),
        [Keys.Y] = (7, 0x008), [Keys.Z] = (2, 0x040),

        // Punctuation
        [Keys.OemSemicolon] = (0, 0x008),   // ;
        [Keys.OemMinus] = (0, 0x400),       // -
        [Keys.OemQuotes] = (0, 0x800),      // '
        [Keys.Oemplus] = (1, 0x001),        // =
        [Keys.OemOpenBrackets] = (1, 0x800),// [
        [Keys.OemCloseBrackets] = (0, 0x001),// ]
        [Keys.OemPipe] = (1, 0x010),        // \
        [Keys.OemPeriod] = (5, 0x100),      // .
        [Keys.Oemcomma] = (5, 0x200),       // ,
        [Keys.OemQuestion] = (6, 0x400),    // /
        [Keys.Oemtilde] = (3, 0x040),       // `
    };

    /// <summary>Apply a key state to the matrix. Returns true if the key is known.</summary>
    public static bool Apply(Keyboard8255 kbd, Keys key, bool pressed)
    {
        if (Map.TryGetValue(key, out var pos))
        {
            kbd.SetKey(pos.row, pos.bit, pressed);
            return true;
        }
        return false;
    }
}
