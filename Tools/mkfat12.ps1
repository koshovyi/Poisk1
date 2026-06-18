<#
.SYNOPSIS
    Build a 720 KB FAT12 floppy image (DOS-readable data disk) from a list of files.

.DESCRIPTION
    Creates a standard 3.5" 720 KB DD image (80 cyl x 2 heads x 9 sec, 1024-byte clusters)
    with a valid BPB so MS-DOS reads it as a data disk. Not bootable — mount it as a second
    drive and access it from a booted DOS system. 8.3 names only; files are stored contiguously.

.EXAMPLE
    .\mkfat12.ps1 -Out ..\Data\disk\basic.img -Files BAS.COM,DEMO.BAS -Label BASIC
#>
param(
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string[]]$Files,
    [string]$Label = "POISK"
)

# --- 720 KB geometry / BPB ---
$bps = 512; $spc = 2; $rsvd = 1; $nFat = 2; $rootEnt = 112
$totSec = 1440; $media = 0xF9; $spf = 3; $spt = 9; $heads = 2
$clusterBytes = $bps * $spc                                   # 1024
$img = New-Object byte[] ($totSec * $bps)                     # 737280, zero-filled

# Boot sector / BPB
$img[0] = 0xEB; $img[1] = 0x3C; $img[2] = 0x90
$oem = [Text.Encoding]::ASCII.GetBytes("MSDOS5.0"); [Array]::Copy($oem, 0, $img, 3, 8)
function W16($off, $v) { $script:img[$off] = $v -band 0xFF; $script:img[$off + 1] = ($v -shr 8) -band 0xFF }
W16 11 $bps; $img[13] = $spc; W16 14 $rsvd; $img[16] = $nFat; W16 17 $rootEnt
W16 19 $totSec; $img[21] = $media; W16 22 $spf; W16 24 $spt; W16 26 $heads
$img[38] = 0x29                                               # extended boot signature
W16 510 0xAA55 | Out-Null; $img[510] = 0x55; $img[511] = 0xAA

$fatStart  = $rsvd * $bps
$rootStart = ($rsvd + $nFat * $spf) * $bps
$dataStart = ($rsvd + $nFat * $spf + [int][math]::Ceiling($rootEnt * 32 / $bps)) * $bps
$totalClusters = [int][math]::Floor(($img.Length - $dataStart) / $clusterBytes)

# FAT (one copy, packed 12-bit; mirrored to the second copy at the end)
$fat = New-Object byte[] ($spf * $bps)
$fat[0] = $media; $fat[1] = 0xFF; $fat[2] = 0xFF                # reserved entries 0,1
function Set-Fat12($cluster, $value) {
    $o = [int]([math]::Floor($cluster * 3 / 2))
    if ($cluster -band 1) {
        $script:fat[$o] = ($script:fat[$o] -band 0x0F) -bor (($value -shl 4) -band 0xF0)
        $script:fat[$o + 1] = ($value -shr 4) -band 0xFF
    } else {
        $script:fat[$o] = $value -band 0xFF
        $script:fat[$o + 1] = ($script:fat[$o + 1] -band 0xF0) -bor (($value -shr 8) -band 0x0F)
    }
}

$nextCluster = 2
$dirIndex = 0
foreach ($path in $Files) {
    if (-not (Test-Path $path)) { throw "file not found: $path" }
    $data = [IO.File]::ReadAllBytes($path)
    $name = [IO.Path]::GetFileNameWithoutExtension($path).ToUpperInvariant()
    $ext  = [IO.Path]::GetExtension($path).TrimStart('.').ToUpperInvariant()
    if ($name.Length -gt 8 -or $ext.Length -gt 3) { throw "name not 8.3: $path" }

    $clusters = [int][math]::Ceiling($data.Length / $clusterBytes)
    if ($clusters -eq 0) { $clusters = 1 }
    $first = $nextCluster
    if ($first + $clusters - 1 -ge $totalClusters + 2) { throw "disk full adding $path" }

    # write data + chain FAT
    for ($k = 0; $k -lt $clusters; $k++) {
        $c = $first + $k
        $srcOff = $k * $clusterBytes
        $len = [math]::Min($clusterBytes, $data.Length - $srcOff)
        [Array]::Copy($data, $srcOff, $img, $dataStart + ($c - 2) * $clusterBytes, $len)
        Set-Fat12 $c ($(if ($k -eq $clusters - 1) { 0xFFF } else { $c + 1 }))
    }
    $nextCluster += $clusters

    # root directory entry
    $de = $rootStart + $dirIndex * 32
    $nm = $name.PadRight(8); for ($i = 0; $i -lt 8; $i++) { $img[$de + $i] = [byte][char]$nm[$i] }
    $ex = $ext.PadRight(3);  for ($i = 0; $i -lt 3; $i++) { $img[$de + 8 + $i] = [byte][char]$ex[$i] }
    $img[$de + 11] = 0x20                                       # archive
    W16 ($de + 14) 0x6000; W16 ($de + 16) 0x4F21                # fixed time/date (1979-ish)
    W16 ($de + 22) 0x6000; W16 ($de + 24) 0x4F21
    W16 ($de + 26) $first                                       # first cluster
    $img[$de + 28] = $data.Length -band 0xFF
    $img[$de + 29] = ($data.Length -shr 8) -band 0xFF
    $img[$de + 30] = ($data.Length -shr 16) -band 0xFF
    $img[$de + 31] = ($data.Length -shr 24) -band 0xFF
    $dirIndex++
    "  {0,-12} {1,7} B  -> {2} cluster(s) from #{3}" -f ($name + '.' + $ext), $data.Length, $clusters, $first
}

# write both FAT copies
[Array]::Copy($fat, 0, $img, $fatStart, $fat.Length)
[Array]::Copy($fat, 0, $img, $fatStart + $spf * $bps, $fat.Length)

$dir = Split-Path $Out -Parent
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[IO.File]::WriteAllBytes($Out, $img)
"Wrote $Out ($($img.Length) bytes, 720 KB FAT12)"
