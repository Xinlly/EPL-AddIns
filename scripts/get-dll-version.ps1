param(
  [Parameter(Mandatory=$true)]
  [string]$Path
)

$ErrorActionPreference = 'Stop'
$assemblyVersion = [System.Reflection.AssemblyName]::GetAssemblyName($Path).Version.ToString()
$info = (Get-Item -LiteralPath $Path).VersionInfo
Write-Output "AssemblyVersion=$assemblyVersion"
Write-Output "FileVersion=$($info.FileVersion)"
Write-Output "ProductVersion=$($info.ProductVersion)"
