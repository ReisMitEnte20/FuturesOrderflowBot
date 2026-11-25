# Claude — Fixes zur CODEX-NACHPRÜFUNG (CODEX_RECHECK_2026-10-30)

**Nur Implementierung + Offline-Tests. Kein Commit/Push/PR/Merge. Keine ATAS-Aktion/Replay/DLL-Deployment.
Keine Rohdatenänderung.** Codex prüft diese Fixes separat. Bereits geschlossene Teilbefunde wurden nicht neu bearbeitet.

- Branch `feature/atas-replay-control-probe`, HEAD `16f5d76b` (unverändert, **kein Commit**).
- Untracked wie zuvor: `research/`, `tools/`, `dashboard/dist-demo/` (erhalten). Externer Recorder auf F: erhalten/in place korrigiert.
- Toolchain: .NET SDK 8.0.422, Python 3.13.9, pyarrow 26.0.0, pytest 9.2.0.

## Zuordnung: offener Befund → Mechanismus → Funktion → Abnahmetest

| # | Mechanismus (laut Recheck) | Betroffene Funktion | Abnahmetest (Produktionspfad) |
|---|---|---|---|
| 1 | Zeitnähe/Baseline beweist keine abgespielte Strecke; `[Ende+5s]` verwirft späten Grenzübertritt; null-Baseline akzeptiert | `P.ReplayProgress/GenuineEndReached` → **ersetzt durch** `P.PlayedEndReached`; `AutoRun` Play-Loop | `regress` A1 (echte `FolderStreams`/`PlayedEndReached` über Dateien mit tMarket+tRecv) |
| 2 | `CleanupAfterRun` im finally ungeschützt; `Directory.GetDirectories` kann werfen → überspringt Abschluss, ersetzt Abbruch-Code | `P.CleanupAfterRun`, `AutoRun`-finally → neuer robuster `AllFinalizedClean` | `regress` A2 „cleanup robust…" (Fehler mitten im Ablauf, alle Aufträge geprüft, nichts wirft) |
| 3a | Barriere-Timeout wird nicht als Fehler behandelt → `clean=true` trotz offener Producer | `C.Finish` | `RecorderCore` Test „barrier_timeout…" (echtes `Finish`) |
| 3b | Indicator vor erstem Callback nicht terminal → späterer Callback startet neuen Kern | `I.Core/OnDispose` → **neuer** `CoreGate` | `RecorderCore` Test „gate_dispose_before_first_callback…" (echtes Gate) |
| 4 | globales Token mehrfach lesbar; zwei Recorder → selber Ordner; Abbruch löscht fremdes Token; EnsureModule-Frühausstieg | `I.ReadRunToken`, `C.Start`-Ordner, `P.AutoRun`/`EnsureModule` → **neuer** `TokenHandshake` + echte Instanz-ID | `RecorderCore`/`regress` Token-Tests (exklusiver Konsum, Owner-Retract, distinkte Ordner) |
| 5 | Snapshot endet vor CSV-/OtherStream-Lesen; Veröffentlichung zweiteilig; Ziel-Race | `B.convert` (Snapshot + Publish) | `pytest` (24) + `bridge_recheck_after` (identische Codex-Szenarien) |

---

## Finding 1 — Enderkennung (abgespielte Strecke binden)

- **Ursache:** Fortschritt wurde nur über Fensternähe (`ReplayProgress` in `[Start−5s, Ende+5s]`) + eine 2,5 s nach Play
  gebildete Baseline bewertet. Ein **verspäteter Preload** am Ende und eine **null-Baseline** ergaben `reached=True`;
  ein erst nach `Ende+5s` sichtbarer **echter** Grenzübertritt ergab fälschlich `reached=False`.
- **Änderung (`P`):** `GenuineEndReached` entfernt; neu `PlayedEndReached(dir, playInvokeUtc, winEnd, loadMargin)`:
  ein Event zählt nur, wenn `winEnd ≤ tMarket ≤ winEnd+loadMargin` **und** `tRecv ≥ playInvokeUtc` (nach Play empfangen).
  Damit sind Preload/Historie (tRecv vor Play) **und** Live (tMarket jenseits der geladenen Spanne) ausgeschlossen; ein
  belegter Grenzübertritt bleibt erhalten (vollständiger Stream-Scan, kein „nur letzte Zeile"). `AutoRun` erfasst
  `playInvokeUtc` unmittelbar vor `Invoke(Play)` und nutzt `PlayedEndReached`. Kein Trade an der Grenze nötig.
- **Identisches Gegenbeispiel vorher/nachher** (`ProgressChecks.dll` vs `regress`):
  - vorher: `late_preload_after_baseline=True`, `late_preload_null_baseline=True`, `first_valid_progress_end_plus_6=False`.
  - nachher: verspäteter Preload (tRecv<Play) → **NICHT** reached; Ende+6s abgespielt → **reached**; Live-Event → **NICHT** reached;
    Ende−1s → **NICHT** reached; abgespielte Grenze ohne Grenz-Trade → **reached**. (6/6)
- **Produktionspfad im Test:** reale `FolderStreams`/`PlayedEndReached` über echte Stream-Dateien mit `tMarket`+`tRecv`.
- **Verbleibende Grenze:** `tRecv≥Play` ist ein daten-/aufnahmezeit-gebundener Herkunftsbeleg, **keine** Live-bestätigte
  Replay-Zustandsbindung. Endgültige Herkunft nur per kurzem realen Lauf verifizierbar (offen, nicht Teil dieser Offline-Runde).

## Finding 2 — Cleanup kann sich nicht mehr selbst abbrechen

- **Ursache:** `CleanupAfterRun(...)` lief im `finally` ungeschützt; z. B. `Directory.GetDirectories` (Rest-Instanz-Scan)
  konnte eine IOException/UnauthorizedAccessException werfen → restliche Abschluss-/Statusausgaben übersprungen und der
  Abbruch-Exitcode durch Main-Fehlercode 2 ersetzt. Eine frühe Cleanup-Exception konnte zudem weitere Ordner-Prüfungen verhindern.
- **Änderung (`P`):** jeder Cleanup-Schritt ist einzeln gekapselt und wirft nie aus `CleanupAfterRun` heraus; neue robuste
  `AllFinalizedClean(attributed, probe, W)` prüft JEDEN Auftrag einzeln (wirft ein Zugriff, gilt der Auftrag als nicht
  finalisiert, die übrigen werden dennoch geprüft); der Rest-Instanz-Scan ist try-gekapselt (Fehler → konservativ nicht
  erfolgreich); der `AutoRun`-`finally` kapselt Cleanup und C:-Prüfung, sodass er nie wirft und den Abbruch-Code erhält.
- **Gegenbeispiel vorher/nachher:** statisch belegt (Recheck). Nachher: `regress` A2 „cleanup robust" — eine IOException bei
  einem von drei Aufträgen wirft nicht, alle drei werden geprüft (`seen=3`), Gesamt = **nicht ok**; alle sauber → ok.
- **Produktionspfad im Test:** die reale `AllFinalizedClean` (von `CleanupAfterRun` genutzt) mit einem Probe, der mitten im
  Ablauf wirft. Die UIA-Schritte (Pause/Replay-Verlassen) sind zusätzlich try-gekapselt (nicht offline ausführbar).
- **Verbleibende Grenze:** der Rest-Instanz-Scan über meta-Sperren beweist nicht, dass keine neue Instanz **ohne** geschriebene
  meta.json aktiv ist oder kurz nach dem Scan startet; echte Instanz-Lebensdauer/Add-Remove nur live. Finding 2 war im Recheck
  „kein falscher Exit 0"; hier wird zusätzlich die Selbst-Abbruch-Robustheit geschlossen.

## Finding 3 — Producer-Timeout + terminaler Adapter

- **Ursache:** (a) `Finish` wartete `BarrierMs` auf `_inFlight`, prüfte danach aber nicht, ob Producer verblieben; `clean`
  ergab sich nur aus Join/WriterFault. (b) `I.OnDispose` setzte bei `_core==null` keinen terminalen Zustand → ein späterer
  erster Callback erzeugte über `Core()` einen neuen laufenden Kern.
- **Änderung:** (a) `C.Finish`: nach der Barriere `if (_inFlight>0) _barrierTimedOut=true;` und
  `clean = joined && !_writerFaulted && !_barrierTimedOut;` (+ `barrierTimedOut`/`summaryWriteFailed` in der Summary).
  (b) neuer ATAS-freier `CoreGate`: `GetOrCreateAndStart` liefert nach `Dispose()` **null** (kein neuer Kern); `I` nutzt
  `CoreGate` für `Core()`/`OnDispose`, alle Callbacks null-prüfen.
- **Identisches Gegenbeispiel vorher/nachher** (`CoreChecks.dll` vs `RecorderCore`-Tests):
  - vorher: `pending_producer_after_barrier_timeout: clean=True`; `indicator_dispose_before_first_callback: later_running=True`.
  - nachher: Barriere-Timeout mit `_inFlight=1` → `barrierTimedOut=true, clean=false`; Gate-Dispose vor erstem Callback →
    `GetOrCreateAndStart` liefert null, kein laufender Kern. (plus periodischer Transient-Flush → clean=false/faulted=true;
    Summary-Schreibfehler → `summaryWriteFailed=true` + `summary_error.txt`.)
- **Produktionspfad im Test:** `RecorderCore.cs`/`CoreGate.cs` werden **direkt** mitkompiliert (gleiche Dateien, die der
  Indicator nutzt); `_inFlight` wird über eine Testnahtstelle gesetzt, um den echten Timeout-Zweig zu treffen.
- **Verbleibende Grenze:** keine ATAS-SDK-Laufzeit; reale Callback-Reihenfolge/Windows-Handle-Verhalten nur live verifizierbar.

## Finding 4 — Token/Chart-Zuordnung

- **Ursache:** `.of_run_token` war ein globaler String ohne exklusiven Konsum/Eigentümer; zwei Recorder lasen ihn,
  und der Ordnername (`Symbol+Sekunde+erste 6 Tokenzeichen`) kollidierte → „zwei Recorder → Ambiguous" konnte scheitern.
  Abbruch löschte die Datei ohne Inhaltsvergleich. `EnsureModule` kehrte bei aktivem Datumsbutton vor der Zielchart-Prüfung zurück.
- **Änderung:** neuer ATAS-freier `TokenHandshake`: `Publish` (atomar temp→rename), `Consume` (EXKLUSIV per atomarem Rename —
  genau ein Gewinner, sonst null), `RetractIfOwner` (löscht nur bei Inhaltsgleichheit). `I.ReadRunToken` konsumiert exklusiv;
  `C` erhält eine **token-unabhängige** Instanz-ID (GUID) für den Ordnernamen (keine Kollision bei gleicher Sekunde/runId).
  `P.AutoRun` nutzt `Publish`/`RetractIfOwner`; `EnsureModule` verifiziert den Zielchart **immer zuerst** eindeutig (sonst Abbruch).
- **Identisches Gegenbeispiel vorher/nachher** (`CoreChecks.dll` vs `RecorderCore`/`regress`):
  - vorher: `two_recorders_global_token: same_runId=True, token_still_exists=True, same_directory=True`.
  - nachher: exklusiver Konsum → genau ein Gewinner, Token-Datei danach weg; `RetractIfOwner` lässt fremdes Token stehen,
    löscht nur das eigene; zwei Kerne mit **demselben** runId → **verschiedene** Ordner (echte Instanz-ID).
- **Produktionspfad im Test:** `TokenHandshake.cs` wird in Recorder, Probe und Test aus **einer** Quelle kompiliert; die
  Tests rufen den echten Konsum/Retract/Ordner-Pfad.
- **Verbleibende Grenze:** echte **chart-gebundene** Zustellung (Auftrag→Zielchart→Recorder) ist nur live verifizierbar; die
  Header-Nähe in `EnsureModule` ist keine Recorder-Identität. Exklusiver Token-Konsum + Instanz-ID schließen die
  Offline-Mehrdeutigkeit, ersetzen aber nicht den Live-Herkunftsnachweis (offen). Autonomes Add/Remove weiter **nicht implementiert**.

## Finding 5 — Exportintegrität (Snapshot-Umfang + atomare Generation)

- **Ursache:** CSV wurde **nach** `post_hashes` gelesen; dom/bbo/cumtrades/bars waren nicht im Snapshot-Satz. Veröffentlichung
  erfolgte als zwei getrennte `os.replace` → Parquet(2) neben Manifest(1) bei Fehler des zweiten; Ziel-Race überschrieb fremden Besitz.
- **Änderung (`B`):** `csv_data_rows` **vor** `post_hashes`; `_INPUT_NAMES` um dom/bbo/cumtrades/bars erweitert; otherStream-Hashes
  aus demselben `pre_hashes`-Snapshot; Manifest führt `inputSnapshotFiles`. Veröffentlichung als **eine Generation**: beide
  Dateien in ein eindeutiges Staging-Verzeichnis, Parquet validieren, dann **atomarer Verzeichnis-Swap** (bei force: altes Ziel
  beiseite → swap → Rollback bei Fehler); frisches Ziel per Verzeichnis-Rename (schlägt fehl, wenn konkurrierend angelegt).
- **Identisches Gegenbeispiel vorher/nachher** (`bridge_recheck.json` vs `bridge_recheck_after.json` + `pytest`):
  - vorher: `csv_after_snapshot {manifest_hash_is_old:true, status:check, published}`, `second_rename_failure {parquet_rows:2, manifest_rows:1}`, `target_race {overwritten:true}`.
  - nachher: CSV-Änderung im Fenster → **SourceChangedError**, nichts veröffentlicht; Swap-Fehler → alte Generation
    **konsistent** (Parquet 1 / Manifest 1); Ziel-Race → fremder Besitz **erhalten**. (`inputSnapshotStable`/`inputSnapshotFiles` über alle 7 Dateien.)
- **Produktionspfad im Test:** echte `convert`-Aufrufe + Parquet-Rücklesen; `os.replace` wird gezielt zum Fehlschlag gebracht,
  um den echten Veröffentlichungspfad zu treffen. Neue Tests: `test_snapshot_covers_csv_read`, `test_snapshot_covers_other_streams`,
  `test_publish_one_generation_rollback_on_swap_failure`, `test_no_partial_generation_on_fresh_target`, `test_concurrent_target_not_overwritten`.
- **Verbleibende Grenze:** Speicher-/Lastskalierung (batch/rowgroup) war nicht Gegenstand; `main()` gibt für `check` weiterhin Exit 0 (Qualitätslabel, kein nachgelagertes Gate — kein konsumierender Pfad im Repo gefunden).

---

## Geänderte / neue Dateien

- `research/data-bridge/atas_bridge.py` — Finding 5 (Snapshot-Umfang, atomare Generation).
- `research/data-bridge/test_atas_bridge.py` — +5 Regressionstests (Finding 5 Recheck). Gesamt **24**.
- `research/recorder-core/RecorderCore.cs` — Finding 3 (Barriere-Timeout → nicht clean; `barrierTimedOut`; echte Instanz-ID).
- `research/recorder-core/CoreGate.cs` — **NEU**, terminaler Adapterzustand (Finding 3).
- `research/recorder-core/TokenHandshake.cs` — **NEU**, exklusiver/owner-gesicherter Token-Lebenszyklus (Finding 4).
- `research/recorder-core/tests/{RecorderCoreTests.csproj,Program.cs}` — +9 Tests (Finding 3/4 Recheck). Gesamt **16**.
- `research/atas-replay-probe/Program.cs` — Finding 1 (`PlayedEndReached`), Finding 4 (TokenHandshake-Nutzung, EnsureModule-Vorab-Verifikation), `regress` erweitert.
- `research/atas-replay-probe/AtasReplayProbe.csproj` — kompiliert `TokenHandshake.cs` mit (Single Source).
- `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs` — nutzt `CoreGate` + `TokenHandshake`; `instanceId` in meta (extern auf F:).

## Ausgeführte Befehle + Ergebnisse

```
# Finding 5 (Brücke)
python -I -m pytest test_atas_bridge.py -q            -> 24 passed  (after/pytest_bridge_after.txt)
python -I after/bridge_recheck_after.py               -> csv=SourceChangedError/nicht publiziert; swap-Fehler konsistent 1/1; race erhalten (after/bridge_recheck_after.json)
Codex-Baseline bridge_recheck.json (Recheck)          -> vorher: hash_is_old=true; parquet 2 vs manifest 1; race overwritten=true

# Finding 1 (Probe)
dotnet exec <Recheck>/ProgressChecks.dll <dir>        -> vorher: late_preload reached=True (x2), end+6 reached=False (baseline/progress_before.txt)
dotnet run -- regress <F:\...>                         -> REGRESS: ALLE BESTANDEN (after/probe_regress_after.txt)

# Finding 3/4 (Recorder-Kern + Gate + Token)
dotnet exec <Recheck>/CoreChecks.dll <dir>            -> vorher: barrier clean=True; dispose-before-first running=True; two recorders same_directory=True (baseline/core_before.txt)
dotnet run (recorder-core/tests)                      -> RECORDERCORE: ALLE BESTANDEN 16/16 (after/recordercore_after.txt)

# Voller ATAS-Indicator-Build (Artefakt, NICHT deployed)
csc OrderFlowReplayRecorder.cs + RecorderCore.cs + CoreGate.cs + TokenHandshake.cs @rsp -> 27136 B (after/recorder_build.txt)

# Reale Aufnahme (read-only)
convert MNQ_20261029_232000Z 08:00-08:01              -> 2491/2464/27, Fenstervol 2701, Rohvol 2729, status=check[summary_not_clean_or_missing], snapshot=7 Dateien, raw_unchanged=true
```

## Schutz der verwendeten Originaldaten

- Geladene ATAS-DLL `%APPDATA%\ATAS\Indicators\OrderFlowReplayRecorder.dll` **unverändert** (SHA256 `9065007A…E5E1D`).
- `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.dll` **unverändert** (gleicher Hash). Neues Build-Artefakt hat anderen Hash
  (`336B5C2D…`), liegt nur unter `Fixes_RECHECK_2026-10-30\build\` — **nicht** im Ladeordner.
- 7 Rohdateien der Aufnahme `MNQ_20261029_232000Z` SHA256-identisch zur Review-Baseline; zusätzlich vor/nach der Konvertierung
  dieser Runde gleich. Brücke liest nur; neue Ausgaben in frischen F:-Zielen. Git-Status unverändert.

## Status (einzeln belegt, KEINE Pauschalaussage)

- **Finding 1/2/3/4/5:** die im Recheck belegten konkreten Offline-Fehler sind behoben und per Produktionspfad-Test
  vorher-rot/nachher-grün (bzw. bei Finding 2 statisch belegter Mechanismus + robuster Ablauftest) belegt. **Keine Aussage „alle Fehler behoben".**
- **Offen (ausdrücklich):** Live-Herkunftsnachweis (Replay-Zustand/Chart), Live-Abnahme mit neuer DLL (nicht deployed),
  **autonomes Recorder-Add/Remove weiterhin nicht implementiert**, echte Instanz-Lebensdauer statt meta-Sperren-Scan,
  Lastskalierung der Brücke. Diese sind nicht Teil dieser Offline-Runde.

Hindsight: Bank `FuturesOrderflowBot` nur **lesend** (recall; keine Writes). |
CodeMunch: Index `ReisMitEnte20/FuturesOrderflowBot` aufgelöst; gezielte Symbol-/Zeilensuche statt Voll-Reads.
