namespace Poisk1.Core.Devices;

/// <summary>
/// A flat hard-disk image (512-byte sectors) with fixed CHS geometry, backed by a file.
/// The whole image is held in memory; <see cref="Flush"/> writes changes back to disk.
/// </summary>
public sealed class HardDisk
{
    public const int SectorSize = 512;

    public int Cylinders { get; }
    public int Heads { get; }
    public int Sectors { get; }      // sectors per track (1-based numbering in CHS)
    public string Name { get; }
    public string? Path { get; }

    private readonly byte[] _data;
    private bool _dirty;

    public HardDisk(byte[] data, int cylinders, int heads, int sectors, string name, string? path = null)
    {
        Cylinders = cylinders; Heads = heads; Sectors = sectors;
        Name = name; Path = path;
        long need = (long)cylinders * heads * sectors * SectorSize;
        // Pad/truncate to the geometry so out-of-range LBAs never fault.
        _data = new byte[need];
        Array.Copy(data, _data, Math.Min(data.Length, need));
    }

    public int TotalSectors => Cylinders * Heads * Sectors;
    public long SizeBytes => (long)TotalSectors * SectorSize;

    /// <summary>CHS → LBA. Sector is 1-based (as the INT 13h / WD2010 sector register).</summary>
    public int Chs2Lba(int cyl, int head, int sector)
        => (cyl * Heads + head) * Sectors + (sector - 1);

    public bool ReadSector(int lba, byte[] dest, int destOffset)
    {
        if (lba < 0 || lba >= TotalSectors) return false;
        Array.Copy(_data, (long)lba * SectorSize, dest, destOffset, SectorSize);
        return true;
    }

    public bool WriteSector(int lba, byte[] src, int srcOffset)
    {
        if (lba < 0 || lba >= TotalSectors) return false;
        Array.Copy(src, srcOffset, _data, (long)lba * SectorSize, SectorSize);
        _dirty = true;
        return true;
    }

    /// <summary>Persist pending writes back to <see cref="Path"/> (no-op if unchanged or pathless).</summary>
    public void Flush()
    {
        if (!_dirty || Path is null) return;
        File.WriteAllBytes(Path, _data);
        _dirty = false;
    }
}
