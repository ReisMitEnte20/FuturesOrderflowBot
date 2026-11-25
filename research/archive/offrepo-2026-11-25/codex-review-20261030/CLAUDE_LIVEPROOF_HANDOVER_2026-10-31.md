# Übergabe — Korrektur der Erfolgsauswertung + offene Funktion (2026-10-31, Code-Runde)

Reine Code-Runde: KEINE weiteren Live-Versuche, keine Aufnahme, kein Deployment, kein Commit, nichts gelöscht.
Nur Compile-Check. Die Aufnahme `MNQ_20261030_235732Z` bleibt als **Aufnahme-Teilnachweis** erhalten.

## 1. Erfolgsauswertung korrigiert (Code)

`research/atas-replay-probe/Program.cs`, `LiveProof`:
- **Getrennte Teilergebnisse** statt einem Sammel-OK: `recordingSaved` (Writer beendet + Summary clean + runId-Match),
  `endProven` (unabhängiges Replay-Endsignal), `cleanupDone` (= `replayViewLeft` UND `recorderRemoved`).
- **GESAMT-PASS / Exit-Code 0 nur, wenn ALLE erforderlichen Teile erfüllt sind:**
  `gesamtPass = recordingSaved && endProven && cleanupDone`. Recorder noch angehängt **oder** Endbestätigung fehlt
  ⇒ KEIN GESAMT-PASS (rc≠0). Phase 9 bricht nicht mehr ab, sondern versucht trotzdem Cleanup; das Verdikt fällt am Ende.
- Auf den bereits beobachteten Lauf angewandt ergibt die korrigierte Logik **KEIN GESAMT-PASS**
  (recordingSaved=true, endProven=false, cleanupDone=false) — die Aufnahme bleibt erhalten, der Fehler wird gemeldet.

## 2. Enderkennung nicht mehr schöngeredet (Code)

- `endProven` kommt AUSSCHLIESSLICH aus `AssessEnd(TryVerifiedReplayPositionUtc(), …)`. `TryVerifiedReplayPositionUtc`
  liefert derzeit `null` → **Endstatus „Unproven"**. 
- Tick-Fortschritt + Stillstand zählt nur noch als **Daten-Korroboration** (`dataWindowRecorded`), ausdrücklich KEIN
  unabhängiges Replay-Endsignal. 
- `ReplayWindow()==null` beweist nur, dass der **Transport** zu ist (`cleanup-Teil`), NICHT, dass der Chart die
  **Replay-ANSICHT** verlassen hat — getrennt ausgewiesen.

## 3. Cleanup-Code untersucht — konkrete offene Funktion benannt

- **Welche Aktion beendet die Replay-ANSICHT des Ziel-Charts?** Entweder der Chart-Ansichtsreiter **„Chart"** oder das
  Ribbon-Replay-Menü **„Zurück zum Hauptfenster"**.
- **Welche vorhandene Funktion sollte sie ausführen, und warum schlägt sie fehl?** Phase 10 rief bisher nur
  `Invoke(RibbonReplayButton)` → schließt den **Transport** (`ReplayWindow()==null`), beendet aber die **Chart-Replay-
  Ansicht NICHT** (Chart bleibt auf Reiter „Replay", Titel „Wiedergabe pausiert", persistent). In dieser Ansicht öffnet
  die Toolbar den Datum-Dialog statt des Indikator-Dialogs, und während Replay ist der Indikator-Dialog gesperrt →
  der **Recorder-Entfernen-Schritt (Phase 11) kann nicht laufen**.
- **Neue, ehrlich benannte Funktion `ExitReplayView(W)`:** nutzt NUR ein klar aktiviertes UIA-Element
  („Zurück zum Hauptfenster", wenn enabled) — **keine blinden Koordinaten-/Toggle-Versuche**. Beobachtet: dieses Menü
  ist im relevanten Zustand **ausgegraut**; der „Chart"-Reiter ist **Custom-WPF (nicht im UIA-Baum)** und schaltet per
  Klick nicht zuverlässig um. **Kein zustandsunabhängiges, verifizierbares Signal/Element identifiziert → `ExitReplayView`
  gibt `false` zurück (OFFENE FUNKTION).** Phase 11 (Remove) wird nur versucht, wenn `replayViewLeft==true`.

## 4. Schutzanforderungen erhalten

- „Ein sichtbarer Chart + neuer gesperrter Ordner" ist **keine** vollständige Herkunftsbestätigung (im Verdikt als
  HERKUNFT-GRENZE protokolliert).
- Die **installierte DLL** ist `OrderFlowReplayRecorder/proto-2-bufferfix` (SHA256 `9065007A…`) und bindet **kein Token**
  (GUID-runId) → **keine token-gebundene Chart/Instanz-Zuordnung**. Zuordnung derzeit nur über erzwungenen
  Ein-Recorder-Betrieb + einzigen Chart + runId-Kontinuität (meta==summary) — ausdrücklich als Einschränkung behandelt.
  Ein gesperrter Ordner = nur offener Handle, kein Aufnahme-Aktivitätsbeweis.

## 5. Teststruktur

`gesamtPass` wird false, wenn Remove fehlschlägt **oder** Endsignal fehlt. Bei „Aufnahme sauber gespeichert, Cleanup
fehlgeschlagen" bleibt die Aufnahme erhalten und der Fehler wird gemeldet (rc≠0, Fehlend: `[Cleanup/Remove]`,
`[unabh. Endsignal]`).

---

## Übergabe

**Was funktioniert nachweislich (aus dem vorherigen Lauf, erhalten):**
- Zielchart eindeutig (ein VulkanChartWpf-Pane); Fokus-/Modaldialog-Handling per UIA; Replay öffnen + Transport
  zugänglich; Zeitraum `[26.10.2026 09:00:00, 09:01:00)` setzen+zurücklesen; **Zeitzone empirisch = UTC+1**;
  LOAD → genau eine neue gesperrte MNQ-Instanz zugeordnet; 1× Play; **Aufnahme sauber gespeichert**
  (`MNQ_20261030_235732Z`: 2464 Trades im Fenster `[08:00:00Z,08:01:00Z)`, Brücke-Status complete, csv==ndjson==2464,
  Summary clean=true/writerFaulted=false/joinTimedOut=false, runId-Match). Recorder-Add/Remove-Gesten praktisch gezeigt
  (per Dateisystem verifiziert). Das sind **Teilnachweise**, kein Gesamt-PASS.

**Welche konkrete Funktion fehlt für den nächsten vollständigen Durchlauf:**
1. `ExitReplayView` — zuverlässiges, verifizierbares **Verlassen der Chart-Replay-Ansicht** (Reiter „Chart" ist
   Custom-WPF; „Zurück zum Hauptfenster" nur zustandsabhängig aktiv). Ohne sie bleibt der Indikator-Dialog gesperrt →
   kein automatisches **Recorder-Remove** → kein Cleanup → kein GESAMT-PASS.
2. `TryVerifiedReplayPositionUtc` — ein **verdrahtetes, auftraggebundenes Replay-Endsignal** (echte Replay-Position/
   -Endzustand aus Transport-UIA oder eindeutig zugeordnetem ATAS-Log). Ohne es bleibt `endProven=false` (Unproven).
3. Optional für vollständige Herkunft: **neue Recorder-DLL mit Token-Konsum** (nur mit gesichertem Backup/Rollback +
   ATAS-Neustart) — diese Runde NICHT durchgeführt.

**Tatsächlich hinterlassener Zustand:**
- Replay-**Transport** inaktiv (`ReplayWindow` zu); Chart visuell weiterhin auf **Replay-Ansicht** (nicht verlassen).
- **1 aktive gesperrte Recorder-Instanz** `MNQ_20261030_235844Z` (neu, angehängt — ≈ Ausgangszustand mit angehängtem
  Recorder). Proof-Aufnahme `MNQ_20261030_235732Z` + alle früheren Aufnahmen **erhalten**.
- Deployed DLL unverändert (`9065007A…`, nichts deployed). Keine neuen C:-Aufnahmen. Git HEAD `16f5d76b`, **kein Commit**.
- Code kompiliert (nur Warnungen). Kein Live-Lauf in dieser Runde.

Hindsight: Bank `FuturesOrderflowBot` nur lesend. | CodeMunch: aktueller lokaler Quellcode maßgeblich.
