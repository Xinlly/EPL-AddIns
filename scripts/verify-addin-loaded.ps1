# verify-addin-loaded.ps1
# Confirm the shadow-copied add-in DLL loaded by EPLAN matches the fresh build.
# Uses SHA-256: different builds can coincidentally have the same byte size, so
# never trust size/mtime alone. Prints SC ... match=True/False; exit 1 on mismatch.
# Pure ASCII on purpose (PowerShell 5.1 parses no-BOM files as ANSI).
#
# Usage:
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts\verify-addin-loaded.ps1
#   ... -SourceDll 'D:\...\xxx.dll' -AddInName 'xxx.dll'
param(
    [string]$SourceDll = 'D:\Users\Admin0\source\repos\EPL-AddIns\EA.EplAddIn.TextBatchEdit\bin\Debug\net472\EA.EplAddIn.TextBatchEdit.dll',
    [string]$AddInName = 'EA.EplAddIn.TextBatchEdit.dll',
    [int]$WaitSec = 70
)
$ErrorActionPreference = 'Continue'
$sc = Join-Path $env:APPDATA 'EPLAN\ShadowCopyAssemblies'
$srcHash = (Get-FileHash $SourceDll).Hash
$srcItem = Get-Item $SourceDll
Write-Output ("SRC {0} {1}" -f $srcItem.Length, $srcItem.LastWriteTime.ToString('HH:mm:ss'))
$ok = $false
for ($i=0; $i -lt $WaitSec; $i++) {
    $hit = Get-ChildItem $sc -Recurse -Filter $AddInName -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($hit) {
        $same = ((Get-FileHash $hit.FullName).Hash -eq $srcHash)
        Write-Output ("SC  {0} {1} piddir={2} match={3}" -f $hit.Length, $hit.LastWriteTime.ToString('HH:mm:ss'),
            ($hit.FullName -replace [regex]::Escape($sc + '\'), ''), $same)
        $ok = $same
        break
    }
    Start-Sleep -Seconds 1
}
if (-not $ok) { Write-Output "LOADED=NO"; exit 1 }
Write-Output "LOADED=YES"
