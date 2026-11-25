# Runde 8 — Beide fehlenden Funktionen implementiert + 1 LiveProof GESAMT-PASS (2026-10-31)

Keine neue DLL deployed, kein Commit, kein Löschen. Daten auf F:, Repo auf A:. Aufnahme `MNQ_20261030_235732Z` erhalten.

## 1. ExitReplayView — implementiert & verifiziert

- **Mechanismus gefunden:** Der Replay-TRANSPORT hat einen Button OHNE Name, nur `HelpText='Zur Echtzeit wechseln'`
  (rect ~1241,320, pat=[Invoke]). Frühere Dumps filterten nach Name und übersahen ihn.
- `ExitReplayView(W)` sucht ihn per UIA (`FindBtnByHelpAny`), invokiert ihn (keine festen Koordinaten) und
  **verifiziert UNABHÄNGIG**: der chart-gebundene `Indikatoren`-Button (`BarButtonItemLink0IndicatorsButton`) ist im
  Replay-Modus `enabled=False` und wird nach echtem Verlassen wieder `enabled=True`. Das ist das verlässliche Signal —
  NICHT der Fenstertitel (stale) und NICHT `ReplayWindow()==null` (schließt nur den Transport, verlässt die Ansicht nicht).
- **Auto-Rewind beachtet:** Am natürlichen Ende REWINDET ATAS automatisch (Stopped→LoadingModules→Initializing→Play,
  ~11 s). Während dieses Transients ('Module laden…') ist 'Zur Echtzeit wechseln' deaktiviert. `ExitReplayView` POLLT
  daher bis der Button aktiv ist (Replay spielt/pausiert wieder), invokiert ihn dann und prüft `Indikatoren`.
- Warum Ribbon-Toggle NICHT reicht: schließt den Transport, lässt den Chart aber in der Replay-Ansicht
  (`[Wiedergabe pausiert]`, Indikatoren bleibt disabled). Ribbon-Menü 'Zurück zum Hauptfenster' ist im Ein-Chart-Layout
  deaktiviert; 'Fenster aktivieren' verlässt nicht; 'Chart'/'Replay'-Labels sind nicht-interaktiver Custom-Text.

## 2. Replay-Endsignal — implementiert & verifiziert (log-gebunden)

- **UIA hat KEINE Replay-Position:** Transport-Player ist Custom-gerendert (nur SynchronizedInput), kein RangeValue-
  Scrubber, kein Zeit-Text. Eine Positionsabfrage via UIA ist mit dieser ATAS-Version unmöglich.
- **Authoritatives Signal aus dem ATAS-Log** (`%AppData%\ATAS\Logs\app_YYYYMMDD.log`):
  - `ReplayLoadedRangeUtcFromLog` liest `Replay DS : Playing from <s> to <e>` → geladener Bereich in **UTC**
    (auftraggebunden: Log-Zeit ≥ Play-Invoke).
  - `ReplayEngineFinishedAfter` erkennt den engine-emittierten Abschluss NACH Play (`Data processing finished` /
    `Started -> Stopped` / `Play -> Stopped`). Da im Play-Loop NICHT pausiert wird, ist ein Treffer der **autonome,
    natürliche** Abschluss — KEIN Tick-Stall.
  - Bei Treffer: `_verifiedReplayEndUtc = geladenes Ende (UTC)` → `TryVerifiedReplayPositionUtc()` liefert es →
    `AssessEnd(pos, playing, winEnd) == Reached` → `endProven=true`.
- Daten-Fortschritt/Stillstand zählt weiterhin NUR als Korroboration (`dataWindowRecorded`), nie als Endbeweis.

## 3. LiveProof GESAMT-PASS (ein Lauf, `liveproof_final2.txt`)

```
[9] Endstatus=Reached (unabh. Replay-Endsignal=2026-10-26 08:01:00Z; engine_finished(LOG))
[view] 'Zur Echtzeit wechseln' aktiv -> Invoke; Indikatoren aktiv=True -> Replay-Ansicht VERLASSEN (verifiziert)
===== ERGEBNIS LiveProof: GESAMT-PASS (rc=0) =====
   - Aufnahme gespeichert (Writer beendet + Summary clean + runId-Match): True
   - Replay-ENDE unabhaengig bestaetigt: True (Reached)
   - Cleanup abgeschlossen (Replay-Ansicht verlassen=True UND Recorder entfernt=True): True
```
- Zugeordnete Aufnahme: `MNQ_20261031_125811Z` runId `540776f4c867497d8c64e0a2ec9e9fec`, Fenster
  `[08:00:00Z, 08:01:00Z)`, 2464 Ticks, summary `clean:true, writerFaulted:false, joinTimedOut:false, totalDrops:0`.
- Davor Lauf 1 (`liveproof_final.txt`): bestätigte Aufnahme + Endsignal, deckte den ExitReplayView-Rewind-Transient
  auf (Button am Ende kurz deaktiviert) → `ExitReplayView` auf Polling umgestellt → Lauf 2 GESAMT-PASS.

## 4. Tests (regress: ALLE BESTANDEN)

Neu: Verdikt-Gating — `LiveProofPass(recordingSaved,endProven,cleanupDone)` / `CleanupDone(recorderRemoved,replayViewLeft)`:
- fehlendes Endsignal → KEIN PASS; fehlgeschlagenes Remove → KEIN PASS; Replay-Ansicht nicht verlassen → KEIN PASS;
  Aufnahme nicht finalisiert → KEIN PASS; Cleanup-Fehlschlag → Aufnahme bleibt erhalten, KEIN PASS.
Bestehende A1/A2/A3/A4-Tests unverändert grün.

## 5. Grenzen (unverändert ehrlich ausgewiesen)

- **HERKUNFT-GRENZE:** installierte DLL `proto-2-bufferfix` (SHA256 9065007A…) konsumiert KEIN Token (GUID-runId).
  Zuordnung nur über erzwungenen Ein-Recorder-Betrieb + einzigen Chart + runId-Kontinuität. Ein gesperrter Ordner =
  nur offener Handle. Vollständige token-gebundene Zuordnung bräuchte die neue DLL (nicht deployed).
- Enderreichung belegt Abspielen bis zur geladenen Grenze, NICHT Anbieter-Vollständigkeit.

## Hinterlassener Zustand

- ATAS: Replay-Ansicht VERLASSEN (Realtime), Transport zu, **0 aktive Recorder** (im Lauf sauber entfernt).
- Aufnahmen erhalten (72 Ordner), inkl. Proof `MNQ_20261030_235732Z` (7 Dateien, 6031 KB) + neue
  `MNQ_20261031_125811Z`. Deployed DLL unverändert. Git HEAD `16f5d76b`, kein Commit.
- Neue Probe-Befehle: `exitview`, `watchend`, `replaymenu`, `playerprobe` (Diagnose).

Hindsight: Bank `FuturesOrderflowBot` (kompakte Übergabe gespeichert). | CodeMunch: lokaler Quellcode `Program.cs` maßgeblich.
