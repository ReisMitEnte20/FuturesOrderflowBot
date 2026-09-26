# Quant-Research-Plattform

Aufbauend auf der bestehenden bar-basierten OHLC-Engine (`TradingBot.Backtesting.Ohlc`).
Ziel: Strategien aus Papers und später KI-Vorschläge unter **denselben reproduzierbaren Regeln**
prüfen. Langfristiger Vergleichsmaßstab ist der **S&P 500 Total Return**.

> Es wird weder behauptet noch garantiert, dass eine hier geprüfte Strategie diesen Maßstab
> schlägt. Die Plattform misst — sie verspricht nichts.

Alle Berechnungen laufen im Backend (`TradingBot.Quant`), das React-Dashboard stellt ausschließlich
**echte** Ergebnisse dar. Es gibt keine Mock-Daten und keine clientseitig erfundenen Kennzahlen.
OHLC bleibt die Grundlage; Orderflow wird nicht erfunden. Rithmic bleibt deaktiviert, es werden
keine Orders erzeugt.

---

## Projektaufbau

| Ort | Inhalt |
|---|---|
| `src/TradingBot.Quant/Series` | Zeitlich ausgerichtete Kapitalkurven und Netto-Renditereihen |
| `src/TradingBot.Quant/DataQuality` | Prüfung auf Lücken, Dubletten, Sessions, Teilkerzen, Roll-Verdacht |
| `src/TradingBot.Quant/Registry` | Persistentes Versuchs-/Kampagnenregister, Datenfingerabdruck |
| `src/TradingBot.Quant/Metrics` | Performancekennzahlen, Handelsaktivität, rollierende Kennzahlen |
| `src/TradingBot.Quant/Benchmark` | Austauschbarer Benchmarkadapter, Beta/Alpha/Information Ratio |
| `src/TradingBot.Quant/Validation` | Walk-forward (rollend/verankert), Purging, Embargo, Holdout |
| `src/TradingBot.Quant/MonteCarlo` | Reproduzierbarer RNG, Permutation, Block-/stationärer Bootstrap |
| `src/TradingBot.Quant/Robustness` | Kosten-/Verzögerungs-/Parameterstress, Signalverzögerungs-Dekorator |
| `src/TradingBot.Quant/Overfitting` | PBO über CSCV, PSR, DSR, effektive Versuchszahl |
| `src/TradingBot.Quant/Research`, `…/Generation` | Paper-Einträge, Generator-/Evaluator-Trennung |
| `src/TradingBot.DevDashboard/Services/Quant` | API-Schicht, Job-Verwaltung (Fortschritt/Abbruch/Limit) |
| `dashboard/src/pages/Research.tsx` | Oberfläche mit sieben Bereichen |

---

## A — Forschungsgrundlage

### Versuchsregister

Dateibasiert unter `artifacts/quant/registry/` (menschenlesbares JSON, nicht im Repository).

Eine **Kampagne** speichert **vor** der Suche: Hypothese, Suchraum, Auswahlkriterium,
Versuchsbudget und den finalen Holdout — und wird beim Anlegen **gesperrt**. Das macht die
effektive Versuchszahl nachprüfbar und verhindert nachträgliches Umdefinieren des Erfolgs.

Ein **Versuch** hält fest: Strategie-Id und -version, Herkunft (`Manual` / `Paper` / `Ai`) mit
Referenz, Parameter, **Datenfingerabdruck** (SHA-256 über alle OHLCV-Werte), Kostenprofil,
Codeversion (Git-Commit), Seed, ausgewerteter Zeitraum samt Rolle, Status und Ergebnisse.

Erzwungen wird:

- **Budget** — über das vorab gespeicherte Budget hinaus wird kein Versuch angenommen.
- **Holdout-Schutz** — ein Zeitraum, der in den Holdout hineinragt, wird abgelehnt, sofern er nicht
  ausdrücklich als `PeriodRole = "holdout"` deklariert ist; der Holdout ist nach einmaliger
  Auswertung verbraucht.
- **Begründungspflicht** — `Failed` und `Discarded` verlangen einen Grund.

Negative, fehlgeschlagene und verworfene Versuche bleiben dauerhaft erfasst. Ohne sie wäre die
Mehrfachtest-Korrektur (PBO/DSR) systematisch zu schwach.

### Renditereihen und Kapitalbasis

`ReturnSeriesBuilder` erzeugt aus einem Backtest eine zeitlich ausgerichtete Kapitalkurve:

- **REALISIERT** = Startkapital + kumulierter NetPnL abgeschlossener Trades.
- **GESAMT** = zusätzlich Mark-to-Market einer offenen Position zum Bar-Close, **abzüglich** der
  geschätzten Glattstellungskosten (Round-Turn-Gebühren + Exit-Slippage). Damit sind beide Kurven
  gebührenkonsistent; die Engine bucht Gebühren erst beim Exit.

Beide Basen werden getrennt geführt und **nie** vermischt — auch die Drawdowns nicht.

Aggregation auf Tag/Woche/Monat nimmt ausschließlich **beobachtete** Perioden (letzter Bar der
Periode). Fehlende Kalenderperioden werden nicht ergänzt. Fällt das Kapital auf ≤ 0, wird die
Renditereihe **abgebrochen** statt eine undefinierte Rendite zu erfinden.

### Datenqualität

Gemeldet werden Dubletten, nicht chronologische Zeitstempel, OHLC-Integritätsverstöße,
Intraday-Lücken (von erwarteten Session-/Wochenendlücken getrennt), Kerzen ohne Volumen,
ausgeschlossene Teilkerzen und ein **Roll-Verdacht** bei großem Preissprung über eine Lücke.
Der Roll-Verdacht ist ausdrücklich ein Verdacht aus der Preisstruktur, keine bestätigte
Kontraktinformation. Es wird nichts gefüllt, interpoliert oder ersetzt.

---

## B — Kennzahlen und Benchmark

Jede Kennzahl trägt **Methode, Eingaben, Grenzen und Datenumfang** und ist im Dashboard aufklappbar.
Ein nicht berechenbarer Wert erscheint als „n. b." mit Begründung — **nie** als 0.

Festgelegte Konventionen:

- Volatilität: Stichproben-Standardabweichung (n−1) × √(Perioden/Jahr).
- Sharpe: (Mittel der Überschussrendite / Standardabweichung) × √(Perioden/Jahr); der risikofreie
  Zins wird **geometrisch** auf die Periode heruntergebrochen.
- Sortino: gleicher Zähler; Nenner = Downside-Deviation gegenüber derselben Mindestrendite,
  Quadratsumme über **alle** Perioden gemittelt (abweichende Konventionen existieren).
- CAGR: geometrisch über die tatsächlich überspannte Kalenderzeit (Jahre = Tage / 365,25).
- Calmar: CAGR / maximaler relativer Drawdown.
- Expected Shortfall / VaR: rein **historisch**, kein Verteilungsmodell.
- Exposure, Umschlag, Gewinnkonzentration (Top-5-Anteil, Herfindahl) aus den Trades.

**Annualisierung** ist wählbar: `Observed` leitet die Perioden pro Jahr aus der tatsächlichen
Beobachtungsdichte ab (datengebunden, bei kurzen Reihen unsicher), `Fixed` nimmt einen gesetzten
Wert (z. B. 252). Der verwendete Faktor steht in jedem Bericht.

**Undefined-Fälle** liefern null mit Begründung: konstante Renditereihe (Sharpe), keine Periode
unter der Mindestrendite (Sortino), Drawdown = 0 (Calmar), zu kurze Reihe (α·n < 1 beim ES),
Kapital ≤ 0 (CAGR). Eine rechnerisch konstante Reihe wird relativ zum Skalenniveau erkannt, damit
Gleitkomma-Rundung kein absurd großes Verhältnis erzeugt.

### Benchmark

Austauschbarer Adapter; voreingestellt CSV aus `data/benchmarks/` (siehe dortige README).
**Ohne echte Datei gibt es keine Vergleichskurve** — es wird keine erzeugt.

Verglichen wird ausschließlich auf **gemeinsamen** Zeitstempeln (innere Verknüpfung, keine
Fortschreibung, keine Interpolation). Beta und Alpha stammen aus der OLS-Regression der
Überschussrenditen samt **Standardfehlern und t-Werten**; zusätzlich Tracking Error,
Information Ratio, Korrelation und R².

Ausdrücklich ausgewiesen wird, wenn

- die Benchmarkreihe **nicht** als Total Return bestätigt ist (dann wird die Vergleichsrendite
  unterschätzt und der Vergleich ist zugunsten der Strategie verzerrt),
- die Währungen abweichen,
- **nicht** bestätigt ist, dass die Strategie-Equity ein **voll finanziertes Konto** abbildet.
  Futures werden auf Margin gehandelt; eine auf die Margin bezogene Rendite ist keine
  Gesamtkapitalrendite und darf nicht mit einem Index verglichen werden.

---

## C — Zeitlich unabhängige Validierung

`WalkForwardPlanner` erzeugt **ausschließlich chronologische** Aufteilungen — rollend oder
verankert. Zufallssplits von Finanzzeitreihen sind bewusst nicht möglich: sie würden Zukunftsdaten
ins Training tragen.

- **Purging**: Trainingsbars, deren Informationsintervall (`LabelSpanBars`) in das Testfenster
  hineinreicht, werden entfernt (nach López de Prado).
- **Embargo**: Bars unmittelbar nach dem Testfenster bleiben gesperrt.
- **Warmup**: die ersten *n* Bars je Abschnitt dienen nur dem Indikatoraufbau.
- **Positionen an Grenzen**: offene Positionen werden am Abschnittsende zwangsweise geschlossen;
  die Kosten fallen im jeweiligen Abschnitt an und werden nicht übertragen.
- **Holdout**: ein Anteil am Ende wird reserviert, im Kampagnenregister hinterlegt und von der
  Suche nicht berührt.

Die **Parameterauswahl je Fenster nutzt ausschließlich das Training**. Die Testabschnitte werden
für alle Kandidaten ausgewertet, aber nur, um später Overfitting messen zu können.

CPCV ist bewusst **noch nicht** enthalten und wird später als **eigenes** Verfahren ergänzt — es
ist nicht dasselbe wie PBO-CSCV.

---

## D — Monte Carlo und Robustheit

Reproduzierbarer Generator (xoshiro256** mit SplitMix64-Start): derselbe Seed liefert exakt
dieselbe Folge, plattformunabhängig.

| Verfahren | Was es erhält, was es zerstört |
|---|---|
| **Permutation** | ausdrücklich **nur Reihenfolgeanalyse**: die Summe bleibt unverändert, es variieren Pfad, Drawdown und Verlustserien; serielle Abhängigkeit wird zerstört |
| **Moving-Block-Bootstrap** | erhält Abhängigkeit innerhalb fester Blöcke, zerstört sie an den Blockgrenzen |
| **Stationärer Bootstrap** | geometrische Blocklängen (Politis/Romano), Reihe bleibt stationär |

Bei mehreren Reihen (Strategie + Benchmark, mehrere Strategien) wird **gemeinsam** resampelt:
ein Index-Pfad je Lauf für alle Reihen, damit deren Abhängigkeit erhalten bleibt.

Gespeichert und berichtet werden Seed, Wiederholungen, Blocklänge, Horizont und die
Kapitalfortschreibung. Ausgegeben werden Verteilungen für Endkapital, maximalen Drawdown und
längste Verlustserie. Ein **Kapitalgrenzenrisiko** wird nur für eine ausdrücklich gesetzte Grenze
und den gewählten Horizont berichtet.

> Die Ergebnisse sind **Szenarien unter den gewählten Annahmen**, keine empirischen
> Wahrscheinlichkeiten für künftige Marktverläufe. Die Simulation zieht aus der beobachteten
> Vergangenheit und kann nichts über Regimewechsel oder bisher nicht beobachtete Verluste sagen.

**Stress**: Gebühren- und Slippage-Gitter, Ausführungsverzögerung (über einen Dekorator, der
Signale um *n* Bars verzögert — die Engine bleibt unverändert) und Parameter-Nachbarschaft.
Szenarien ohne gültigen Wert zählen nicht als schlechtes Ergebnis, sondern werden getrennt
ausgewiesen.

---

## E — Overfitting und Mehrfachtests

**PBO über CSCV** nach Bailey/Borwein/López de Prado/Zhu. Ausdrücklich festgelegt:

- Vorauswahlmetrik: Sharpe je Periode (austauschbar).
- Rangrichtung: höher ist besser, Rang 1 = schlechtester Kandidat.
- Gleichstände: mittlere Ränge.
- Blockaufteilung: zusammenhängende, gleich lange Zeitblöcke; überzählige Perioden am Ende werden
  verworfen und gemeldet (kein Auffüllen).
- Ungültige Werte: betroffene Kandidatenspalten werden ausgeschlossen und ausgewiesen.
- Identische Kandidatenspalten werden erkannt und gemeldet — sie sind keine unabhängigen Versuche.

**PSR** und **DSR** nach den Originalquellen, mit dem Sharpe **je Periode** (nicht annualisiert),
Schiefe und Wölbung. DSR verwendet den erwarteten Maximal-Sharpe unter der Nullhypothese bei
*N* Versuchen.

**Tatsächliche und effektive Versuchszahl** werden unterschieden. Die effektive Zahl kann aus der
mittleren paarweisen Korrelation der Kandidaten geschätzt werden
(`N/(1+(N−1)·ρ̄)`); diese Heuristik stammt **nicht** aus den Originalquellen und ist als Annahme
gekennzeichnet. Korrelierte Varianten werden nicht kommentarlos als unabhängig gezählt.

**SPA / Reality Check** (White, Hansen) ist bewusst **nicht** enthalten und als eigener
Forschungsbaustein vorgesehen. Es gibt **keine Gesamtpunktzahl** und **keine Garantie** aus einem
niedrigen PBO.

---

## F — Dashboard

Route `/research`, Bereiche: **Übersicht · Benchmark · Walk-forward · Monte Carlo · Robustheit ·
Overfitting · Experimente**.

Echte Visualisierungen: Kapitalkurve (realisiert und gesamt) mit Unterwasserkurve, indexierter
Benchmarkvergleich, Monatsrenditen-Tabelle mit Farbskala, rollierender Sharpe und rollierende
Volatilität, Train/Test-Zeitachse je Fenster mit Holdout-Markierung, Verteilungshistogramme mit
P5/Median/P95, Kosten-Heatmap, Szenariotabellen, PBO-Streudiagramm und λ-Verteilung, PSR/DSR mit
Datenbasis und Annahmen, vollständiges Versuchsregister mit Detailansicht und CSV-Export.

Lang laufende Verfahren laufen als Hintergrund-Job mit **Fortschritt, Abbruch** und einer harten
**Laufzeitgrenze** (Standard 10 Minuten); Seeds sind reproduzierbar.

---

## G — Spätere Strategiequellen

**Paper-Einträge** (`artifacts/quant/papers/`) halten Quelle, Hypothese, Datenanforderungen,
Regeln und **dokumentierte Abweichungen** fest, damit ein Ergebnis nicht fälschlich als
Bestätigung oder Widerlegung des Papers gelesen wird.

**Generator und Evaluator sind strikt getrennt.** Der Generator kennt den Suchraum und die
bisherigen Rückmeldungen, aber nicht den Holdout. Rückmeldungen werden automatisch von
Holdout-Kennzahlen bereinigt (Präfix `holdout.`); geschieht das, wird es vermerkt — es deutet auf
einen Fehler im Evaluator hin. Das Versuchsbudget wird eingehalten. **Kein autonomes
Live-Trading.**

---

## Prüfung der Statistik

- **Referenzwerte von Hand**: Mittelwert, Standardabweichungen, Schiefe, Wölbung, Perzentile,
  Normalverteilung (Tabellenwerte), PSR (vollständig durchgerechnetes Beispiel), Sharpe/Sortino/
  CAGR/Calmar/Drawdown/ES auf einer handgerechneten Kapitalkurve, Mark-to-Market-Abzug,
  Monatsaggregation, rollierendes Fenster, Purging-/Embargo-Indizes.
- **Synthetische Nullfälle**: reines Rauschen ohne Vorteil ⇒ PBO nahe ½; Permutation lässt die
  Summe unverändert; zwei identische Reihen behalten beim gemeinsamen Resampling die Korrelation 1.
- **Kontrollierter Effekt**: eine Reihe mit echtem Vorteil unter Rauschreihen ⇒ PBO < 10 %.
- **Randfälle**: identische Kandidaten, konstante Renditen, kleine Stichproben, NaN-Spalten,
  Kapital ≤ 0, Tracking Error 0, fehlende Benchmarkdaten, zu wenige Perioden für ein WF-Fenster.
- **Reproduzierbarkeit**: gleicher Seed ⇒ identische Monte-Carlo-Verteilungen; CSCV ist
  deterministisch.
- **Querprüfung gegen die verifizierte Engine**: der realisierte maximale Drawdown des
  Referenzlaufs beträgt über die neue Auswertungsschicht **536,22** und der Netto-PnL **−524,78** —
  exakt die Werte der vorangegangenen Engine-Prüfrunde.

---

## Bekannte Grenzen

- **Bar-Auflösung, keine Tick-Simulation.** Intrabar-Pfade sind unbekannt; bei SL+TP in derselben
  Kerze gilt konservativ SL.
- **Kosten aus Beispielprofilen.** Solange `config/` nur `*.example.json` enthält, sind alle
  Netto-Kennzahlen vorläufig; das Dashboard weist das aus.
- **Kurzer Datenbestand.** Der derzeit verfügbare lokale Ausschnitt umfasst wenige Tage. Auf
  Tagesbasis entstehen dadurch sehr wenige Renditeperioden; die Plattform meldet das und rechnet
  nichts schön. Für belastbare Aussagen braucht es deutlich längere Historien.
- **Annualisierung kurzer Reihen** ergibt rechnerisch korrekte, aber fachlich nicht
  interpretierbare Werte (CAGR über wenige Tage). Die Kennzahl weist das über ihre
  Grenzen-Angabe aus.
- **Einzige Referenzstrategie** ist bisher ein SMA-Crossover — ausdrücklich eine Teststrategie
  ohne Edge-Behauptung.
- **Kein SPA/Reality Check, kein CPCV** (beide als eigene Bausteine vorgesehen).
- **Keine Aussage über künftige Performance.** Niedriger PBO, hoher PSR/DSR und bestandene
  Stresstests sind die Abwesenheit bestimmter Warnsignale — kein Nachweis einer Edge.
