# Prints the CHANGELOG.md section of a version, for the notes of its GitHub release.
# Usage: pwsh .github/release-notes.ps1 -Version 1.5.0
param([Parameter(Mandatory)] [string]$Version)

$lines = Get-Content (Join-Path $PSScriptRoot '..' 'CHANGELOG.md') -Encoding utf8
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^## \[?$([regex]::Escape($Version))\]?( |$)") { $start = $i + 1; break }
}
if ($start -lt 0) { throw "Version $Version not found in CHANGELOG.md" }

$end = $start
while ($end -lt $lines.Count -and $lines[$end] -notmatch '^## ' -and $lines[$end] -notmatch '^\[[^\]]+\]: ') { $end++ }

($lines[$start..($end - 1)] -join "`n").Trim()
