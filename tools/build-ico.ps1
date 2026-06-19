# Builds a multi-resolution app.ico from PNG sources (PNG-compressed icon entries).
# Usage: powershell -ExecutionPolicy Bypass -File tools\build-ico.ps1
$ErrorActionPreference = 'Stop'

$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\iPrtSc\Assets'))
$out    = Join-Path $assets 'app.ico'

# size -> file name (must exist in Assets)
$sources = [ordered]@{
    16  = 'icon-16.png'
    32  = 'icon-32.png'
    48  = 'icon-48.png'
    256 = 'icon-256.png'
}

$entries = foreach ($kv in $sources.GetEnumerator()) {
    $path = Join-Path $assets $kv.Value
    if (-not (Test-Path $path)) { throw "Missing: $path" }
    [pscustomobject]@{ Size = [int]$kv.Key; Bytes = [IO.File]::ReadAllBytes($path) }
}

$ms = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter($ms)

# ICONDIR header
$bw.Write([uint16]0)              # reserved
$bw.Write([uint16]1)              # type = icon
$bw.Write([uint16]$entries.Count) # image count

# Directory entries are 6 + 16*count bytes; image data follows.
$offset = 6 + (16 * $entries.Count)
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }  # 0 means 256
    $bw.Write([byte]$dim)         # width
    $bw.Write([byte]$dim)         # height
    $bw.Write([byte]0)            # palette
    $bw.Write([byte]0)            # reserved
    $bw.Write([uint16]1)          # color planes
    $bw.Write([uint16]32)         # bits per pixel
    $bw.Write([uint32]$e.Bytes.Length)
    $bw.Write([uint32]$offset)
    $offset += $e.Bytes.Length
}
foreach ($e in $entries) { $bw.Write($e.Bytes) }

$bw.Flush()
[IO.File]::WriteAllBytes($out, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()

Write-Host "Created $out ($([IO.File]::ReadAllBytes($out).Length) bytes, $($entries.Count) sizes)"
