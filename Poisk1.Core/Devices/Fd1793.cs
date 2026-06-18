using Poisk1.Core.Io;

namespace Poisk1.Core.Devices;

/// <summary>
/// FDC FD1793 controller (= KR1818VG93, a WD1793 clone). Ports 0xC0–0xC3:
///   0 — command(W)/status(R), 1 — track, 2 — sector, 3 — data.
/// "Instant" model: the command executes immediately, the sector data is buffered and served
/// byte by byte through the data register with DRQ raised (no real cycles/wait states).
/// The B504 controller selects the drive/side/density via port 0xC4 (set methods).
/// </summary>
public sealed class Fd1793 : IIoDevice
{
    public const ushort Base = 0x00C0;

    private readonly FloppyDisk?[] _drives = new FloppyDisk?[2];
    private int _drive, _side;

    private byte _track, _sector, _dataReg, _status;
    private bool _drq, _intrq;

    private readonly byte[] _buf = new byte[FloppyDisk.SectorSize];
    private int _bufPos, _bufLen;
    private bool _reading, _writing, _multi;
    private int _wrCyl, _wrHead, _wrSec; // write target

    // Time-dependent failure: when there is no disk, the FDC "spins" waiting for the index
    // for ~0.6s and only then returns NOT_READY (like the hardware). Without this, booting
    // without a floppy flies by instantly.
    public Func<long>? Cycles;                 // machine cycle counter (set by B504)
    private const long FailDelayCycles = 3_000_000; // ~0.6 s at 5 MHz
    private long _busyUntil;
    private bool _pendingFail;
    private long Now => Cycles?.Invoke() ?? long.MaxValue;

    // --- status bits ---
    private const byte S_BUSY = 0x01, S_DRQ = 0x02, S_INDEX = 0x02, S_TRACK0 = 0x04,
                       S_LOSTDATA = 0x04, S_CRC = 0x08, S_SEEKERR = 0x10, S_RNF = 0x10,
                       S_HEADLOADED = 0x20, S_PROTECT = 0x40, S_NOTREADY = 0x80;

    public bool Drq => _drq;
    public bool Intrq => _intrq;
    public bool Busy => (_status & S_BUSY) != 0;

    /// <summary>Whether the drive is physically connected (0/1). Disabled = "no drive".</summary>
    public readonly bool[] Present = { true, true };

    public void SetDrive(int d) => _drive = d & 1;
    public void SetSide(int s) => _side = s & 1;
    public void Attach(int drive, FloppyDisk? disk) { if ((uint)drive < 2) _drives[drive] = disk; }
    public FloppyDisk? Disk(int drive) => (uint)drive < 2 ? _drives[drive] : null;

    public void Reset()
    {
        _status = 0; _drq = _intrq = false; _reading = _writing = false;
        _bufPos = _bufLen = 0; _track = 0; _pendingFail = false;
    }

    /// <summary>Complete the deferred read failure (no disk) once the timeout has elapsed.</summary>
    public void PollTimer()
    {
        if (_pendingFail && Now >= _busyUntil)
        {
            _pendingFail = false; _drq = false; _intrq = true;
            _status = S_NOTREADY; // now the BIOS sees "not ready"
        }
    }

    public long FailCount; // [DEBUG] how many times we "spun" without a disk

    // Begin "spinning" the FDC without a disk: BUSY for ~0.6s, then NOT_READY (like an index timeout).
    private void BeginFail()
    {
        FailCount++;
        _reading = _writing = false; _drq = false; _intrq = false;
        _status = S_BUSY; _busyUntil = Now + FailDelayCycles; _pendingFail = true;
    }

    private FloppyDisk? Cur => _drives[_drive];
    // Drive "present" (responds to seek/restore even without a disk) vs "disk ready".
    private bool DrivePresent => Present[_drive];
    private bool Ready => Present[_drive] && Cur != null;

    // ---------------- Ports 0xC0–0xC3 ----------------

    public byte ReadByte(ushort port)
    {
        switch (port - Base)
        {
            case 0: // status (reading clears INTRQ)
                PollTimer();          // the failure time may have arrived
                byte st = _status;
                _intrq = false;
                return st;
            case 1: return _track;
            case 2: return _sector;
            case 3: return ReadData();
            default: return 0xFF;
        }
    }

    public void WriteByte(ushort port, byte value)
    {
        switch (port - Base)
        {
            case 0: Command(value); break;
            case 1: _track = value; break;
            case 2: _sector = value; break;
            case 3: WriteData(value); break;
        }
    }

    private byte ReadData()
    {
        if (_reading && _bufPos < _bufLen)
        {
            _dataReg = _buf[_bufPos++];
            if (_bufPos >= _bufLen) SectorDone(); // sector fully read
        }
        return _dataReg;
    }

    private void WriteData(byte value)
    {
        _dataReg = value;
        if (_writing && _bufPos < _bufLen)
        {
            _buf[_bufPos++] = value;
            if (_bufPos >= _bufLen)
            {
                Cur?.WriteSector(_wrCyl, _wrHead, _wrSec, _buf);
                SectorDone();
            }
        }
    }

    // Sector completion: in multi-sector mode (m-bit) we move to the next sector of the
    // same track; otherwise (or when the sectors run out) we finish the command.
    private void SectorDone()
    {
        if (_multi && Cur != null)
        {
            byte next = (byte)(_sector + 1);
            if (_reading && Cur.ReadSector(_track, _side, next, _buf)) { _sector = next; _bufPos = 0; return; }
            if (_writing && next <= Cur.Sectors) { _sector = next; _wrSec = next; _bufPos = 0; return; }
        }
        EndTransferOk();
    }

    private void EndTransferOk()
    {
        _reading = _writing = _multi = false;
        _drq = false; _intrq = true;
        _status &= unchecked((byte)~(S_BUSY | S_DRQ)); // success: clear busy/drq
    }

    // ---------------- Commands ----------------

    private void Command(byte cmd)
    {
        _intrq = false;
        int type = cmd >> 4;

        if (type == 0x0D) // Force Interrupt
        {
            _reading = _writing = false; _drq = false; _intrq = true;
            _status = (byte)((DrivePresent ? 0 : S_NOTREADY) | (_track == 0 ? S_TRACK0 : 0) | S_HEADLOADED);
            return;
        }

        if (type <= 0x07) // Type I: restore / seek / step
        {
            switch (type)
            {
                case 0x00: _track = 0; break;                 // Restore
                case 0x01: _track = _dataReg; break;          // Seek
                case 0x02: case 0x03: break;                  // Step (hold the track)
                case 0x04: case 0x05: if (_track < 0xFF) _track++; break; // Step-in
                case 0x06: case 0x07: if (_track > 0) _track--; break;    // Step-out
            }
            // A present drive responds to seek/restore even without a disk (for BIOS detection).
            _status = (byte)((DrivePresent ? 0 : S_NOTREADY) | (_track == 0 ? S_TRACK0 : 0) | S_HEADLOADED);
            _intrq = true;
            return;
        }

        if (type == 0x08 || type == 0x09) // Read Sector (bit4 = multi-sector)
        {
            if (!Ready) { BeginFail(); return; } // no disk → "spin" and only then NOT_READY
            if (Cur!.ReadSector(_track, _side, _sector, _buf))
            {
                _bufPos = 0; _bufLen = FloppyDisk.SectorSize;
                _reading = true; _multi = (cmd & 0x10) != 0; _drq = true;
                _status = S_BUSY | S_DRQ;
            }
            else { _status = S_RNF; _intrq = true; } // sector not found
            return;
        }

        if (type == 0x0A || type == 0x0B) // Write Sector (bit4 = multi-sector)
        {
            if (!Ready) { BeginFail(); return; }
            if (Cur!.WriteProtected) { _status = S_PROTECT; _intrq = true; return; }
            _wrCyl = _track; _wrHead = _side; _wrSec = _sector;
            _bufPos = 0; _bufLen = FloppyDisk.SectorSize;
            _writing = true; _multi = (cmd & 0x10) != 0; _drq = true;
            _status = S_BUSY | S_DRQ;
            return;
        }

        if (type == 0x0C) // Read Address — returns the ID field (6 bytes: cyl, side, sector, size, CRC, CRC)
        {
            if (!Ready) { _status = S_NOTREADY; _intrq = true; return; }
            _buf[0] = _track; _buf[1] = (byte)_side; _buf[2] = _sector; _buf[3] = 0x02; // 512
            _buf[4] = 0; _buf[5] = 0;
            _sector = _track; // as in WD179x: the track number is placed into the sector register
            _bufPos = 0; _bufLen = 6; _reading = true; _drq = true;
            _status = S_BUSY | S_DRQ;
            return;
        }

        // 0x0E Read Track / 0x0F Write Track (format) — simplified: finish immediately.
        _status = (byte)(Ready ? 0 : S_NOTREADY);
        _intrq = true;
    }
}
