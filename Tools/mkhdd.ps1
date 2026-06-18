<#
.SYNOPSIS
    Build a partitioned FAT16 hard-disk image for the Poisk-1 B942 controller and populate it
    with files copied from a FAT12 floppy image (e.g. the MS-DOS 5.0 + Norton Commander disk).

.DESCRIPTION
    Produces a raw image with CHS geometry <Cyl>x<Heads>x17 (512-byte sectors): an MBR with a
    single active FAT16 partition starting at LBA 17 (cylinder 0, head 1), a FAT16 BPB, two FATs,
    a root directory and the file data. 8.3 names, files stored contiguously.

    The image is a DATA disk (no boot code). To boot from it: start the emulator with a DOS
    floppy in A: and this image as the hard disk, then run `SYS C:` to transfer the system.

.EXAMPLE
    .\mkhdd.ps1 -Out ..\Data\hdd_disk\dos_nc.img -From ..\Data\disk\poisk720.img -Cyl 602
#>
param(
    [Parameter(Mandatory)][string]$Out,
    [Parameter(Mandatory)][string]$From,   # FAT12 floppy image to copy files from
    [int]$Cyl = 602, [int]$Heads = 4, [int]$Spt = 17,
    # which files to copy from the source floppy (8.3, case-insensitive); default = a DOS+NC set
    [string[]]$Files = @('COMMAND.COM','NC.EXT','NC.INI','NC.MNU','NCMAIN.EXE','NCS.EXE','MEM.EXE')
)

$bps = 512

# ---------- read the source FAT12 floppy ----------
$src = [IO.File]::ReadAllBytes($From)
function R16($b,$o){ $b[$o] + $b[$o+1]*256 }
$f_bps=R16 $src 11; $f_spc=$src[13]; $f_rsvd=R16 $src 14; $f_nfat=$src[16]
$f_rootEnt=R16 $src 17; $f_spf=R16 $src 22
$f_rootStart=($f_rsvd+$f_nfat*$f_spf)*$f_bps
$f_dataStart=($f_rsvd+$f_nfat*$f_spf+[int][math]::Ceiling($f_rootEnt*32/$f_bps))*$f_bps
$f_fatStart=$f_rsvd*$f_bps
function F12($cl){ $o=$f_fatStart+[int][math]::Floor($cl*3/2); $v=$src[$o]+$src[$o+1]*256; if($cl -band 1){$v -shr 4}else{$v -band 0xFFF} }
function ReadFile12($first,$size){
    $out=New-Object byte[] $size; $pos=0; $c=$first; $cb=$f_bps*$f_spc
    while($c -ge 2 -and $c -lt 0xFF8 -and $pos -lt $size){
        $len=[math]::Min($cb,$size-$pos)
        [Array]::Copy($src,$f_dataStart+($c-2)*$cb,$out,$pos,$len); $pos+=$len; $c=F12 $c
    }
    return ,$out
}
$want = @{}; foreach($f in $Files){ $want[$f.ToUpperInvariant()]=$true }
$picked = @()  # list of @{Name;Ext;Data}
for($i=0;$i -lt $f_rootEnt;$i++){
    $o=$f_rootStart+$i*32; if($src[$o] -eq 0){break}; if($src[$o] -eq 0xE5){continue}
    $attr=$src[$o+11]; if(($attr -band 0x08) -ne 0){continue}
    $nm=(-join ($src[$o..($o+7)]|%{[char]$_})).Trim(); $ex=(-join ($src[($o+8)..($o+10)]|%{[char]$_})).Trim()
    $full = if($ex){"$nm.$ex"}else{$nm}
    if(-not $want.ContainsKey($full.ToUpperInvariant())){continue}
    $first=R16 $src ($o+26); $sz=$src[$o+28]+$src[$o+29]*256+$src[$o+30]*65536
    $picked += @{ Name=$nm; Ext=$ex; Data=(ReadFile12 $first $sz) }
    "  copied {0,-12} {1,7} B" -f $full,$sz
}
if($picked.Count -eq 0){ throw "no requested files found on $From" }

# ---------- build the FAT16 image ----------
$totSec = $Cyl*$Heads*$Spt
$img = New-Object byte[] ($totSec*$bps)

# partition geometry: starts at cyl0/head1/sec1 = LBA 17
$partStart = $Spt                      # 17
$partSec   = $totSec - $partStart
$spc = 4                                # 2 KB clusters
$rsvd=1; $nfat=2; $rootEnt=512; $media=0xF8
$rootSecs=[int][math]::Ceiling($rootEnt*32/$bps)            # 32
# solve secPerFat so it covers the cluster count
$spf=1
for($k=0;$k -lt 8;$k++){
    $dataSec=$partSec-$rsvd-$nfat*$spf-$rootSecs
    $clusters=[int][math]::Floor($dataSec/$spc)
    $spf=[int][math]::Ceiling(($clusters+2)*2/$bps)
}
$dataSec=$partSec-$rsvd-$nfat*$spf-$rootSecs
$clusters=[int][math]::Floor($dataSec/$spc)

function PutMBR {
    # partition entry @ 0x1BE
    $e=0x1BE
    $img[$e]=0x80                                   # active
    $img[$e+1]=1; $img[$e+2]=1; $img[$e+3]=0        # CHS first: head1 sec1 cyl0
    $img[$e+4]=0x04                                 # type FAT16 <32MB
    $lc=$Cyl-1; $lh=$Heads-1; $ls=$Spt
    $img[$e+5]=$lh
    $img[$e+6]=[byte](($ls -band 0x3F) -bor ((($lc -shr 8) -band 3) -shl 6))
    $img[$e+7]=[byte]($lc -band 0xFF)
    $img[$e+8]=$partStart -band 0xFF; $img[$e+9]=($partStart -shr 8) -band 0xFF
    $img[$e+10]=($partStart -shr 16) -band 0xFF; $img[$e+11]=($partStart -shr 24) -band 0xFF
    $img[$e+12]=$partSec -band 0xFF; $img[$e+13]=($partSec -shr 8) -band 0xFF
    $img[$e+14]=($partSec -shr 16) -band 0xFF; $img[$e+15]=($partSec -shr 24) -band 0xFF
    $img[510]=0x55; $img[511]=0xAA
}

$pbase=$partStart*$bps                              # partition boot sector offset
function W16p($off,$v){ $script:img[$pbase+$off]=$v -band 0xFF; $script:img[$pbase+$off+1]=($v -shr 8) -band 0xFF }
function PutBPB {
    $img[$pbase]=0xEB; $img[$pbase+1]=0x3C; $img[$pbase+2]=0x90
    $oem=[Text.Encoding]::ASCII.GetBytes("MSDOS5.0"); [Array]::Copy($oem,0,$img,$pbase+3,8)
    W16p 11 $bps; $img[$pbase+13]=$spc; W16p 14 $rsvd; $img[$pbase+16]=$nfat; W16p 17 $rootEnt
    if($partSec -lt 65536){ W16p 19 $partSec } else { W16p 19 0 }
    $img[$pbase+21]=$media; W16p 22 $spf; W16p 24 $Spt; W16p 26 $Heads
    # hidden sectors (32-bit) = partStart
    $img[$pbase+28]=$partStart -band 0xFF; $img[$pbase+29]=($partStart -shr 8) -band 0xFF
    $img[$pbase+30]=0; $img[$pbase+31]=0
    if($partSec -ge 65536){ $img[$pbase+32]=$partSec -band 0xFF; $img[$pbase+33]=($partSec -shr 8) -band 0xFF; $img[$pbase+34]=($partSec -shr 16) -band 0xFF }
    $img[$pbase+36]=0x80                              # drive 0x80
    $img[$pbase+38]=0x29                              # ext boot sig
    $vol=[Text.Encoding]::ASCII.GetBytes("POISK_HDD  "); [Array]::Copy($vol,0,$img,$pbase+43,11)
    $fst=[Text.Encoding]::ASCII.GetBytes("FAT16   "); [Array]::Copy($fst,0,$img,$pbase+54,8)
    $img[$pbase+510]=0x55; $img[$pbase+511]=0xAA
}

$fat1=$pbase+$rsvd*$bps
$rootOff=$pbase+($rsvd+$nfat*$spf)*$bps
$dataOff=$rootOff+$rootSecs*$bps
$cb=$spc*$bps
function SetFat16($cl,$val){
    $o=$fat1+$cl*2
    $script:img[$o]=$val -band 0xFF; $script:img[$o+1]=($val -shr 8) -band 0xFF
}

PutMBR; PutBPB
SetFat16 0 (0xFF00 -bor $media); SetFat16 1 0xFFFF

$nextCluster=2; $dirIdx=0
foreach($p in $picked){
    $data=$p.Data; $need=[int][math]::Ceiling($data.Length/$cb); if($need -eq 0){$need=1}
    $first=$nextCluster
    for($k=0;$k -lt $need;$k++){
        $cl=$first+$k
        $srcOff=$k*$cb; $len=[math]::Min($cb,$data.Length-$srcOff)
        [Array]::Copy($data,$srcOff,$img,$dataOff+($cl-2)*$cb,$len)
        SetFat16 $cl ($(if($k -eq $need-1){0xFFFF}else{$cl+1}))
    }
    $nextCluster+=$need
    $de=$rootOff+$dirIdx*32
    $nm=$p.Name.PadRight(8); for($i=0;$i -lt 8;$i++){$img[$de+$i]=[byte][char]$nm[$i]}
    $ex=$p.Ext.PadRight(3);  for($i=0;$i -lt 3;$i++){$img[$de+8+$i]=[byte][char]$ex[$i]}
    $img[$de+11]=0x20
    $img[$de+26]=$first -band 0xFF; $img[$de+27]=($first -shr 8) -band 0xFF
    $img[$de+28]=$data.Length -band 0xFF; $img[$de+29]=($data.Length -shr 8) -band 0xFF
    $img[$de+30]=($data.Length -shr 16) -band 0xFF; $img[$de+31]=($data.Length -shr 24) -band 0xFF
    $dirIdx++
}
# mirror FAT
[Array]::Copy($img,$fat1,$img,$fat1+$spf*$bps,$spf*$bps)

$dir=Split-Path $Out -Parent; if($dir -and -not(Test-Path $dir)){ New-Item -ItemType Directory -Force -Path $dir | Out-Null }
[IO.File]::WriteAllBytes($Out,$img)
"Wrote $Out  geom ${Cyl}x${Heads}x${Spt} = $([int]($totSec*$bps/1MB)) MB; FAT16 spf=$spf clusters=$clusters; files=$($picked.Count)"
