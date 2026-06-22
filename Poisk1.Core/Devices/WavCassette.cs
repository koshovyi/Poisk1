namespace Poisk1.Core.Devices;

/// <summary>
/// Offline decoder for the "Poisk" cassette .wav (cas2wav format). Unlike the raw .cas,
/// the .wav contains a header with the load address (Seg:Ofs), so the program can be
/// placed EXACTLY where it needs to go. Decoding is not in real time — no timing problems.
///
/// Encoding: bit "1" = long cycle (1000 Hz), bit "0" = short cycle (2000 Hz), MSB-first.
/// Layout: leader(1...) + 0 + 0x16 + header block(256+CRC) + 0xFF×4 + leader2 + 0x16 + data(blocks 256+CRC).
/// </summary>
public static class WavCassette
{
    public sealed record Program(ushort Seg, ushort Ofs, byte FileType, byte[] Data, string Name = "");

    /// <summary>Decode a .wav into a program (address + data). Throws an exception on failure.</summary>
    public static Program Decode(string wavPath)
    {
        var (samples, rate) = ReadWavMono8(wavPath);
        var bits = DecodeBits(samples, rate);

        // Sequential parsing with re-synchronization: before each 0x16 there is a leader ("1")
        // and a single "0" pulse that shifts the alignment — so we re-sync every time.
        const int MinLeader = 1000; // a real leader is ~2048 "1"; preambles/data are shorter
        int i = 0;
        byte ReadByte()
        {
            int v = 0;
            for (int k = 0; k < 8; k++) { v = (v << 1) | (i < bits.Count && bits[i] ? 1 : 0); i++; }
            return (byte)v;
        }
        // Find a LONG leader (≥MinLeader "1"), skip the single "0" and the 0x16 sync.
        // This way we bypass preambles (e.g., in DEMO) and false syncs in the data.
        void SkipLeaderAndSync()
        {
            while (i < bits.Count)
            {
                int run = 0, j = i;
                while (j < bits.Count && bits[j]) { j++; run++; }
                if (run >= MinLeader) { i = j; break; } // i — at the "0" right after the leader
                i = j + 1;                              // not a leader: skip the run and the "0"
            }
            i++;          // single "0" (sync-pulse)
            ReadByte();   // 0x16 byte
        }

        // --- Header block ---
        SkipLeaderAndSync();
        var block = new byte[256];
        for (int k = 0; k < 256; k++) block[k] = ReadByte();
        ReadByte(); ReadByte(); // header CRC
        if (block[0] != 0xA5) throw new InvalidDataException($"Invalid magic: 0x{block[0]:X2} (.wav decode failed).");

        byte fileType = block[9];
        int flen = block[10] | (block[11] << 8);
        ushort seg = (ushort)(block[12] | (block[13] << 8));
        ushort ofs = (ushort)(block[14] | (block[15] << 8));
        var name = new string(Enumerable.Range(1, 8).Select(k => (char)block[k]).ToArray()).TrimEnd();

        // --- Data (after leader2 + 0x16) ---
        SkipLeaderAndSync();
        var data = new byte[flen];
        int got = 0;
        while (got < flen)
        {
            int take = Math.Min(256, flen - got);
            for (int k = 0; k < take; k++) data[got++] = ReadByte();
            ReadByte(); ReadByte(); // data block CRC
        }
        return new Program(seg, ofs, fileType, data, name);
    }

    /// <summary>Read a .wav as 8-bit mono samples + rate (for authentic playback).</summary>
    public static (byte[] samples, int rate) LoadSamples(string path) => ReadWavMono8(path);

    /// <summary>[DEBUG] Parse the stream structure: leaders, syncs, first bytes of blocks.</summary>
    public static void Diagnose(string path, Action<string> log)
    {
        var (samples, rate) = ReadWavMono8(path);
        var bits = DecodeBits(samples, rate);
        log($"samples={samples.Length} rate={rate} bits={bits.Count}");
        int i = 0, blockNo = 0;
        while (i < bits.Count && blockNo < 6)
        {
            int leader = 0;
            while (i < bits.Count && bits[i]) { i++; leader++; }
            if (i >= bits.Count) break;
            i++; // single 0
            // read 24 bytes
            var bytes = new List<byte>();
            for (int n = 0; n < 24 && i + 8 <= bits.Count; n++)
            {
                int v = 0; for (int k = 0; k < 8; k++) v = (v << 1) | (bits[i++] ? 1 : 0);
                bytes.Add((byte)v);
            }
            log($"[leader={leader} \"1\"] bytes: {string.Join(" ", bytes.ConvertAll(b => b.ToString("X2")))}");
            // skip to the next leader: find a long run of "1"
            int run = 0, start = i;
            while (i < bits.Count)
            {
                if (bits[i]) run++; else run = 0;
                i++;
                if (run >= 64) { i -= run; break; } // reached the next leader
            }
            blockNo++;
        }
    }

    // --- WAV → 8-bit mono samples ---
    private static (byte[] samples, int rate) ReadWavMono8(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 44 || b[0] != (byte)'R' || b[1] != (byte)'I') throw new InvalidDataException("Not RIFF/WAV.");
        int channels = b[22] | (b[23] << 8);
        int rate = b[24] | (b[25] << 8) | (b[26] << 16) | (b[27] << 24);
        int bps = b[34] | (b[35] << 8); // bits per sample

        // Find the "data" chunk.
        int pos = 12;
        int dataOff = -1, dataLen = 0;
        while (pos + 8 <= b.Length)
        {
            int id = b[pos] | (b[pos + 1] << 8) | (b[pos + 2] << 16) | (b[pos + 3] << 24);
            int len = b[pos + 4] | (b[pos + 5] << 8) | (b[pos + 6] << 16) | (b[pos + 7] << 24);
            if (id == 0x61746164) { dataOff = pos + 8; dataLen = len; break; } // "data"
            pos += 8 + len + (len & 1);
        }
        if (dataOff < 0) throw new InvalidDataException("'data' chunk not found.");
        dataLen = Math.Min(dataLen, b.Length - dataOff);

        int bytesPerSample = (bps / 8) * channels;
        if (bytesPerSample < 1) bytesPerSample = 1;
        int n = dataLen / bytesPerSample;
        var s = new byte[n];
        for (int i = 0; i < n; i++)
        {
            int off = dataOff + i * bytesPerSample;
            s[i] = bps == 8 ? b[off] : (byte)((short)(b[off] | (b[off + 1] << 8)) / 256 + 128); // 16→8 unsigned
        }
        return (s, rate);
    }

    // --- Samples → bits (by the period between rising edges) ---
    private static List<bool> DecodeBits(byte[] s, int rate)
    {
        // Period threshold between a "1" (long) and a "0" (short) cycle.
        // cas2wav@43200: long ≈ rate/1000, short ≈ rate/2000. The threshold is in the middle.
        double thresh = rate / 1500.0;
        var edges = new List<int>();
        for (int i = 1; i < s.Length; i++)
            if (s[i - 1] < 128 && s[i] >= 128) edges.Add(i); // rising crossing of 128
        var bits = new List<bool>(edges.Count);
        for (int i = 1; i < edges.Count; i++)
            bits.Add((edges[i] - edges[i - 1]) >= thresh); // longer period → "1"
        return bits;
    }

}
