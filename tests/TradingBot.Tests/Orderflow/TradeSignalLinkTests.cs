using FluentAssertions;
using TradingBot.Application.MarketData;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Research;
using TradingBot.Research.Orderflow;
using TradingBot.Research.Runner;
using TradingBot.Tests.Backtesting;
using Xunit;

namespace TradingBot.Tests.Orderflow;

/// <summary>
/// Trade-Nachvollzug im Lauf-Export: Signal (Schluss der Signal-Bar), Entry-/Exit-Fill-Tick, Tickpreis vor Slippage,
/// Brutto/Gebuehren je Trade. Belegt: der Mitschnitt aendert keine Ergebnisse, jede Zuordnung ist durch Engine-Daten
/// bestaetigt (Folge-Tick, Fillpreis = Tickpreis ± Slippage), gleiche Zeitstempel werden ueber die Tick-Reihenfolge
/// aufgeloest, und bei fehlendem/unpassendem Mitschnitt bleibt alles null (keine Schaetzung).
/// </summary>
public class TradeSignalLinkTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 26, 13, 30, 0, TimeSpan.Zero);
    private static readonly string[] Pattern = { "buy", "sell", "buy", "neutral", "sell", "buy", "sell", "buy" };

    private static List<MarketTick> Bars(string[] types, int perBar = 500, bool sameTimestamp = false)
    {
        var ticks = new List<MarketTick>(types.Length * perBar);
        int idx = 0; decimal price = 20000m;
        foreach (var type in types)
        {
            for (int i = 0; i < perBar; i++)
            {
                decimal p = type switch { "buy" => price + i * 0.25m, "sell" => price - i * 0.25m, _ => price };
                var aggr = type switch { "buy" => AggressorSide.Buy, "sell" => AggressorSide.Sell, _ => (i % 2 == 0 ? AggressorSide.Buy : AggressorSide.Sell) };
                // sameTimestamp: viele Ticks teilen sich je 50 einen Zeitstempel (Burst) -> Reihenfolge muss aus dem Index kommen.
                var ts = sameTimestamp ? T0.AddMilliseconds(idx / 50) : T0.AddMilliseconds(idx);
                ticks.Add(new MarketTick { Symbol = "NQ", Timestamp = ts, Price = p, Volume = 1m, Aggressor = aggr });
                idx++;
            }
            price = type == "buy" ? price + perBar * 0.25m : type == "sell" ? price - perBar * 0.25m : price;
        }
        return ticks;
    }

    private static OrderflowRunRequest Request(IReadOnlyList<MarketTick> ticks, IReadOnlyList<StrategyCandidate> cands, decimal slip) => new()
    {
        Ticks = ticks, Symbol = "NQ",
        Instrument = BacktestTestData.Instrument(), Fee = BacktestTestData.Fee(slip), Broker = BacktestTestData.Broker(),
        Risk = BacktestTestData.Risk(), Account = BacktestTestData.Account(),
        TicksPerBar = 500, Candidates = cands, DataSha256 = "testsha", DataSource = "synthetic", CapabilitiesSufficient = true,
        DeltaThreshold = DeltaFlowStrategy.DefaultDeltaThreshold
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_closed_trade_is_linked_to_signal_and_fill_ticks(bool sameTimestamp)
    {
        var ticks = Bars(Pattern, sameTimestamp: sameTimestamp);
        var r = await new OrderflowMultiStrategyRunner().RunAsync(Request(ticks, OrderflowProofCandidates.BuildGated("NQ"), slip: 1m));
        r.Workspace.Provenance.TradeDetailVersion.Should().Be(1);
        decimal slip = 1m * 0.25m, pv = 20m;
        foreach (var s in r.Workspace.Strategies)
        {
            s.Trades.Should().NotBeEmpty();
            foreach (var t in s.Trades)
            {
                t.SignalLink.Should().Be(TradeSignalLinker.Linked, $"{s.Name} #{t.Index} -> [{t.SignalLink}]");
                var es = t.EntrySignal!; var xs = t.ExitSignal!;
                es.TickIndex.Should().Be((es.BarIndex + 1) * 500 - 1, "Signal = Schluss (letzter Tick) der Signal-Bar");
                t.EntryTickIndex.Should().Be(es.TickIndex + 1, "Market fuellt am unmittelbar folgenden Tick");
                t.ExitTickIndex.Should().Be(xs.TickIndex + 1);
                xs.BarIndex.Should().BeGreaterThan(es.BarIndex);
                es.Direction.Should().Be(t.Side);
                es.PositionBefore.Should().Be("Flat");
                xs.PositionBefore.Should().Be(t.Side);
                es.Reason.Should().BeEmpty("die Teststrategien liefern keinen Begruendungstext - nichts erfinden");
                es.ReferencePrice.Should().Be(r.Workspace.Candles[es.BarIndex].C);

                ticks[t.EntryTickIndex!.Value].Price.Should().Be(t.EntryTickPrice!.Value);
                ticks[t.ExitTickIndex!.Value].Price.Should().Be(t.ExitTickPrice!.Value);
                bool lng = t.Side == "Long";
                t.EntryPrice.Should().Be(lng ? t.EntryTickPrice.Value + slip : t.EntryTickPrice.Value - slip, "Slippage steckt im Fillpreis");
                t.ExitPrice.Should().Be(lng ? t.ExitTickPrice.Value - slip : t.ExitTickPrice.Value + slip);
                t.EntryTimeUtc.Should().Be(TradeSignalLinker.Iso(ticks[t.EntryTickIndex.Value].Timestamp));
                t.ExitTimeUtc.Should().Be(TradeSignalLinker.Iso(ticks[t.ExitTickIndex.Value].Timestamp));

                // Ergebnis: Brutto aus Fills (Slippage enthalten) - Gebuehren = Netto; Slippage NICHT doppelt abgezogen.
                decimal gross = (t.ExitPrice - t.EntryPrice) * (lng ? 1 : -1) * t.Qty * pv;
                t.GrossPnL.Should().Be(gross);
                t.Fees.Should().Be(2 * 2.15m * t.Qty);
                t.NetPnL.Should().Be(gross - t.Fees!.Value);
            }
        }
    }

    [Fact]
    public async Task Recording_does_not_change_trades_or_results()
    {
        var ticks = Bars(Pattern);
        var req = Request(ticks, OrderflowProofCandidates.BuildGated("NQ"), slip: 1m);
        var recorded = await new OrderflowMultiStrategyRunner().RunAsync(req);
        foreach (var cand in OrderflowProofCandidates.BuildGated("NQ"))
        {
            var direct = await new BacktestStrategyRunner().RunAsync(new StrategyRunInputs
            {
                Candidate = cand, Config = cand.BaseConfig, Ticks = ticks, Symbol = "NQ",
                Instrument = req.Instrument, Fee = req.Fee, Broker = req.Broker, Risk = req.Risk, Account = req.Account,
                OrderFlowTicksPerBar = 500
            });
            var mine = recorded.RunResults.Single(x => x.StrategyName == cand.Name);
            mine.Trades.Should().BeEquivalentTo(direct.Trades, o => o.WithStrictOrdering());
            mine.Diagnostics!.SignalsGenerated.Should().Be(direct.Diagnostics!.SignalsGenerated);
        }
    }

    [Fact]
    public async Task Without_recorded_signals_nothing_is_estimated()
    {
        var ticks = Bars(Pattern);
        var req = Request(ticks, new[] { OrderflowProofCandidates.BuildGated("NQ")[0] }, slip: 1m);
        var run = await new BacktestStrategyRunner().RunAsync(new StrategyRunInputs
        {
            Candidate = req.Candidates[0], Config = req.Candidates[0].BaseConfig, Ticks = ticks, Symbol = "NQ",
            Instrument = req.Instrument, Fee = req.Fee, Broker = req.Broker, Risk = req.Risk, Account = req.Account,
            OrderFlowTicksPerBar = 500
        });
        var bars = OrderFlowBarAggregator.AggregateByTicks(ticks, 500).ToList();
        var links = TradeSignalLinker.Link(ticks, bars, 500, Array.Empty<RecordedSignal>(), run.Trades, 0.25m, 1m);
        links.Should().HaveCount(run.Trades.Count);
        links.Should().OnlyContain(l => l.EntrySignal == null && l.ExitSignal == null && l.EntryTickIndex == null && l.ExitTickIndex == null);
        links.Should().OnlyContain(l => l.Status.Contains("nicht aufgezeichnet"));
    }

    [Fact]
    public async Task Inconsistent_fill_is_not_linked()
    {
        var ticks = Bars(Pattern);
        var log = new List<RecordedSignal>();
        var cand = OrderflowProofCandidates.BuildGated("NQ")[0];
        var rec = cand with { CreateStrategy = c => new SignalRecordingStrategy(cand.CreateStrategy(c), log) };
        var req = Request(ticks, new[] { rec }, slip: 1m);
        var run = await new BacktestStrategyRunner().RunAsync(new StrategyRunInputs
        {
            Candidate = rec, Config = rec.BaseConfig, Ticks = ticks, Symbol = "NQ",
            Instrument = req.Instrument, Fee = req.Fee, Broker = req.Broker, Risk = req.Risk, Account = req.Account,
            OrderFlowTicksPerBar = 500
        });
        var bars = OrderFlowBarAggregator.AggregateByTicks(ticks, 500).ToList();
        // Falsche Slippage-Annahme (0 statt 1 Tick): Fillpreis passt nicht -> keine Zuordnung, keine Schaetzung.
        var wrong = TradeSignalLinker.Link(ticks, bars, 500, log, run.Trades, 0.25m, 0m);
        wrong.Should().OnlyContain(l => l.EntrySignal == null && l.EntryTickIndex == null && l.Status.Contains("Entry-Fill"));
        var right = TradeSignalLinker.Link(ticks, bars, 500, log, run.Trades, 0.25m, 1m);
        right.Should().OnlyContain(l => l.Status == TradeSignalLinker.Linked);
    }
}
