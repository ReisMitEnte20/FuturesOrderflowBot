# Claude — Fixes zu CODEX_REVIEW_2026-10-30 (Runde 2)

**Nur Codeänderungen + Offline-Tests. Kein Commit/Push/Merge. Keine ATAS-UI/Replay-Aktion. Keine neue
DLL in ATAS deployed.** Dies ist KEINE unabhängige Review; Codex prüft diese Fixes separat.

Bearbeitet wurden ausschließlich die fünf belegten P1-Findings aus `CODEX_REVIEW_2026-10-30.md`.
Für jedes: Gegenbeispiel zuerst als **fehlschlagender** Test gegen den bisherigen Produktionscode
reproduziert, dann kleinster Fix, dann derselbe Test **bestanden** — gegen den **tatsächlichen
Produktionsablauf**, nicht gegen isolierte boolesche Helfer.

- Branch `feature/atas-replay-control-probe`, HEAD `16f5d76b` (unverändert, **kein Commit**).
- Working tree untracked wie zuvor: `research/`, `tools/`, `dashboard/dist-demo/` (erhalten).
- Recorder-Quelle extern auf F: (erhalten, in place korrigiert). Neue gemeinsame Kern-Quelle auf A:.
- Toolchain: .NET SDK 8.0.422, Python 3.13.9, pyarrow 26.0.0, pytest 9.2.0.

---

## Übersicht

| # | Thema | Ursache (belegt) | Fix | Regressionstest vorher→nachher |
|---|---|---|---|---|
| 1 | falsche Enderkennung | `MarketProgress` zählt fremde/live/preload-tMarkts; `EndReached` hat 1s-Toleranz | fenster-gebundener `ReplayProgress` + `GenuineEndReached` (Baseline nach Play); Toleranz entfernt | ProbeChecks: EndReached=True (mixed/−1s/preload) → `regress` 5/5 reached=false/true korrekt |
| 2 | unvollständiger Cleanup | Cleanup am Methodenende (kein finally); `finalized` ignoriert `clean`/`runId`; Rest-Writer nur Hinweis | `try/finally` garantiert Cleanup; `RecorderFinalizedClean` fordert clean+runId+Writer-Ende; Rest-Writer ⇒ Fehler | `regress` A2 6/6 (alt/fremd/clean=false/gesperrt abgelehnt) |
| 3 | Writer-Lebenszyklus | kein terminaler Zustand (Restart nach Dispose); Flush-Fehler verschluckt ⇒ clean=true; Zähler-Race | `RecorderCore` mit terminalem Zustand, In-Flight-Barriere, gemerkten Flush-Fehlern, getrenntem Flush/Dispose | WriterHarness: restart=True, transient clean=True → `RecorderCore` 7/7 |
| 4 | unbewiesene Zuordnung | Filter ohne Auftrags-ID; `AttributionVerdict(int)`; Größte-Fläche-Fallback | Token-Handshake (`.of_run_token`→meta/summary); `AttributionVerdict(tokens,token)`; Fallback entfernt | ProbeChecks: fremder MNQ=Unique → `regress` A4 4/4 (fremd=None, Token=Unique, 2=Ambiguous) |
| 5 | Exportintegrität | datetime-µs-Kürzung; Hash nach Lesen; Status ignoriert Fehler; Ziel überschrieben (partiell) | ns-Parsing; pre/post-Snapshot; fehler-bewusster Status; atomare Veröffentlichung + Ziel-Ablehnung | `bridge_baseline` 4/4 defekt → pytest **19 passed** (8 neue) |

---

## Finding 1 — Enderkennung

- **Ursache:** `MarketProgress` nahm den spätesten `tMarket` über alle Streams **ohne** Fenster-/Herkunftsbindung;
  ein Live-Event (heutiges Datum) oder ferner Preload (08:03) hob den Fortschritt über das Fensterende.
  `EndReached` verlangte nur `≥ Ende−1s`.
- **Fix:** `research/atas-replay-probe/Program.cs`
  - `ReplayProgress(streams, winStart, winEnd)` — nur `tMarket` innerhalb `[winStart−5s, winEnd+5s]` (Live/ferner Preload fällt raus).
  - `EndReached(progress, winEnd)` — **ohne** Toleranz (`≥ winEnd`).
  - `GenuineEndReached(streams, baseline, winStart, winEnd)` — verlangt zusätzlich Fortschritt **nach** Play
    (Baseline bei Play-Start `< Ende`; ein vorab geladenes End-Signal zählt nicht).
  - `AutoRun` erfasst die Baseline direkt nach Play und nutzt `GenuineEndReached` (keine Grenz-Trade-Pflicht).
- **Regressionstest (echter FolderStreams-Pfad):** `regress` legt Datei-Fixtures an → echte `FolderStreams()` → echte Entscheidungsfunktionen.
  - vorher (ProbeChecks.dll, Pre-Fix-DLL): `mixed live+historisch=True`, `end−1s=True`, `preload 08:03=True`.
  - nachher: alle drei `NICHT reached`; genuine Enderreichung ohne Grenz-Trade `reached`; Preload-am-Ende `NICHT genuin`. **5/5.**
- **Testumfang:** Entscheidungslogik über reale Dateistände. **Nicht** getestet: ob ATAS live tatsächlich DOM/BBO/Cumulative mit diesen Mustern liefert.
- **Verbleibender Nachweis:** Live-Lauf, dass ein echtes Nicht-Erreichen (Timeout/Stopp) abgelehnt wird.

## Finding 2 — Cleanup

- **Ursache:** `CleanupAfterRun` war ein normaler Aufruf am Ende; `EnsureModule`-Fehler (`return 5`) und Exceptions
  umgingen ihn. `finalized = hasSummary && !locked` ignorierte `clean` und verglich keine `runId`; Rest-Writer war nur ein Hinweis.
- **Fix:** `Program.cs`
  - `AutoRun` vollständig in `try/finally`; Blocker als `AbortRun`-Exception ⇒ Cleanup läuft **garantiert** (auch bei früher Rückkehr/Exception).
  - Jeder zugeordnete Auftrag wird in `attributed` (Ordner+Token) registriert und im finally geprüft.
  - `RecorderFinalizedClean(hasSummary, locked, summaryJson, expectedRunId)` = Summary + Writer-Ende + `clean`(A3) + `runId`-Match.
  - Verbleibende gesperrte Instanz nach Replay-Verlassen ⇒ Cleanup **nicht** erfolgreich (kein bloßer Hinweis).
- **Regressionstest:** `regress` A2 — alte Summary ohne `clean`, `clean=false`, fremde `runId`, fehlende Summary, gesperrter Writer ⇒ alle **nicht finalisiert**; nur clean+runId+entsperrt ⇒ finalisiert. **6/6.**
- **Testumfang:** Finalisierungs-Entscheidung + statische Kontrollfluss-Garantie (try/finally). Konsistent mit Review: kein Live-UI-Fehler injiziert.
- **Verbleibender Nachweis:** Live-Injektion von Fehlern nach Replay-Öffnung/Zuordnung/Playback; **autonomes Recorder-Entfernen** bleibt UI-Lücke (Custom-WPF-Dialog, nicht UIA) ⇒ ein sauberer Live-Abschluss des **letzten** Fensters ist ohne Dispose-Load/manuelles Entfernen offen.

## Finding 3 — Writer-Lebenszyklus

- **Ursache:** `_running=false` war Annahmestopp **und** Startbedingung (Callback nach Dispose startete neu);
  periodischer Flush-Fehler wurde verschluckt (später clean=true); Guard/TryAdd/Count nicht mit Dispose synchronisiert;
  finaler Flush-Fehler übersprang `Dispose` (Handles offen); Summary-Enqueue-Fallback nach `_running=false` wirkungslos.
- **Fix:** neue gemeinsame Kern-Quelle `research/recorder-core/RecorderCore.cs` (ATAS-frei), vom Indicator genutzt:
  - terminaler Zustand `Idle→Running→Disposing→Disposed` (kein Neustart nach Dispose).
  - `Enqueue` mit In-Flight-Zähler; `Finish` wartet die laufenden Producer ab, **dann** `CompleteAdding`/Join/Summary (Zähler konsistent).
  - jeder Flush-Fehler setzt dauerhaft `writerFaulted`; `Flush` und `Dispose` getrennt gekapselt (Handles schließen trotz Flush-Fehler).
  - Summary-Schreibfehler: Retry + `summary_error.txt` + `summaryWriteFailed` (kein toter Enqueue-Fallback).
  - nach Annahmestopp abgewiesene Events werden als `rejectedAfterStop` bilanziert; eindeutiger Ordnername (Sekunde+runId-Fragment).
  - `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs` ist jetzt die reine SDK-Callback-Schicht über `RecorderCore`.
- **Regressionstest (echter Lebenszyklus):** `research/recorder-core/tests` kompiliert **genau** `RecorderCore.cs` (kein Copy-Harness) und übt Start/Enqueue/WriterLoop/Finish/Summary.
  - vorher (Codex WriterHarness.dll, extrahierter Pre-Fix-Code): `callback_after_dispose_restarts_writer=True`, transienter Flush ⇒ `summary_clean=True, writerFaulted=False`.
  - nachher: kein Restart; transienter Flush ⇒ clean=false/faulted=true; finaler Flush-Fehler ⇒ Handles geschlossen, clean=false; Join-Timeout ⇒ joinTimedOut/clean=false; mehrfaches Finish sicher; 500 Events ⇒ Dateizeilen=Summary=500/clean=true; Events nach Stop ⇒ `rejected≥5`. **7/7.**
- **Testumfang:** die geänderte Lifecycle-Klasse direkt (Produktionscode). **Nicht** getestet: ATAS' reale Callback-Reihenfolge/Threading.
- **Verbleibender Nachweis:** Live-Lauf mit der neuen DLL (frische `clean`-Summary). Build-Artefakt (siehe unten) ist **nicht** in ATAS deployed.

## Finding 4 — Chart-/Recorder-Zuordnung

- **Ursache:** Filter aus gesperrtem Ordner + Startzeit + „MNQ" ohne Auftrags-/Chart-ID; eine im Steuerungsprozess
  gesetzte Env-Variable erreicht das laufende ATAS nicht; `EnsureModule` nahm als Fallback die **größte** Chart-Fläche.
- **Fix:** `Program.cs` + Recorder.
  - **Token-Handshake:** `AutoRun` schreibt vor dem Laden ein eindeutiges Token nach `<Recordings>\.of_run_token`;
    der Zielrecorder liest es (`ReadRunToken`) und schreibt es als `runId` in meta/summary; `AutoRun` verlangt exakt dieses Token zurück.
  - `AttributionVerdict(candidateTokens, expectedToken)` — 0 Treffer=None, 1=Unique, >1=Ambiguous; ohne Token-Match **keine** Zuordnung.
  - `EnsureModule`: **kein** Größte-Fläche-Fallback mehr; Zielchart über eindeutigen Header (`ATAS_TARGET_CHART`, Default „MNQ 4RXV"); bei Mehrdeutigkeit Abbruch.
- **Regressionstest:** `regress` A4 — einziger fremder MNQ-Recorder (ohne Token) ⇒ **None**; korrekter Token-Recorder ⇒ **Unique**; zwei Token-Treffer ⇒ **Ambiguous**; leeres Token ⇒ None. **4/4.** vorher (ProbeChecks): fremder MNQ ⇒ `Unique`.
- **Testumfang:** Zuordnungslogik über reale meta-Dateien. **Nicht** getestet: Live-Verhalten der ATAS-Env/Chart-Identität.
- **Verbleibender Nachweis:** Live, dass der Zielrecorder das Token wirklich konsumiert und ein fremder Chart live abgelehnt wird.

## Finding 5 — Exportintegrität der Brücke

- **Ursache:** `parse_iso` kürzte auf datetime-µs (Sub-µs-Grenzen falsch); Hashes nach dem Lesen berechnet
  (Manifest beschrieb evtl. andere Eingabe); `status=complete` ignorierte bad-json/Summary/Diskrepanzen; Ziel wurde
  direkt überschrieben (partielles Parquet bei Abbruch).
- **Fix:** `research/data-bridge/atas_bridge.py`
  - `parse_iso_ns` — ganzzahlige **Nanosekunden**; Fensterfilter + Spalten `t_market/t_recv` verlustfrei (`int64`→`timestamp[ns]`).
  - pre/post-SHA-256-Snapshot der Eingaben; Änderung während der Konvertierung ⇒ `SourceChangedError`, **kein** Export.
  - `qualityFlags.status`/`statusReasons` beziehen bad-json, fehlende/`clean≠true`-Summary, CSV/NDJSON-Diskrepanz, off-grid ein.
  - atomare Veröffentlichung (temp→validieren→`os.replace`); bestehendes Ziel ohne `--force` ⇒ `TargetExistsError`.
- **Regressionstest:** `research/data-bridge/test_atas_bridge.py` (echte `convert`-Aufrufe + Parquet-Rücklesen).
  - vorher (`baseline/bridge_baseline.json`): 3 Sub-µs-Zeiten → 1 distinkter ns-Wert; Fenstergrenze `.0000002` → 3 statt 2; mutierte Quelle ⇒ `status=complete`, Hash der geänderten Eingabe; bad-json ⇒ `complete`; Abbruch ⇒ PARTIAL-Parquet.
  - nachher: 8 neue Tests **bestehen** (ns verlustfrei, Grenze exklusiv, `SourceChangedError`, Status `check`, Ziel-Ablehnung, intaktes Ziel bei Abbruch). Gesamt **19 passed**. Die 8 neuen Tests **scheitern** gegen die Pre-Fix-Brücke (`baseline/pytest_bridge_before.txt`: 8 failed).
- **Reale Aufnahme (read-only):** `MNQ_20261029_232000Z`, Fenster 08:00–08:01 → 2491 total / 2464 im Fenster / 27 Nachlauf / Vol 2701 (unverändert), `status=check` + `statusReasons=[summary_not_clean_or_missing]` (Alt-Aufnahme ohne A3-`clean` korrekt **nicht** als complete), `inputSnapshotStable=true`.
- **Testumfang:** Produktions-`convert`-Pfad. **Nicht** Lasttest/30-TB (batch/rowgroup bleibt Voraussetzung vor Skalierung).

---

## Geänderte / neue Dateien

- `research/data-bridge/atas_bridge.py` — Finding 5 (ns, Snapshot, Status, atomar).
- `research/data-bridge/test_atas_bridge.py` — +8 Regressionstests (Finding 5).
- `research/data-bridge/README.md` — Integritäts-Doku.
- `research/recorder-core/RecorderCore.cs` — NEU, ATAS-freier Lifecycle-/Writer-Kern (Finding 3).
- `research/recorder-core/tests/{RecorderCoreTests.csproj,Program.cs}` — NEU, Regressionstest Finding 3.
- `research/atas-replay-probe/Program.cs` — Findings 1/2/4 (ReplayProgress/GenuineEndReached, RecorderFinalizedClean,
  AttributionVerdict(Token), try/finally-Cleanup, Token-Handshake, EnsureModule ohne Größte-Fläche, `regress`-Kommando).
- `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs` — SDK-Callback-Schicht über `RecorderCore` (Finding 3, extern auf F:).

## Ausgeführte Befehle + Ergebnisse

```
# Finding 5 (Brücke)
python -I baseline/bridge_baseline.py <bridge>           -> 4/4 Defekte reproduziert (baseline/bridge_baseline.json)
python -I -m pytest test_atas_bridge.py -q               -> 19 passed  (after/pytest_bridge_after.txt)
  (8 neue Tests gegen Pre-Fix-Brücke)                    -> 8 failed   (baseline/pytest_bridge_before.txt)

# Finding 3 (Recorder-Kern)
dotnet exec <REVIEW>/WriterHarness.dll <dir>             -> restart=True, transient clean=True (baseline/recordercore_before.txt)
dotnet run (recorder-core/tests)                         -> RECORDERCORE: ALLE BESTANDEN 7/7 (after/recordercore_after.txt)
csc OrderFlowReplayRecorder.cs + RecorderCore.cs @build  -> 25600 B Build-Artefakt (after/recorder_build.txt, NICHT deployed)

# Findings 1/2/4 (Probe)
dotnet exec <REVIEW>/ProbeChecks.dll <dir>               -> EndReached=True (mixed/−1s/preload), foreign=Unique (baseline/probe_before.txt)
dotnet build -c Release (atas-replay-probe)              -> 0 Fehler
dotnet run -- regress <F:\...>                           -> REGRESS: ALLE BESTANDEN 18/18 (after/probe_regress_after.txt)
```

## Build-Artefakt / ATAS-Status

- Volles ATAS-Indicator-DLL kompiliert als **Artefakt** nach `F:\…\Fixes_2026-10-30\build\OrderFlowReplayRecorder.dll`
  (25600 B, 1 vorbestehende Nullable-Warnung).
- **NICHT** in ATAS deployed: `%APPDATA%\ATAS\Indicators\OrderFlowReplayRecorder.dll` unverändert (22016 B, Stand 16:59).
  `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.dll` ebenfalls unverändert (22016 B).

## Rohdaten-Unverändertheit

- Verwendete Aufnahme `F:\ATAS_Data\Recordings\MNQ_20261029_232000Z`: alle 7 Rohdateien SHA-256-identisch zur
  Review-Baseline (`final_hashes.json`), zusätzlich vor/nach der Konvertierung in dieser Runde geprüft (gleich).
  Die Brücke liest nur; neue Ausgaben landen in frischen Zielen auf F:. Alt-Leftover auf C: nicht angefasst.

## Offen (ehrlich)

- Live-Abnahme aller UI-nahen Teile (A1 echtes Nicht-Erreichen, A2 Fehler-Injektion, A4 Live-Token-Konsum, A3 frische clean-Summary)
  erfordert einen ATAS-Replay-Lauf mit der neuen DLL — **in dieser Runde bewusst nicht** (nur Code/Offline).
- Autonomes Recorder-Hinzufügen/-Entfernen bleibt UI-Lücke (Indikator-Dialog Custom-WPF, nicht UIA).
- Kein Serienbetrieb behauptet.

Hindsight: Bank `FuturesOrderflowBot` nur **lesend** (recall; keine Writes, kein Löschen). |
CodeMunch: Index `ReisMitEnte20/FuturesOrderflowBot` aufgelöst; Symbol-/Zeilensuche in `Program.cs` statt Voll-Reads.
