# Compares the key set of every Strings.<lang>.resx against the English Strings.resx and
# reports missing (untranslated) and orphaned (no longer used) keys per locale. Also fails if
# Strings.Designer.cs is out of sync with Strings.resx (run tools\gen-strings.ps1).
#
# Missing keys are a to-do list, not a failure: the English fallback covers them, so a
# translation lag never blocks a release. Run it before every release.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $repoRoot 'src\iPrtSc\Resources'

function Get-Keys([string]$path) {
    [xml]$doc = Get-Content -LiteralPath $path -Encoding UTF8
    @($doc.root.data | ForEach-Object { $_.name })
}

$baseKeys = Get-Keys (Join-Path $dir 'Strings.resx')
$failed = $false

# Designer in sync?
$designer = Get-Content -LiteralPath (Join-Path $dir 'Strings.Designer.cs') -Raw -Encoding UTF8
$designerKeys = @([regex]::Matches($designer, 'public static string (\w+) =>') | ForEach-Object { $_.Groups[1].Value })
$stale = Compare-Object $baseKeys $designerKeys
if ($stale) {
    Write-Host 'Strings.Designer.cs is out of sync with Strings.resx - run tools\gen-strings.ps1' -ForegroundColor Red
    $failed = $true
}

Get-ChildItem -Path $dir -Filter 'Strings.*.resx' | Sort-Object Name | ForEach-Object {
    $lang = $_.BaseName.Substring('Strings.'.Length)
    $keys = Get-Keys $_.FullName
    $missing = @($baseKeys | Where-Object { $keys -notcontains $_ })
    $orphan = @($keys | Where-Object { $baseKeys -notcontains $_ })
    if ($missing.Count -eq 0 -and $orphan.Count -eq 0) {
        Write-Host "[$lang] complete ($($keys.Count) keys)" -ForegroundColor Green
        return
    }
    Write-Host "[$lang] $($missing.Count) missing, $($orphan.Count) orphaned" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host "    missing:  $_" }
    $orphan  | ForEach-Object { Write-Host "    orphaned: $_" -ForegroundColor DarkYellow }
}

if ($failed) { exit 1 }
