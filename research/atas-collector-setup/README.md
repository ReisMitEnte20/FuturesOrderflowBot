# atas-collector-setup/ – Setup.exe und All-in-one-Paket

Baut das auslieferbare ZIP des ATAS Session Collectors: `Setup.exe` plus `payload\` mit Oberfläche, Probe, Bridge,
privater Python-Laufzeit und Recorder-DLL. Nicht Teil von `TradingBot.sln`.

| Datei | Inhalt |
|---|---|
| `Installer.cs` | Installationslogik ohne Oberfläche: Paket prüfen, ATAS erkennen, kopieren, gemeinsamen Datenordner konfigurieren, Recorder installieren (mit Sicherung), Verknüpfungen, Abschlussprüfungen. Erfolg nur, wenn alles bestanden ist. |
| `SetupForm.cs`, `Program.cs` | Assistent (eine Seite) und unbeaufsichtigter Modus. |
| `build-package.ps1` | Frischer Build aller Komponenten aus dem aktuellen Commit, Manifest, ZIP, SHA-256. |
| `test-package.ps1` | Prüft ein fertiges ZIP frisch entpackt, ohne ATAS und ohne den echten Benutzerordner anzufassen. |
| `package\` | `README.md` und `LIZENZHINWEISE.txt`, die ins ZIP kommen. |

## Paket bauen und prüfen

```powershell
powershell -ExecutionPolicy Bypass -File research\atas-collector-setup\build-package.ps1 -PythonDir <Ordner der privaten Python-Laufzeit>
powershell -ExecutionPolicy Bypass -File research\atas-collector-setup\test-package.ps1 -Zip artifacts\collector-package\ATAS-Session-Collector-Setup-v1.3.1.zip
```

`build-package.ps1` verlangt einen sauberen Git-Stand unter `research\` (sonst `-AllowDirty`, was im Paket vermerkt
wird) und schreibt den Commit in `payload\manifest.json` und `VERSION-UND-HASHES.txt`. Ausgabe nach
`artifacts\collector-package\` (gitignored). ZIP und Prüfsumme werden als GitHub-Release-Assets veröffentlicht, nicht
committet.

Build-Rechner: .NET-8-SDK; für die Recorder-DLL zusätzlich ein installiertes ATAS und die .NET-10-Laufzeit (siehe
`../atas-recorder/README.md`). Der Zielrechner braucht nichts davon.

**Private Python-Laufzeit (`-PythonDir`):** CPython 3.13.9 mit pyarrow 26.0.0, isoliert über `python313._pth`
(Inhalt: `.`, `DLLs`, `Lib`, `Lib\site-packages`). Sie liegt nicht im Repository. Für 1.3.0 wurde der Ordner
`python\` unverändert aus dem Release-Asset `ATAS-Session-Collector-v1.2.5-vmtest2.zip`
(SHA-256 `11F5DE29CFE9DF3BE9BEB4D4E7C92FE9B16BCAC84D8FA435C0FC3DD0F7F097DA`) übernommen; das Skript entfernt nur
`__pycache__`, prüft Version und Bridge-Selbsttest und hält eine Baum-Prüfsumme in `VERSION-UND-HASHES.txt` fest.

## Unbeaufsichtigter Modus

```
Setup.exe /silent [/programdir "<Ordner>"] [/datadir "<Ordner>"] [/noshortcuts] [/log "<Datei>"] [/payload "<Ordner>"]
```

Rückgabe 0 = installiert und alle Abschlussprüfungen bestanden, 1 = nicht abgeschlossen. Ohne Ordnerangaben gelten
die Ordner einer vorhandenen Installation, sonst die Vorschläge. `/testroot "<Ordner>"` ist nur für Tests: Es leitet
`%APPDATA%`, Desktop und Startmenü in diesen Ordner um und überspringt die Prüfung auf ein laufendes ATAS.

## Gemeinsamer Datenordner

`../recorder-core/SharedDataRoot.cs` wird unverändert in Recorder, Oberfläche, Probe und Setup kompiliert. Die Datei
`%APPDATA%\AtasSessionCollector\recorder-output-root.txt` enthält den Recordings-Ordner; Setup und Oberfläche
schreiben sie, der Recorder `proto-2c-sharedroot` liest sie bei jedem Instanzstart. Tests:
`../recorder-core/tests` (SharedDataRoot), `AtasReplayProbe.exe regress` (Vorprüfungen), `test-package.ps1`.

Was mit dem Paket geprüft ist und was nicht, steht in `package\README.md`. Kurz: Installation, Konfiguration und
Bridge sind geprüft; der neue Recorder ist noch nie in ATAS gelaufen.
