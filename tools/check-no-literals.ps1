# Fails (exit 1) when a XAML file under src\ carries hardcoded user-visible text instead of a
# localized {x:Static res:Strings.*} reference. Runs before every build (see the CheckNoLiterals
# target in iPrtSc.csproj) so new features cannot quietly reintroduce English literals.
#
# Scanned: Text= / Content= / ToolTip= / Header= / Title= attribute values, and text placed
# directly between tags. Anything without a letter ("…", "#000000") is ignored, as are the few
# deliberate non-translatable values in $Allowed.

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# Values that are the same in every language (product name, format names, e-mail address, glyph).
$Allowed = @('iPrtSc', 'PNG', 'JPEG', 'A', 'code.ihor.k@gmail.com')

$attr = [regex]'(?<name>\b(?:Text|Content|ToolTip|Header|Title)|\bAutomationProperties\.Name)="(?<value>[^"]*)"'
$inner = [regex]'>(?<value>[^<>{}]+)<'

$hits = New-Object System.Collections.Generic.List[string]

Get-ChildItem -Path (Join-Path $repoRoot 'src') -Recurse -Filter *.xaml |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object {
        $file = $_
        # Blank out comments but keep newlines, so reported line numbers stay right.
        $text = [regex]::Replace([System.IO.File]::ReadAllText($file.FullName), '<!--.*?-->',
            { param($m) [regex]::Replace($m.Value, '[^\r\n]', ' ') }, 'Singleline')

        $report = {
            param($m, $label)
            $v = $m.Groups['value'].Value.Trim()
            if ($v -notmatch '[\p{L}]') { return }
            if ($v.StartsWith('{')) { return }
            if ($Allowed -contains $v) { return }
            $line = ($text.Substring(0, $m.Index) -split "`n").Count
            $rel = $file.FullName.Substring($repoRoot.Length + 1)
            $hits.Add("${rel}:${line}: $label '$v'")
        }

        foreach ($m in $attr.Matches($text))  { & $report $m $m.Groups['name'].Value }
        foreach ($m in $inner.Matches($text)) { & $report $m 'text' }
    }

if ($hits.Count -gt 0) {
    Write-Host "Hardcoded UI text found. Move it to Resources\Strings.resx and bind with {x:Static res:Strings.Key}:" -ForegroundColor Red
    $hits | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
Write-Host 'check-no-literals: OK'
