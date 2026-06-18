<#
.SYNOPSIS
    Wrap a DOS .COM program into a Poisk-1 B003 ("ПЗУ-адаптер") option-ROM image.

.DESCRIPTION
    Faithful PowerShell port of Tronix's COM2ROM v1.0 (com2rom.pas). Layout:

        [ stub.bin (512 bytes) ][ raw .COM bytes ][ 0xFF padding ]

    The 512-byte stub is a standard option-ROM (55 AA signature). At boot it installs a
    dummy int 21h, copies the program to 1000:0100 and FAR-jumps there — i.e. it runs the
    .COM exactly as DOS would (program origin 0x100), which is how Poisk cassette games
    were turned into ROM cartridges.

    The image is padded to 32 KB (size byte 0x40) or 64 KB (0x80) and the final byte is set
    so the whole image sums to 0 (mod 256), as the BIOS ROM scan expects.

.EXAMPLE
    .\com2rom.ps1 -Com "TETRIS.COM" -Out "..\Data\roms\tetris.bin"

.EXAMPLE
    # batch-convert a folder of .COM files into Data/roms
    Get-ChildItem *.COM | .\com2rom.ps1 -OutDir "..\Data\roms"
#>
[CmdletBinding(DefaultParameterSetName = 'single')]
param(
    [Parameter(Mandatory, ParameterSetName = 'single', Position = 0)]
    [string]$Com,
    [Parameter(Mandatory, ParameterSetName = 'single', Position = 1)]
    [string]$Out,

    [Parameter(Mandatory, ParameterSetName = 'batch', ValueFromPipeline)]
    [System.IO.FileInfo]$InputFile,
    [Parameter(Mandatory, ParameterSetName = 'batch')]
    [string]$OutDir,

    # Load address baked into the stub copy/jmp; defaults match the stub (1000:0100).
    [int]$Segment = 0x1000,
    [int]$Offset  = 0x0100,
    [string]$Stub = (Join-Path $PSScriptRoot 'stub.bin')
)

begin {
    # Patch positions inside stub.bin (from com2rom.pas constants).
    $POS_JMP_OFF = 0x1E8; $POS_JMP_SEG = 0x1EA
    $POS_CPY_OFF = 0x23;  $POS_CPY_SEG = 0x1B

    $stubBytes = [System.IO.File]::ReadAllBytes($Stub)
    if ($stubBytes.Length -ne 512) { throw "stub.bin must be 512 bytes (got $($stubBytes.Length))" }

    # Wrap raw bytes as a single all-literals ("stored") LZ4 block. This is valid LZ4 that
    # the stub's decompressor decodes byte-for-byte — no compression needed for emulation.
    function Build-Lz4Stored([byte[]]$data) {
        $L = $data.Length
        $block = New-Object System.Collections.Generic.List[byte]
        if ($L -lt 15) {
            $block.Add([byte]($L -shl 4))                 # token: high nibble = literal len
        } else {
            $block.Add([byte]0xF0)                        # token: 15 → length continues in ext bytes
            $r = $L - 15
            while ($r -ge 255) { $block.Add([byte]255); $r -= 255 }
            $block.Add([byte]$r)
        }
        $block.AddRange($data)                            # the literals themselves
        return , $block.ToArray()
    }

    function Convert-One([string]$comPath, [string]$outPath) {
        $com = [System.IO.File]::ReadAllBytes($comPath)
        $block = Build-Lz4Stored $com
        # Payload the stub expects at offset 0x200: magic(4) + chunkSize(4, LE) + LZ4 block.
        $magic = [byte[]](0x02, 0x21, 0x4C, 0x18)         # ignored by the decompressor
        $chunk = $block.Length
        $payload = New-Object byte[] (8 + $chunk)
        [Array]::Copy($magic, 0, $payload, 0, 4)
        $payload[4] = $chunk -band 0xFF; $payload[5] = ($chunk -shr 8) -band 0xFF
        $payload[6] = ($chunk -shr 16) -band 0xFF; $payload[7] = ($chunk -shr 24) -band 0xFF
        if ($payload[6] -ne 0 -or $payload[7] -ne 0) { throw "$comPath : LZ4 chunk exceeds 64 KB" }
        [Array]::Copy($block, 0, $payload, 8, $block.Length)     # the LZ4 block itself

        $total = $stubBytes.Length + $payload.Length
        if ($total -le 32768) { $size = 32768; $sizeByte = 0x40 }
        elseif ($total -le 65536) { $size = 65536; $sizeByte = 0x80 }
        else { throw "$comPath : program too large for a 64 KB ROM ($total bytes)" }

        $buf = New-Object byte[] $size
        for ($i = 0; $i -lt $size; $i++) { $buf[$i] = 0xFF }     # pad with 0xFF
        [Array]::Copy($stubBytes, 0, $buf, 0, $stubBytes.Length)
        [Array]::Copy($payload, 0, $buf, $stubBytes.Length, $payload.Length)

        $buf[2] = $sizeByte
        # Patch load address (no-op for the default 1000:0100, but honors -Segment/-Offset).
        $buf[$POS_JMP_OFF] = $Offset -band 0xFF;  $buf[$POS_JMP_OFF + 1] = ($Offset -shr 8) -band 0xFF
        $buf[$POS_JMP_SEG] = $Segment -band 0xFF; $buf[$POS_JMP_SEG + 1] = ($Segment -shr 8) -band 0xFF
        $buf[$POS_CPY_OFF] = $Offset -band 0xFF;  $buf[$POS_CPY_OFF + 1] = ($Offset -shr 8) -band 0xFF
        $buf[$POS_CPY_SEG] = $Segment -band 0xFF; $buf[$POS_CPY_SEG + 1] = ($Segment -shr 8) -band 0xFF

        # Checksum: make the whole image sum to 0 (mod 256).
        $crc = 0
        for ($i = 0; $i -le $size - 2; $i++) { $crc = ($crc + $buf[$i]) -band 0xFF }
        $buf[$size - 1] = (256 - $crc) -band 0xFF

        [System.IO.File]::WriteAllBytes($outPath, $buf)
        "{0,-16} {1,6} B  -> {2,5} KB ROM  {3}" -f (Split-Path $comPath -Leaf), $com.Length, ($size / 1024), (Split-Path $outPath -Leaf)
    }
}

process {
    if ($PSCmdlet.ParameterSetName -eq 'batch') {
        if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
        $name = [System.IO.Path]::GetFileNameWithoutExtension($InputFile.Name).ToLowerInvariant()
        Convert-One $InputFile.FullName (Join-Path $OutDir "$name.bin")
    }
}

end {
    if ($PSCmdlet.ParameterSetName -eq 'single') {
        $dir = Split-Path $Out -Parent
        if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
        Convert-One $Com $Out
    }
}
