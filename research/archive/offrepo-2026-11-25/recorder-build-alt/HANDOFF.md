# ATAS Order-Flow Replay Recorder — Lokaler Handoff

Stand: 2026-10-22. **Außerhalb des Repos** gesichert (nicht in Git, keine Marktdaten/Secrets hier).

## Dateien (dauerhaft, dieser Ordner)
`%USERPROFILE%\ATAS_OF_Recorder\`
- `OrderFlowReplayRecorder.cs` — isolierter, nur-lesender ATAS-`Indicator` (strukturell orderlos). Zeichnet aus den echten
  SDK-Callbacks append-only auf: `ticks.ndjson` (+ `ticks.csv`), `cumtrades.ndjson` (new/update, dedupliziert),
  `bbo.ndjson`, `dom.ndjson`, `bars.ndjson` (OHLCV, Delta, Min/Max-Delta, VWAP, POC, Footprint je Preis),
  `meta.json`, `gaps.ndjson`, `summary.json`. Getrennt: `tMarket` (Replay-Marktzeit) vs `tRecv` (lokale Wanduhr).
  Lokale Seq + Queue-/Drop-Zähler + Lückenmarkierung; nicht-blockierendes Disk-I/O.
- `validate_recording.py` — Cross-Checks + 1×-vs-beschleunigt-Vergleich (kompiliert geprüft).
- `build.csproj` — Referenzliste der nötigen ATAS-DLLs (nur Referenz; lokaler Build nicht möglich, s. u.).

Aufnahme-Ausgabe (zur Laufzeit): `F:\ATAS_Data\Recordings\<Instrument>_<ts>Z\` — ausserhalb Git, Datenlaufwerk F: (Default im Recorder; kein C:-Fallback). [Umzug C:->F: am 2026-10-29]

## Verifikationsstand
- Alle benutzten Signaturen per Reflection gegen die installierte `ATAS.Indicators.dll` **8.0.15.303** geprüft.
- Compile-Check fand zwei echte Fehler (`using DataAnnotations`, mehrdeutiges `TradeDirection`) → **behoben**.
- **Kein lokaler Vollbuild möglich:** ATAS zielt auf **.NET 10**, lokal nur .NET 7/8-SDK (reiner TFM-Artefakt).

## Stand 2026-10-23 — ERLEDIGT (End-to-End bewiesen)
Build-Weg geklaert: ATAS hat KEINEN eingebauten Compiler (kein Roslyn im Ordner); DLL extern bauen.
.NET-10-SDK fehlt, aber .NET-10-Runtime (10.0.3) vorhanden -> Build per Roslyn (8er-SDK) direkt gegen
Runtime+ATAS-DLLs (siehe build.rsp, kein SDK-Install noetig). DLL -> %APPDATA%\ATAS\Indicators (Hot-Reload).
Zwei echte Build-Fehler behoben (TradeDirection + MarketDataType mehrdeutig -> Alias auf ATAS.Indicators.*).
Laufzeit-Fixes: periodischer Flush (auch im Leerlauf, TryTake), Play-Gate fuer Bars (ab erstem echten Trade).

1. Build in ATAS: ERLEDIGT (Log "Created/Changed library", keine Ladefehler).
2. Echte Aufnahme: ERLEDIGT. MNQZ6, 23.06.2026 13:50:00-13:55:40 UTC, Modus bestaetigt
   "Ticks und Orderbuch (Level 2)" = ECHTES Ticks+DOM. 62585 Trades, 139790 DOM-Updates, Drops 0,
   globale Seq lueckenlos, tMarket monoton. ~60 MB / ~770 ev/s.
3. 1x vs beschleunigt (~10x): ERLEDIGT. Auf gemeinsamem Fenster Trades/Vol/Delta IDENTISCH (0 Diff),
   DOM 99,85% (212/139624). Beschleunigt 9446 ev/s, Drops 0, QueueHighWater 129.
4. Importer-Anschluss: ERLEDIGT. ticks.csv -> AtasTickCsvImporter (Profil = atas-tick.example.json):
   62585/62585 akzeptiert, 0 Issues, voll klassifiziert (Buy 31920/Sell 30665/Unknown 0),
   Capabilities DeltaCVD=True, Sigma Vol 66685 / Delta 1919 EXAKT = Recorder. OrderFlowBars gebaut (126).

## Stand 2026-10-23 (II) — Mehrstrategie-Nachweis + Datenprobleme
- Mehrstrategie-Harness dauerhaft gesichert: `multistrat\` (Program.cs, multistrat.csproj, RUN.md, results.txt).
  Startbefehl/Konfig/SHA-256-Fingerabdruck in RUN.md. 3 deterministische Strategien (eine nutzt Delta),
  getrennte Ledgers bewiesen (solo==kombiniert), reproduzierbar, Teilbar ausgeschlossen + EndOfData getrennt.
- DOM untersucht: Buch rekonstruierbar/konsistent (Best Bid/Ask, saubere Tiefe), dataType==side, keine Quellenmischung;
  173/139790 (0,12%) Ausreisser (meist Level knapp ueber Markt + wenige extreme Glitches wie 298454/929985 = Feed-Fehldrucke).
  Preise 1-Punkt-granular (ATAS tickSize=1 fuer diesen MNQ-Kontrakt). DOM NICHT als Strategieeingabe qualifiziert (offener Rest).
- Nativer Footprint-Bug BELEGT: GetCandle(i) liefert im Replay 1-Tick-Candles (nativ 76 vs real 64622 Vol).
  Korrektur-Kandidat deployed (Candle mit 1 Bar Puffer lesen) + Diagnosefeld cbAtFlush -> kurze Verifikationsaufnahme noetig.

## Ergebnisansicht (2026-10-23 III)
- Lokale HTML-Ansicht des Mehrstrategie-Prototyps: `multistrat\view.html` (aus `template.html` + `results.json`,
  erzeugt vom Harness). Equity-Kurven je Strategie, Summary, Trades, klare Technischer-Test-Kennzeichnung.

## Offene Datenprobleme (kurz, Stand 2026-10-23)
- **Preisgranularitaet / TickSize UNGEKLAERT:** MNQ real = 0,25 (per M5-Chart bestaetigt), aber die Juni-Replay-Aufnahme
  ist 1-Punkt-granular (alle Preise ganzzahlig; ATAS InstrumentInfo.TickSize=1). Recorder konvertiert nichts (faithful);
  Ursache liegt im Replay-/Instrument-Kontext, nicht bewiesen. Aufnahme-Chart war NICHT-Zeit-Bartyp "4RXV".
- **Nativer Footprint (bars.ndjson) DEFEKT:** GetCandle(i) liefert im Replay 1-Tick-Candles (76 vs 64622 Vol). Fix-Kandidat
  (1 Bar Puffer, proto-2-bufferfix) deployed, aber NICHT verifiziert. NICHT als Strategieeingabe genutzt.
- **DOM unvalidiert:** Buch rekonstruierbar/konsistent, aber 173/139790 Ausreisser inkl. Extrem-Glitches (298454/929985)
  ungeklaert; kein direkter ATAS-Abgleich. NICHT als Strategieeingabe genutzt.
- **Erfassungsgrenze:** keine registrierten lokalen Drops + reproduzierbare Laeufe beweisen NICHT die vollstaendige
  Erfassung aller ATAS-/Anbieter-Ereignisse.

## Offen (nicht blockierend)
- `bars.ndjson` (nativer Footprint-Read) liefert degenerierte Bars (~1 Vol/Bar) -> Bug im Candle-Read
  waehrend Replay. IRRELEVANT, weil Footprint aus dem Tick-Strom exakt rekonstruiert wird (OrderFlowBarAggregator).
  Bei Bedarf separat debuggen (ATAS Replay-Candle-Semantik).
- DOM: wenige Ausreisserpreise (z.B. 4116/929985) -> beim Auswerten filtern.
- Harness/Build-Artefakte liegen im Scratchpad (importtest/, build.rsp); nicht ins Git.

## Offene Vorbehalte
- Reales historisches DOM ist connector-/abo-/instrument-/datumsabhängig; generierter DOM = synthetisch (steht im Replay-Modus,
  per `meta.json` kenntlich). Vor der Aufnahme den echten „Ticks + DOM"-Modus bestätigen.
- Lokale Seq beweist **keine** Vollständigkeit beim Datenanbieter; identische 1×/fast-Totale ≠ verlustfrei.
- Reuse-Anschluss: `ticks.csv` folgt `config/import-profiles/atas-tick.example.json` → bestehender `AtasTickCsvImporter`
  → `MarketTick` → `OrderFlowBarAggregator`. Kein neuer Importpfad/Backtester nötig.

## Grenzen dieser Session
Keine Orders, keine Feed-/Konto-Änderung, kein Export, kein Commit/Push/Merge, keine Hindsight-Writes. ORB-Arbeit und
MCP-Bridge unangetastet.
