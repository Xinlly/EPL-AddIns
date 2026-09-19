# build-probe.ps1
# Compile the standalone TBE thread probe add-in (read-only diagnostic).
# It does NOT touch the product csproj / slnx and is not part of any build.
#
# Usage (Windows PowerShell 5.1, from repo root or from scripts\probe):
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File build-probe.ps1
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File build-probe.ps1 -EplanBin "C:\Program Files\EPLAN\Platform\2.9.4\Bin"
#
# Output: scripts\probe\bin\EA.EplAddIn.TbeThreadProbe.dll
#
# After build:
#   1) Start EPLAN, open a project with a large number of texts.
#   2) Add-in manager -> register/load EA.EplAddIn.TbeThreadProbe.dll
#      (name follows the <Company>.EplAddIn.* convention so the manager
#      recognizes it; unregister and delete the file when the probe is done).
#   3) Select 5000-10000 texts in the GED, click the menu
#      "TBE Thread Probe" (added to the Tools/Utilities menu), press Run.
#   4) While the background rounds run, click EPLAN menus / switch pages.
#   5) Report the on-screen verdict and %TEMP%\TbeThreadProbe.log back.
#
# PURE ASCII (PS 5.1 parses a no-BOM file as ANSI).

param(
    [string]$EplanBin = ""
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $scriptDir)
$src = Join-Path $scriptDir "TbeThreadProbeAddIn.cs"
$outDir = Join-Path $scriptDir "bin"
$outDll = Join-Path $outDir "EA.EplAddIn.TbeThreadProbe.dll"

# --- 1) locate EplApi reference DLLs -------------------------------------
$refDirs = @()
if ($EplanBin -ne "") { $refDirs += $EplanBin }
$refDirs += (Join-Path $repoRoot "references\EplApi")
if (Test-Path "C:\Program Files\EPLAN") {
    $refDirs += (Get-ChildItem "C:\Program Files\EPLAN" -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Get-ChildItem $_.FullName -Directory -Filter "Bin" -ErrorAction SilentlyContinue } |
        ForEach-Object { $_.FullName })
}

$refDir = $null
foreach ($d in $refDirs) {
    if ($d -and (Test-Path (Join-Path $d "Eplan.EplApi.DataModelu.dll"))) { $refDir = $d; break }
}
if (-not $refDir) {
    throw "EplApi DLLs not found. Pass -EplanBin or copy them to references\EplApi. Looked in: $($refDirs -join ' ; ')"
}
Write-Host "Using EplApi references from: $refDir"

$needed = @(
    "Eplan.EplApi.AFu.dll",
    "Eplan.EplApi.Baseu.dll",
    "Eplan.EplApi.DataModelu.dll",
    "Eplan.EplApi.Guiu.dll",
    "Eplan.EplApi.HEServicesu.dll"
)
foreach ($n in $needed) {
    if (-not (Test-Path (Join-Path $refDir $n))) { throw "Missing reference: $n in $refDir" }
}

# --- 2) locate a Roslyn csc (prefer MSBuild/dotnet, fall back to FX csc) -
$cscCandidates = @()
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsRoot = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
    if ($vsRoot) { $cscCandidates += (Join-Path $vsRoot "MSBuild\Current\Bin\Roslyn\csc.exe") }
}
$cscCandidates += "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$csc = $null
foreach ($c in $cscCandidates) { if ($c -and (Test-Path $c)) { $csc = $c; break } }
if (-not $csc) { throw "No csc.exe found (install .NET SDK/VS, or use Framework64 csc)." }
Write-Host "Using compiler: $csc"

# --- 3) compile -----------------------------------------------------------
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$asmRefs = @()
foreach ($n in $needed) { $asmRefs += "/r:`"$(Join-Path $refDir $n)`"" }
$fxDir = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2"
if (Test-Path $fxDir) {
    foreach ($a in @("mscorlib.dll", "System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll")) {
        $p = Join-Path $fxDir $a
        if (Test-Path $p) { $asmRefs += "/r:`"$p`"" }
    }
} else {
    foreach ($a in @("System.dll", "System.Core.dll", "System.Drawing.dll", "System.Windows.Forms.dll")) {
        $p = Join-Path "C:\Windows\Microsoft.NET\Framework64\v4.0.30319" $a
        if (Test-Path $p) { $asmRefs += "/r:`"$p`"" }
    }
}

$cscArgs = @(
    "/nologo", "/target:library", "/platform:x64",
    "/out:`"$outDll`""
)
$cscArgs += $asmRefs
$cscArgs += "`"$src`""

Write-Host ("csc args: " + ($cscArgs -join " "))
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$hash = (Get-FileHash $outDll -Algorithm SHA256).Hash
Write-Host ""
Write-Host "BUILD OK: $outDll"
Write-Host "SHA256  : $hash"
Write-Host ""
Write-Host "Next: register this DLL in the EPLAN add-in manager, restart if prompted,"
Write-Host "select 5000-10000 texts, run menu 'TBE Thread Probe', then send:"
Write-Host "  - the window verdict"
Write-Host "  - $env:TEMP\TbeThreadProbe.log"
