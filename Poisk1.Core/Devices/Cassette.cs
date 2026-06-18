using Poisk1.Core.Io;

namespace Poisk1.Core.Devices;

/// <summary>
/// Cassette interface of the "Poisk". The BIOS reads the signal level from port 0x62, bit 4
/// (level &lt; 0 → bit = 1) and clocks the transitions with its own polling loop.
///
/// The signal is reproduced from .cas data using the cas2wav encoding (Tronix, 2013):
///   bit "1" = long pulse (1000 Hz), bit "0" = short pulse (2000 Hz), one sine cycle per bit;
///   bit4 = sign of the sine (0 in the first half of the period, 1 in the second) → 2 transitions per pulse.
///   Tape layout: leader(2048×"1") + 0-bit + 0x16 + header block(256+CRC16)
///   + 4×0xFF + leader2 + 0x16 + data blocks(256+CRC16).
///
/// Timing is set by <see cref="ShortHalfCycles"/> (CPU cycles per half-period of the short
/// pulse); the long pulse is twice as long. The value is tuned to the BIOS polling loop.
/// </summary>
public sealed class Cassette : IIoDevice
{
    public const ushort DataPort = 0x62;
    public const byte DataBit = 0x10; // bit 4

    /// <summary>CPU cycles per half-period of the short pulse (0-bit). Long = 2×. Tunable.</summary>
    public long ShortHalfCycles { get; set; } = 300;

    private readonly Func<long> _cycles;
    private bool[] _pulses = Array.Empty<bool>(); // true = long (1), false = short (0)
    private bool _inserted;
    private bool _playing;

    // Playback mode for a real .wav (authentic input).
    private byte[]? _wav;
    private double _cps;            // CPU cycles per one .wav sample
    private const int CpuHz = 5_000_000;
    public double WavCyclesPerSample { get; set; } // 0 = auto (CpuHz/rate); otherwise — tuned
    public long LeadInCycles { get; set; } = 20000; // silence before the leader (for BIOS synchronization)

    // Playback cursor (monotonic in time).
    private long _startCycle;
    private int _curPulse;
    private long _curPulseStart;

    // "Arming": playback starts from zero on the FIRST read of port 0x62 by the cassette
    // loop of the BIOS — this way the leader is always fresh, regardless of menu navigation timing.
    private bool _armed;

    public Cassette(Func<long> cycles) => _cycles = cycles;

    public bool Inserted => _inserted;
    public bool Playing => _playing;
    public string? Name { get; private set; }
    public long Reads { get; private set; } // [DEBUG] counter of 0x62 reads

    /// <summary>Insert a .cas (raw program data) and start "playback".</summary>
    public void Insert(byte[] casData, string name, byte fileType = 0x80, ushort seg = 0x0060, ushort ofs = 0x081E)
    {
        Name = name;
        _pulses = BuildSignal(casData, name, fileType, seg, ofs);
        _inserted = true;
        _curPulse = 0;
        _curPulseStart = 0;
        _startCycle = _cycles();
        _playing = true;
    }

    /// <summary>
    /// Insert a REAL .wav (8-bit samples) and start authentic playback:
    /// bit 4 level = sign of the sample (sample &lt; 128 → bit=1), in sync with CPU cycles.
    /// Rate: WavCyclesPerSample or auto CpuHz/rate.
    /// </summary>
    public void InsertWav(byte[] samples, int rate, string name)
    {
        Name = name;
        _wav = samples;
        _cps = WavCyclesPerSample > 0 ? WavCyclesPerSample : (double)CpuHz / rate;
        _pulses = Array.Empty<bool>();
        _armed = true;                          // start deferred until the first read of 0x62
        _startCycle = long.MaxValue;            // until armed-and-read — constant level
        _inserted = true;
        _playing = true;
    }

    public void Eject()
    {
        _inserted = false;
        _playing = false;
        _armed = false;
        _pulses = Array.Empty<bool>();
        _wav = null;
        Name = null;
    }

    public byte ReadByte(ushort port)
    {
        if (port != DataPort) return 0x00;
        Reads++;
        if (!_playing) return 0x00;
        if (_armed) { _armed = false; _startCycle = _cycles() + LeadInCycles; } // tape starts here
        return Level() ? DataBit : (byte)0x00;
    }

    public void WriteByte(ushort port, byte value) { /* writing to the cassette is not supported */ }

    private long PulseLen(int i) => (_pulses[i] ? 2 : 1) * 2 * ShortHalfCycles; // long=4H, short=2H

    /// <summary>Current bit level (true = signal "low", bit4=1).</summary>
    private bool Level()
    {
        if (_wav is not null) // authentic .wav
        {
            long idx = (long)((_cycles() - _startCycle) / _cps);
            if (idx < 0) return false;
            if (idx >= _wav.Length) { _playing = false; return false; }
            return _wav[idx] < 128; // sign: negative half-wave → bit4=1
        }

        long t = _cycles() - _startCycle;
        // Advance the cursor forward to the pulse that contains time t.
        while (_curPulse < _pulses.Length && t >= _curPulseStart + PulseLen(_curPulse))
        {
            _curPulseStart += PulseLen(_curPulse);
            _curPulse++;
        }
        if (_curPulse >= _pulses.Length) { _playing = false; return false; } // end of tape
        long off = t - _curPulseStart;
        long half = PulseLen(_curPulse) / 2;
        return off >= half; // first half — 0, second — 1
    }

    // ====================== Signal generation from .cas ======================

    private static bool[] BuildSignal(byte[] data, string name, byte fileType, ushort seg, ushort ofs)
    {
        var pulses = new List<bool>(1 << 20);

        void Bit(bool one) => pulses.Add(one);
        void WriteByte(byte b) { for (int i = 7; i >= 0; i--) Bit(((b >> i) & 1) != 0); }
        void Leader() { for (int i = 0; i < 2048; i++) Bit(true); Bit(false); WriteByte(0x16); }
        void Block(byte[] block256)
        {
            for (int i = 0; i < 256; i++) WriteByte(block256[i]);
            ushort crc = Crc16(block256);
            WriteByte((byte)(crc >> 8));
            WriteByte((byte)(crc & 0xFF));
        }

        // Leader 1 + header block (Poisk_hdr in the first 16 bytes of the 256-byte block).
        Leader();
        var hdr = new byte[256];
        hdr[0] = 0xA5;
        var nm = (name.ToUpperInvariant() + "        ").Substring(0, 8);
        for (int i = 0; i < 8; i++) hdr[1 + i] = (byte)nm[i];
        hdr[9] = fileType;
        ushort flen = (ushort)data.Length;
        hdr[10] = (byte)(flen & 0xFF); hdr[11] = (byte)(flen >> 8);
        hdr[12] = (byte)(seg & 0xFF); hdr[13] = (byte)(seg >> 8);
        hdr[14] = (byte)(ofs & 0xFF); hdr[15] = (byte)(ofs >> 8);
        Block(hdr);

        // Stream closing + leader 2.
        for (int i = 0; i < 4; i++) WriteByte(0xFF);
        Leader();

        // Data blocks of 256 bytes each (the last one is padded with zeros).
        for (int p = 0; p < data.Length; p += 256)
        {
            var block = new byte[256];
            int n = Math.Min(256, data.Length - p);
            Array.Copy(data, p, block, 0, n);
            Block(block);
        }

        return pulses.ToArray();
    }

    // CRC16 (X^16+X^12+X^5+1), init 0xFFFF, xor-out 0xFFFF — as in cas2wav.
    private static ushort Crc16(byte[] block256)
    {
        ushort crc = 0xFFFF;
        for (int i = 0; i < 256; i++)
            crc = (ushort)((crc << 8) ^ Crc16Table[(byte)((crc >> 8) ^ block256[i])]);
        return (ushort)(crc ^ 0xFFFF);
    }

    private static readonly ushort[] Crc16Table =
    {
        0x0000,0x1021,0x2042,0x3063,0x4084,0x50A5,0x60C6,0x70E7,0x8108,0x9129,0xA14A,0xB16B,0xC18C,0xD1AD,0xE1CE,0xF1EF,
        0x1231,0x0210,0x3273,0x2252,0x52B5,0x4294,0x72F7,0x62D6,0x9339,0x8318,0xB37B,0xA35A,0xD3BD,0xC39C,0xF3FF,0xE3DE,
        0x2462,0x3443,0x0420,0x1401,0x64E6,0x74C7,0x44A4,0x5485,0xA56A,0xB54B,0x8528,0x9509,0xE5EE,0xF5CF,0xC5AC,0xD58D,
        0x3653,0x2672,0x1611,0x0630,0x76D7,0x66F6,0x5695,0x46B4,0xB75B,0xA77A,0x9719,0x8738,0xF7DF,0xE7FE,0xD79D,0xC7BC,
        0x48C4,0x58E5,0x6886,0x78A7,0x0840,0x1861,0x2802,0x3823,0xC9CC,0xD9ED,0xE98E,0xF9AF,0x8948,0x9969,0xA90A,0xB92B,
        0x5AF5,0x4AD4,0x7AB7,0x6A96,0x1A71,0x0A50,0x3A33,0x2A12,0xDBFD,0xCBDC,0xFBBF,0xEB9E,0x9B79,0x8B58,0xBB3B,0xAB1A,
        0x6CA6,0x7C87,0x4CE4,0x5CC5,0x2C22,0x3C03,0x0C60,0x1C41,0xEDAE,0xFD8F,0xCDEC,0xDDCD,0xAD2A,0xBD0B,0x8D68,0x9D49,
        0x7E97,0x6EB6,0x5ED5,0x4EF4,0x3E13,0x2E32,0x1E51,0x0E70,0xFF9F,0xEFBE,0xDFDD,0xCFFC,0xBF1B,0xAF3A,0x9F59,0x8F78,
        0x9188,0x81A9,0xB1CA,0xA1EB,0xD10C,0xC12D,0xF14E,0xE16F,0x1080,0x00A1,0x30C2,0x20E3,0x5004,0x4025,0x7046,0x6067,
        0x83B9,0x9398,0xA3FB,0xB3DA,0xC33D,0xD31C,0xE37F,0xF35E,0x02B1,0x1290,0x22F3,0x32D2,0x4235,0x5214,0x6277,0x7256,
        0xB5EA,0xA5CB,0x95A8,0x8589,0xF56E,0xE54F,0xD52C,0xC50D,0x34E2,0x24C3,0x14A0,0x0481,0x7466,0x6447,0x5424,0x4405,
        0xA7DB,0xB7FA,0x8799,0x97B8,0xE75F,0xF77E,0xC71D,0xD73C,0x26D3,0x36F2,0x0691,0x16B0,0x6657,0x7676,0x4615,0x5634,
        0xD94C,0xC96D,0xF90E,0xE92F,0x99C8,0x89E9,0xB98A,0xA9AB,0x5844,0x4865,0x7806,0x6827,0x18C0,0x08E1,0x3882,0x28A3,
        0xCB7D,0xDB5C,0xEB3F,0xFB1E,0x8BF9,0x9BD8,0xABBB,0xBB9A,0x4A75,0x5A54,0x6A37,0x7A16,0x0AF1,0x1AD0,0x2AB3,0x3A92,
        0xFD2E,0xED0F,0xDD6C,0xCD4D,0xBDAA,0xAD8B,0x9DE8,0x8DC9,0x7C26,0x6C07,0x5C64,0x4C45,0x3CA2,0x2C83,0x1CE0,0x0CC1,
        0xEF1F,0xFF3E,0xCF5D,0xDF7C,0xAF9B,0xBFBA,0x8FD9,0x9FF8,0x6E17,0x7E36,0x4E55,0x5E74,0x2E93,0x3EB2,0x0ED1,0x1EF0,
    };
}
