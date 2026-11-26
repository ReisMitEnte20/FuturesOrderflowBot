# Multi-Timeframe-Orderflow-Backtest (5m · 1m · 4RxV) mit Entscheidungsspur und Trade-Rewind

**Klickweg:** Backtesting → **1 · Lauf starten** → „Multi-Timeframe 5m · 1m · 4RxV“

1. Strategie wählen.
2. Datensatz wählen: synthetisch oder ATAS-Aufnahme mit Tag.
3. Session wählen: London, New York oder beide.
4. Datenprüfung lesen.
5. **Backtest starten**.

Danach **2 · Ergebnis ansehen**: Journal → **Trade nachspielen**, die Ansicht wechselt zu **3 · Trade nachspielen**.
Direktlinks: `/backtesting?view=mtf&mrun=<Lauf>&mtrade=<Index>`.

## Wo die Strategie Marktereignisse verarbeitet

| Pfad | Was die Strategie bisher sah | Was das Frontend zeichnete |
|---|---|---|
| Tick-Bericht (DeltaFlow) | `OrderFlowBarAggregatorStrategy`: eine N-Tick-Sicht | 5-Minuten-Kerzen im Browser (`tickReplay.ts`), nur Anzeige |
| London + New York | dieselbe N-Tick-Sicht hinter `SessionGateStrategy` | wie oben |
| **neu: Multi-Timeframe** | `MultiTimeframeHost`: 5m, 1m und rangeXV **gleichzeitig** aus einer Ereignisfolge | Rewind zeigt den vom Server aufgezeichneten Zustand |

**Die Lücke:** Die Strategie kannte nur eine Sicht. Mehrere sichtbare Charts entstanden nur im Browser.

**Die Lösung:** `MultiTimeframeHost` ist ein normales `IStrategy` auf der **bestehenden** `BacktestEngine`, die Kette
RiskManager → OrderManager → PositionManager bleibt dieselbe. Er schreibt alle Sichten bei jedem Ereignis fort, auch ohne
Browser. Es gibt keine zweite Engine.

## Verarbeitungsreihenfolge je Ereignis (verbindlich für Schnelllauf, Replay und Rewind)

1. Die Engine füllt offene Orders mit diesem Ereignis.
2. Barabschlüsse werden erkannt:
   - Eine Zeitbar gilt als geschlossen mit dem **ersten Ereignis ab ihrem Ende**.
   - Eine rangeXV-Bar schließt, wenn das Ereignis ihre Spanne überschreiten würde.
3. Das Ereignis fließt in alle Sichten und in den Sessionkontext.
4. Die Strategie entscheidet auf diesem Zustand.
5. Die Order ist frühestens beim **nächsten** Ereignis füllbar.

**Gleiche Zeitstempel:** Es gilt die Reihenfolge der Quelldatei. Die Reihenfolge zu BBO- und DOM-Strömen wird nicht
behauptet; diese Ströme sind hier nicht beteiligt.

## 4RxV

- **Dein Chart:** laut `%APPDATA%\ATAS\Workspaces_v3\Trading321.ws` ist es der ATAS-Charttyp `rangeXV` mit Period 4
  (MNQ); der ES-Chart ist rangeXV 2.
- **Unsere Umsetzung ist vorläufig:**
  - Die Spanne beträgt 4 × TickSize.
  - Der auslösende Trade eröffnet die neue Bar zu seinem Preis.
  - Bei Preissprüngen entstehen keine Zwischenbars.
  - Zum Sessionbeginn startet eine neue Bar.
- **ATAS-Gleichheit ist nicht bestätigt.** Oberfläche und Bericht zeigen das an jeder Stelle.

## Kontext (Sessionreset; Konto und Tagesrisiko laufen weiter)

| Wert | Definition |
|---|---|
| Session-VWAP | Σ(Tradepreis × Volumen) / Σ Volumen ab Sessionbeginn – eigene Berechnung, keine ATAS-Linie |
| CVD | Σ(Käufer- − Verkäuferaggressor) ab Sessionbeginn |
| Volumenprofil | sich entwickelnder POC (bei Gleichstand der niedrigere Preis); VAH/VAL 70 %, jeweils die größere Nachbarstufe hinzunehmen |
| TPO | 30-Minuten-Perioden ab Sessionbeginn, jede gehandelte Preisstufe zählt einmal je Periode; eigener POC/VAH/VAL, getrennt vom Volumenprofil |
| Swings | 5m-Fraktal mit Stärke 2: Extrempunkt (Bar/Zeit) und Bestätigung (Ereignis bei Abschluss der zweiten Folgebar) getrennt |
| Absorption | Fenster 30 s, aggressives Volumen ≥ 150, Fortschritt ≤ 2 Ticks – nur Beobachtung, keine Behauptung über Limitorders |

## Entscheidungsspur und Rewind

- **Spur:** Sie wird beim Entscheiden gespeichert (`DecisionTrace`). Sie enthält:
  - Ereignis, Marktzeit, Session, Strategieversion und Parameter;
  - die 5m-, 1m- und rangeXV-Bars (offen und geschlossen);
  - den Kontext;
  - je Regel Rolle, Istwert, Sollwert und Ergebnis (erfüllt / verletzt / nicht prüfbar);
  - die von der Regel benutzten Werte, getrennt von nur sichtbaren.
- **Beinahe-Einstiege:** Der Trigger war erfüllt, der Einstieg wurde abgelehnt; der tatsächliche Grund wird mitgespeichert.
- **Zuordnung:** Jeder Trade ist über die IDs verknüpft: Signal → Order → Fill.
- **Rewind:**
  - Der Server wiederholt den Lauf ab Sessionbeginn, damit VWAP, CVD, Profile und Strategie am Fensterbeginn exakt ihren
    damaligen Zustand haben. Es wird keine Kopie der Session pro Trade angelegt.
  - Er prüft per Fingerabdruck, ob die Wiederholung dem gespeicherten Lauf entspricht.
  - Er liefert je Ereignis den Zustand aller drei Sichten.
  - Der Browser zeigt nur, was bei der Cursorposition bekannt war.
- **Alte Läufe:** Ohne Spur erscheint „damals nicht aufgezeichnet“.

## Datenprüfung vor dem Start

Der Lauf zeigt, was der gewählte Datensatz erlaubt. Er unterscheidet:
- ATAS-Minutenwerte,
- generierte Replay-Ereignisse,
- eigene Berechnungen,
- fehlende Daten,
- vorläufige Annahmen (rangeXV).

Drops und Quellenkonflikte blockieren den Start. Eine Aufnahme mit generierter Tickfolge startet nur als ausdrücklich
gewählter **Demonstrationslauf**, und der Bericht bleibt so beschriftet.

## Grenzen

- **Die ATAS-Semantik von rangeXV ist unbestätigt.** Es fehlt ein Vergleich mehrerer aufeinanderfolgender Bars gegen
  deinen ATAS-Chart (über die Live-Bridge auf dem 4RxV-Chart).
- **Nur der technische MTF-Funktionstest ist verfügbar**, keine Handelsstrategie.
- **Robustheitsprüfungen für MTF-Läufe** (Walk-forward, Monte Carlo, Kostenstress, PBO) sind nicht angebunden. Die
  Kennzahlen im Bericht stammen nur aus dem Basislauf.
- **Ungeprüfte Daten:** BBO, DOM und Tape stehen in den vorhandenen Aufnahmen nur generiert zur Verfügung.
