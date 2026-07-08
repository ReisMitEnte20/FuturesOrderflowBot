# Research Analytics Layer (Phase 12C)

Read-only Doku. **Keine Broker-API, keine Live-Execution, keine Gewinnversprechen.**
Der Research-Layer (`TradingBot.Research`) hilft, Strategien/Parameter ROBUST zu bewerten,
statt zu raten. Er tradet niemals live und sendet keine Orders — er nutzt ausschließlich die
bestehende BacktestEngine (über eine Runner-Abstraktion) und rechnet auf abgeschlossenen Trades.

## Was Research Analytics macht

Vergleicht mehrere Strategie-Kandidaten auf denselben Daten/Profilen und liefert:
Backtest-Kennzahlen · Monte-Carlo-Robustheit · Walk-Forward (IS/OOS) · Parameter-Sweep ·
Fee-/Slippage-Sensitivität · Overfitting-/Robustness-Report · ein gewichtetes Ranking.

**Alle Ergebnisse sind NetPnL-basiert NACH Fees/Slippage.** Tick-/Fee-Werte kommen aus den
Profilen — nichts hardcoded. DataQuality und OrderFlowCapabilities (Phase 12B) fließen als
harte Robustheits-Inputs ein: schlechte Daten oder unzureichendes Datenlevel werten ab.

## Parameter Sweep

`ParameterGrid` erzeugt das kartesische Produkt aus `ParameterRange`s (Int/Decimal/Bool/explizit).
`ParameterSweepRunner` führt jede Kombination als Backtest aus und rankt die Ergebnisse.
`MaxRuns` begrenzt hart (kein endloser Sweep). Deterministisch bei gleicher Config.
Keine versteckte Optimierung — nur systematisches Durchtesten + transparentes Ranking.

## Monte Carlo — was es kann und was NICHT

- **Kann:** die Empfindlichkeit von Endgewinn und Drawdown auf REIHENFOLGE (Reshuffle) bzw.
  Zusammensetzung (Bootstrap mit Zurücklegen) der HISTORISCHEN Trades zeigen. Liefert
  Drawdown-Verteilung, Worst-5%-Drawdown (95. Perzentil), Median/CI des NetPnL,
  Probability-of-Loss/Drawdown/Ruin. Deterministisch über einen Seed (reproduzierbar).
- **Kann NICHT:** die Zukunft vorhersagen. Es ist ein Robustheits-Test auf Basis der bereits
  beobachteten Trades — nicht mehr. Ein guter MC-Worst-Case ist kein Gewinnversprechen.

Reshuffle lässt den Endgewinn konstant (nur der Pfad/Drawdown variiert); Bootstrap variiert beides.

## Walk Forward — warum es Overfitting reduziert

Die Daten werden in disjunkte Fenster geteilt: je Fenster ein **In-Sample (IS)** und ein
zeitlich darauf folgendes **Out-of-Sample (OOS)** (Rolling oder Anchored). Die beste Config
wird **ausschließlich auf IS** ausgewählt und dann auf dem NIE gesehenen OOS getestet.
OOS-Ergebnisse werden strikt getrennt von IS gespeichert.

Eine Strategie, die nur auf IS gut aussieht (überoptimiert), fällt auf OOS auf: die
**Walk-Forward-Efficiency** (OOS-NetPnL/Trade ÷ IS-NetPnL/Trade) wird klein und eine
Overfitting-Warnung feuert. So kann OOS niemals in die Selektion einfließen — genau das
verhindert das „Backtest-schön-optimieren".

## Warum Out-of-Sample wichtiger ist als In-Sample

In-Sample lässt sich immer schönrechnen: mit genug Parametern findet man Regeln, die die
Vergangenheit perfekt erklären (Kurvenanpassung). Nur Out-of-Sample zeigt, ob etwas
GENERALISIERT. Deshalb gewichtet das Ranking OOS-Performance, Monte-Carlo-Worst-Case,
Drawdown und Parameter-Stabilität zusammen (0.55) höher als rohen NetProfit (0.15) —
eine weniger profitable, aber stabilere Strategie kann höher ranken als eine überoptimierte.

## Warum NetPnL nach Fees/Slippage zählt

Bruttogewinne sind irreführend. Der `SensitivityAnalyzer` re-runt echte Backtests mit
höherer Slippage bzw. skalierten Fees und zeigt, ab wann NetPnL verschwindet. Eine Strategie,
die nur bei perfekten Kosten profitabel ist, wird als „fragil" markiert (Robustness-Report).

## Warum Overfitting gefährlich ist

Optimierung verstärkt Overfitting: ein Optimizer findet zuverlässig die Parameter, die
Zufall/Datenfehler der Vergangenheit am besten ausnutzen — das sieht profitabel aus und
verliert live. Der `RobustnessAnalyzer` sammelt Warnsignale (zu wenige Trades, Abhängigkeit
von wenigen Gewinnern, IS/OOS-Divergenz, guter PF bei schlechtem Drawdown, Fragilität gegen
Slippage, enge Parameter-Robustheit, MC-Worst-Case zu schlecht, schlechte Datenqualität,
unzureichende Capabilities) und bündelt sie in einem `RobustnessScore` — ausdrücklich eine
**Hilfsmetrik, keine Wahrheit**.

## Wie Claude später Strategien vergleicht

1. Kandidaten definieren (`StrategyCandidate`: Name + Config + Strategie-Fabrik).
2. `ResearchEngine.RunAsync` über echte, klassifizierte Daten → Backtest + Monte Carlo +
   Robustness je Kandidat + Ranking.
3. Für einzelne Kandidaten: `ParameterSweepRunner`, `WalkForwardAnalyzer`, `SensitivityAnalyzer`.
4. Entscheidung nach **OOS + Robustheit**, nicht nach IS-NetProfit.

## Benchmark: risk-adjusted gegen S&P 500 / ES buy-and-hold

Das eigentliche Ziel ist eine Edge, die **nach Fees/Slippage risikoadjustiert** besser ist als
passiv den S&P 500 (ES) zu halten. Eine Strategie, die nur **brutto** gut aussieht, aber nach
Kosten, Slippage oder Out-of-Sample schlecht ist, zählt als **nicht robust**. Später zu
vergleichen (geplant, noch nicht gebaut): NetPnL nach Kosten, Max Drawdown, Sharpe/Sortino,
MAR/Return-to-Drawdown, Walk-Forward-Stabilität, Monte-Carlo-Worst-Case, OOS-Performance und
Time-in-market/Capital-Efficiency. Datengrundlage dafür: echte Orderflow-Daten
(siehe [MARKET_DATA_SOURCE_GUIDE.md](MARKET_DATA_SOURCE_GUIDE.md)) — **einfache OHLCV-Strategien
dienen nur der Infrastruktur-Validierung, nicht dem Edge-Ziel.**

## Benötigte Datenqualität

Research ist nur so gut wie die Daten. Für Orderflow-Setups sind echte Aggressor-/Bid-Ask-Daten
nötig (Phase 12B: `OrderFlowCapabilities`). Ist das Datenlevel unzureichend oder hat die
Qualität Fehler, wertet der Robustness-Report ab bzw. markiert die Ergebnisse als unzuverlässig.
Erst saubere Daten → dann Research → erst dann Entscheidungen.
