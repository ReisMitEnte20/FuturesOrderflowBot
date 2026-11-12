# AtasReplayProbe — Bedienungsdoku (kurz)

Isoliertes Werkzeug zur **automatischen ATAS-Replay-Steuerung + Aufnahme-Nachweis** über Windows UI
Automation. **Nicht Teil der Solution** (`TradingBot.sln`), **nicht committet**. Es wird **nichts in ATAS
injiziert/gepatcht** — nur UIA als normaler Client, Metadaten-Reflection, Screenshots, Datei-Reads.
Keine Orders, keine Konto-/Feed-Änderung. BUY/SELL/Flat/Schließen im Trading-Panel bleiben unangetastet.

## Build
```
cd research/atas-replay-probe
dotnet build -c Release
```

## Nachgewiesener End-to-End-Ablauf (genau dieser Versuch, 2026-10-29)
Voraussetzung: ATAS läuft, der Ziel-Chart (z. B. `#MNQZ6 4RXV`) hat den Indikator
`OF Replay Recorder (read-only)` angehängt.

```
dotnet run -c Release -- open-replay   out.txt      # Ribbon 'RibbonReplayButton' (AutomationId) Off->On -> Transportfenster
dotnet run -c Release -- inspect2                   # Transport inventarisieren (PlayButton/Speed/Datum/Modul)
dotnet run -c Release -- load          out.txt      # vorbelegten Abschnitt via 'Datum auswählen'/'Auswählen' laden; wartet Initializing->Initialized; meldet aktiven Recorder-Ordner
dotnet run -c Release -- capture --folder <ORDNER> out.txt   # 1x bestätigen, PlayButton invoke, <=120s Pro-Stream-Bytes + tMarket messen, Pause (KEIN 'Zur Echtzeit')
dotnet run -c Release -- end-recorder  --go out.txt # Indikator-Dialog öffnen, Recorder-Zeile per Papierkorb entfernen, 'Übernehmen' -> OnDispose schreibt summary.json
```

## Befehlsübersicht
- `open-replay` — Replay-Transportfenster über Ribbon-Button öffnen (Toggle Off→On). Startet KEIN Play.
- `inspect2` — Buttons/Speed/Play/Datum/Modul im Replay-Fenster auflisten.
- `inspect-date` — Datumsdialog (`ReplayDateSelectionDialog`) inventarisieren (Start/Ende/Modus); setzt nichts.
- `load` — vorbelegten Abschnitt laden, auf Abspielbereitschaft warten, aktiven (gesperrten) Recorder-Ordner melden.
- `capture --folder <name> [--manual]` — **strikt** zugeordneter Aufnahme-Nachweis NUR für `<name>`
  (meta.json-Bestätigung, 1×-Prüfung, `PlayButton.IsEnabled`-Gate, Play→≤120s→Pause). Erfolg nur aus
  Vorher/Nachher-Bytes + tMarket; tMarket=Replay-Datum ⇒ echter Nachweis. `--manual` wartet auf manuelles Play.
- `end-recorder [--go]` — Indikator-Dialog des sichtbaren Charts öffnen, `OF Replay Recorder`-Zeile per
  Papierkorb entfernen + `Übernehmen`; beendet genau diese Instanz sauber (summary.json). Ohne `--go` nur Inspektion.
- `diag [out.txt out.png]` — Vordergrund/PlayButton-Treffer + Screenshot (Replay-Fenster bzw. Vollbild).
- `poke <hover|click|rclick> <x> <y> [x0 y0 x1 y1] [out.png]` — EINE verifizierte On-Screen-Aktion + (Zoom-)Screenshot.
- `dump-window <name> [out.txt]` / `rm-indicator <muster> [--go]` — Dialoginspektion/UIA-Entfernen (Hilfskommandos).
- Weitere Analyse-/Hilfskommandos: `api-scan`, `uia-dump`, `inspect-menu`, `chart-tools`, `indicators`,
  `selectmodule`, `prepare`, `toggle-header`, `close-window`, `rclick-chart`, `close-atas`.

## Wichtige Beobachtungen (belegt)
- `RibbonReplayButton` (CheckBox, AutomationId) öffnet/schließt Replay sauber per UIA — kein Vordergrund nötig.
- Nach `load` liest `PlayButton.IsEnabled` während „Module laden…" ~70 s `False`; danach bereit
  (Panelstatus „Drücken Sie Play"). Das ist die Ladephase, **kein** Fehler.
- Ein Moduswechsel (Replay an/aus) **disposed** vorhandene Recorder-Instanzen (schreibt `summary.json`)
  und erzeugt eine neue. So trennen sich Live- und Replay-Daten sauber in getrennte Ordner.
- `summary.json` entsteht erst beim Indikator-Dispose (Recorder entfernen oder Moduswechsel).
- `summary.json`-Byte-Zähler = On-Disk-Bytes **minus ein Zeilenende je Event**; Event-Zähler = Dateizeilen exakt.

## Autonomer Durchlauf (`autorun`) — ein Startbefehl, keine Eingriffe
```
cd research/atas-replay-probe && dotnet run -c Release -- autorun autorun.txt
```
Zustandsmaschine mit überprüfbaren Übergängen, fensterübergreifende UIA-Suche, Laufzeit-abgeleitete Zielpositionen,
≤1 gezielter Retry je Schritt, Fehler => Exitcode ≠ 0. Ablauf je Fenster:
1. Replay offen sicherstellen; falls es spielt, pausieren (sonst ist die Datumsauswahl gesperrt).
2. `EnsureModule`: Datum-basiertes Replay sicherstellen — Zielchart als Modul wählen (echter Klick auf „Modul auswählen"
   mit Fensteraktivierung + Klick auf größte on-screen `VulkanChartWpf`-Pane) und Modus via `ModeMenuButton`-**CheckBox**
   „Datum auswählen" (AutoId `BarCheckItemLink2`) setzen. Ist Datum bereits aktiv, wird dieser Schritt übersprungen.
3. Datum Start/Ende setzen+zurücklesen (12-Ziffern-Maske) — **Ladebereich mit Puffer** (Fensterende + ~3 min), damit das
   Replay beim Pausieren NICHT das Bereichsende erreicht und „Datum auswählen" fürs nächste Fenster aktiv bleibt.
4. Laden („Auswählen"), Bereitschaft = **neue gesperrte F:-Instanz** + Titel „gestoppt" nach Ladephase.
5. 1× setzen+zurücklesen, Play, überwachen bis `tMarket >= Fensterende` (kein fixer Sleep; Stall-Erkennung), Pause.
6. Zeitzone-Gegenprobe (|beobachtet − erwartet UTC| ≤ 15 min, sonst Abbruch). Folge-Load/Dispose-Load finalisiert (summary.json).
7. C:-Gegenprobe: keine NEUEN Aufnahmeordner auf C: während des Laufs (Alt-Leftover ignoriert).

Belegt (2026-10-30): ein `autorun` nahm 26.10. 09:00–09:01 **und** 15:30–15:31 Berlin nacheinander bei 1× auf,
beide auf `F:\ATAS_Data\Recordings` finalisiert; Marktzeit 08:00→08:01Z / 14:30→14:31Z (tz-Abw. 0,0 min), drops=0,
off-grid=0, keine unbekannten Aggressoren, Byte-Reconcile ok. Timing gesamt 197s = Bedienung 27 + Laden 46 + Wiedergabe 124.
Grenze: die **Kaltstart-Modulwahl + Modus-Umschaltung** (aus vollständig zurückgesetztem Replay) ist nicht
deterministisch (Fensteraktivierungs-Parität auf Custom-WPF-Transport); bei bereits datums-basiertem Replay läuft `autorun`
vollständig autonom durch. Hinweis: datums-basiertes Replay setzt eine funktionierende Replay-Repository-Verbindung voraus.

## Serienbetrieb (`series`) — Phase 1 (Mehrtages-Kurztest)
```
dotnet run -c Release -- open-replay          # einmalig Replay-Transport oeffnen
# Recorder 'OF Replay Recorder (read-only)' muss am Chart haengen (Indikatoren-Dialog -> Doppelklick -> Uebernehmen)
dotnet run -c Release -- series series.txt     # EIN Startbefehl: nimmt 4 Kurzfenster nacheinander auf
```
`series` fährt pro Fenster vollautomatisch: Datum **setzen+zurücklesen** → laden → Bereitschaft (neue
Recorder-Instanz + Titel „gestoppt") → eindeutige Ordnerzuordnung → 1× → Play bis Marktzeitende → Pause.
Getrennte Ordner je Fenster; **Auto-Finalisierung**: der Folge-Load disponiert die vorherige Instanz
(`summary.json`), ein finaler Dispose-Load schließt das letzte Fenster. **Abbruch (kein blindes Wiederholen)**
bei: Datum nicht setzbar, keine neue Instanz, nicht ladebereit (Verfügbarkeit), Marktzeit-Abweichung >15 min
(Zeitzone/Zuordnung). Fenster sind im Quelltext hinterlegt (26./27.10.2026, je 09:00–09:01 und 15:30–15:31 Berlin).

### Datum programmatisch setzen (`setdate`, Kern von `series`)
- Editierbare Maske ist **`dd.MM.yyyy HH:mm` = 12 Ziffern (KEINE Sekunden)** → nur 12 Ziffern tippen (Sekunden laufen über).
- `ValuePattern.SetValue` **committed nicht** (WPF liest bei Fokusverlust aus der Bindung zurück) → echtes **Tippen** (SendInput Unicode) nötig.
- **Vordergrund erzwingen** (`AttachThreadInput`+Alt-Trick) vor dem Feldklick, sonst trifft der Klick ein überlappendes Fenster.
- In 2er-Gruppen mit Pause an Trenner-Übergängen; **Ende zuerst, Start zuletzt**, je mit Rücklese-Retry (Dialog verwirft Start>Ende).
- Lade-/Bestätigen-Button im Dialog heißt **„Auswählen"** (triggert Laden); „Bestätigen" ist nur der Kalender-Popup-Button.

### Zeitzone (belegt)
Dialog-Zeitbasis = **UTC+1** (fix; Juni-Kalibrierung: Dialog 14:50 → tMarket 13:50Z). Für Normalzeit-Daten
(z. B. Okt, Berlin=UTC+1) gilt **Dialog-Eingabe == Berlin-Wanduhr**. Verifiziert: Berlin 09:00/15:30 →
beobachtete Marktzeit 08:00/14:30 UTC (Abweichung 0,0 min). Umrechnung Berlin→UTC via `TimeZoneInfo`
„W. Europe Standard Time" (DST-korrekt); je Fenster gegen beobachtete tMarket geprüft, Abbruch bei >15 min.

### Phase 2 (volle Fenster) — Aufwandsschätzung aus Phase 1
Fenster: 09:00–12:00 (3 h) + 15:30–18:00 (2,5 h), je 2 Tage = **11 h Replay bei 1×** + Laden.
Speicher (aus 1-min-Proben hochgerechnet): ruhig/vorbörslich ~6–8 MB/min, US-Session ~14 MB/min →
grob **~7 GB gesamt** (2 Tage). **Beschleunigung vor Serieneinsatz separat prüfen** (gleicher Abschnitt
1× vs. beschleunigt; Tick-Reihenfolge/Preise/Volumen/Aggressor vergleichen; Empfangszeiten dürfen abweichen).

## Grenzen (nicht belegt)
- Nur ein **vorbelegter** Abschnitt wurde geladen — **beliebige Datumsauswahl** ist offen.
- **Session-Wechsel** und **Serienbetrieb** (mehrere Läufe hintereinander) sind offen.
- Eine einzelne aktive Instanz bei zwei Replay-Charts beweist **nicht allein** die eindeutige
  Chart-Zuordnung — die Zuordnung zum rechten 4RXV stützt sich auf die Nutzerangabe + „genau eine Instanz".
- Aus der Aufnahme **nicht** die Anbieter-Vollständigkeit oder DOM-Qualität ableiten.

Aufnahmen liegen außerhalb des Repos auf dem Datenlaufwerk **`F:\ATAS_Data\Recordings\`** (Projektvorgabe: keine
Aufnahmen auf C:). Überschreibbar via Umgebungsvariable `ATAS_RECORDINGS_DIR`; der Recorder schreibt per Default nach
`F:\ATAS_Data\Recordings` und bricht ab, falls das Ziellaufwerk fehlt (kein stiller C:-Fallback). Keine Marktdaten committen.
Datenstruktur auf F:: `Recordings\` (Aufnahmen), `Recorder\` (Quelle/DLL/Validator/build), `Evidence\` (Nachweise).

---

## Sammellauf (`collect`) — mehrere Fenster in EINE gemeinsame Trade-CSV je Session+Jahr

EIN Startbefehl mit JSON-Konfiguration. Je Fenster wird der bewährte Aufnahmeablauf (`RunRecordingJob`)
verwendet — konfigurierbare Geschwindigkeit (setzen→zurücklesen→verifizieren), auftraggebundenes
Replay-Endsignal aus dem ATAS-Log (auch bei 100× über ein log-basiertes Play-Started-Signal), unveränderte
Cleanup-Prüfungen — und danach die Fenster-Trades **offline, atomar und wiederanlauffähig** an die gemeinsame
CSV angehängt (`research/data-bridge/atas_collect.py`, baut auf der Brücke `atas_bridge.py` auf).

```bash
AtasReplayProbe collect collect_config.example.json lauf.txt
```

Nur **Trade-Ticks** landen in der CSV (kein DOM/BBO/Bars). Spalten mit voller Zuordnung:
`session,year,job_id,recording_id,run_id,src_seq,recorder_seq,t_market_utc,instrument,contract_or_series,
price,volume,aggressor_side,exch_order_id,aggr_exch_order_id,open_interest`. Zeit ns-verlustfrei, Preis als
Decimal verlustfrei, `contract_or_series=unknown` (Symbollabel beweist die Serie nicht — nichts erfunden).

Ausgabe je Session+Jahr unter `F:\ATAS_Data\Collections\<Session>_<Jahr>\`: `trades.csv` (gemeinsame Datei) +
`collection_manifest.json` (Fortschritt/Herkunft/Commit-Punkt). **Wiederanlauf:** bereits committete Fenster
werden übersprungen (kein Doppel-Anhängen); ein unvollständiger Anhänge-Vorgang (Abbruch vor dem Commit) wird
beim nächsten Lauf sicher auf den letzten bestätigten Byte-Stand zurückgesetzt und sauber wiederholt. Originale
Aufnahmen werden nie gelöscht; neue Zwischenaufnahmen werden (vorerst) NICHT automatisch gelöscht.

Konfig-Felder: `session, year, timezone` (TimeZoneInfo-Id, DST-korrekt), `speed` (z.B. `"100x"` oder `"1x"`),
`collectionDir` (auf F:), `python`, `collectorScript`, `keepRecorderAttached` (Standard false = pro Fenster
add/remove → saubere, finalisierte Ordnertrennung als Voraussetzung fürs Anhängen), `windows[]` mit
`startBerlin`/`endBerlin` (`dd.MM.yyyy HH:mm:ss`). Beispiel: `collect_config.example.json`.

Collector direkt (ohne ATAS), z.B. für Prüfungen/Vergleich:
```bash
python research/data-bridge/atas_collect.py status  --collection F:\ATAS_Data\Collections\TestMix_2026
python research/data-bridge/atas_collect.py compare --a <rec1x> --b <rec100x> --start <utc> --end <utc>
```

Offline-Tests: `cd research/data-bridge && python -m pytest`  (Brücke + Collector).
Probe-Regression inkl. Speed/Watchdog/Resume-Helfer: `AtasReplayProbe regress`.
