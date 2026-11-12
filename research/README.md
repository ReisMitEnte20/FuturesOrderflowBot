# research/ – ATAS-Datenerfassung (Collector) und Bridge

Isolierte Werkzeuge zur **lokalen Aufzeichnung echter ATAS-Replay-Trades**. Bewusst **nicht** Teil von
`TradingBot.sln` (Windows-/UI-Automation-spezifisch). Research/Simulation-only: keine Orders, keine Broker-Anbindung,
keine Kontoänderungen. Die Werkzeuge bedienen ATAS ausschließlich als UI-Automation-Client (kein Injizieren/Patchen).

| Ordner | Inhalt |
|---|---|
| `atas-replay-probe/` | ATAS-Replay-Steuerung (UIA + visueller OCR-Fallback): Recorder hinzufügen, Zeitraum laden, 100× abspielen, Ende erkennen (inkl. Auto-Loop), aufräumen; `collect` hängt je Fenster an eine gemeinsame Collection an. Bedienung: `README_BEDIENUNG.md`, Vorlage: `collect_config.example.json`. |
| `atas-session-collector-gui/` | WinForms-Oberfläche („ATAS Session Collector“): Datumsbereich + Session (New York 09:30–12:00 ET / London) wählen, Vorschau, Start/Fortsetzen/Kontrolliert stoppen/Not-Aus. Ruft die Probe je Tag auf. |
| `data-bridge/` | Python-Bridge: prüft Recorder-Aufnahmen und hängt Trades atomar und wiederanlauffähig an `trades.csv` + `collection_manifest.json` an (Schema `atas-collection-trades-1`, keine Deduplizierung). |
| `recorder-core/` | Kernlogik des ATAS-Indikators „OF Replay Recorder“ (Token-Handshake, Writer, Rückzug) mit Konsolentests. |
| `atas-recorder/` | Quelltext des ATAS-Indikators selbst (aktueller Stand `proto-4-recheck` und Sicherung `proto-2-bufferfix`) mit Build-Skript. Die ausgelieferte DLL ist **nicht** aus dem aktuellen Stand gebaut; Einzelheiten und Nachweisstand in `atas-recorder/README.md`. |

## Bauen und testen

```bash
dotnet build research/atas-replay-probe/AtasReplayProbe.csproj -c Release
dotnet build research/atas-session-collector-gui/AtasSessionCollector.csproj -c Release
dotnet run --project research/recorder-core/tests/RecorderCoreTests.csproj -c Release
python -m pytest research/data-bridge -q
```

GUI-Selbsttest (Zeitzonen/Kalender, ohne ATAS): `AtasSessionCollector.exe --selftest`.

Auslieferbares Paket (self-contained single-file, Probe-TFM `net8.0-windows10.0.19041.0` für Windows-OCR):

```bash
dotnet publish research/atas-replay-probe/AtasReplayProbe.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <out>
dotnet publish research/atas-session-collector-gui/AtasSessionCollector.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <out>
```

Paketlayout neben der GUI-EXE: `AtasReplayProbe.exe`, `bridge\atas_collect.py`, `bridge\atas_bridge.py`,
optional private Python-Laufzeit unter `python\python.exe` (sonst `ATAS_PYTHON` bzw. `python` aus dem PATH, mit `pyarrow`).

## Daten und Pfade (außerhalb von Git)

Aufnahmen, Collections, Caches und Pakete werden **nie** committet. Der Datenordner ist frei wählbar
(GUI: „Datenordner“, gespeichert in `collector-data.json` neben der EXE bzw. unter `%APPDATA%`); ohne Wahl wird
`<Paketordner>\ATAS_Data` verwendet. Darunter:

- `Recordings\` – Rohaufnahmen des Recorders. Achtung: Der Recorder schreibt standardmäßig fest nach
  `F:\ATAS_Data\Recordings`. Den Output-Ordner in ATAS von Hand zu setzen ist kein bestätigter Weg zu einem anderen
  Pfad (in früheren Tests Rückfall beim Instanzwechsel, siehe `atas-recorder/README.md`). Geprüft ist nur der
  Datenordner `F:\ATAS_Data`.
- `Collections\<Instrument>_<Session>_<Fenster>_<Von>_bis_<Bis>\` – `trades.csv`, `collection_manifest.json`, `coverage_report.txt`, `run_protocol.txt`.
- `Logs\`

Auf dem Entwicklungsrechner liegt die geprüfte Woche unter
`F:\ATAS_Data\Collections\MNQ_NewYork_0930-1200_America-New-York_2026-10-26_bis_2026-10-30` (4.746.875 Trades).
Sie wird von `TradingBot.OrderflowRun --collection <manifest> --day yyyy-MM-dd` (Tagesläufe, siehe dessen README)
und vom Tick-Replay des Dashboards (`/api/tickreplay`, Konfiguration `TickReplay:CollectionDir`, Cache unter
`artifacts/tickreplay-cache/`, gitignored) gelesen; die Originaldaten werden dabei nur gelesen.

Lauf-spezifische Konfigurationen (konkrete Fenster/Pfade) und Einmal-Prüfskripte bleiben lokal; als Vorlage dient
`atas-replay-probe/collect_config.example.json`.
