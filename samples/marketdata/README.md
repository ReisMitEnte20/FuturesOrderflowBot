# MarketData CSV-Formate

Zwei unterstützte Formate. Spalten werden **header-basiert** und **case-insensitive** zugeordnet;
die Reihenfolge der Spalten ist egal. Leerzeilen und Zeilen, die mit `#` beginnen, werden ignoriert.
Zahlen im Invariant-Format (Punkt als Dezimaltrenner), Zeitstempel ISO 8601 (UTC empfohlen).

## 1. Minimal Tick CSV (nur OHLCV-Aggregation)

Pflichtspalten:

| Spalte | Bedeutung |
|--------|-----------|
| `timestamp` | Zeitpunkt des Ticks (ISO 8601, z. B. `2026-06-23T13:30:00Z`) |
| `symbol` | Instrument (z. B. `NQ`) |
| `price` | Trade-Preis (> 0) |
| `volume` | gehandeltes Volumen (≥ 0) |

→ Damit möglich: **Time-Candles, Tick-Bars, Volume-Bars** (OHLCV).
→ Damit **NICHT** möglich: Orderflow/Delta (siehe unten).

## 2. Erweiterte Orderflow CSV

Zusätzliche optionale Spalten:

| Spalte | Bedeutung |
|--------|-----------|
| `bid` | bestes Bid zum Tick-Zeitpunkt |
| `ask` | bester Ask zum Tick-Zeitpunkt |
| `tradedirection` | Aggressor-Seite: `buy`/`b`/`ask`/`1` oder `sell`/`s`/`bid`/`-1` (oder leer/`unknown`) |
| `bidvolume` | am Bid gehandeltes Volumen (Verkäufer-Aggressor) |
| `askvolume` | am Ask gehandeltes Volumen (Käufer-Aggressor) |

→ Damit zusätzlich möglich: **OrderFlowBar** mit BidVolume, AskVolume, Delta, CumulativeDelta.

## Wichtig: Keine erfundenen Orderflow-Daten

Die Aggressor-Klassifikation (`Aggressor`) wird **nur** gesetzt, wenn echte Daten vorliegen:

1. **`tradedirection`** ist die maßgebliche Quelle.
2. Fehlt sie, wird die Seite **nur** abgeleitet, wenn **genau eine** der Spalten
   `bidvolume`/`askvolume` ein Volumen > 0 trägt.
3. Andernfalls bleibt der Tick `Unknown`.

Der `OrderFlowBarAggregator` **wirft** `OrderFlowUnavailableException`, sobald ein
unklassifizierter Tick (`Unknown`) für eine Orderflow-Bar verwendet werden soll.
Delta/Bid-/Ask-Volumen werden **niemals** geschätzt oder erfunden.

**Nur approximierbar (NICHT für echte Orderflow-Analyse verwenden):** Eine Aggressor-Schätzung
allein aus dem Trade-Preis gegen Bid/Ask (Tick-Rule/Lee-Ready o. Ä.) ist bewusst NICHT
implementiert, weil sie für seriöse Orderflow-Analyse zu ungenau ist. Wer echtes Delta will,
braucht eine Datenquelle mit echter Trade-Direction bzw. Bid-/Ask-Volumen.
