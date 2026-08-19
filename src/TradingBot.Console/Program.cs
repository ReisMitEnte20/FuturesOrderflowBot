using System;
using System.IO;
using TradingBot.Application.Strategies;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Backtesting;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData.Import;
using TradingBot.Research;
using TradingBot.Research.Runner;

namespace TradingBot.Console;

/// <summary>
/// Research-Demo: fährt einen Research-Lauf mit echten Sierra-OrderFlow-Ticks
/// gegen das OrderFlowSetupTemplateStrategy. Zeigt den kompletten Flow:
/// Sierra CSV ? MarketTick (StreamTicks) ? OrderFlowBarAggregatorStrategy ?
/// OrderFlowSetupTemplateStrategy ? BacktestEngine ? ResearchEngine ? Results.
/// Keine Broker-API, keine Live-Execution, kein Fake-Orderflow.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        var sierraPath = args.Length > 0 ? args[0] : GetDefaultSierraPath();
        if (string.IsNullOrWhiteSpace(sierraPath) || !File.Exists(sierraPath))
        {
            System.Console.Error.WriteLine($"Sierra-Datei nicht gefunden: {sierraPath}");
            System.Console.Error.WriteLine("Usage: dotnet run --project src/TradingBot.Console \"<path/to/sierra.txt>\"");
            return;
        }

        System.Console.WriteLine($"=== Research mit Sierra-Daten: {Path.GetFileName(sierraPath)} ===");

        // --- 1. Profile programmatisch bauen (statt JSON-Load für Demo) ---
        var instrument = new InstrumentProfile
        {
            Symbol = "NQ",
            BrokerSymbol = "NQ",
            Exchange = "CME",
            Currency = "USD",
            TickSize = 0.25m,
            TickValue = 5.0m,
            PointValue = 20.0m,
            ContractMultiplier = 20.0m,
            TradingTimezone = "America/New_York",
            SessionStart = new TimeOnly(9, 30, 0),
            SessionEnd = new TimeOnly(16, 0, 0),
            MaxContracts = 5,
            DefaultStopLossTicks = 40,
            DefaultTakeProfitTicks = 60,
            DefaultTrailingStopTicks = 20
        };

        var fee = new FeeProfile
        {
            BrokerName = "AMP Futures",
            ExecutionProvider = "Rithmic",
            Instrument = "NQ",
            CommissionPerSide = 0.25m,
            ExchangeFeePerSide = 0.10m,
            ClearingFeePerSide = 0.02m,
            RoutingFeePerSide = 0.0m,
            NfaFeePerSide = 0.02m,
            OtherFeePerSide = 0.0m,
            EstimatedSlippageTicks = 1.0m,
            MaxAllowedSlippageTicks = 3.0m,
            MonthlyPlatformFee = 0.0m,
            MonthlyDataFeedFee = 0.0m
        };

        var broker = new BrokerProfile
        {
            BrokerName = "AMP Futures",
            ExecutionProvider = "Rithmic",
            AccountType = AccountType.Funded,
            AccountCurrency = "USD",
            SupportsMarketOrders = true,
            SupportsLimitOrders = true,
            SupportsStopOrders = true,
            SupportsBracketOrders = true,
            SupportsOcoOrders = true,
            SupportsServerSideStops = true,
            SupportsClientSideStops = true,
            SupportsCancelReplace = true,
            MaxOrdersPerSecond = 5,
            ApiRateLimit = 100,
            ReconnectBehavior = "ResumeState",
            PartialFillBehavior = "TrackPartial"
        };

        var risk = new RiskConfig
        {
            MaxDailyLoss = 1000.0m,
            MaxLossPerTrade = 300.0m,
            MaxTradesPerDay = 10,
            MaxContracts = 5,
            MaxOpenPositions = 1,
            MaxOrdersPerMinute = 10,
            MaxConsecutiveLosses = 3,
            EnforceSession = true,
            ProfitLockEnabled = false,
            ProfitLockThreshold = null,
            TrailingDrawdownEnabled = false,
            TrailingDrawdownAmount = null
        };

        var account = new TradingAccount
        {
            AccountId = "DEMO-12345",
            BrokerName = "AMP Futures",
            AccountType = AccountType.Funded,
            Currency = "USD",
            StartingBalance = 50_000m
        };

        // --- 2. Strategie-Kandidat & Config ---
        var candidate = new StrategyCandidate
        {
            Name = "OrderFlowSetupTemplateStrategy",
            CreateStrategy = cfg => new OrderFlowSetupTemplateStrategy(),
            BaseConfig = new StrategyConfig
            {
                Name = "OrderFlowSetupTemplateStrategy",
                Symbol = "NQ",
                Enabled = true,
                RequiredDataType = StrategyDataType.Tick, // wir bekommen Ticks, Aggregator wandelt intern zu OrderFlowBars
                SuggestedContracts = 1,
                StopLossTicks = 40,
                TakeProfitTicks = 60,
                Parameters = new Dictionary<string, string>
                {
                    ["MinDelta"] = "200",
                    ["MinVolume"] = "50",
                    ["RequiredConfirmations"] = "3",
                    ["UseVwapFilter"] = "false",
                    ["UseSessionHighLowFilter"] = "false",
                    ["CooldownBars"] = "2"
                }
            }
        };

        var config = candidate.BaseConfig;

        // --- 3. ResearchRequest bauen (nutzt CreateFromSierraFile) ---
        var request = BacktestStrategyRunner.CreateResearchRequestFromSierraFile(
            sierraPath: sierraPath,
            symbol: "NQ",
            candidate: candidate,
            config: config,
            instrument: instrument,
            fee: fee,
            broker: broker,
            risk: risk,
            account: account,
            slippageOverride: null,
            maxRows: 50000, // erste 50k Ticks für schnellen Demo-Lauf
            fromUtc: null,
            toUtc: null,
            researchConfig: new ResearchConfiguration
            {
                RunMonteCarlo = true,
                MonteCarloSimulations = 200,
                MonteCarloSeed = 42,
                RunRobustness = true
            });

        System.Console.WriteLine($"Ticks geladen: {request.Ticks.Count:N0}");
        System.Console.WriteLine($"DataQualityOk: {request.DataQualityOk}, CapabilitiesSufficient: {request.CapabilitiesSufficient}");

        // --- 4. ResearchEngine fahren ---
        var runner = new BacktestStrategyRunner();
        var engine = new ResearchEngine(runner);
        var result = await engine.RunAsync(request);

        // --- 5. Ergebnisse ausgeben ---
        System.Console.WriteLine($"\n=== Research Result: {result.Status} ===");
        if (result.Message != null) System.Console.WriteLine($"Message: {result.Message}");

        foreach (var run in result.Ranking)
        {
            var m = run.Metrics;
            System.Console.WriteLine($"\n--- {run.StrategyName} (Rank {run.Rank}, Score {run.CompositeScore:F3}) ---");
            System.Console.WriteLine($"  NetPnL:       {m.NetProfit,10:N2} USD");
            System.Console.WriteLine($"  Trades:       {m.TradeCount,10}");
            System.Console.WriteLine($"  WinRate:      {m.WinRate,9:P1}");
            System.Console.WriteLine($"  ProfitFactor: {m.ProfitFactor,9:N2}");
            System.Console.WriteLine($"  MaxDD:        {m.MaxDrawdown,10:N2} USD");
            System.Console.WriteLine($"  Expectancy:   {m.Expectancy,9:N2}");
            if (m.MonteCarloMedianNetProfit.HasValue)
            {
                System.Console.WriteLine($"  MC MedianNP:  {m.MonteCarloMedianNetProfit!.Value,10:N2} USD");
                System.Console.WriteLine($"  MC WorstDD5%: {m.MonteCarloWorstDrawdown5!.Value,10:N2} USD");
                System.Console.WriteLine($"  MC ProbLoss:  {m.MonteCarloProbabilityOfLoss!.Value,9:P1}");
            }
            if (run.Penalties.Count > 0)
            {
                System.Console.WriteLine($"  Penalties:    {string.Join(", ", run.Penalties)}");
            }
        }

        System.Console.WriteLine("\nDone.");
    }

    private static string GetDefaultSierraPath()
    {
        // Sucht nach einer Beispieldatei im Repo
        var repoRoot = AppContext.BaseDirectory;
        while (!Directory.GetFiles(repoRoot, "*.sln").Any() && Directory.GetParent(repoRoot) != null)
            repoRoot = Directory.GetParent(repoRoot)!.FullName;

        var sample = Path.Combine(repoRoot, "samples", "sierra", "raw", "MES-1-tick-20251228.txt");
        if (File.Exists(sample)) return sample;

        return Path.Combine(repoRoot, "samples", "sierra", "raw", "example-sierra-data.txt");
    }

    
    private static InstrumentProfile LoadInstrument(string path)
        => System.Text.Json.JsonSerializer.Deserialize<InstrumentProfile>(File.ReadAllText(path))!;

    private static FeeProfile LoadFee(string path)
        => System.Text.Json.JsonSerializer.Deserialize<FeeProfile>(File.ReadAllText(path))!;

    private static BrokerProfile LoadBroker(string path)
        => System.Text.Json.JsonSerializer.Deserialize<BrokerProfile>(File.ReadAllText(path))!;

    private static RiskConfig LoadRisk(string path)
        => System.Text.Json.JsonSerializer.Deserialize<RiskConfig>(File.ReadAllText(path))!;
}
