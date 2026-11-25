# Claude — Fixes zur zweiten Codex-Nachprüfung (CODEX_RECHECK_2_2026-10-30)

**Nur Implementierung + Offline-Tests. Kein Commit/Push/PR/Merge. Keine ATAS-Aktion/Replay/DLL-Deployment.
Keine Rohdaten-/DLL-Änderung.** Bearbeitet wurden ausschließlich die drei noch offenen Automation-Befunde
(Enderkennung, Token-/Chart-Zuordnung, Cleanup-Restmechanismus). Brücke und Recorder-Kern wurden **nicht**
angefasst (unabhängig abgenommen); geändert wurde nur `research/atas-replay-probe/Program.cs`.

- Branch `feature/atas-replay-control-probe`, HEAD `16f5d76b` (unverändert, **kein Commit**).
- Untracked wie zuvor: `research/`, `tools/`, `dashboard/dist-demo/` (erhalten).
- Toolchain: .NET SDK 8.0.422, Python 3.13.9, pyarrow 26.0.0, pytest 9.2.0.

## Wichtige Unterscheidung vorab

- **„falscher Erfolg ausgeschlossen"** — JA für alle drei Befunde (die Codex-Gegenbeispiele erzeugen keinen
  Fehl-Erfolg mehr).
- **„automatischer erfolgreicher Ablauf implementiert"** — NEIN für Enderkennung und vollständige Chart-Zuordnung.
  Diese brauchen ein verifiziertes Live-Replay-Signal bzw. eine chartgebundene Instanz-Identität und bleiben
  ausdrücklich offen (sichere Zwischenlösung, siehe je Finding). Ein stets abbrechender Ablauf wird **nicht** als
  erfüllte Automation ausgegeben.

---

## Finding 1 — Enderkennung

**1) Zu garantierende Eigenschaft:** Ein Fenster gilt nur dann als erreicht, wenn der **beauftragte Replay** nachweislich
bis zur Fenstergrenze abgespielt wurde (kein Preload, keine Historie, kein Live).
**2) Welches Signal beweist das tatsächlich:** NUR ein vom Replay-Transport gemeldeter, dem Auftrag/der Instanz
zugeordneter Wiedergabe-Fortschritt/-Abschluss. `tMarket`/`tRecv` aus den Recorder-Dateien beweisen es **nicht**
(tRecv ist Empfangs-Wanduhr; ein nach Play empfangener Preload erfüllt dieselbe Bedingung).
**3) Durchsetzender Produktionspfad:** `AutoRun` Play-Schleife → `DecideEnd(...)` → Verdikt ausschließlich aus
`AssessEnd(verifiedReplayPos, playing, winEnd)`. Fehlt das verifizierte Signal → `Unproven` → kein Erfolg.

- **Ursache:** `PlayedEndReached` wertete `tMarket≥Ende ∧ tRecv≥Play` als Wiedergabenachweis; ein nach Play empfangener
  Preload-Datensatz genügte (`reached=True`), ohne jede abgespielte Strecke. Zusätzlich war die obere Grenze fix +15min,
  obwohl das Fenster nur bis Ende+3min geladen wird (Ende+10min galt fälschlich).
- **Fix (`Program.cs`):** `PlayedEndReached` entfernt. Neu: `EndVerdict{Unproven,Reached,StoppedBeforeEnd}`,
  `AssessEnd(verifiedReplayPosUtc, replayPlayingObserved, winEnd)` (Erfolg nur mit verifiziertem Signal),
  `PlayedBoundaryDataHint(...)` (reine **Protokoll**-Querprüfung, nie Erfolgsgrund, begrenzt auf die **tatsächlich
  geladene** Spanne `[Ende, loadedEnd]` via neuem `LoadedEndUtc(win)` aus DialogStart/DialogEnd),
  `DecideEnd(...)` (loggt den Daten-Hinweis, entscheidet aus `AssessEnd`). `TryVerifiedReplayPositionUtc()` liefert
  derzeit `null` (kein erfundenes Signal) → Enderkennung bleibt bewusst `Unproven`. `AutoRun` leitet aus `Unproven`
  KEINEN Erfolg ab und bricht das Fenster ab (`end_unproven_no_verified_replay_signal`).
- **Identisches Gegenbeispiel vorher/nachher** (`ProvenanceChecks.dll` vs `regress`):
  - vorher: `post_Play_received_preload … reached=True`; `end_plus_10_minutes reached=True`.
  - nachher: Preload nach Play → Daten-Hinweis true, aber `DecideEnd=Unproven` (kein Erfolg); Ende+10min → kein
    Daten-Hinweis (ausserhalb geladener Spanne) und `Unproven`; verifiziertes Signal≥Ende+playing → `Reached`;
    Signal<Ende → `StoppedBeforeEnd`; kein/ohne-playing Signal → `Unproven`.
- **Produktionspfad im Test:** `DecideEnd`/`AssessEnd`/`PlayedBoundaryDataHint` sind exakt die Funktionen, die die
  `AutoRun`-Schleife aufruft; die Tests legen reale Stream-Dateien an und rufen genau diese.
- **Verbleibende Lücke:** `falscher Erfolg ausgeschlossen` ✔. **Fertige Enderkennung NICHT implementiert**: es ist noch
  kein verifiziertes, auftraggebundenes Replay-Positionssignal verdrahtet (`TryVerifiedReplayPositionUtc`→null) — das
  erfordert einen Live-ATAS-Transportwert und ist nur live abnehmbar. Bis dahin endet jedes Fenster in `Unproven`→Abbruch.

## Finding 4 — Token-/Chart-Zuordnung

**1) Zu garantierende Eigenschaft:** Die tatsächlich aufnehmende Instanz ist die **beauftragte** (Zielchart), nicht
irgendein Token-Gewinner.
**2) Welches Signal beweist das tatsächlich:** Eine chartgebundene Instanz-Identität ODER ein technisch erzwungener,
eindeutig verifizierter **Ein-Recorder-Betrieb** (genau eine aktive Instanz, keine weitere irgendwo), kombiniert mit dem
Auftragstoken. Ein atomarer Token-Gewinn allein beweist nur „einen", nicht „den richtigen".
**3) Durchsetzender Produktionspfad:** `AutoRun` erfasst ALLE aktiven (gesperrten) MNQ-Instanzen global
(`LockedMnq()`), Verdikt über `AttributionVerdict(lockedInstances, token)`.

- **Ursache:** Attribution zählte nur Token-Treffer. Konsumierte eine fremde Instanz das globale Token, erhielt die
  Zielinstanz `null`; die fremde galt als `Unique` (falsche Herkunft).
- **Fix (`Program.cs`):** `AttributionVerdict` neu: genau **eine** aktive MNQ-Instanz (keine weitere aktive irgendwo),
  diese **neu** (von diesem Lauf gestartet) UND mit dem Auftragstoken → `Unique`; `>1` aktive Instanz → `Ambiguous`
  (Ablehnung); `0`/nicht-neu/fremdes Token → `None`. `AutoRun` baut die globale Liste der aktiven Instanzen und wählt
  die einzige neue nur bei `Unique`. Damit ist ein **Ein-Recorder-Betrieb technisch erzwungen**: sobald eine fremde
  Instanz existiert (die das Token gewann → zusätzlich die Zielinstanz), sind es ≥2 aktive → Ablehnung.
- **Identisches Gegenbeispiel vorher/nachher** (`ProvenanceChecks.dll` vs `regress`):
  - vorher: `foreign=expected-job target=NULL verdict=Unique`.
  - nachher: Codex foreign-first über den echten `TokenHandshake` (fremd gewinnt Token, Ziel bekommt null) → zwei
    aktive Instanzen → `Ambiguous` (NICHT Unique); genau 1 neue Instanz mit Token → `Unique`; 1 Instanz fremdes/kein
    Token oder nicht neu → `None`; `0`→None; `2`→Ambiguous.
- **Produktionspfad im Test:** `AttributionVerdict` ist die von `AutoRun` genutzte Funktion; der foreign-first-Test
  nutzt den echten `TokenHandshake.Publish/Consume` (die Probe kompiliert dieselbe Quelle).
- **Verbleibende Lücke:** `falscher Erfolg ausgeschlossen` für den dokumentierten Zwei-Instanz-Fall ✔. **Vollständige
  chartgebundene Herkunft NICHT implementiert**: ein **allein** laufender fremder Recorder (Zielinstanz startet gar
  nicht) und das Token konsumiert, wäre die einzige Instanz mit Token — Chart-Identität ist offline nicht verifizierbar
  und bleibt eine Live-Lücke. Der erzwungene Ein-Recorder-Betrieb (genau eine aktive Instanz) ist der kleinste
  tragfähige Offline-Weg; echte Zielchart-Bindung erfordert einen Live-Instanz-/Chart-Nachweis.

## Finding 2 — Cleanup (Restmechanismus)

**1) Zu garantierende Eigenschaft:** Ein Fehler beim Pausieren darf das kontrollierte Replay-Verlassen nicht
überspringen; Fehlergrund, Cleanup-Ergebnis und Gesamtergebnis bleiben erhalten.
**2) Welches Signal beweist das:** getrennte Erfolgs-/Fehlerzustände je Schritt (`pauseOk`, `exitAttempted`, `exitOk`).
**3) Durchsetzender Produktionspfad:** `CleanupAfterRun` ruft `CleanupSteps(pause, exit, W)`; `AutoRun.finally` wertet
`exitOk` als `replayExited`.

- **Ursache:** Pause und Replay-Verlassen lagen in **einem** try-Block; eine Pause-Exception sprang in den catch und
  übersprang den Exit-Versuch.
- **Fix (`Program.cs`):** neue `CleanupSteps(Action pause, Func<bool> exit, Action<string> W)` kapselt Pause und Exit in
  **getrennten** try/catch; Exit wird auch nach einem Pause-Fehler versucht; beide Fehlerzustände bleiben erhalten.
  `CleanupAfterRun` nutzt sie. Die bereits geschlossenen Punkte (robuste `AllFinalizedClean`, nicht-werfender finally,
  Erhalt von Abbruchgrund/Exitcode) bleiben unverändert.
- **Identisches Gegenbeispiel vorher/nachher:** vorher (Recheck2, statisch belegt) überspringt ein Pause-`Invoke`-Fehler
  das Replay-Verlassen. Nachher: `regress` „Pause-Fehler → Replay-Verlassen dennoch versucht + erfolgreich" und
  „Pause- und Exit-Fehler → beide gemeldet, Exit versucht, nicht erfolgreich".
- **Produktionspfad im Test:** `CleanupSteps` ist exakt die von `CleanupAfterRun` genutzte Schrittlogik; der Test
  injiziert einen Pause-Fehler und prüft, dass Exit dennoch versucht wird.
- **Verbleibende Lücke:** **Autonome Recorder-Entfernung weiterhin NICHT implementiert** (Funktionslücke, Custom-WPF-
  Dialog nicht UIA). Ein Meta-Sperren-Scan ersetzt keinen vollständigen Instanznachweis. Beides nur live schließbar.

---

## Geänderte Dateien

- `research/atas-replay-probe/Program.cs` — Finding 1 (`EndVerdict`/`AssessEnd`/`PlayedBoundaryDataHint`/`DecideEnd`/
  `TryVerifiedReplayPositionUtc`/`LoadedEndUtc`, AutoRun-Play-Schleife), Finding 4 (`AttributionVerdict`
  Ein-Recorder-Betrieb, `LockedMnq` in AutoRun), Finding 2 (`CleanupSteps`, getrennte Pause/Exit), `regress` erweitert.
- **Nicht geändert:** `atas_bridge.py`, `RecorderCore.cs`, `CoreGate.cs`, `TokenHandshake.cs`,
  `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs` (nicht zwingend für diese drei Befunde).

## Ausgeführte Befehle + Ergebnisse

```
# Baseline (pre-fix) — Codex-Gegenbeispiele unverändert
dotnet exec <Recheck2>/ProvenanceChecks.dll <dir>  -> preload reached=True; end+10min reached=True; foreign verdict=Unique
# Nachher — korrigierter Produktionspfad
dotnet run -- regress <F:\...>                      -> REGRESS: ALLE BESTANDEN (A1 7 + A4 9 + A2 10 + A3 3)
# Keine Regression der abgenommenen Teile
recorder-core/tests                                -> RECORDERCORE: ALLE BESTANDEN (16)
data-bridge pytest                                 -> 24 passed
```

## Schutz der Originaldaten

- Geladene ATAS-DLL und `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.dll` **unverändert** (SHA256 `9065007A…E5E1D`).
  Keine neue DLL gebaut/deployed in dieser Runde (nur Probe-Quelle geändert).
- 7 Rohdateien der Aufnahme `MNQ_20261029_232000Z` nur lesend verwendet, unverändert. Neue Evidence unter
  `F:\…\Fixes_RECHECK2_2026-10-30\`. Git-Status unverändert. Frühere Berichte erhalten.

## Status (einzeln belegt)

- **Finding 1:** falscher Erfolg ausgeschlossen ✔ (Preload/Ende+10min → Unproven). Fertige Enderkennung **offen**
  (verifiziertes Replay-Positionssignal fehlt → stets Unproven/Abbruch; Live-Nachweis nötig).
- **Finding 4:** falscher Erfolg für den dokumentierten Zwei-Instanz-Fall ausgeschlossen ✔ (foreign-first → Ambiguous).
  Vollständige chartgebundene Herkunft **offen** (allein laufender Fremd-Recorder nur live unterscheidbar).
- **Finding 2:** Restmechanismus geschlossen ✔ (Pause-Fehler überspringt Exit nicht mehr). Autonome Add/Remove
  weiterhin **Funktionslücke**.
- **Keine Aussage „alle Fehler behoben".** Keine Freigabe für unbeaufsichtigten Serienbetrieb.

Hindsight: Bank `FuturesOrderflowBot` nur **lesend** (recall; keine Writes). |
CodeMunch: Index `ReisMitEnte20/FuturesOrderflowBot` aufgelöst; gezielte Symbol-/Zeilensuche statt Voll-Reads.
