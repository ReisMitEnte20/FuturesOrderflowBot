# ATAS Offline-Datenbrücke

Wandelt eine EINZELNE ATAS-Recorder-Aufnahme verlustarm in Parquet (Trades) + ein separates
Manifest. Rohdaten bleiben unverändert; dieses Tool **liest nur**.

## Start
```
python atas_bridge.py <recording_dir> --start 2026-10-26T08:00:00Z --end 2026-10-26T08:01:00Z --out <F:\...\Bridge\...> [--force]
```
- `--start/--end`: Fenstergrenzen **UTC**, halb-offen `[start, end)` (Ende exklusiv). Europe/Berlin→UTC
  erfolgt im Steuerungslauf; hier wird UTC erwartet.
- Ausgabe auf **F:** (`trades.parquet` + `manifest.json`). Code im Repo auf **A:**.
- `--force`: ersetzt ein bestehendes Ziel. OHNE `--force` wird ein bestehendes Ziel **abgelehnt** (kein
  ungefragtes Überschreiben).

## Integrität (Codex-Finding 5, behoben)
- **Zeitpräzision verlustfrei**: Zeitstempel/Fenstergrenzen werden als ganzzahlige **Nanosekunden** geparst
  (`parse_iso_ns`), nicht über datetime-Mikrosekunden. Sub-Mikrosekunden-Grenzen entscheiden korrekt.
- **Stabiler Eingabe-Snapshot**: Eingabehashes werden **vor und nach** dem Lesen geprüft. Ändert sich die
  Quelle während der Konvertierung → `SourceChangedError`, **nichts** wird veröffentlicht.
- **Status bezieht Fehler ein**: `qualityFlags.status` ist nur `complete`, wenn keine Diskrepanz vorliegt;
  bad-json, fehlende/`clean≠true`-Summary, CSV/NDJSON-Diskrepanz oder off-grid → `check` (+ `statusReasons`).
- **Atomare Veröffentlichung**: Schreiben in ein temporäres Ziel, Parquet validieren, dann atomar umbenennen.
  Ein Abbruch hinterlässt **kein** partielles Parquet neben dem alten Manifest; bestehendes Ziel bleibt intakt.

## Tests
```
python -m pytest test_atas_bridge.py -q
```
Synthetische Fixtures prüfen: Fensterfilter [start,end) (Ende exklusiv), Zählungen, Buy/Sell/Unknown-Volumen,
Preisraster getrennt von tickSize, Reihenfolge bei identischen Zeitstempeln, Rest-Tick-Zählung,
unveränderte Rohdatei-Hashes, deterministischer logischer Output, sichtbare CSV/NDJSON/Summary-Diskrepanzen.

## Semantik-Leitplanken (bewusst konservativ)
- `t_recv_utc` ist die lokale Empfangs-Wanduhr beim Abspielen eines **historischen** Replays — **keine**
  historische Börsen-Empfangszeit. `t_recv - t_market` ist **keine** gemessene Feed-Latenz. **Keine** künstliche Latenz.
- Aggressorseite aus Quellfeld `direction` (Buy/Sell); sonst `Unknown`. Nichts hinzuerfunden.
- Reihenfolge gleichzeitiger Events bleibt erhalten (stabile Dateireihenfolge = `src_seq`). **Keine** Börsensequenz erfunden.
- Kontrakt/Serie bleibt `unknown` (Symbollabel `MNQ`/`#MNQZ6` beweist nicht die historische Quellserie). Venue `unknown`.
- Instrument-`tickSize` und beobachtetes `observedPriceGrid` werden **getrennt** ausgewiesen.
- DOM/Quotes sind eigene, hier **nicht qualifizierte** Typen (nur Präsenz+Hash+Count). **Kein** künstliches L2/L3 aus Trades.
- Ausgeführtes Bid/Ask-Volumen ist **keine** ruhende Orderbuchgröße.
- Rest-Ticks (Vor-/Nachlauf) bleiben in den Rohdaten; der Parquet-Output enthält nur `[start,end)`, Rest wird gezählt.
- Diskrepanzen (CSV vs NDJSON vs Summary) werden **sichtbar** gemacht, nicht geglättet. `summary.clean==true` erst mit
  Recorder-Schema `ofrec-summary-2` (A3); fehlt es, ist der saubere Abschluss **nicht** belegt.

## Spalten `trades.parquet`
`run_id, recording_id, src_seq, recorder_seq, t_market_utc, t_recv_utc, price, volume, aggressor_side,
exch_order_id, aggr_exch_order_id, open_interest, data_type, schema_version`

`schemaVersion = atas-bridge-trades-1`. Spätere Nutzer: C#, Python, NautilusTrader, hftbacktest — jeweils erst nach
Book-Qualifikation für DOM/Quotes. Diese Runde: nur Trades qualifiziert.
