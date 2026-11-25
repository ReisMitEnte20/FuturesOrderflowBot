# London + New York — gemeinsamer Lauf und gemeinsames Replay

Simulation/Research only. Keine Live-Orders, keine Broker-Verbindung.

## Klickweg

- **Backtest:** Backtesting → **London + New York** → Zeitraum (Von/Bis) → **Start** → **Trades im Chart**.
- **Replay:** Backtesting → Chart & Replay → **London + New York** (Lauf und Handelstag wählen, optional „Folgetag anschließen“).
- **Einzelne Session:** „Handelstag abspielen“ hat jetzt eine Sessionwahl (London / New York). London und New York am selben Tag
  sind zwei Sessions und keine Fassungen voneinander.

## Daten und Zeit

- London und New York liegen in getrennten Datenordnern:
  - `TickReplay:CollectionsRoot`, Standard `F:\ATAS_Data\ATAS_Data Collection\Collections`.
  - `TickReplay:LondonCollectionsRoot`, Standard `F:\ATAS_Data\ATAS_Data Collection(London)\Collections`.

  Beide werden über die bestehende Quellenwahl des Tick-Replays gelesen (`TickReplayService.ForCollectionsRoots`). Keine CSV wird
  kopiert oder zusammengeführt. Gelesen wird nur der committete Byte-Abschnitt je Job (Cache unter `artifacts/tickreplay-cache`).
- Sessionplan: `config/sessions/london-newyork.example.json`; lokale Abweichungen in `*.local.json`.
  - London 08:00–11:00 `Europe/London`, New York 09:30–12:00 `America/New_York`.
  - Die UTC-Fenster werden je Tag sommerzeitrichtig berechnet. Die Pause ist nicht fest: 3 h 30 min im Regelfall, 2 h 30 min,
    wenn die USA schon bzw. noch Sommerzeit haben und Großbritannien nicht.
  - Gelaufen wird mit den tatsächlichen UTC-Fenstern der Manifeste.
- Je Session wird geprüft:
  - Aufnahme-ID und Recorder-Drops (`summary.json`).
  - Manifest: `summary_clean` und `tick_drops`.
  - Fenster gegen den Plan.
  - Beim Laden zeilenweise: Instrument MNQ, Aufnahme-/Lauf-Identität, Chronologie und Aggressor.
  - Der Kontrakt muss innerhalb der Session einheitlich sein und zwischen London und New York gleich.
- Tagesstatus:

  | Status | Bedeutung | Lauf |
  |---|---|---|
  | vollständig | beide Sessions ok | läuft |
  | unvollständig | eine Session fehlt, ist fehlerhaft oder hat einen Quellenkonflikt | ausgelassen, nie als „normale Pause“ |
  | keine Daten | Feiertag oder Lücke; aus den Daten nicht unterscheidbar | ausgelassen |
  | nicht eindeutig | überlappende Fenster aus verschiedenen Aufnahmen | ausgelassen |

  Überlappende Fenster aus *derselben* Aufnahme werden so geteilt, dass die spätere Session am Ende der früheren beginnt. Dieselben
  Ticks laufen so nicht doppelt.

## Ein Lauf, eine Strategie, ein Konto

- Es gibt EINEN Lauf der bestehenden tick-getriebenen `BacktestEngine`:
  - Kette: Strategy → RiskManager → OrderManager → PositionManager mit dem simulierten Fill-Adapter.
  - Es läuft eine Strategieinstanz, die gegatete DeltaFlow-Strategie über `OrderflowProofCandidates`, mit einem Konto.
  - Die Ticks werden tageweise gestreamt (`SessionRunService.SessionTickFeed`).
- Die Pause wird nicht abgewartet:
  - Nach dem letzten London-Tick folgt sofort der erste New-York-Tick mit seinem originalen Marktzeitstempel.
  - Die Engine-Uhr steht auf Marktzeit. Zeitbasierte Abläufe sehen die tatsächlich vergangene Zeit.
- `SessionGateStrategy` (`TradingBot.Application/Sessions`) erzeugt nur Signale und regelt die Sessiongrenzen:

  | Regel | Wirkung |
  |---|---|
  | Sessionstart | Die angefangene N-Tick-Bar der Vorsession wird verworfen. Session-CVD und Session-VWAP starten neu. |
  | Close-Out (vorab festgelegt) | Ab Sessionende − N min (Standard 5) keine neuen Einstiege. Eine offene Position erhält ein Flat-Signal, das die Engine mit dem vorhandenen Fill-/Kostenmodell am nächsten Tick füllt. Es gibt keinen rückwirkenden Ausstieg am „letzten“ Tick. |
  | Kein ausführbarer Tick vor der Grenze | Ist vor dem ersten Tick der nächsten Session noch eine Position oder Order offen, stoppt der Lauf **vor** diesem Tick als „unvollständig – Position vor Sessionende nicht glattstellbar“ (Engine-`TickGuard` → `SessionGateStrategy.BeforeTick`). Es gibt keinen rückwirkenden Fill auf dem letzten Tick und keine Glattstellung am ersten Tick nach der Pause; keine offene Order gelangt in die nächste Session. Der Bericht zeigt getrennt: letzter beobachteter Preis, offene Bewertung (brutto, nicht gebucht) und realisiertes Ergebnis. Dieselbe Regel gilt am Datenende. Ohne Guard bricht das Gate den Lauf fail-closed ab. |
  | Tagesrisiko | Der RiskManager prüft je UTC-Kalendertag für London und New York gemeinsam. Hat London das Tagesverlustlimit erreicht, sind in New York an diesem Tag keine neuen Einstiege möglich. |

- Was beim Sessionwechsel zurückgesetzt wird und was weiterläuft, steht in der Tabelle „Regeln und Indikatoren“ des Berichts
  (`SessionRunService.IndicatorPolicy`). Indikatoren mit Zeitfenster über die Pause gelten als nicht gültig. DeltaFlow nutzt keine solchen.
- Der Bericht zeigt:
  - Trades, Gebühren und Netto für London, New York und Gesamt.
  - Übergänge: letzter und erster Tick, übersprungene Dauer, Kontostand vor/nach, PnL und Trades im Sprung.
  - Sessiondaten mit Quelle, Aufnahme, Kontrakt und SHA.
  - Session-Close-Status, Tageslimit und das Journal mit Sprung in den Chart.
  - Je Trade Ein-/Ausstiegsgrund sowie Signal-, Order- und Fill-IDs (Engine: `BacktestResult.Orders`/`TradeLinks`), dazu eine
    ID-Prüfung (jede Order mit Trade verknüpft, keine geteilten Fills, keine überlappenden Trades) und die Häufigkeit der Gründe.
  - Kontraktprüfung: Sind die Kennungen „unknown“, steht dort „Kontraktidentität ungeprüft“. Zwei unbekannte Kennungen sind kein Nachweis.
- Läufe werden lokal unter `artifacts/sessions-runs/` gespeichert (gitignored).

## Unabhängig nachgerechnet: 17.11.2026

Grundlage sind die Original-Ticks (committeter Byte-Abschnitt der beiden Manifeste, SHA-256 identisch mit dem Lauf) und die
Profilwerte aus `config/`. Die Nachrechnung ist ein eigenes Skript ohne Projektcode. Alle 667 Trades stimmen in Zeit, Fill-Preis,
Gebühren und Netto überein (0 Abweichungen):

- London: 113 Trades, Gebühren 88,14, Netto −121,14.
- New York: 554 Trades, Gebühren 432,12, Netto −8.553,12.
- Endkonto: 1.325,74.

Die 554 New-York-Trades sind getrennte geschlossene Positionen:

- 1.108 Orders mit 1.108 verschiedenen Signal-IDs.
- 554 verschiedene Einstiegs- und 554 Ausstiegs-Fills, kein Fill wird von zwei Trades geteilt.
- Kein Trade überlappt einen anderen, und es gibt keinen Reverse (ein Gegensignal schließt nur).
- Ursache ist die Taktung: 1.677.014 NY-Ticks ergeben 3.354 Bars mit je 500 Ticks, also etwa alle 2,7 s eine Entscheidung, bei
  einer Delta-Schwelle von nur ±30.

Kosten je Round-Turn: 0,78 Gebühren plus 2 Ticks Slippage (1,00 USD).

## Replay

- Die Sessions eines Tags liegen hintereinander in einem Tick-Speicher mit einer Marktuhr.
- Am Sessionende steht die Uhr kurz (≈1,2 s Wandzeit) an der Grenze. Dabei erscheint der Hinweis „London beendet · 3 h 30 min
  übersprungen · New York beginnt“. New-York-Kerzen, -Werte und -Trades sind in dieser Zeit noch nicht sichtbar.
- Danach springt die Uhr auf den ersten echten New-York-Tick und spielt weiter. Die reale Pause wird nicht abgewartet.
- Gespeicherte Fills erscheinen erst, wenn die Uhr sie erreicht hat.
- Delta/CVD/VWAP starten je Session neu.

## API

- `GET  /api/backtest/sessions/plan?from=YYYY-MM-DD&to=YYYY-MM-DD[&closeOutMinutes=N]`
- `POST /api/backtest/sessions/run` mit Body `{from, to, ticksPerBar, deltaThreshold, initialBalance, maxDailyLoss?, closeOutMinutes?, applyFees, slippageTicks?}`.
  Der Aufruf ist synchron. Maximal 31 Kalendertage, es läuft immer nur ein Lauf gleichzeitig.
- `GET  /api/backtest/sessions/runs`, `GET /api/backtest/sessions/runs/{runId}`

## Live-Paper (später)

`TradingSessionSchedule.StateAt(utc)` liefert für jeden Zeitpunkt Phase, aktuelle und nächste Session sowie `NewEntriesAllowed`. Das ist
dieselbe Regel wie im Backtest. Während der echten Pause sind keine neuen Einstiege erlaubt. Sie ist in diesem Stand **nicht** an den
Paper-Betrieb angebunden: keine Live-Orders, keine Broker-Verbindung.

## Grenzen

- Nur DeltaFlow; andere Strategien sind im Lauf nicht wählbar.
- `contract_or_series` ist in den Aufnahmen „unknown“. Die Kontraktidentität ist damit **ungeprüft**; Front-Month und Roll sind nicht prüfbar.
- Ein Lauf, der an einer Sessiongrenze stoppt, bleibt unvollständig. Es gibt keine Option, nur die betroffene Session zu überspringen.
- Unvollständige Tage werden komplett ausgelassen. Es gibt keine Option „nur die vorhandene Session handeln“.
- Feiertag und fehlende Aufnahme sind aus den Daten nicht unterscheidbar; kein Börsenkalender.
- Der Lauf ist synchron und auf 31 Kalendertage begrenzt. Es gibt keinen Fortschrittsbalken und keinen Abbruch-Knopf.
- Das Konto kann negativ werden. Das Research-Risikoprofil hat kein Margin-/Nullstopp. Das Tageslimit ist nur aktiv, wenn es im Lauf
  gesetzt wird.
- Das Replay zeigt einen Tag (optional mit Folgetag). Mehrtägige Läufe werden tageweise abgespielt.
