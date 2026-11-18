# Prueft ein fertig gebautes All-in-one-ZIP FRISCH ENTPACKT, ohne ATAS und ohne den echten Benutzerordner anzufassen
# (Setup laeuft mit /testroot: %APPDATA%, Desktop und Startmenue werden in einen Testordner umgeleitet).
#
#   powershell -ExecutionPolicy Bypass -File research\atas-collector-setup\test-package.ps1 -Zip <zip> [-WorkDir <ordner>]
#
# Abgedeckt: Pruefsumme, Entpacken in Pfad mit Leerzeichen/Umlauten, fehlendes ATAS, beschaedigtes Paket, Installation
# ohne Python/.NET im PATH, Datenordner ohne Laufwerk F:, erneutes Ausfuehren (keine Doppelinstallation, Daten bleiben),
# Aktualisieren einer aelteren Recorder-DLL mit Sicherung, Wechsel des Datenordners, Bridge-Aufruf aus der Installation.
# NICHT abgedeckt (braucht ATAS): Aufnahme, Schreibpfad des Recorders in ATAS, Remove/Add, ATAS-Neustart, RDP.
param(
    [Parameter(Mandatory = $true)] [string]$Zip,
    [string]$WorkDir = (Join-Path $env:TEMP ("asc_pkgtest_" + [Guid]::NewGuid().ToString('N').Substring(0, 8)))
)
$ErrorActionPreference = 'Stop'
$ue = [string][char]0xDC; $oe = [string][char]0xD6; $ae = [string][char]0xC4
$script:fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    Write-Host ("  [{0}] {1}{2}" -f $(if ($ok) { 'OK  ' } else { 'FAIL' }), $name, $(if ($detail) { "  -> $detail" } else { '' }))
    if (-not $ok) { $script:fail++ }
}
function Setup([string]$exe, [string[]]$a, [string]$log) {
    # Minimaler PATH: beweist, dass weder ein globales Python noch ein .NET-SDK benoetigt wird.
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = (($a + @('/log', $log)) | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['PATH'] = "$env:SystemRoot\System32;$env:SystemRoot"
    foreach ($k in 'DOTNET_ROOT', 'PYTHONPATH', 'PYTHONHOME', 'ATAS_PYTHON') { $psi.EnvironmentVariables.Remove($k) }
    $p = [Diagnostics.Process]::Start($psi); $p.WaitForExit(); return $p.ExitCode
}
function LogText([string]$log) { if (Test-Path -LiteralPath $log) { [IO.File]::ReadAllText($log) } else { '' } }

Write-Host "# Pakettest  $Zip"
Write-Host "# Arbeitsordner $WorkDir"
New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
# Kurze 8.3-Namen (z. B. aus %TEMP%) in lange Namen aufloesen, damit Pfadvergleiche eindeutig sind.
Add-Type -Namespace Native -Name Paths -MemberDefinition '[DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern int GetLongPathName(string s, System.Text.StringBuilder b, int n);'
$lp = New-Object Text.StringBuilder 2048
if ([Native.Paths]::GetLongPathName($WorkDir, $lp, 2048) -gt 0) { $WorkDir = $lp.ToString() }

# ---- 0) Pruefsumme + frisch entpacken ----
$shaFile = "$Zip.sha256"
$have = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash
if (Test-Path -LiteralPath $shaFile) { Check 'ZIP-Pruefsumme stimmt mit .sha256' (([IO.File]::ReadAllText($shaFile)).Split(' ')[0].Trim() -ieq $have) $have }
$unz = Join-Path $WorkDir "Entpackt mit Leerzeichen und ${ue}mlaut"
New-Item -ItemType Directory -Force -Path $unz | Out-Null
& "$env:SystemRoot\System32\tar.exe" -xf $Zip -C $unz
$pkg = (Get-ChildItem -LiteralPath $unz -Directory | Select-Object -First 1).FullName
$setup = Join-Path $pkg 'Setup.exe'
Check 'Setup.exe und payload\manifest.json vorhanden' ((Test-Path -LiteralPath $setup) -and (Test-Path -LiteralPath (Join-Path $pkg 'payload\manifest.json')))
$forbidden = @(Get-ChildItem -LiteralPath $pkg -Recurse -File | Where-Object { $_.Name -match '\.(csv|parquet|ndjson|log|scid)$' -or $_.Name -in 'collector-data.json', 'install.json' } | Where-Object { $_.FullName -notmatch '\\site-packages\\' })
Check 'Keine Daten-/Konfigurationsdateien im Paket' ($forbidden.Count -eq 0) (($forbidden | Select-Object -First 3 | ForEach-Object Name) -join ', ')

$user = Join-Path $WorkDir "Benutzer $ae"
$prog = Join-Path $WorkDir "Programme $oe\ATAS Session Collector"
$data = Join-Path $WorkDir "Daten mit Leerzeichen $ue"
$common = @('/silent', '/testroot', $user, '/programdir', $prog, '/datadir', $data)

# ---- 1) ATAS fehlt -> verstaendlicher Abbruch, nichts installiert ----
$log = Join-Path $WorkDir 't1.log'
$rc = Setup $setup $common $log
Check 'ATAS fehlt: Setup bricht ab (rc=1)' ($rc -eq 1) "rc=$rc"
Check 'ATAS fehlt: Meldung nennt ATAS als Voraussetzung' ((LogText $log) -match 'ATAS ist Voraussetzung')
Check 'ATAS fehlt: nichts installiert' (-not (Test-Path -LiteralPath (Join-Path $prog 'AtasSessionCollector.exe')))
Check 'ATAS fehlt: kein Erfolgstext' (-not ((LogText $log) -match 'ERFOLGREICH'))

# ---- 2) Beschaedigtes/unvollstaendiges Paket -> Abbruch ----
$ind = Join-Path $user 'Roaming\ATAS\Indicators'
New-Item -ItemType Directory -Force -Path $ind | Out-Null
$victim = Join-Path $pkg 'payload\app\bridge\atas_bridge.py'
$bak = "$victim.weg"; Move-Item -LiteralPath $victim -Destination $bak
$log = Join-Path $WorkDir 't2.log'
$rc = Setup $setup $common $log
Check 'Unvollstaendiges Paket: Abbruch (rc=1) mit Dateiname' (($rc -eq 1) -and ((LogText $log) -match 'atas_bridge\.py'))
Move-Item -LiteralPath $bak -Destination $victim

# ---- 3) Erstinstallation; vorhandene aeltere Recorder-DLL wird gesichert ----
$old = Join-Path $ind 'OrderFlowReplayRecorder.dll'
[IO.File]::WriteAllBytes($old, [Text.Encoding]::Unicode.GetBytes('OrderFlowReplayRecorder/proto-2-bufferfix (Testattrappe)'))
$oldHash = (Get-FileHash -LiteralPath $old -Algorithm SHA256).Hash
$log = Join-Path $WorkDir 't3.log'
$rc = Setup $setup $common $log
$t = LogText $log
Check 'Installation erfolgreich (rc=0, alle Abschlusspruefungen)' (($rc -eq 0) -and ($t -match 'INSTALLATION ERFOLGREICH ABGESCHLOSSEN')) "rc=$rc"
if ($rc -ne 0) { Write-Host $t }
Check 'Kein FEHLER im Protokoll' (-not ($t -match 'FEHLER'))
Check 'Bridge-Selbsttest mit privater Laufzeit bestanden' ($t -match '\[OK\]\s+Bridge-Selbsttest')
Check 'Alte Recorder-DLL gesichert' (@(Get-ChildItem -LiteralPath (Join-Path $user 'Roaming\AtasSessionCollector\recorder-backup') -File | Where-Object { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq $oldHash }).Count -eq 1)
Check 'Neue Recorder-DLL installiert' ((Get-FileHash -LiteralPath $old -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath (Join-Path $pkg 'payload\recorder\OrderFlowReplayRecorder.dll') -Algorithm SHA256).Hash)
$shared = ([IO.File]::ReadAllText((Join-Path $user 'Roaming\AtasSessionCollector\recorder-output-root.txt'))).Trim()
Check 'Gemeinsame Recorder-Konfiguration zeigt auf <Daten>\Recordings' ($shared -ieq (Join-Path $data 'Recordings')) $shared
Check 'Recorder-Konfiguration ohne Laufwerk F:' (-not ($shared -match '^F:'))
$gui = (Get-Content -LiteralPath (Join-Path $prog 'collector-data.json') -Raw | ConvertFrom-Json).dataDir
Check 'Oberflaeche auf denselben Datenordner eingestellt' ($gui -ieq $data) $gui
Check 'Datenunterordner angelegt' ((Test-Path -LiteralPath (Join-Path $data 'Collections')) -and (Test-Path -LiteralPath (Join-Path $data 'Recordings')) -and (Test-Path -LiteralPath (Join-Path $data 'Logs')))
Check 'Verknuepfungen (Startmenue + Desktop)' ((Test-Path -LiteralPath (Join-Path $user 'Startmenue\ATAS Session Collector.lnk')) -and (Test-Path -LiteralPath (Join-Path $user 'Desktop\ATAS Session Collector.lnk')))
$sc = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $user 'Desktop\ATAS Session Collector.lnk'))
Check 'Verknuepfung zeigt auf installierte Oberflaeche' ($sc.TargetPath -ieq (Join-Path $prog 'AtasSessionCollector.exe')) $sc.TargetPath

# ---- 4) Bridge aus der Installation, fremdes Arbeitsverzeichnis, ohne Python im PATH ----
$py = Join-Path $prog 'python\python.exe'
$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = $py; $psi.Arguments = '"' + (Join-Path $prog 'bridge\atas_collect.py') + '" selfcheck'
$psi.UseShellExecute = $false; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.WorkingDirectory = $env:SystemRoot; $psi.EnvironmentVariables['PATH'] = "$env:SystemRoot\System32"
$p = [Diagnostics.Process]::Start($psi); $o = $p.StandardOutput.ReadToEnd(); $e = $p.StandardError.ReadToEnd(); $p.WaitForExit()
Check 'Installierte Bridge findet atas_bridge (frueher: ModuleNotFoundError)' (($p.ExitCode -eq 0) -and ($o -match '"result": "ok"')) ($e.Trim().Split("`n")[-1])

# ---- 5) Erneutes Ausfuehren: keine Doppelinstallation, Daten und Einstellungen bleiben ----
$keep = Join-Path $data 'Collections\Bestehende Collection\trades.csv'
New-Item -ItemType Directory -Force -Path (Split-Path $keep -Parent) | Out-Null
[IO.File]::WriteAllText($keep, "bestehende,daten`r`n")
$keepRec = Join-Path $data 'Recordings\MNQ_20261026_133000Z\meta.json'
New-Item -ItemType Directory -Force -Path (Split-Path $keepRec -Parent) | Out-Null
[IO.File]::WriteAllText($keepRec, '{"recorder":"test"}')
function ProgFiles { @(Get-ChildItem -LiteralPath $prog -Recurse -File | Where-Object { $_.FullName -notmatch '__pycache__' }).Count }
$filesBefore = ProgFiles
$log = Join-Path $WorkDir 't5.log'
$rc = Setup $setup @('/silent', '/testroot', $user) $log      # OHNE Ordnerangabe: muss die vorhandene Installation finden
$t = LogText $log
Check 'Zweiter Lauf erfolgreich (rc=0)' ($rc -eq 0) "rc=$rc"
Check 'Zweiter Lauf erkennt vorhandene Installation und deren Ordner' (($t -match 'Vorhandene Installation gefunden') -and ($t.Contains($prog)) -and ($t.Contains($data)))
Check 'Zweiter Lauf kopiert nichts doppelt' ($t -match '0 Dateien kopiert')
Check 'Gleiche Dateianzahl im Programmordner' ((ProgFiles) -eq $filesBefore)
Check 'Recorder nicht erneut gesichert' (@(Get-ChildItem -LiteralPath (Join-Path $user 'Roaming\AtasSessionCollector\recorder-backup') -File).Count -eq 1)
Check 'Genau eine Verknuepfung je Ort' ((@(Get-ChildItem -LiteralPath (Join-Path $user 'Desktop') -File).Count -eq 1) -and (@(Get-ChildItem -LiteralPath (Join-Path $user 'Startmenue') -File).Count -eq 1))
Check 'Bestehende Collection unveraendert' ([IO.File]::ReadAllText($keep) -eq "bestehende,daten`r`n")
Check 'Bestehende Aufnahme unveraendert' ([IO.File]::ReadAllText($keepRec) -eq '{"recorder":"test"}')

# ---- 6) Datenordner wechseln: alle Komponenten folgen, alte Daten bleiben liegen ----
$data2 = Join-Path $WorkDir "Zweiter Datenordner $oe"
$log = Join-Path $WorkDir 't6.log'
$rc = Setup $setup @('/silent', '/testroot', $user, '/datadir', $data2) $log
$shared2 = ([IO.File]::ReadAllText((Join-Path $user 'Roaming\AtasSessionCollector\recorder-output-root.txt'))).Trim()
$gui2 = (Get-Content -LiteralPath (Join-Path $prog 'collector-data.json') -Raw | ConvertFrom-Json).dataDir
Check 'Datenordnerwechsel erfolgreich (rc=0)' ($rc -eq 0) "rc=$rc"
Check 'Recorder-Konfiguration und Oberflaeche folgen dem neuen Datenordner' (($shared2 -ieq (Join-Path $data2 'Recordings')) -and ($gui2 -ieq $data2)) "$shared2 | $gui2"
Check 'Alter Datenordner unangetastet' ((Test-Path -LiteralPath $keep) -and (Test-Path -LiteralPath $keepRec))

# ---- 7) Nicht vorhandenes Laufwerk als Datenordner -> Abbruch ----
$missing = $null
foreach ($c in 'Z', 'Y', 'X', 'W', 'V') { if (-not (Test-Path "${c}:\")) { $missing = "${c}:\ATAS Daten"; break } }
if ($missing) {
    $log = Join-Path $WorkDir 't7.log'
    $rc = Setup $setup @('/silent', '/testroot', $user, '/datadir', $missing) $log
    Check 'Fehlendes Laufwerk: Abbruch mit Meldung (rc=1)' (($rc -eq 1) -and ((LogText $log) -match 'Laufwerk') -and (-not ((LogText $log) -match 'ERFOLGREICH')))
    $shared3 = ([IO.File]::ReadAllText((Join-Path $user 'Roaming\AtasSessionCollector\recorder-output-root.txt'))).Trim()
    Check 'Fehlgeschlagener Lauf laesst die Konfiguration unveraendert' ($shared3 -ieq $shared2)
}

# ---- 8) Deinstallation (isolierte Testinstallation): Programm weg, Daten unveraendert ----
$state = Get-Content -LiteralPath (Join-Path $user 'Roaming\AtasSessionCollector\install.json') -Raw | ConvertFrom-Json
$regPath = 'HKCU:\Software\AtasSessionCollector-Test\Uninstall'
$regBefore = @(Get-ChildItem $regPath -ErrorAction SilentlyContinue).Count
Check 'Registrierung (Testschluessel) vorhanden' ($regBefore -ge 1)
$foreign = Join-Path $prog 'eigene_notiz.txt'; [IO.File]::WriteAllText($foreign, 'nicht von der Installation')
$keep2 = Join-Path $data2 'Collections\Daten nach Wechsel\trades.csv'
New-Item -ItemType Directory -Force -Path (Split-Path $keep2 -Parent) | Out-Null; [IO.File]::WriteAllText($keep2, "x,y`r`n")
$log = Join-Path $WorkDir 't8.log'
$rc = Setup (Join-Path $prog 'Uninstall.exe') @('/silent', '/testroot', $user) $log
$t = LogText $log
Start-Sleep -Seconds 6     # Uninstall.exe loescht sich nach Prozessende selbst
Check 'Deinstallation erfolgreich (rc=0)' (($rc -eq 0) -and ($t -match 'DEINSTALLATION ABGESCHLOSSEN')) "rc=$rc"
if ($rc -ne 0) { Write-Host $t }
Check 'Programmdateien entfernt' (-not (Test-Path -LiteralPath (Join-Path $prog 'AtasSessionCollector.exe')) -and -not (Test-Path -LiteralPath (Join-Path $prog 'python\python.exe')) -and -not (Test-Path -LiteralPath (Join-Path $prog 'Uninstall.exe')))
Check 'Fremde Datei im Programmordner bleibt' (Test-Path -LiteralPath $foreign)
Check 'Verknuepfungen entfernt' (-not (Test-Path -LiteralPath (Join-Path $user 'Desktop\ATAS Session Collector.lnk')) -and -not (Test-Path -LiteralPath (Join-Path $user 'Startmenue\ATAS Session Collector.lnk')))
Check 'Fremde Vorgaenger-DLL wiederhergestellt' ((Test-Path -LiteralPath $old) -and ((Get-FileHash -LiteralPath $old -Algorithm SHA256).Hash -eq $oldHash))
Check 'Eintrag Installierte Apps entfernt' (@(Get-ChildItem $regPath -ErrorAction SilentlyContinue).Count -eq ($regBefore - 1))
Check 'Daten unveraendert (beide Datenordner)' ((Test-Path -LiteralPath $keep) -and (Test-Path -LiteralPath $keepRec) -and (Test-Path -LiteralPath $keep2) -and ([IO.File]::ReadAllText($keep) -eq "bestehende,daten`r`n"))
Check 'Installationsstand entfernt' (-not (Test-Path -LiteralPath (Join-Path $user 'Roaming\AtasSessionCollector\install.json')))
$log = Join-Path $WorkDir 't9.log'
$rc = Setup $setup @('/silent', '/testroot', $user, '/uninstall') $log
Check 'Erneute Deinstallation ohne Installation -> verstaendlicher Abbruch, nichts entfernt' (($rc -eq 1) -and ((LogText $log) -match 'Keine Installation gefunden'))

Write-Host ''
Write-Host $(if ($script:fail -eq 0) { 'PAKETTEST: ALLE BESTANDEN' } else { "PAKETTEST: $($script:fail) FEHLGESCHLAGEN" })
Write-Host "Protokolle: $WorkDir\t*.log"
exit $(if ($script:fail -eq 0) { 0 } else { 1 })
