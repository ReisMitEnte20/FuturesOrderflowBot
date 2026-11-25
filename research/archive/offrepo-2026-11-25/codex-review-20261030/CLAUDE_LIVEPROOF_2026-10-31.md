# Claude — Live-Funktionsnachweis ATAS-Replay-Aufnahme (2026-10-31)

Ziel: EIN kurzes Replay-Fenster vollautomatisch über das bestehende Programm
`A:\Projects\FuturesOrderflowBot\research\atas-replay-probe` aufnehmen und sauber abschließen.
Kein Commit/Push/Merge. Daten/Nachweise auf F:, Repo auf A:. Keine Orders/Konto-/Feed-Änderung. Keine DLL deployed.

## 1. Was wurde repariert (im bestehenden Programm, Program.cs)

- **Fokus-/Modaldialog-Bug behoben.** Beleg: der „Datum auswählen"-Dialog ist ein **eigenes Top-Level-Fenster**;
  `poke`/`Foreground(main)` aktivierte stets das Hauptfenster → Klicks erreichten den Modaldialog nicht (der
  Indikator-Dialog dagegen ist ein Kind des Hauptfensters → dort funktionierten Klicks).
  - Neu `FindModalByName` + `CloseDateDialogIfOpen`: modale Dialoge werden **per UIA-Invoke** (Abbrechen) behandelt,
    ohne Koordinaten/Hauptfensterfokus.
  - Datumsdialog wird jetzt **cross-window** über `FindIdAny("StartEdit"/"EndEdit")` + `FindBtnAny("Auswählen")`
    angesprochen (vorher fälschlich hauptfenster-scoped/falsche AutomationId); `TypeIntoField` bringt das **Dialog**-
    fenster selbst in den Vordergrund.
- **Verlässliche Zustandssignale.** „Replay aktiv" == `ReplayWindow()` vorhanden — NICHT der Fenstertitel
  (`[Im Wiedergabemodus]` / `[Wiedergabe pausiert]` ist stale/persistent) und NICHT allein der Ribbon-Toggle
  (kann vom tatsächlichen Modus desyncen). Neu `replay-toggle` (unbedingter Invoke) zur Desync-Reparatur, `normalize`.
- **Zielchart eindeutig** (`VerifyTargetChartUnique`): ohne `ATAS_TARGET_CHART` gilt **genau ein VulkanChartWpf-Pane**
  als eindeutiger Zielchart (kein Größte-Fläche-Fallback); mit Namen: eindeutiger Header.
- **Recorder-Verwaltung als wiederverwendbare Funktionen** (`OpenIndicatorDialog`, Add/Remove): Indikator-Dialog via
  UIA-Invoke (`BarButtonItemLink0IndicatorsButton`); versehentliches Öffnen des Datum-Dialogs wird erkannt + behandelt;
  Add = Suche→Doppelklick→Übernehmen, Remove = Zeile→Papierkorb→Übernehmen. **Ergebnis per DATEISYSTEM verifiziert**
  (Add: neuer gesperrter Ordner; Remove: Writer-Ende + Summary). Add/Remove wurden in dieser Runde praktisch gezeigt.
- **Empirische Zeitzone** (kein pauschaler UTC-Offset): Offset = erster `tMarket` (UTC) vs. Dialog-Start (Wanduhr).
- **Enderkennung** auf belegten, abgespielten Fortschritt gestützt: zusammenhängender Marktzeit-Fortschritt bis zum
  geladenen Ende WÄHREND beobachteten Abspielens + Stillstand am Ende (Transport-Titel „gestoppt" ist in diesem
  Layout unzuverlässig — flackerte auf „Module laden…"). Preload/Empfangszeit allein lösen keinen Erfolg aus
  (`playingObserved` + zusammenhängende Progression erforderlich).
- Neue Diagnose-/Hilfsbefehle: `typeat`, `allctrls`, `replayrect`, `replay-toggle`, `normalize`, `rmrec`, `liveproof`.
- **Offline-Brücke und Recorder-Kern NICHT verändert** (keine konkrete Abhängigkeit erforderte das).

## 2. Der eine Startbefehl

```
cd A:\Projects\FuturesOrderflowBot\research\atas-replay-probe
dotnet run -c Release -- liveproof
```
Ablauf (ein Aufruf, ohne manuelle Zwischenschritte): offenen Dialog behandeln → Zielchart eindeutig → Replay AUS für
Recorder-Prüfung → Recorder prüfen/bei Bedarf hinzufügen (Dateisystem-verifiziert) → Replay öffnen + Transport →
Datum-Modus → Zeitraum+1× setzen/zurücklesen → laden + Bereitschaft → genau EINE neue gesperrte Instanz zuordnen →
Play → abgespielten Fortschritt bis Ende beobachten → pausieren → Replay verlassen → Recorder entfernen →
Writer-Ende + saubere Summary der zugeordneten Aufnahme prüfen.

## 3. Hat der vollständige Test funktioniert?

**Weitgehend JA — der Aufnahme-/Replay-Kern ist vollautomatisch bestanden (rc=0); EIN abschließender Cleanup-Schritt
(Recorder-Entfernen) lief nicht autonom durch.**

Belege des einen Laufs (`F:\…\LiveProof_2026-10-31\liveproof_run2.txt`):
- **Zielchart:** einziger VulkanChartWpf-Chart, Instrument `#MNQZ6` (Continuous MNQ @ CME).
- **Zeitraum (zurückgelesen):** `[26.10.2026 09:00:00, 26.10.2026 09:01:00)` Dialog-/Berlin-Zeit, halb-offen.
  **Zeitzone empirisch = UTC+1** → UTC-Fenster `[2026-10-26T08:00:00Z, 2026-10-26T08:01:00Z)`.
- **Aufnahmeordner (zugeordnet):** `F:\ATAS_Data\Recordings\MNQ_20261030_235732Z`, runId `b06dada4273c4489b803271fc620c04a`
  — **genau EINE** neue gesperrte MNQ-Instanz nach dem LOAD (erzwungener Ein-Recorder-Betrieb).
- **Replay-Fortschritt (getrennt von Trades):** zusammenhängend `08:00:05 → 08:00:59Z` während beobachteten Abspielens
  bei 1× (~55 s realer Lauf); Enderreichung belegt (`data_reached_loaded_end + progress_settled`).
- **Aufgezeichnete Trades (getrennt):** `ticks.ndjson` 2464 Trades; Brücke `[08:00:00Z,08:01:00Z)` → **status complete**,
  `tradesInWindow=2464`, `csv_data_rows=2464 == ndjson 2464`, `summary_clean=True`, off-grid 0.
  (Ausgabe `F:\ATAS_Data\Bridge\LiveProof_MNQ_235732Z_0800\{trades.parquet,manifest.json}`.)
- **Writer-Abschluss + Summary:** `clean:true, writerFaulted:false, joinTimedOut:false, totalDrops:0`,
  totalEvents 39264; Writer beendet (Ordner entsperrt); `summary.runId == meta.runId == zugeordnete runId`.
- **Rohdaten/Fenster:** geladener Bereich == Fenster, daher `restTicksBefore/After = 0` (kein Nachlauf; nichts
  zugeschnitten). Rohdateien unverändert (Brücke liest nur). Fenster dokumentiert als `[Start, Ende)`.
- **End ≠ Anbieter-Vollständigkeit:** Enderreichung belegt nur das Abspielen bis zur Grenze, nicht Anbieter-Vollständigkeit.

## 4. Was fehlt konkret noch

1. **Automatisches vollständiges Verlassen der Replay-ANSICHT** nach dem Lauf. Nach „Replay verlassen" bleibt der Chart
   in der „Replay"-Ansicht (Titel „Wiedergabe pausiert", persistent). In dieser Ansicht öffnet die Chart-Toolbar den
   Datum-Dialog statt des Indikator-Dialogs, und während Replay ist der Indikator-Dialog gesperrt → der
   **Recorder-Entfernen-Schritt (Phase 11) lief nicht durch** (`Invoke-Fehler: nicht aktiviertes Element`).
   Das Ribbon-Menü „Zurück zum Hauptfenster" ist ausgegraut; der „Chart"-Reiter-Klick schaltet die Ansicht nicht um.
   Dies ist der einzige UI-Schritt, der in dieser Session nicht zuverlässig automatisierbar war. Die zugeordnete
   Aufnahme wurde trotzdem **sauber finalisiert** (das Replay-Verlassen disponiert den Recorder → Summary geschrieben).
2. **Recorder-Add/Remove-Koordinaten**: der Indikator-Dialog (Katalog/„Hinzugefügt"/Papierkorb/Übernehmen) ist
   **Custom-WPF, nicht im UIA-Baum** → die Klickpunkte sind für 1920×1080 maximiert kalibriert (auflösungs-/
   layoutabhängige benannte Lücke). Funktion + Ergebnis sind per Dateisystem verifiziert, aber nicht auflösungsrobust.
3. **Token-/Instanz-Bindung an den Chart**: die INSTALLIERTE DLL ist `OrderFlowReplayRecorder/proto-2-bufferfix`
   (SHA256 `9065007A…`), die das Auftragstoken NICHT konsumiert (GUID-runId). Die Zuordnung erfolgt daher über
   **erzwungenen Ein-Recorder-Betrieb** (genau eine neue gesperrte MNQ-Instanz nach LOAD) + **einzigen Chart** +
   runId-Kontinuität (meta==summary) — nicht „ein sichtbarer Chart reicht". Eine vollständige, token-gebundene
   Chart↔Instanz-Zuordnung bräuchte die neue Recorder-DLL (mit `TokenHandshake.Consume`); Deployment nur mit
   gesichertem Backup + dokumentiertem Rollback + ATAS-Neustart — **in dieser Runde bewusst NICHT** durchgeführt
   (Schutzprüfung nicht umgangen, sondern als Grenze benannt). Ein gesperrter Ordner beweist nur einen offenen Handle;
   die Aufnahmeaktivität ist hier zusätzlich durch 2464 echte Trades + zusammenhängenden Fortschritt belegt.

## Hinterlassener ATAS-/Recorder-Zustand

- Replay-Transport **inaktiv** (`ReplayWindow` zu, Ribbon-Toggle Off), kein Dialog offen; Chart visuell noch auf
  „Replay"-Ansicht (Wiedergabe pausiert).
- **1 Recorder-Instanz aktiv** (neu, live/paused angehängt) — entspricht ~dem Ausgangszustand, in dem ATAS mit
  angehängtem Recorder gestartet wurde. Die Proof-Aufnahme `MNQ_20261030_235732Z` ist finalisiert + erhalten.
- Alle vorherigen Aufnahmen erhalten. Deployed DLL unverändert (`9065007A…`, nichts deployed). Keine NEUEN C:-Aufnahmen
  (Recorder-Default F:); C:-Alt-Leftover unverändert. Git: HEAD `16f5d76b`, nur untracked `research/`,`tools/`,`dashboard/dist-demo/`, **kein Commit**.

Hindsight: Bank `FuturesOrderflowBot` nur lesend (recall; keine Writes). | CodeMunch: optional; aktueller lokaler Quellcode maßgeblich.
