using FluentAssertions;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Config;
using Xunit;

namespace TradingBot.Tests.Config;

public class ProfileValidatorTests
{
    private static InstrumentProfile ValidInstrument() => new()
    {
        Symbol = "NQ", BrokerSymbol = "NQ", Exchange = "CME",
        TickSize = 0.25m, TickValue = 5.0m, PointValue = 20.0m, ContractMultiplier = 20.0m,
        MaxContracts = 5, SessionStart = new TimeOnly(9, 30), SessionEnd = new TimeOnly(16, 0)
    };

    [Fact]
    public void Valid_instrument_passes()
    {
        var act = () => ProfileValidator.Validate(ValidInstrument());
        act.Should().NotThrow();
    }

    [Fact]
    public void Instrument_with_zero_tickSize_is_rejected()
    {
        var bad = ValidInstrument() with { TickSize = 0m };
        var act = () => ProfileValidator.Validate(bad);
        act.Should().Throw<ProfileValidationException>().WithMessage("*TickSize*");
    }

    [Fact]
    public void Instrument_with_zero_maxContracts_is_rejected()
    {
        var bad = ValidInstrument() with { MaxContracts = 0 };
        var act = () => ProfileValidator.Validate(bad);
        act.Should().Throw<ProfileValidationException>().WithMessage("*MaxContracts*");
    }

    [Fact]
    public void Instrument_with_session_start_after_end_is_rejected()
    {
        var bad = ValidInstrument() with { SessionStart = new TimeOnly(16, 0), SessionEnd = new TimeOnly(9, 30) };
        var act = () => ProfileValidator.Validate(bad);
        act.Should().Throw<ProfileValidationException>().WithMessage("*SessionStart*");
    }

    [Fact]
    public void Instrument_with_empty_symbol_is_rejected()
    {
        var bad = ValidInstrument() with { Symbol = "" };
        var act = () => ProfileValidator.Validate(bad);
        act.Should().Throw<ProfileValidationException>().WithMessage("*Symbol*");
    }

    [Fact]
    public void Broker_with_empty_name_is_rejected()
    {
        var bad = new BrokerProfile { BrokerName = "", ExecutionProvider = "Rithmic" };
        var act = () => ProfileValidator.Validate(bad);
        act.Should().Throw<ProfileValidationException>().WithMessage("*BrokerName*");
    }

    [Fact]
    public void Fee_with_negative_value_is_rejected()
    {
        var bad = new FeeProfile
        {
            BrokerName = "AMP Futures", ExecutionProvider = "Rithmic", Instrument = "MNQ",
            CommissionPerSide = -1m
        };
        var act = () => ProfileValidator.Validate(bad);
        act.Should().Throw<ProfileValidationException>().WithMessage("*Commission*");
    }

    [Fact]
    public void Risk_with_zero_maxDailyLoss_is_rejected()
    {
        var bad = new RiskConfig { MaxDailyLoss = 0m, MaxContracts = 5, MaxOpenPositions = 1 };
        var act = () => ProfileValidator.Validate(bad);
        act.Should().Throw<ProfileValidationException>().WithMessage("*MaxDailyLoss*");
    }

    [Fact]
    public void Valid_risk_passes()
    {
        var ok = new RiskConfig
        {
            MaxDailyLoss = 1000m, MaxLossPerTrade = 300m, MaxTradesPerDay = 10,
            MaxContracts = 5, MaxOpenPositions = 1, MaxOrdersPerMinute = 10, MaxConsecutiveLosses = 3
        };
        var act = () => ProfileValidator.Validate(ok);
        act.Should().NotThrow();
    }
}
