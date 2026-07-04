namespace TradingBot.DevDashboard.Services;

public sealed record ModuleInfo(string Name, string Description);
public sealed record SafetyItem(string Label, bool Ok);
public sealed record PipelineStage(string Name, string Sub);

/// <summary>
/// Statischer Projektstatus (read-only). Enthält KEINE Trading-Logik und keine Order-/Broker-Aufrufe.
/// </summary>
public sealed class ProjectStatusService
{
    public string CurrentPhase => "Phase 10A abgeschlossen";
    public string TestStatus => "241 / 241 bestanden";
    public string KnownGoodCommit => "08ac0b2";

    public IReadOnlyList<ModuleInfo> DoneModules { get; } = new List<ModuleInfo>
    {
        new("Solution / Skeleton", "9 Projekte, Abhängigkeiten nach innen Richtung Domain"),
        new("Domain Models + Interfaces", "Records, Enums, Core-Interfaces"),
        new("Config / Profile System", "Broker/Instrument/Fee/Risk aus JSON, validiert"),
        new("Fee + PnL Engine", "Gross/Net getrennt, decimal-genau"),
        new("RiskManager", "Fail-closed Gatekeeper, RiskDecision"),
        new("OrderManager", "Dedup, Lifecycle, OrderFactory (SL/TP/Bracket/BE/Trailing)"),
        new("PositionManager", "Netting, Average Entry, Realized/Unrealized PnL"),
        new("MarketData Modul", "CSV-Reader, Replay-Feed, Heartbeat, Aggregation"),
        new("Backtest Engine", "Deterministisch, Fill-Modell, Slippage/Fees, Kennzahlen"),
        new("Exit-aware Risk Handling", "Reduce/Close/Flatten nicht durch Entry-Limits blockiert"),
        new("Paper Trading Engine", "Session (Start/Stop/Pause), simulierte Fills, Journal"),
        new("Paper Trading Monitor", "Live-Monitor im DevDashboard (PAPER SIMULATION ONLY)"),
        new("Project Documentation", "PROJECT_STATUS.md, ARCHITECTURE.md, PAPER_TRADING.md, README"),
    };

    public IReadOnlyList<string> OpenModules { get; } = new List<string>
    {
        "Dashboard (final)",
        "Live Execution Adapter",
        "Live Safety Checklist",
        "Echte Broker-Anbindung",
    };

    public IReadOnlyList<SafetyItem> Safety { get; } = new List<SafetyItem>
    {
        new("Keine Live-Execution vorhanden", true),
        new("Keine Broker-API vorhanden", true),
        new("Paper / Replay only", true),
        new("RiskManager vorhanden", true),
        new("KillSwitch vorhanden (Interface)", true),
        new("OrderManager nutzt RiskDecision", true),
        new("Keine hardcoded Fees", true),
        new("Keine hardcoded TickValues", true),
        new("MarketData erzeugt keine Fake-Orderflow-Daten", true),
    };

    public IReadOnlyList<PipelineStage> Pipeline { get; } = new List<PipelineStage>
    {
        new("MarketData", "CSV / Replay"),
        new("Aggregators", "Candle / OrderFlow"),
        new("Strategy", "nur Signale"),
        new("RiskManager", "Gate"),
        new("OrderManager", "Submit"),
        new("BrokerAdapter", "Mock"),
        new("PositionManager", "Netting"),
        new("PnL / Journal", "Reporting"),
    };
}
