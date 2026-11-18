# Baut das All-in-one-Paket des ATAS Session Collectors frisch aus dem aktuellen Git-Stand:
#   <OutDir>\ATAS-Session-Collector-Setup-v<Version>.zip  (+ .sha256)
#
# Inhalt: Setup.exe + payload\ (Oberflaeche, Probe, Bridge, private Python-Laufzeit, Recorder) + Doku.
# Aufruf (PowerShell, im Repository):
#   powershell -ExecutionPolicy Bypass -File research\atas-collector-setup\build-package.ps1 -PythonDir <Ordner>
#
# -PythonDir  Ordner der privaten Python-Laufzeit (python.exe, python313._pth, Lib\site-packages\pyarrow ...).
#             Sie wird unveraendert uebernommen (ohne __pycache__). Herkunft siehe README.md in diesem Ordner.
# -AllowDirty Erlaubt uncommittete Aenderungen unter research\ (nur fuer Entwicklungs-Builds; wird im Paket vermerkt).
#
# Voraussetzungen auf dem Build-Rechner: .NET-8-SDK, installiertes ATAS (nur als Referenz fuer den Recorder-Build),
# .NET-10-Laufzeit inkl. Windows Desktop. Nichts davon wird auf dem Zielrechner benoetigt.
param(
    [Parameter(Mandatory = $true)] [string]$PythonDir,
    [string]$Version = '1.3.9',
    [string]$OutDir = '',
    [switch]$AllowDirty
)
$ErrorActionPreference = 'Stop'
$research = Split-Path $PSScriptRoot -Parent
$repo = Split-Path $research -Parent
if ($OutDir -eq '') { $OutDir = Join-Path $repo 'artifacts\collector-package' }

function Sha([string]$p) { (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
function WriteUtf8([string]$p, [string]$text) { [IO.File]::WriteAllText($p, $text, (New-Object Text.UTF8Encoding($false))) }
function Run([string]$exe, [string[]]$a) {
    & $exe @a
    if ($LASTEXITCODE -ne 0) { throw "Fehlgeschlagen (Exit $LASTEXITCODE): $exe $($a -join ' ')" }
}

# ---- Quellstand ----
$commit = (& git -C $repo rev-parse HEAD).Trim()
$dirty = @(& git -C $repo status --porcelain --untracked-files=no -- research)
if ($dirty.Count -gt 0 -and -not $AllowDirty) { throw "Uncommittete Aenderungen unter research\ - erst committen (oder -AllowDirty fuer einen Entwicklungs-Build):`n$($dirty -join "`n")" }
$sourceState = if ($dirty.Count -gt 0) { "$commit + UNCOMMITTETE AENDERUNGEN (Entwicklungs-Build)" } else { $commit }
Write-Host "Quellstand: $sourceState"

$name = "ATAS-Session-Collector-Setup-v$Version"
$stage = Join-Path $OutDir "stage\$name"
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$payload = Join-Path $stage 'payload'
$app = Join-Path $payload 'app'
New-Item -ItemType Directory -Force -Path $app, (Join-Path $payload 'recorder'), (Join-Path $app 'bridge') | Out-Null

# ---- 1) Programme frisch bauen (self-contained, eine Datei je Programm) ----
$pub = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true',
         '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=none', '-p:DebugSymbols=false', '-v', 'q', '--nologo')
$tmp = Join-Path $OutDir 'stage\_publish'
if (Test-Path $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }
foreach ($p in @(
        @{ proj = 'atas-session-collector-gui\AtasSessionCollector.csproj'; exe = 'AtasSessionCollector.exe'; dst = $app },
        @{ proj = 'atas-replay-probe\AtasReplayProbe.csproj'; exe = 'AtasReplayProbe.exe'; dst = $app },
        @{ proj = 'atas-collector-setup\AtasCollectorSetup.csproj'; exe = 'Setup.exe'; dst = $stage })) {
    $o = Join-Path $tmp ([IO.Path]::GetFileNameWithoutExtension($p.exe))
    Run 'dotnet' (@('publish', (Join-Path $research $p.proj), '-o', $o) + $pub)
    Copy-Item -LiteralPath (Join-Path $o $p.exe) -Destination (Join-Path $p.dst $p.exe)
}
# Uninstall.exe = dieselbe Setup-Datei; als 'Uninstall.exe' gestartet arbeitet sie im Deinstallationsmodus.
Copy-Item -LiteralPath (Join-Path $stage 'Setup.exe') -Destination (Join-Path $app 'Uninstall.exe')

# ---- 2) Bridge ----
foreach ($f in 'atas_collect.py', 'atas_bridge.py') { Copy-Item -LiteralPath (Join-Path $research "data-bridge\$f") -Destination (Join-Path $app "bridge\$f") }

# ---- 3) Private Python-Laufzeit (unveraendert, ohne __pycache__) ----
if (-not (Test-Path (Join-Path $PythonDir 'python.exe'))) { throw "python.exe nicht gefunden in -PythonDir '$PythonDir'" }
$pyDst = Join-Path $app 'python'
& robocopy $PythonDir $pyDst /E /XD __pycache__ /XF *.pyc /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy fehlgeschlagen (Exit $LASTEXITCODE)" }
$pyInfo = (& (Join-Path $pyDst 'python.exe') -B -c "import sys, pyarrow; print(sys.version.split()[0] + ' / pyarrow ' + pyarrow.__version__)").Trim()
if ($LASTEXITCODE -ne 0) { throw "Private Python-Laufzeit startet nicht oder pyarrow fehlt." }
$chk = (& (Join-Path $pyDst 'python.exe') -B (Join-Path $app 'bridge\atas_collect.py') selfcheck)
if ($LASTEXITCODE -ne 0) { throw "Bridge-Selbsttest mit der privaten Laufzeit fehlgeschlagen." }
Get-ChildItem -LiteralPath $app -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force
Write-Host "Python: $pyInfo"

# ---- 4) Recorder (proto-2c-sharedroot, deterministischer Build) ----
$recOut = Join-Path $tmp 'recorder'
Run 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $research 'atas-recorder\build.ps1'), '-Variant', 'proto2c', '-OutDir', $recOut)
Copy-Item -LiteralPath (Join-Path $recOut 'OrderFlowReplayRecorder.dll') -Destination (Join-Path $payload 'recorder\OrderFlowReplayRecorder.dll')

# ---- 5) Doku ----
$docs = Join-Path $PSScriptRoot 'package'
Copy-Item -LiteralPath (Join-Path $docs 'README.md') -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $docs 'LIZENZHINWEISE.txt') -Destination (Join-Path $stage 'LIZENZHINWEISE.txt')
Copy-Item -LiteralPath (Join-Path $docs 'LIZENZHINWEISE.txt') -Destination (Join-Path $app 'LIZENZHINWEISE.txt')

# ---- 6) Manifest ueber alle Installationsdateien ----
$files = [IO.Directory]::GetFiles($payload, '*', [IO.SearchOption]::AllDirectories) | Sort-Object | ForEach-Object {
    [pscustomobject][ordered]@{ path = $_.Substring($payload.Length + 1).Replace([char]92, [char]47); size = (New-Object IO.FileInfo($_)).Length; sha256 = (Sha $_) }
}
$built = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
$manifest = [ordered]@{ product = 'ATAS Session Collector'; version = $Version; commit = $sourceState; built = $built; files = @($files) }
WriteUtf8 (Join-Path $payload 'manifest.json') ($manifest | ConvertTo-Json -Depth 5)

# Zusammenfassende Pruefsumme der Python-Laufzeit (viele Einzeldateien): SHA-256 ueber "pfad:sha256"-Zeilen.
$pyLines = ($files | Where-Object { $_.path -like 'app/python/*' } | ForEach-Object { "$($_.path):$($_.sha256)" }) -join "`n"
$sha = [Security.Cryptography.SHA256]::Create()
$pyTree = -join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($pyLines)) | ForEach-Object { $_.ToString('X2') })
$pyCount = @($files | Where-Object { $_.path -like 'app/python/*' }).Count

function Line([string]$rel) { $p = Join-Path $stage $rel; '{0,-44} {1}  {2,12:N0} B' -f $rel, (Sha $p), (Get-Item -LiteralPath $p).Length }
$ver = @"
ATAS Session Collector - All-in-one-Paket (Vorabversion)
========================================================
Paketversion : $Version
Gebaut       : $built (frisch aus dem Quellstand, Skript research\atas-collector-setup\build-package.ps1)
Quellstand   : https://github.com/ReisMitEnte20/FuturesOrderflowBot  Commit $sourceState
Plattform    : Windows 10/11 x64

Komponenten und SHA-256
-----------------------
$(Line 'Setup.exe')
$(Line 'payload\app\AtasSessionCollector.exe')
$(Line 'payload\app\AtasReplayProbe.exe')
$(Line 'payload\app\Uninstall.exe')
$(Line 'payload\app\bridge\atas_collect.py')
$(Line 'payload\app\bridge\atas_bridge.py')
$(Line 'payload\recorder\OrderFlowReplayRecorder.dll')
payload\app\python\  ($pyCount Dateien)        Baum-Pruefsumme $pyTree
                                             Python $pyInfo
Alle Installationsdateien einzeln: payload\manifest.json (Setup prueft sie vor und nach der Installation).

Herkunft
--------
- Setup.exe, AtasSessionCollector.exe ($Version), AtasReplayProbe.exe ($Version): mit 'dotnet publish' aus dem oben genannten
  Commit gebaut (self-contained .NET 8, eine Datei je Programm). Die Programme tragen den Commit als Produktversion.
- bridge\*.py: unveraenderte Kopien aus research\data-bridge\ dieses Commits.
- Recorder-DLL: Variante 'proto-2c-sharedroot', deterministisch gebaut aus
  research\atas-recorder\proto-2c-sharedroot\OrderFlowReplayRecorder.cs + research\recorder-core\SharedDataRoot.cs
  (research\atas-recorder\build.ps1 -Variant proto2c). Sie ist NICHT die bisher ausgelieferte DLL 'proto-2-bufferfix':
  Gegenueber deren Quellsicherung ist nur die Bestimmung des Ausgabeordners geaendert (gemeinsame Konfiguration statt
  festem Pfad F:\ATAS_Data\Recordings) und meta.json um den verwendeten Ordner ergaenzt.
- Python-Laufzeit: unveraendert aus dem Paket 'ATAS-Session-Collector-v1.2.5-vmtest2' uebernommen (CPython 3.13.9 mit
  pyarrow 26.0.0, isoliert ueber python313._pth), ohne __pycache__.

Pruefstand: siehe README.md, Abschnitt 'Was geprueft ist und was nicht'.
"@
WriteUtf8 (Join-Path $stage 'VERSION-UND-HASHES.txt') ($ver -replace "`r?`n", "`r`n")

# ---- 7) ZIP + Pruefsumme ----
$zip = Join-Path $OutDir "$name.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Push-Location (Split-Path $stage -Parent)
try { Run "$env:SystemRoot\System32\tar.exe" @('-a', '-c', '-f', $zip, $name) } finally { Pop-Location }
$zh = Sha $zip
WriteUtf8 "$zip.sha256" "$($zh.ToLower()) *$name.zip`n"
Remove-Item -LiteralPath $tmp -Recurse -Force
Write-Host "OK  $zip"
Write-Host "    $((Get-Item -LiteralPath $zip).Length) Bytes, SHA-256 $zh"
Write-Host "    Quellstand $sourceState"
