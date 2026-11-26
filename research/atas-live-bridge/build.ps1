# Baut OrderFlowLiveBridge.dll (ATAS-Indikator, nur lesend) mit Roslyn gegen die .NET-10-Laufzeit und die installierten
# ATAS-Bibliotheken – wie research/atas-recorder/build.ps1. Kopiert NICHTS nach ATAS (Installation ist ein eigener Schritt).
#   powershell -ExecutionPolicy Bypass -File build.ps1
param(
    [string]$AtasDir = 'C:\Program Files (x86)\ATAS Platform',
    [string]$DotnetRoot = 'C:\Program Files\dotnet',
    [string]$OutDir = (Join-Path $PSScriptRoot 'out')
)
$ErrorActionPreference = 'Stop'
function Latest([string]$dir, [string]$pattern) {
    $d = Get-ChildItem -Path $dir -Directory -Filter $pattern -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name -replace '-.*$', '') } | Select-Object -Last 1
    if (-not $d) { throw "Nicht gefunden: $dir\$pattern" }
    $d.FullName
}
$core = Latest (Join-Path $DotnetRoot 'shared\Microsoft.NETCore.App') '10.*'
$desktop = Latest (Join-Path $DotnetRoot 'shared\Microsoft.WindowsDesktop.App') '10.*'
$sdk = Latest (Join-Path $DotnetRoot 'sdk') '*'
$csc = Join-Path $sdk 'Roslyn\bincore\csc.dll'
if (-not (Test-Path $csc)) { throw "csc.dll nicht gefunden: $csc" }
$atas = 'ATAS.Indicators', 'ATAS.DataFeedsCore', 'ATAS.Types', 'OFT.Core', 'OFT.Rendering', 'OFT.Attributes', 'OFT.Models', 'Utils.Common' |
    ForEach-Object { Join-Path $AtasDir "$_.dll" }
foreach ($a in $atas) { if (-not (Test-Path $a)) { throw "ATAS-Bibliothek fehlt: $a" } }
$refs = @()
foreach ($f in (Get-ChildItem $core, $desktop -Filter *.dll)) {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($f.FullName); $refs += $f.FullName } catch { }
}
$refs += $atas
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$out = Join-Path $OutDir 'OrderFlowLiveBridge.dll'
$rsp = Join-Path $OutDir 'build.rsp'
$lines = @('-nologo', '-target:library', '-deterministic', '-optimize+', "-out:`"$out`"") + ($refs | ForEach-Object { "-r:`"$_`"" }) +
         @("`"$(Join-Path $PSScriptRoot 'OrderFlowLiveBridge.cs')`"")
Set-Content -Path $rsp -Value $lines -Encoding ASCII
& (Join-Path $DotnetRoot 'dotnet.exe') $csc "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen ($LASTEXITCODE)" }
$h = (Get-FileHash $out -Algorithm SHA256).Hash
Write-Host "OK: $out  SHA-256 $h"
