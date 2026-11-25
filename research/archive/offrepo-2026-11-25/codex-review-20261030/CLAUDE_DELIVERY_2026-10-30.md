# Claude-Lieferung für Codex-Review — 2026-10-30

Reviewbare Änderung + Nachweise. **Kein Commit/Push/PR/Merge.** Dies ist KEINE unabhängige Review.
Frühere Review-Nachweise in diesem Ordner bleiben erhalten (HANDOFF.md unverändert).

## 1. Kurz: Was funktioniert nachweislich (offline verifiziert)
- **Teil A, Entscheidungslogik (A1/A3/A4)**: als pure, getestete Funktionen implementiert und in `autorun` verdrahtet.
  C#-`selftest` grün (12 Checks).
- **Teil A, Recorder-Abschlussreihenfolge (A3)**: `OnDispose` korrigiert (erst Annahme stoppen → drainen/joinen →
  DANN Summary mit `clean`-Status). Recorder-DLL kompiliert (22016 B) und in den ATAS-Ladeordner deployed.
- **Teil B, Offline-Datenbrücke**: ATAS-Aufnahme → `trades.parquet` + `manifest.json`. 11 pytest grün; auf einer realen
  Aufnahme ausgeführt (2464 Trades im Fenster, Nachlauf korrekt gefiltert, Rohdaten unverändert).

Noch NICHT bewiesen: ein vollständiger **Live-Kaltstart-Lauf mit den neuen Fixes** (siehe Abschnitt 8 Grenzen).

## 2. Die fünf Befunde
| # | Status | Codepfad | Nachweis |
|---|---|---|---|
| A1 Endabdeckung | **behoben** | `research/atas-replay-probe/Program.cs`: `EndReached(..)`, Play-Schleife nutzt `EndReached`, danach `if(!reachedEnd) ABBRUCH`; Marktfortschritt über ALLE Streams via `MarketProgress(..)`; Trade-Abdeckung getrennt berichtet | `selftest` (EndReached: Ende-2s→Fehler, null→Fehler); Bridge filtert `[start,end)` real (27 Nachlauf-Ticks ausgeschlossen) |
| A2 Gemeinsamer Abschluss | **teilweise** | `Program.cs`: `CleanupAfterRun(..)` auf ALLEN Pfaden (Pause + Replay verlassen + Finalisierungsprüfung), `cleanupOk` fließt in Erfolg ein | Code kompiliert; **Grenze**: Recorder-Entfernen vom Chart ist NICHT UIA-automatisierbar (Dialogzeilen Custom-WPF) → nur Rest-Instanz-Hinweis, keine autonome Entfernung |
| A3 Writer/Summary-Reihenfolge | **behoben** | `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs`: `OnDispose` reihenfolgekorrekt; `clean/writerFaulted/joinTimedOut` in Summary (`schemaVersion=ofrec-summary-2`); Enqueue-Race-Guard | DLL kompiliert+deployed; `selftest` `SummaryCleanClose` (alte Summary ohne clean → nicht belegt, fault/timeout → nicht clean) |
| A4 Eindeutige Zuordnung | **behoben** | `Program.cs`: Attribution über NEUE gesperrte Instanz **+ meta.startWallUtc≥loadClick + Instrument + Eindeutigkeit** via `AttributionVerdict(..)`; mehrdeutig → Abbruch; `runId` aus meta gelesen | `selftest` (0→None, 1→Unique, 2→Ambiguous abgelehnt); Recorder schreibt `runId` in meta+summary |
| A5 Kaltstart | **teilweise** | `Program.cs`: autorun bricht bei 0 korrelierten Instanzen mit konkretem Befund ab (laeuft NIE ohne korrekt angehängten Recorder); kein Doppel-Add, da nur bei Abwesenheit addiert würde | **Grenze**: automatisches Hinzufügen braucht den Indikator-Katalog (Custom-WPF, nicht UIA); manuell-verifizierte Sequenz existiert (`indicators`+`poke dclick`+`end-recorder`), aber nicht autonom |

## 3. Geänderte/neue Dateien, Branch/HEAD
- Branch `feature/atas-replay-control-probe`, HEAD `16f5d76b` (unverändert; **kein Commit**).
- Untracked im Repo (A:), NICHT gestaget: `research/`, `tools/`, `dashboard/dist-demo/` (unangetastet gelassen).
- Geändert/neu:
  - `research/atas-replay-probe/Program.cs` — A1/A2/A4-Fixes, pure Funktionen (`EndReached`,`AttributionVerdict`,`SummaryCleanClose`,`MarketProgress`,`ReadMetaField`), `selftest`-Kommando, `CleanupAfterRun`.
  - `research/data-bridge/atas_bridge.py`, `test_atas_bridge.py`, `README.md` — NEU (Teil B).
  - `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs` — A3-Fix (liegt auf F:, nicht im Git-Repo).
  - `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.dll` — neu gebaut (A3); deployed nach `%APPDATA%\ATAS\Indicators\` (Addon-Ladeordner bleibt auf C:).

## 4. Tatsächlich ausgeführte Tests (offline)
- `dotnet run -c Release -- selftest` → **ALLE BESTANDEN** (A1 EndReached ×4, A4 AttributionVerdict ×3, A3 SummaryCleanClose ×6).
- `python -m pytest research/data-bridge/test_atas_bridge.py -q` → **11 passed**.
- `dotnet build -c Release` (Probe) → 0 Fehler. Recorder-DLL `csc @build_f.rsp` → 0 Fehler.

## 5. Live-Nachweise (getrennt von Offline-Tests/statischer Prüfung)
- Letzter berichteter Live-Lauf ist `autorun v10` (vor diesen Fixes; HANDOFF Abschnitt „LETZTER BERICHTETER ERFOLG").
- Mit den neuen A1–A5-Fixes wurde **in dieser Runde KEIN neuer Live-Replay-Lauf** durchgeführt (bewusst: „keine endlosen/
  wiederholten UI-Versuche"; zudem war die Replay-Repository-Verbindung zeitweise abgelehnt). Die Fixes sind offline
  (selftest/pytest) und statisch (Code) belegt, nicht per neuem Live-Lauf.
- Offene Live-Verifikation: A3-`clean`-Feld in einer frischen Summary; A1-Abbruch bei echtem Nicht-Erreichen; A2/A5 UI.

## 6. Beispielmanifest (Datenbrücke), reale Aufnahme
Quelle `F:\ATAS_Data\Recordings\MNQ_20261029_232000Z`, Fenster `[2026-10-26T08:00:00Z, 08:01:00Z)`:
```
instrument: {symbolLabel: MNQ, venue: unknown, contractOrSeries: unknown,
             tickSize: 0.25, observedPriceGrid: 0.25, offGridVsTickSize: 0}
coverage:   {tradesInWindow: 2464, tradesTotalInFile: 2491, restTicksBefore: 0, restTicksAfter: 27,
             firstTradeInWindowUtc: 2026-10-26T08:00:00.002980Z, lastTradeInWindowUtc: 2026-10-26T08:00:59.899479Z}
trades:     {rows: 2464, totalVolume: 2701, buyVolume: 1452, sellVolume: 1249, unknownVolume: 0}
reconciliation: {ndjson_lines_total: 2491, ndjson_trade_events_total: 2491, csv_data_rows: 2491,
                 summary_ticks_events: 2491, csv_eq_trades: true, summary_eq_ndjson_lines: true, summary_clean: null}
```
`summary_clean: null` → Alt-Aufnahme ohne A3-Statusfeld: sauberer Abschluss NICHT belegt (korrekt nicht geglättet).
Ausgabe: `F:\ATAS_Data\Bridge\MNQ_20261029_232000Z_0800\{trades.parquet, manifest.json}`.

## 7. Exakte Start-/Prüfbefehle
```
# Teil A — Entscheidungslogik offline
cd A:\Projects\FuturesOrderflowBot\research\atas-replay-probe
dotnet build -c Release
dotnet run -c Release -- selftest

# Teil B — Brücke + Tests
cd A:\Projects\FuturesOrderflowBot\research\data-bridge
python -m pytest test_atas_bridge.py -q
python atas_bridge.py "F:\ATAS_Data\Recordings\<ordner>" --start 2026-10-26T08:00:00Z --end 2026-10-26T08:01:00Z --out "F:\ATAS_Data\Bridge\<ziel>"

# Live-Autorun (nur mit vorbereitetem, verbundenem, datums-basiertem Replay; nicht Teil dieser Runde)
dotnet run -c Release -- autorun autorun.txt
```

## 8. Bekannte Grenzen / offene Zuordnungen
- **A2/A5 autonom unvollständig**: Indikator-Dialog (Katalog + „Hinzugefügt"-Zeilen) ist Custom-WPF und NICHT im UIA-Baum;
  autonomes Add/Remove bräuchte entweder einen UIA-Pfad zu diesen Zeilen oder Screenshot-OCR (LLM-in-loop). Ohne LLM im
  laufenden Programm ist nur die manuell-verifizierte Sequenz möglich. autorun verhindert aber Läufe OHNE Recorder (A4-Abbruch).
- **Kaltstart-Modul/Modus** (frisch zurückgesetztes Replay) bleibt nicht deterministisch (Fensteraktivierungs-Parität auf
  Custom-Transport). Bei bereits datums-basiertem Replay lief autorun v10 durch.
- **Replay-Repository-Verbindung** war zeitweise abgelehnt („Repository Connection Login Failed") → datums-basiertes Replay
  (historisches Laden nach Datum) setzt eine funktionierende Verbindung voraus.
- **Kontrakt/Serie = unknown**: Symbollabel #MNQZ6/MNQ beweist NICHT die historische Quellserie; Venue unknown.
- **DOM/Quotes**: eigene, HIER NICHT qualifizierte Typen (nur Präsenz+Hash+Count); kein künstliches L2/L3 aus Trades.
- „0 lokale Drops" ≠ Anbieter-Vollständigkeit; `summary.clean` ≠ korrekte Chart-Zuordnung.

## 9. ATAS-Endzustand + Speicherorte
- ATAS läuft; **keine gesperrten Recorder-Instanzen** (kein offener Writer) nach dem DLL-Reload.
- Aufnahmen: `F:\ATAS_Data\Recordings`. Brücken-Ausgabe: `F:\ATAS_Data\Bridge`. Nachweise: `F:\ATAS_Data\Evidence`.
- Recorder-Quelle/DLL/Build: `F:\ATAS_Data\Recorder`. Geladene DLL: `%APPDATA%\ATAS\Indicators\OrderFlowReplayRecorder.dll` (C:, Addon-Ladeordner, bleibt).
- Alt-Leftover `C:\…\ATAS_OF_Recordings\MNQ_20261029_223429Z` unverändert (nicht gelöscht/migriert).
- Code im Repo: `A:\Projects\FuturesOrderflowBot\research\{atas-replay-probe,data-bridge}` (untracked, kein Commit).

## 10. Keine unbelegten Schlüsse
Ausdrücklich NICHT behauptet: „Datei gesperrt → richtiger Chart", „Summary vorhanden → alles geschrieben",
„0 Drops → Anbieter vollständig", „Ticks vorhanden → ganzes Fenster", „Tests grün → unbeaufsichtigter Serienbetrieb bewiesen".
