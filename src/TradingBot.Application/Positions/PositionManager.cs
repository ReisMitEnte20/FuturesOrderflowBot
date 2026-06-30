using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Positions;

/// <summary>
/// Verwaltet offene Positionen und PnL. Verarbeitet Fills mit korrektem Netting
/// (Aufbau, Teilverkauf, Glattstellung, Flip). Thread-safe über einen Lock.
///
/// PnL-Modell: GrossPnL wird aus den ECHTEN Fill-Preisen via PnLCalculator (Tick-Mathematik)
/// berechnet – Slippage steckt damit bereits im Preis. Als Gebühren werden ausschließlich
/// die Per-Side-Kommissionen pro Fill addiert (FeeCalculator). NetPnL = RealizedGross −
/// gezahlte Gebühren (Cash-Basis; bei Glattstellung exakt der Round Turn).
///
/// Hat KEINE Execution-Referenz und sendet niemals Orders.
/// </summary>
public sealed class PositionManager : IPositionManager
{
    private readonly IFeeCalculator _feeCalculator;
    private readonly IPnLCalculator _pnlCalculator;
    private readonly object _sync = new();
    private readonly Dictionary<string, Book> _books = new(StringComparer.OrdinalIgnoreCase);

    public PositionManager(IFeeCalculator feeCalculator, IPnLCalculator pnlCalculator)
    {
        _feeCalculator = feeCalculator ?? throw new ArgumentNullException(nameof(feeCalculator));
        _pnlCalculator = pnlCalculator ?? throw new ArgumentNullException(nameof(pnlCalculator));
    }

    public Position? GetPosition(string symbol)
    {
        lock (_sync)
            return _books.TryGetValue(symbol, out var b) ? b.ToPosition() : null;
    }

    public IReadOnlyCollection<Position> OpenPositions
    {
        get
        {
            lock (_sync)
                return _books.Values.Where(b => b.SignedQty != 0).Select(b => b.ToPosition()).ToList();
        }
    }

    public Position ApplyFill(FillEvent fill, InstrumentProfile instrument, FeeProfile feeProfile)
    {
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(instrument);
        ArgumentNullException.ThrowIfNull(feeProfile);
        if (fill.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(fill), "Fill-Menge muss > 0 sein.");

        lock (_sync)
        {
            if (!_books.TryGetValue(fill.Symbol, out var book))
            {
                book = new Book(fill.Symbol);
                _books[fill.Symbol] = book;
            }

            // 1) Gebühren dieses Fills (eine Seite) akkumulieren.
            book.Fees += _feeCalculator.CalculatePerSide(feeProfile, fill.Quantity);

            // 2) Netting.
            int oldSigned = book.SignedQty;
            int delta = fill.Side == OrderSide.Buy ? fill.Quantity : -fill.Quantity;

            if (oldSigned == 0 || Math.Sign(oldSigned) == Math.Sign(delta))
            {
                // Aufbau (öffnen oder in gleiche Richtung erhöhen) -> gewichteter Average Entry.
                decimal oldAbs = Math.Abs(oldSigned);
                decimal addAbs = Math.Abs(delta);
                book.AvgEntry = oldSigned == 0
                    ? fill.FillPrice
                    : (oldAbs * book.AvgEntry + addAbs * fill.FillPrice) / (oldAbs + addAbs);
                book.SignedQty = oldSigned + delta;
            }
            else
            {
                // Gegenrichtung -> reduzieren / glattstellen / flippen.
                int closingQty = Math.Min(Math.Abs(delta), Math.Abs(oldSigned));
                PositionSide closingSide = oldSigned > 0 ? PositionSide.Long : PositionSide.Short;

                book.RealizedGross += _pnlCalculator.CalculateGrossPnL(
                    instrument, closingSide, book.AvgEntry, fill.FillPrice, closingQty);

                int newSigned = oldSigned + delta;
                if (Math.Abs(delta) > Math.Abs(oldSigned))
                {
                    // Flip: Restmenge eröffnet neue Position zum Fill-Preis.
                    book.SignedQty = newSigned;
                    book.AvgEntry = fill.FillPrice;
                }
                else
                {
                    // Teil- oder Vollschließung ohne Flip; Average Entry bleibt für den Rest.
                    book.SignedQty = newSigned;
                    if (newSigned == 0) book.AvgEntry = 0m;
                }
            }

            // 3) Net = realisierter Gross minus aller gezahlten Gebühren (inkl. Slippage = 0 hier).
            book.RealizedNet = book.RealizedGross - book.Fees.TotalFees;

            // 4) Unrealized auf Basis des letzten Fill-Preises aktualisieren.
            book.RecomputeUnrealized(_pnlCalculator, instrument, fill.FillPrice);

            return book.ToPosition();
        }
    }

    public Position MarkToMarket(string symbol, decimal lastPrice, InstrumentProfile instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        lock (_sync)
        {
            if (!_books.TryGetValue(symbol, out var book))
                book = new Book(symbol);
            book.RecomputeUnrealized(_pnlCalculator, instrument, lastPrice);
            return book.ToPosition();
        }
    }

    public bool Reconcile(string symbol, Position? brokerPosition)
    {
        lock (_sync)
        {
            var local = _books.TryGetValue(symbol, out var b) ? b.ToPosition() : null;

            var localSide = local?.Side ?? PositionSide.Flat;
            var localQty = local?.Quantity ?? 0;
            var brokerSide = brokerPosition?.Side ?? PositionSide.Flat;
            var brokerQty = brokerPosition?.Quantity ?? 0;

            return localSide == brokerSide && localQty == brokerQty;
        }
    }

    /// <summary>Interner veränderlicher Buchungssatz pro Symbol.</summary>
    private sealed class Book
    {
        public Book(string symbol) => Symbol = symbol;

        public string Symbol { get; }
        public int SignedQty { get; set; }          // >0 Long, <0 Short, 0 Flat
        public decimal AvgEntry { get; set; }
        public decimal RealizedGross { get; set; }
        public decimal RealizedNet { get; set; }
        public decimal UnrealizedGross { get; set; }
        public FeeBreakdown Fees { get; set; } = FeeBreakdown.Zero;

        public void RecomputeUnrealized(IPnLCalculator pnl, InstrumentProfile instrument, decimal lastPrice)
        {
            if (SignedQty == 0)
            {
                UnrealizedGross = 0m;
                return;
            }
            var side = SignedQty > 0 ? PositionSide.Long : PositionSide.Short;
            UnrealizedGross = pnl.CalculateGrossPnL(instrument, side, AvgEntry, lastPrice, Math.Abs(SignedQty));
        }

        public Position ToPosition() => new()
        {
            AccountId = string.Empty,
            Symbol = Symbol,
            Side = SignedQty > 0 ? PositionSide.Long : SignedQty < 0 ? PositionSide.Short : PositionSide.Flat,
            Quantity = Math.Abs(SignedQty),
            AverageEntryPrice = AvgEntry,
            UnrealizedGrossPnL = UnrealizedGross,
            RealizedGrossPnL = RealizedGross,
            RealizedNetPnL = RealizedNet,
            Fees = Fees
        };
    }
}
