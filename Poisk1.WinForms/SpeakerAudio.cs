using System.Runtime.InteropServices;

namespace Poisk1.WinForms;

/// <summary>
/// Streaming speaker audio output via WinMM <c>waveOut</c> (16-bit mono, no external
/// dependencies). Accepts samples from the machine in frames into a ring of ready buffers and
/// feeds them to the driver; completed buffers are returned to the pool. If audio is unavailable it simply stays silent.
/// </summary>
public sealed class SpeakerAudio : IDisposable
{
    private const int BufSamples = 512;     // ~11.6 ms at 44.1 kHz
    private const int NumBuffers = 8;       // ~93 ms of headroom
    private const uint WHDR_DONE = 0x00000001;
    private const uint WAVE_MAPPER = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength, dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags, dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr h, uint dev, ref WaveFormatEx fmt, IntPtr cb, IntPtr inst, uint flags);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr h, IntPtr hdr, uint cb);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr h, IntPtr hdr, uint cb);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr h, IntPtr hdr, uint cb);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr h);

    private IntPtr _hwo;
    private readonly IntPtr[] _hdrPtr = new IntPtr[NumBuffers];
    private readonly GCHandle[] _dataHandle = new GCHandle[NumBuffers];
    private readonly bool[] _free = new bool[NumBuffers];
    private readonly short[] _pending = new short[BufSamples * NumBuffers * 2];
    private int _pendHead, _pendCount;
    private bool _ok;
    private readonly uint _hdrSize = (uint)Marshal.SizeOf<WaveHdr>();

    public SpeakerAudio()
    {
        try
        {
            var fmt = new WaveFormatEx
            {
                wFormatTag = 1, // PCM
                nChannels = 1,
                nSamplesPerSec = (uint)Poisk1.Core.Machine.AudioRate,
                wBitsPerSample = 16,
                nBlockAlign = 2,
                nAvgBytesPerSec = (uint)Poisk1.Core.Machine.AudioRate * 2,
                cbSize = 0,
            };
            if (waveOutOpen(out _hwo, WAVE_MAPPER, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;

            for (int i = 0; i < NumBuffers; i++)
            {
                var data = new short[BufSamples];
                _dataHandle[i] = GCHandle.Alloc(data, GCHandleType.Pinned);
                var hdr = new WaveHdr { lpData = _dataHandle[i].AddrOfPinnedObject(), dwBufferLength = BufSamples * 2 };
                _hdrPtr[i] = Marshal.AllocHGlobal((int)_hdrSize);
                Marshal.StructureToPtr(hdr, _hdrPtr[i], false);
                waveOutPrepareHeader(_hwo, _hdrPtr[i], _hdrSize);
                _free[i] = true;
            }
            _ok = true;
        }
        catch { _ok = false; }
    }

    /// <summary>Feed samples from the machine (16-bit @ AudioRate); distribute them across the driver buffers.</summary>
    public void Feed(short[] src, int count)
    {
        if (!_ok) return;

        // Return completed buffers to the pool.
        for (int i = 0; i < NumBuffers; i++)
        {
            if (_free[i]) continue;
            var h = Marshal.PtrToStructure<WaveHdr>(_hdrPtr[i]);
            if ((h.dwFlags & WHDR_DONE) != 0) _free[i] = true;
        }

        // Add new samples to the queue (drop the oldest on overflow).
        for (int i = 0; i < count; i++)
        {
            if (_pendCount >= _pending.Length) { _pendHead = (_pendHead + 1) % _pending.Length; _pendCount--; }
            _pending[(_pendHead + _pendCount) % _pending.Length] = src[i];
            _pendCount++;
        }

        // Fill free buffers with full chunks and hand them to the driver.
        for (int i = 0; i < NumBuffers && _pendCount >= BufSamples; i++)
        {
            if (!_free[i]) continue;
            var data = (short[])_dataHandle[i].Target!;
            for (int k = 0; k < BufSamples; k++)
            {
                data[k] = _pending[_pendHead];
                _pendHead = (_pendHead + 1) % _pending.Length;
            }
            _pendCount -= BufSamples;
            // Clear the DONE flag before re-writing the same header.
            var h = Marshal.PtrToStructure<WaveHdr>(_hdrPtr[i]);
            h.dwFlags &= ~WHDR_DONE;
            h.dwBufferLength = BufSamples * 2;
            Marshal.StructureToPtr(h, _hdrPtr[i], false);
            _free[i] = false;
            waveOutWrite(_hwo, _hdrPtr[i], _hdrSize);
        }
    }

    public void Dispose()
    {
        if (_hwo != IntPtr.Zero)
        {
            waveOutReset(_hwo);
            for (int i = 0; i < NumBuffers; i++)
                if (_hdrPtr[i] != IntPtr.Zero)
                {
                    waveOutUnprepareHeader(_hwo, _hdrPtr[i], _hdrSize);
                    Marshal.FreeHGlobal(_hdrPtr[i]);
                    if (_dataHandle[i].IsAllocated) _dataHandle[i].Free();
                }
            waveOutClose(_hwo);
            _hwo = IntPtr.Zero;
        }
    }
}
