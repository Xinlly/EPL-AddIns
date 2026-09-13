param(
  [Parameter(Mandatory=$true)]
  [string]$PropsPath
)

$ErrorActionPreference = 'Stop'

$now = Get-Date
$buildPart = $now.ToString('yyMM')
$bucket = [int][Math]::Floor($now.Minute / 6)
$revisionPart = $now.Day * 1000 + $now.Hour * 10 + $bucket

$dir = Split-Path -Parent $PropsPath
if (-not (Test-Path -LiteralPath $dir)) {
  New-Item -ItemType Directory -Path $dir -Force | Out-Null
}

$content = @"
<Project>
  <PropertyGroup>
    <VersionBuildPart>$buildPart</VersionBuildPart>
    <VersionRevisionPart>$revisionPart</VersionRevisionPart>
  </PropertyGroup>
</Project>
"@

Set-Content -LiteralPath $PropsPath -Value $content -Encoding UTF8
