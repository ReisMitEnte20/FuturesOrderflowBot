# Research Dashboard (Phase 12D)

Read-only Doku. **RESEARCH / SIMULATION ONLY — keine Broker-API, keine Live-Execution, keine
echten Orders, keine Netzwerkcalls.** Das Research Dashboard macht die Research-Analytics aus
Phase 12C (`TradingBot.Research`) visuell im `TradingBot.DevDashboard` sichtbar — unter der Route
**`/research`**.

## Was das Research Dashboard zeigt

Eine deterministische **Demo-Auswertung** von drei Beispiel-Kandidaten, aufbereitet in sechs
Bereichen:

- **A · Summary Cards** (Bestkandidat): Best Strategy Candidate, Robustness Score, Net Profit
  (nach Kosten), Max Drawdown, Profit Factor, Expectancy, Monte-Carlo-Worst-5%-Drawdown,
  Walk-Forward-Efficiency, Overfitting Risk.
- **B · Strategy Ranking** (Tabelle): Rank · Strategy Name · Net Profit · Max Drawdown ·
  Profit Factor · Expectancy · Robustness Score · Monte Carlo Worst 5% · OOS Net · Warning Count.
- **C · Monte Carlo**: Runs, Median Net Profit, Worst 5% Drawdown, Probability of Loss,
  Probability of Drawdown > X, 90 %-Confidence-Interval, Median/Worst/Best.
- **D · Walk Forward**: IS/OOS-Segmente, In-Sample- und Out-of-Sample-Ergebnis,
  Walk-Forward-Efficiency, Overfitting-Warnung (wenn IS gut und OOS schlecht) + Überblick über
  alle Kandidaten.
- **E · Fee / Slippage Sensitivity**: NetPnL bei steigender Slippage bzw. steigenden Fees,
  Break-even-Punkte und ein Hinweis, wenn eine Strategie nur bei niedrigen Kosten profitabel ist.
- **F · Equity Curve & Drawdown**: einfache, dependency-freie Inline-SVG-Visualisierung der
  kumulierten NetPnL-Equity und des Underwater-Drawdowns.

Die drei Demo-Kandidaten sind bewusst so gebaut, dass sie je eine Lektion illustrieren:

| Kandidat | Rolle | Was er zeigt |
|---|---|---|
| **MNQ Delta-Reversal (Demo)** | robuster Gewinner | positives OOS, hohe WFE, kostenrobust → Rang 1 |
| **MNQ Absorption-Breakout (Demo, kostenfragil)** | dünne Marge | profitabel nur bei niedrigen Kosten — verschwindet bei mehr Fees/Slippage |
| **MNQ Sweep-Scalper (Demo, überoptimiert)** | Overfitting | In-Sample profitabel, Out-of-Sample negativ → Overfitting-Warnung |

## Was Demo-Daten sind

**Alle Zahlen stammen aus künstlichen, deterministischen Trade-Listen** (`ResearchDemoService`),
NICHT aus echten Backtests auf echten Marktdaten. Erzeugung:

- Pro Kandidat wird die **exakte Gewinnzahl** (`round(TradeCount × WinRate)`) festgelegt; nur die
  **Reihenfolge** der Gewinner/Verlierer wird per festem Seed gemischt (realistische Cluster und
  Drawdowns), plus deterministischer Betrags-Jitter. Fees sind eine klar markierte
  **Demo-Kostenannahme** (kein echter Broker-Tarif).
- Gleicher Seed → identisches Ergebnis. Der Service berechnet einmal und cached das Ergebnis
  (read-only, keine Side-Effects).

Die Auswertung selbst läuft über die **echten** Research-Klassen: `BacktestStatisticsCalculator`
(Kennzahlen), `MonteCarloSimulator` (Monte Carlo), `StrategyRankingService` (Ranking),
`RobustnessAnalyzer` (Overfitting/Robustness). Walk-Forward-Fenster und Kosten-Sensitivität werden
aus denselben Demo-Trades mit derselben Formel wie in `TradingBot.Research` gebildet.

> Das Dashboard behauptet **nicht**, dass dies echte Strategie-Performance ist. Ein oranger
> „Demo-Daten"-Hinweis steht dauerhaft oben auf der Seite.

## Warum Monte Carlo keine Zukunftsvorhersage ist

Monte Carlo prüft nur, wie empfindlich Endgewinn und Drawdown auf die **Zusammensetzung** der
bereits beobachteten Trades reagieren. Die Demo nutzt **Bootstrap** (Ziehen mit Zurücklegen), damit
Endgewinn UND Drawdown variieren und Probability-of-Loss / Confidence-Interval aussagekräftig sind
(die Alternative Reshuffle ließe den Endgewinn konstant und variierte nur den Drawdown-Pfad). Es
sagt nichts über zukünftige Marktphasen, sondern ist ein **Robustheits-Test auf
historischen/simulierten Trades**. Ein guter Worst-5%-Case ist kein Gewinnversprechen.

## Warum OOS / Walk Forward wichtig ist

In-Sample lässt sich immer schönrechnen: mit genug Parametern erklärt man die Vergangenheit
perfekt (Kurvenanpassung). **Out-of-Sample** zeigt, ob etwas GENERALISIERT. Im Walk Forward wird die
Config **nur auf In-Sample** selektiert und dann auf dem nie gesehenen Out-of-Sample getestet. Fällt
die **Walk-Forward-Efficiency** (OOS-NetPnL/Trade ÷ IS-NetPnL/Trade) klein aus oder wird OOS negativ,
feuert eine Overfitting-Warnung — genau das demonstriert der überoptimierte Demo-Kandidat.

## Warum NetPnL nach Kosten zählt

Bruttogewinne sind irreführend. Was zählt, ist **NetPnL nach Fees und Slippage**. Der
Sensitivity-Bereich zeigt, ab wann der NetPnL bei höheren Kosten verschwindet. Eine Strategie, die
nur bei perfekten/niedrigen Kosten profitabel ist (siehe kostenfragiler Demo-Kandidat), ist fragil —
in der Realität mit echten Gebühren und schlechteren Fills wäre sie ein Verlierer.

## Warum das Dashboard keine Live-Trades senden kann

- Das `TradingBot.DevDashboard` referenziert **nicht** `TradingBot.Execution` (per Test abgesichert:
  auch der komplette `TradingBot.Research`-Referenzbaum enthält kein Execution).
- Es gibt **keine** Broker-SDKs, **keine** API-Keys, **keine** Netzwerkcalls, **keine**
  Buy-/Sell-/Flatten-/Order-Buttons und **keine** Live-Controls.
- Der `ResearchDemoService` ist **read-only**: er rechnet lokal auf Demo-Daten und cached das
  Ergebnis. Es existiert im gesamten Projekt kein Code-Pfad zu einem echten Broker (erst Phase 15/16
  hinter demselben Interface — nach separatem Safety-Audit).

## Dashboard starten

```powershell
dotnet run --project src\TradingBot.DevDashboard\TradingBot.DevDashboard.csproj
# oder mit Live-Reload:
dotnet watch --project src\TradingBot.DevDashboard\TradingBot.DevDashboard.csproj run
```

Dann im Browser die angezeigte URL öffnen und **„Research Dashboard"** wählen (Route `/research`).
Weitere Seiten: `/` (Projektstatus) und `/paper` (Paper Trading Monitor,
siehe [PAPER_TRADING.md](PAPER_TRADING.md)).
