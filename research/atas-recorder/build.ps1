# Baut OrderFlowReplayRecorder.dll (ATAS-Indikator) ohne .NET-10-SDK: Roslyn aus einem installierten .NET-SDK
# kompiliert direkt gegen die .NET-10-Laufzeit und die ATAS-Bibliotheken. Kopiert NICHTS nach ATAS.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1                 # aktueller Quellstand (proto-4-recheck)
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Variant proto2 # Sicherung proto-2-bufferfix
param(
    [ValidateSet('current', 'proto2')] [string]$Variant = 'current',
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

$atas = 'ATAS.Indicators', 'ATAS.DataFeedsCore', 'ATAS.Types', 'OFT.Core', 'OFT.Rendering', 'OFT.Attributes',
        'OFT.Models', 'Utils.Common' | ForEach-Object { Join-Path $AtasDir "$_.dll" }
foreach ($a in $atas) { if (-not (Test-Path $a)) { throw "ATAS-Bibliothek fehlt: $a" } }

# Nur verwaltete Assemblies referenzieren (die Laufzeitordner enthalten auch native DLLs).
$refs = @()
foreach ($f in (Get-ChildItem $core, $desktop -Filter *.dll)) {
    try { [void][Reflection.AssemblyName]::GetAssemblyName($f.FullName); $refs += $f.FullName } catch { }
}
$refs += $atas

if ($Variant -eq 'proto2') {
    $sources = @(Join-Path $PSScriptRoot 'proto-2-bufferfix-sicherung\OrderFlowReplayRecorder.cs')
    $extra = @()
} else {
    $coreDir = Join-Path $PSScriptRoot '..\recorder-core'
    $sources = @((Join-Path $PSScriptRoot 'OrderFlowReplayRecorder.cs'),
                 (Join-Path $coreDir 'RecorderCore.cs'), (Join-Path $coreDir 'CoreGate.cs'),
                 (Join-Path $coreDir 'TokenHandshake.cs'))
    $extra = @('-nullable:enable')
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$out = Join-Path $OutDir 'OrderFlowReplayRecorder.dll'
$rsp = Join-Path $OutDir 'build.rsp'
$lines = @('-nologo', '-target:library', "-out:`"$out`"") + $extra +
         ($refs | ForEach-Object { "-r:`"$_`"" }) + ($sources | ForEach-Object { "`"$_`"" })
Set-Content -Path $rsp -Value $lines -Encoding ASCII

& (Join-Path $DotnetRoot 'dotnet.exe') $csc "@$rsp"
if ($LASTEXITCODE -ne 0) { throw "Build fehlgeschlagen (Exit $LASTEXITCODE)" }
$h = (Get-FileHash $out -Algorithm SHA256).Hash
Write-Host "OK  $out"
Write-Host "    Variante $Variant, $((Get-Item $out).Length) Bytes, SHA-256 $h"
Write-Host "    Laufzeit $core | Roslyn $sdk"
