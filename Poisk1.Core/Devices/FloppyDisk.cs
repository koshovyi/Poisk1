namespace Poisk1.Core.Devices;

/// <summary>
/// Floppy disk image (.img/.IMG — "raw" sector dump). The geometry is determined
/// by size: 360 KB = 40 cyl × 2 sides × 9 sectors × 512; 720 KB = 80 × 2 × 9 × 512.
/// Sectors are numbered from 1 (as in CHS), sides/cylinders — from 0.
/// </summary>
public sealed class FloppyDisk
{
    public const int SectorSize = 512;

    private readonly byte[] _data;

    public int Cylinders { get; }
    public int Heads { get; }
    public int Sectors { get; }
    public bool WriteProtected { get; set; }
    public string Name { get; }

    public FloppyDisk(byte[] data, string name)
    {
        _data = data;
        Name = name;
        // 9 sectors × 512 × 2 sides = 9216 bytes/cylinder; cyl = size / 9216.
        Heads = 2; Sectors = 9;
        int perCyl = Heads * Sectors * SectorSize;
        Cylinders = perCyl > 0 ? data.Length / perCyl : 0;
        if (Cylinders == 0) Cylinders = 40; // fallback
    }

    /// <summary>Linear offset of a sector (CHS). -1 if outside the image bounds.</summary>
    private int Offset(int cyl, int head, int sector)
    {
        if (cyl < 0 || cyl >= Cylinders || head < 0 || head >= Heads || sector < 1 || sector > Sectors)
            return -1;
        int lba = (cyl * Heads + head) * Sectors + (sector - 1);
        int off = lba * SectorSize;
        return off + SectorSize <= _data.Length ? off : -1;
    }

    /// <summary>Read a sector into <paramref name="dest"/> (512 bytes). true — success.</summary>
    public bool ReadSector(int cyl, int head, int sector, byte[] dest)
    {
        int off = Offset(cyl, head, sector);
        if (off < 0) return false;
        Array.Copy(_data, off, dest, 0, SectorSize);
        return true;
    }

    /// <summary>Write a sector from <paramref name="src"/> (512 bytes). true — success.</summary>
    public bool WriteSector(int cyl, int head, int sector, byte[] src)
    {
        if (WriteProtected) return false;
        int off = Offset(cyl, head, sector);
        if (off < 0) return false;
        Array.Copy(src, 0, _data, off, SectorSize);
        return true;
    }

    /// <summary>Current image contents (for saving to disk).</summary>
    public byte[] Data => _data;
}
