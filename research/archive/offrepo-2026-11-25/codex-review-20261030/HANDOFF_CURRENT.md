# HANDOFF_CURRENT — FuturesOrderflowBot / ATAS Session Collector

Stand: 2026-11-06 (Claude). Wechsel zu Codex wegen Wochenlimit.
**Keine neue Implementierung, Aufnahme oder Testserie in dieser Übergabe gestartet.**
Collector bleibt unverändert. Keine Orders, keine Datenlöschung, kein Git-Commit. Hindsight nur lesend.

---

## 1. Repository / Git

- **Repo-Pfad:** `A:\Projects\FuturesOrderflowBot`
- **Branch:** `feature/atas-replay-control-probe`
- **HEAD:** `16f5d76b5c584763c404786365fdbf273021bf24` — „Merge pull request #16 … React als einzige UI, DevDashboard zum reinen API-Host"
- **Uncommittete (tracked) Änderungen:** KEINE (`git status --short` zeigt nur untracked).
- **Untracked Verzeichnisse:**
  - `research/` — enthält den **gesamten Collector-Quellcode** (Probe + GUI), bewusst isoliert, NICHT in `TradingBot.sln`, NICHT committet.
  - `dashboard/dist-demo/`
  - `tools/`
- Es wurde in dieser und den vorigen Sessions **kein Commit/Push/Merge** zum Collector durchgeführt.

---

## 2. Collector 1.2.4 — Quellen, EXEs, Hashes, Build, Rollback

### 2.1 Funktionierende Quellen (untracked, unter `research/`)
- **Probe:** `research/atas-replay-probe/`
  - `Program.cs` (Hauptlogik, enthält die drei aktuellen Fixes), `OcrFallback.cs` (OCR-Transporterkennung), `AtasReplayProbe.csproj`
  - **TFM:** `net8.0-windows10.0.19041.0` (WinRT-OCR `Windows.Media.Ocr`)
- **GUI:** `research/atas-session-collector-gui/`
  - `MainForm.cs`, `CollectorRunner.cs`, `Headless.cs`, `Program.cs`, `AppPaths.cs`, `Preconditions.cs`, `SessionCalendar.cs`, `AtasSessionCollector.csproj`
  - **TFM:** `net8.0-windows` (WinForms, WinExe), AppVersion 1.2.1
- **Bridge (Python):** liegt ausgeliefert im Paket unter `bridge/` (siehe 2.2); Schemas `atas-collection-trades-1` (Collect) / `atas-bridge-trades-1` (Bridge).

### 2.2 Ausgeliefertes GUI-Paket
`F:\ATAS_Data\Apps\ATAS-Session-Collector\`
- `AtasSessionCollector.exe` (GUI, self-contained single-file)
- `AtasReplayProbe.exe` (Probe, self-contained single-file)
- `bridge\atas_collect.py` (24241 B), `bridge\atas_bridge.py` (22436 B), `bridge\requirements.txt` (pyarrow)
- `BEDIENUNG.md`, `VERSION.txt`, `collector-data.json` (`{ "dataDir": "F:\\ATAS_Data" }`)
- Desktop-Verknüpfung: `…\Desktop\ATAS Session Collector.lnk` → `AtasSessionCollector.exe`

### 2.3 Vollständige SHA-256-Hashes (Ist-Stand im Paket)
- `AtasReplayProbe.exe`     = `89808EB46CF8E55DD4EC05CC34F52289C468193541929D40B54C07EE9B36D74D`  (187.582.283 B)  **[1.2.4, NEU]**
- `AtasSessionCollector.exe` = `5A795B3F2128F7C156DF88C43D45E25149DB73833CE37646C2E1FD75C4D0B5AC`  (161.689.149 B)  **[GUI 1.2.1, unverändert]**

### 2.4 Build-Befehle (aus dem Repo-Root; erzeugen self-contained single-file)
```bash
# Probe (TFM net8.0-windows10.0.19041.0)
dotnet publish research\atas-replay-probe\AtasReplayProbe.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <out>

# GUI
dotnet publish research\atas-session-collector-gui\AtasSessionCollector.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o <out>
```
Laufzeit-Umgebungsvariable der Probe im GUI-Betrieb: `ATAS_RECORDINGS_DIR=F:\ATAS_Data\Recordings` (setzt die GUI selbst).
Einzel-Testlauf der Probe (ohne GUI/Collector): `AtasReplayProbe.exe liveproof <out.txt>` (30-Min-Fenster 26.10. 14:30–15:00 Berlin @100x). Aufräum-Kommando: `AtasReplayProbe.exe rmrec <out.txt>` (Replay verlassen + aktive Recorder entfernen).

### 2.5 Rollback
- `F:\ATAS_Data\Apps\ATAS-Session-Collector-rollback-1.2.0\` — enthält die **komplett-verifizierte** Alt-Probe
  `AtasReplayProbe.exe` SHA-256 `37068DF2911ACF16BCE5B3920AA4CB6D76831B6AD92F2A70977F8C46EDB7479E` (fuhr die ganze Woche 26.–30.10. durch) + GUI 1.2.0.
- `F:\ATAS_Data\Apps\ATAS-Session-Collector-rollback-1.1.0\` — ältere Version.
- Rollback = diese EXE(s) zurück ins Paket kopieren.

---

## 3. Was ist im GUI-Paket vs. nicht

**IM Paket (ausgeliefert):**
- Probe **1.2.4** mit drei Fixes (siehe 4), GUI **1.2.1** (inkl. Not-Aus-Knopf „ALLES BEENDEN"), Bridge, Doku.

**NICHT im Paket / NICHT committet:**
- Sämtlicher Quellcode (`research/` ist untracked). Die Probe-Fixes existieren nur im **Arbeitsbaum** `research/atas-replay-probe/Program.cs` + `OcrFallback.cs` (kein Commit).
- Der GUI-Fenstertitel zeigt weiterhin „v1.2.1" (Assembly-Version), während das Paket inhaltlich 1.2.4 ist (nur die Probe wurde seit 1.2.1 geändert; `VERSION.txt` ist maßgeblich).

---

## 4. Bestätigte Funktionen + verbleibende Grenzen

### 4.1 Bestätigt (live, 2026-11-06)
- **Kompletter GUI-Lauf 26.–28.10.2026** (MNQ New York 09:30–12:00 = UTC 13:30–16:00, 100x) lief **über alle 3 Tage ohne Hänger** durch (vom Nutzer bestätigt).
- Drei Probe-Fixes greifen:
  - **Fix 1 (Phase 4, Transportleiste sichtbar machen):** Präsenz-Check an der Fensterzahl — sichtbare Leiste = eigenes Top-Level-Fenster „Replay …" NEBEN dem Chart; eingeklappt/zu = nur das Chartfenster. Recovery bei `ReplayWindow()==null`: **primär Replay-Ribbon toggeln** (frischer Eintritt zeigt die Leiste aufgeklappt), Chevron nur sekundär (OCR-Anker am „OF Replay Recorder"-Label, ATAS vorher in den Vordergrund). Eingriff nur ohne „Replay"-Fenster → schließt nie einen funktionierenden Transport.
  - **Fix 2 (Phase 8, Play im visuellen Fallback):** fällt auf die in DIESEM Lauf (Phase 4/6) validierte Play-Geometrie zurück, wenn die frische OCR nach dem Laden scheitert; nur bei Statuszeile „Play" (kein Blindklick). `ClickFloat`/`RaiseReplayFloat` heben das schwebende Transport-Float vor Klick/OCR nach vorn.
  - **Fix 3 (Phase 9, Loop-Ende):** persistentes `maxMp` (fällt nie) → `everReachedEnd`, sobald das Fensterende erreicht ist; plus `loopedBack` (Marktzeit fällt >2 min unter `maxMp`, nachdem sie ≤5 min ans Ende kam = ATAS Auto-Loop) → sofort stoppen. Die alte, zu strikte Bedingung „Daten@Ende UND stall≥4" (verpasste den Loop) ist entfernt.
- Früher verifiziert: Resume byte-identisch; kontrolliertes Stoppen an der Tagesgrenze + Fortsetzen (Headless `--run … --stop-after N`); Not-Aus beendet nur die eigenen Prozesse (AtasReplayProbe + Python-Kinder), ATAS bleibt aktiv.

### 4.2 Verbleibende konkrete Grenzen
- **Nur MNQ** unterstützt/getestet.
- Replay-Transport sollte im Start-Modus **„Datum auswählen"** stehen.
- **Visueller OCR-Fallback** ist aktiv, wenn ATAS den Transport custom-rendert (0 UIA-Buttons). Hängt an OCR der Leisten-Beschriftungen; der Chevron-Aufklapp-Anker („OF Replay Recorder"-Label) kann fehlschlagen → deshalb ist Ribbon-Toggle primär.
- **GUI nicht per UI-Automation steuerbar, während sie hinter ATAS (quasi-Vollbild) liegt** — irrelevant für die normale Mensch-Bedienung; Automatisierung nur bei frisch gestarteter GUI oder via Headless (`Program.cs` Headless-Pfad, `--run`).
- ATAS schreibt in manchen Sitzungen **keine** Replay-Logzeilen → Enderkennung dann rein titel-/datenbasiert (durch Fix 3 abgedeckt).
- Keine verifizierte Kontraktidentität; aufgenommen wird, was ATAS als Replay liefert.
- **Keine Live-Execution, keine Broker-Anbindung, keine Orders.**

---

## 5. Vorhandene Collections (Manifestpfade, Zeitfenster, Trades)

Basis: `F:\ATAS_Data\Collections\`. Jede Collection hat `collection_manifest.json`, `trades.csv`, `coverage_report.txt`, `run_protocol.txt`.
CSV-Schema (`atas-collection-trades-1`), Spalten:
`session,year,job_id,recording_id,run_id,src_seq,recorder_seq,t_market_utc,instrument,contract_or_series,price,volume,aggressor_side,exch_order_id,aggr_exch_order_id,open_interest`

### 5.1 KANONISCHE vollständige Woche (Ziel für den Folgeauftrag)
**`MNQ_NewYork_0930-1200_America-New-York_2026-10-26_bis_2026-10-30`**
- Manifest: `F:\ATAS_Data\Collections\MNQ_NewYork_0930-1200_America-New-York_2026-10-26_bis_2026-10-30\collection_manifest.json`
- `trades.csv`: **920.333.836 B**, SHA-256 `CF46B44DEBDC78DA1D957911A5C5D5A147ABD7CE7398FDDCD5BD51877FD5389E`
- Fenster: täglich **09:30–12:00 America/New_York = UTC 13:30–16:00** (DST-korrekt), Speed 100x. Alle 5 Tage `committed`:

| Datum | job_id (UTC) | rows | volume |
|---|---|---|---|
| 2026-10-26 | NewYork_2026_20261026T133000Z_20261026T160000Z | 1.011.851 | 1.141.585 |
| 2026-10-27 | NewYork_2026_20261027T133000Z_20261027T160000Z | 823.530 | 962.024 |
| 2026-10-28 | NewYork_2026_20261028T133000Z_20261028T160000Z | 659.481 | 809.075 |
| 2026-10-29 | NewYork_2026_20261029T133000Z_20261029T160000Z | 1.192.766 | 1.346.551 |
| 2026-10-30 | NewYork_2026_20261030T133000Z_20261030T160000Z | 1.059.247 | 1.248.894 |
| **Σ** | | **4.746.875** | **5.508.129** |

### 5.2 Teil-Collections desselben korrekten Fensters (konsistente Teilmengen, NICHT eigenständig neu)
- `…0930-1200_America-New-York_2026-10-26_bis_2026-10-27` (26.+27., 355.630.530 B)
- `…0930-1200_America-New-York_2026-10-26_bis_2026-10-28` (26.–28., 483.376.816 B)
- `…0930-1200_America-New-York_2026-10-27_bis_2026-10-28` (27.+28., 284.947.296 B)
- Alle mit identischen job-rows wie oben (reconciliert).

### 5.3 ÄLTERE, ABWEICHENDE Sessionfenster — NICHT mit der Woche verwechseln
- `MNQ_NewYork_1530-1800_Europe-Berlin_2026-10-26_bis_2026-10-26` und `…_2026-10-26_bis_2026-10-28`
  - Labeling **15:30–18:00 Europe/Berlin**, job-IDs bei **UTC 14:30–17:00Z** → ein **anderes Marktfenster** als die korrekte NY-Session (UTC 13:30–16:00Z).
  - Abweichende, niedrigere Trades: 26.=616.917/723.776, 27.=488.873/586.645, 28.=353.006/442.476.
  - **Legacy/falsches Fenster** aus einem früheren Lauf — für den Research-Import NICHT verwenden.

### 5.4 Test-/Finder-Fragmente (ignorieren)
- `_cmp0800_2026`, `TestMix_2026`, `_findertest_2026-10-27` — winzige 1–3-Minuten-Testfenster (z. B. UTC 08:00–08:01), keine Produktionsdaten.

---

## 6. Aktueller ATAS-/Recorder-/Prozesszustand (read-only beobachtet)

- **ATAS:** `OFT.Platform.exe` läuft (PID 29028). Fenstertitel: **„ATAS - [BOT] [Im Wiedergabemodus]"** → ATAS ist aktuell **im Replay-Modus** (Rest aus dem letzten Lauf; Transportleiste vermutlich eingeklappt/offen). Kein separates sichtbares „Replay"-Top-Level-Fenster zum Zeitpunkt der letzten Prüfung.
- **Collector/Probe:** KEINE `AtasReplayProbe.exe`/`AtasSessionCollector.exe`-Prozesse aktiv.
- **Python:** nur PID 16148 = `jcodemunch-mcp.exe` (MCP-Server, **NICHT** die Collector-Bridge).
- **Recordings:** `F:\ATAS_Data\Recordings\` hat 143 Ordner, davon 5 ohne `summary.json`. Hinweis: `summary.json` entsteht erst beim Entfernen/Dispose des Recorders; „kein summary" ≠ „aktiv gesperrt". Nach einem sauberen Collector-Tag ist die Zahl aktiver gesperrter Recorder 0. Der echte Live-Lock-Zustand wurde NICHT erneut geprüft (kein ATAS-Eingriff in dieser Übergabe).
- Empfehlung vor dem nächsten echten Lauf: ATAS einmal in den normalen Chart zurück (bzw. `rmrec`), damit ein sauberer Startzustand besteht.

---

## 7. Nächster Auftrag: vorhandene Woche in das React-Research-Dashboard bringen (über den bestehenden Orderflow-Pfad)

**Ziel:** Die Woche aus 5.1 (`…26_bis_2026-10-30`) über den **bestehenden** Orderflow-/Research-Pfad im React-Research-Dashboard verfügbar machen.

### 7.1 Relevante Architektur (verifiziert per Code)
- UI: React (Vite) auf `http://127.0.0.1:5899`, Einstieg `/research`. API-Host: .NET DevDashboard auf `http://localhost:5034` (`/api/backtest`, `/api/quant`, `/api/rithmic`). API-Basis zentral `dashboard/src/lib/apiBase.ts` (override `VITE_API_URL`).
- React-Clients: `dashboard/src/lib/backtestApi.ts` (→ `:5034/api/backtest`), `dashboard/src/lib/quantApi.ts` (→ `:5034/api/quant`).
- Endpunkte (Minimal-API): `src/TradingBot.DevDashboard/Program.cs` — `/api/backtest/{strategies,instruments,data-sources,candles,run}`, `/api/quant/{status,benchmarks,analyze,jobs/walkforward,jobs/montecarlo}`.
- **Data-Sources:** `src/TradingBot.DevDashboard/Services/BacktestApiService.cs` → `GetDataSources()` (ab Zeile ~147). Liefert aktuell nur:
  1. `sierra-mesm26` (lokale Sierra-Tickdatei `A:\Projects\MARKET DATA\MESM26-CME.txt` → Zeitkerzen),
  2. `csv:<name>` aus `samples/ohlc/*.csv` (OHLC-CSV).
  **→ ATAS-Collections sind hier noch NICHT registriert.** Backtest-Engine ist `OhlcBacktestEngine` (konsumiert OHLC-Kerzen).
- **Bestehender ATAS-Orderflow-Importpfad:**
  - `src/TradingBot.Infrastructure/MarketData/Import/AtasOrderFlowBarCsvImporter.cs` + Profile `config/import-profiles/atas-orderflow-bar.example.json`, `config/import-profiles/atas-footprint.example.json` (`sourceType: OrderFlowBars`/`Footprint`).
  - Diese erwarten ATAS **OrderFlow-BAR-Export** (Spalten: `bartimestamp, symbol, OHLC, volume, bidvolume, askvolume, delta[, cumulativedelta]`).
  - Tick→Bar-Aggregation vorhanden: `src/TradingBot.Application/MarketData/OrderFlowBarAggregator.cs` (Bid/Ask-Volumen, Delta; wirft `OrderFlowUnavailableException`, wenn Aggressor-Klassifikation fehlt).

### 7.2 Kern-Lücke (wichtig)
Die Collection-`trades.csv` ist **Tick-/Trade-Ebene** (`t_market_utc, price, volume, aggressor_side, …`), **keine Bars**. Der vorhandene Importer/die Data-Sources erwarten OHLC-Kerzen bzw. OrderFlow-**Bars**. Es fehlt die Brücke „Collection-Ticks → (OrderFlow-)Bars/Kerzen → Data-Source im Research-Dashboard".

### 7.3 Mögliche Fortsetzung (NICHT begonnen — 0 Schritte erledigt)
- **Option A:** In `BacktestApiService.GetDataSources()` eine neue DataSource für ATAS-Collections ergänzen (z. B. Scan `F:\ATAS_Data\Collections\*`), plus Loader, der `trades.csv`-Ticks **streamend** über `OrderFlowBarAggregator` zu OrderFlow-Bars/Kerzen aggregiert; via `/api/backtest/data-sources` + `/candles` an die `/research`-Seite liefern.
- **Option B:** `trades.csv` → ATAS-OrderFlow-Bar-förmige CSV aggregieren und über `AtasOrderFlowBarCsvImporter` mit einem **echten** (nicht `.example`) Import-Profil in ein Data-Source-Verzeichnis importieren.
- **Entscheidung nötig:** reine OHLC-Kerzen (für `OhlcBacktestEngine`) vs. OrderFlow-Bars (Delta/Footprint-Research).
- **Zwingende Projektregeln (CLAUDE.md/AGENTS.md):** große Marktdaten nur **lokal + streamend** lesen (kein `ReadAllText`, kein `.ToList()` über ganze Datei — `trades.csv` der Woche ist 920 MB!); **kein Fake-Orderflow** (fehlt Aggressor/Bid/Ask → `InsufficientData`); NetPnL nach Fees/Slippage; Dashboard/Research referenzieren NICHT `TradingBot.Execution`.

### 7.4 Fortschritt dieses Auftrags
**Noch nicht begonnen** — in dieser Session wurde nur diese Übergabe geschrieben. Erste konkrete Fortsetzungsstelle: `src/TradingBot.DevDashboard/Services/BacktestApiService.cs:147` (`GetDataSources`) bzw. `AtasOrderFlowBarCsvImporter` + Import-Profil.

---

## 8. Leitplanken für Codex
- Collector bleibt unverändert. Keine Orders, keine Datenlöschung, kein Git-Commit/Push/Merge ohne Freigabe.
- Keine Zugangsdaten speichern. Hindsight nur lesend. Frühere Berichte (`VERSION.txt`, Hindsight-Bank `FuturesOrderflowBot`) erhalten.
- Research/Simulation-only; keine Phase 13 / Live-Execution; keine Broker-Calls.
