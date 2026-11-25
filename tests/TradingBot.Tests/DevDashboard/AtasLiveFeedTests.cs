using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using TradingBot.DevDashboard.Services.Live;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>ATAS-Live-Feed (nur lesend): Zustand, Herkunft, Wertzustände, Stabilität. Alle Nachrichten synthetisch.</summary>
public sealed class AtasLiveFeedTests
{
    private DateTimeOffset _now = new(2026, 11, 25, 14, 31, 0, TimeSpan.Zero);
    private AtasLiveFeed Feed() => new(() => _now, TimeSpan.FromSeconds(3), _ => "New York");

    private static string Hello(string session = "S1", string source = "atas", string instrument = "MNQZ6") =>
        $$"""{"type":"hello","schema":"atas-live-1","source":"{{source}}","instrument":"{{instrument}}","exchange":"CME","tickSize":0.25,"chartType":"TimeFrame","timeFrame":"1","atasVersion":"8.0.15.306","bridge":"OF Live Bridge 1","sessionId":"{{session}}"}""";
    private static string Trade(long seq, string t, decimal p, decimal v, string d) =>
        $$"""{"type":"trade","seq":{{seq}},"t":"{{t}}","r":"{{t}}","p":{{p.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"v":{{v}},"d":"{{d}}"}""";

    [Fact]
    public void A_real_trade_appears_with_market_time_price_volume_and_aggressor()
    {
        var f = Feed();
        f.OnConnected("127.0.0.1:1");
        f.Ingest(Hello());
        f.Ingest(Trade(1, "2026-11-25T14:30:59.123456Z", 21000.25m, 3, "Buy"));
        var s = f.Snapshot();
        s.Status.Should().Be("verbunden");
        s.Instrument.Should().Be("MNQZ6");
        s.Trades.Should().ContainSingle();
        var t = s.Trades[0];
        (t.TMarket, t.Price, t.Volume, t.Side).Should().Be(("2026-11-25T14:30:59.1234560Z", 21000.25m, 3m, "Buy"));
        s.LastPrice.Source.Should().Be(Prov.Live);
        s.Session.Should().Be("New York");
        s.Mode.Should().Be("live");
    }

    [Fact]
    public void Updates_of_a_cumulative_trade_replace_its_volume_and_are_never_double_counted()
    {
        var f = Feed(); f.OnConnected("x"); f.Ingest(Hello());
        f.Ingest("""{"type":"cum","seq":1,"id":7,"ev":"new","t":"2026-11-25T14:30:00Z","p0":100,"p1":100,"v":1,"n":1,"d":"Buy"}""");
        f.Ingest("""{"type":"cum","seq":2,"id":7,"ev":"update","t":"2026-11-25T14:30:00Z","p0":100,"p1":100.25,"v":3,"n":2,"d":"Buy"}""");
        f.Ingest("""{"type":"cum","seq":3,"id":7,"ev":"update","t":"2026-11-25T14:30:00Z","p0":100,"p1":100.5,"v":5,"n":3,"d":"Buy"}""");
        f.Ingest("""{"type":"cum","seq":4,"id":8,"ev":"new","t":"2026-11-25T14:30:01Z","p0":100.5,"p1":100.5,"v":2,"n":1,"d":"Sell"}""");
        var s = f.Snapshot();
        s.TapeVolume.Value.Should().Be(7m, "5 (letzter Stand von #7) + 2, nicht 1+3+5+2");
        s.Tape.Should().HaveCount(2);
        s.Tape.Single(x => x.Id == 7).Updates.Should().Be(2);
        s.Counters.CumUpdates.Should().Be(2);
    }

    [Fact]
    public void Disconnect_marks_everything_as_disconnected_drops_the_book_and_reconnect_needs_a_fresh_snapshot()
    {
        var f = Feed(); f.OnConnected("x"); f.Ingest(Hello());
        f.Ingest("""{"type":"domSnapshot","seq":1,"t":"2026-11-25T14:30:00Z","levels":[{"side":"Bid","p":100,"v":5},{"side":"Bid","p":99.75,"v":7},{"side":"Ask","p":100.25,"v":4}]}""");
        f.Ingest("""{"type":"bbo","seq":2,"t":"2026-11-25T14:30:00Z","side":"Bid","p":100,"v":5}""");
        f.Snapshot().Book.State.Should().Be("vollstaendig");
        f.Snapshot().Book.BidLevels.Should().Be(2);

        f.OnDisconnected("Test");
        var s = f.Snapshot();
        s.Status.Should().Be("getrennt");
        s.Book.State.Should().Be("verworfen_getrennt");
        s.Book.Bids.Should().BeEmpty("alte DOM-Level dürfen nicht als aktuelles Buch stehen bleiben");
        s.Bid.State.Should().Be(ValState.Disconnected);

        f.OnConnected("x"); f.Ingest(Hello());
        f.Ingest("""{"type":"dom","seq":1,"t":"2026-11-25T14:31:00Z","side":"Ask","p":100.5,"v":3}""");
        f.Snapshot().Book.State.Should().Be("unvollstaendig", "nur Updates seit Wiederverbindung, noch kein Snapshot");
        f.Ingest("""{"type":"domSnapshot","seq":2,"t":"2026-11-25T14:31:01Z","levels":[{"side":"Ask","p":100.5,"v":3}]}""");
        f.Snapshot().Book.State.Should().Be("vollstaendig");
        f.Snapshot().CoverageBreaks.Should().ContainSingle();
    }

    [Fact]
    public void Silent_feed_becomes_stale_and_the_book_is_not_shown_as_current()
    {
        var f = Feed(); f.OnConnected("x"); f.Ingest(Hello());
        f.Ingest("""{"type":"domSnapshot","seq":1,"t":"2026-11-25T14:30:00Z","levels":[{"side":"Bid","p":100,"v":5}]}""");
        _now = _now.AddSeconds(5);
        var s = f.Snapshot();
        s.Status.Should().Be("veraltet");
        s.Book.State.Should().Be("veraltet");
    }

    [Fact]
    public void Source_or_instrument_switch_leaves_no_old_candles_tape_or_dom()
    {
        var f = Feed(); f.OnConnected("x"); f.Ingest(Hello("REC", "test"));
        f.Ingest(Trade(1, "2026-11-25T14:30:00Z", 100, 1, "Buy"));
        f.Ingest("""{"type":"cum","seq":2,"id":1,"ev":"new","t":"2026-11-25T14:30:00Z","p0":100,"p1":100,"v":1,"n":1,"d":"Buy"}""");
        f.Ingest("""{"type":"dom","seq":3,"t":"2026-11-25T14:30:00Z","side":"Bid","p":99.75,"v":2}""");
        var g = f.Snapshot().Generation;
        f.Ingest(Hello("LIVE", "atas"));
        var s = f.Snapshot();
        s.Generation.Should().Be(g + 1);
        s.Trades.Should().BeEmpty(); s.Tape.Should().BeEmpty(); s.Candles.Should().BeEmpty();
        s.Book.Bids.Should().BeEmpty();
        s.Counters.Trades.Should().Be(0);
        s.SourceKind.Should().Be(Prov.Live);
    }

    [Fact]
    public void Lost_events_are_counted_never_silent()
    {
        var f = Feed(); f.OnConnected("x"); f.Ingest(Hello());
        f.Ingest(Trade(1, "2026-11-25T14:30:00Z", 100, 1, "Buy"));
        f.Ingest(Trade(5, "2026-11-25T14:30:01Z", 100, 1, "Buy"));   // 2..4 fehlen
        f.Ingest("{kaputt");
        f.Ingest("""{"type":"hb","seq":6,"r":"2026-11-25T14:30:02Z","drops":{"trade":2,"dom":3}}""");
        f.NoteBackendDrop(4);
        var c = f.Snapshot().Counters;
        (c.SeqGaps, c.SeqMissing, c.ParseErrors, c.BridgeDropped, c.BackendDropped).Should().Be((1L, 3L, 1L, 5L, 4L));
        c.LinesReceived.Should().Be(5);
        c.Processed.Should().Be(4);
    }

    [Fact]
    public void Real_zero_is_a_value_while_missing_data_is_no_value_yet()
    {
        var f = Feed(); f.OnConnected("x"); f.Ingest(Hello());
        f.Snapshot().Cvd.State.Should().Be(ValState.NoValueYet);
        f.Snapshot().Cvd.Value.Should().BeNull();
        f.Ingest(Trade(1, "2026-11-25T14:30:00Z", 100, 2, "Buy"));
        f.Ingest(Trade(2, "2026-11-25T14:30:01Z", 100, 2, "Sell"));
        var cvd = f.Snapshot().Cvd;
        (cvd.Value, cvd.State, cvd.Source).Should().Be((0m, ValState.Value, Prov.Derived), "echte Null");
        new AtasLiveFeed().Snapshot().Bid.Source.Should().Be(Prov.Unavailable, "ohne Bridge ist die Quelle ungeklärt");
    }

    [Fact]
    public void Test_data_is_marked_as_test_everywhere()
    {
        var f = Feed(); f.OnConnected("x"); f.Ingest(Hello(source: "test"));
        f.Ingest(Trade(1, "2026-11-25T14:30:00Z", 100, 1, "Buy"));
        var s = f.Snapshot();
        s.SourceKind.Should().Be(Prov.Test);
        s.LastPrice.Source.Should().Be(Prov.Test);
        s.Candles.Should().OnlyContain(c => c.Source == Prov.Test);
    }

    [Fact]
    public void Closed_atas_bar_is_compared_with_the_trades_of_the_same_interval()
    {
        var f = Feed(); f.OnConnected("x"); f.Ingest(Hello());
        f.Ingest(Trade(1, "2026-11-25T14:29:59.9Z", 99, 1, "Buy"));           // vor der Kerze: Abdeckung belegt
        f.Ingest(Trade(2, "2026-11-25T14:30:00.1Z", 100, 2, "Buy"));
        f.Ingest(Trade(3, "2026-11-25T14:30:20Z", 101, 1, "Sell"));
        f.Ingest(Trade(4, "2026-11-25T14:30:59Z", 100.5m, 3, "Buy"));
        f.Ingest(Trade(5, "2026-11-25T14:31:00.2Z", 100.75m, 1, "Sell"));
        f.Ingest("""{"type":"bar","seq":6,"bar":10,"closed":true,"t":"2026-11-25T14:30:00Z","o":100,"h":101,"l":100,"c":100.5,"v":6,"bid":1,"ask":5,"delta":4,"ticks":3,"vwap":100.4166666,"fp":[{"p":100.5,"b":0,"a":3},{"p":101,"b":1,"a":0},{"p":100,"b":0,"a":2}]}""");
        f.Ingest("""{"type":"bar","seq":7,"bar":11,"closed":false,"t":"2026-11-25T14:31:00Z","o":100.75,"h":100.75,"l":100.75,"c":100.75,"v":1,"bid":1,"ask":0,"delta":-1,"ticks":1,"fp":[]}""");
        var cmp = f.Compare().Single(c => c.Bar == 10);
        cmp.Status.Should().Be("verglichen");
        cmp.Rows.Should().OnlyContain(r => r.Difference == 0m, string.Join("; ", cmp.Rows.Select(r => $"{r.Field} {r.Atas}/{r.Dashboard}")));
        cmp.Rows.Select(r => r.Field).Should().Contain(new[] { "Open", "Volumen", "Delta", "VWAP (Anker Kerzenbeginn)", "Footprint 100.5 Ask" });
        f.Snapshot().AtasBars.Should().OnlyContain(b => b.Source == Prov.Chart);
    }

    [Fact]
    public void Bar_overlapping_a_disconnect_is_not_compared()
    {
        var f = Feed(); f.OnConnected("x"); f.Ingest(Hello());
        f.Ingest(Trade(1, "2026-11-25T14:29:59Z", 100, 1, "Buy"));
        _now = new DateTimeOffset(2026, 11, 25, 14, 30, 30, TimeSpan.Zero);
        f.OnDisconnected("Test"); f.OnConnected("x"); f.Ingest(Hello());
        f.Ingest("""{"type":"bar","seq":2,"bar":1,"closed":true,"t":"2026-11-25T14:30:00Z","o":1,"h":1,"l":1,"c":1,"v":1,"bid":0,"ask":1,"delta":1,"ticks":1,"fp":[]}""");
        f.Ingest("""{"type":"bar","seq":3,"bar":2,"closed":false,"t":"2026-11-25T14:31:00Z","o":1,"h":1,"l":1,"c":1,"v":1,"bid":0,"ask":1,"delta":1,"ticks":1,"fp":[]}""");
        f.Compare().Single(c => c.Bar == 1).Status.Should().StartWith("nicht_vergleichbar: Verbindungsunterbrechung");
    }

    [Fact]
    public async Task Listener_accepts_only_loopback_ndjson_and_reports_disconnect()
    {
        int port; { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); }
        var feed = new AtasLiveFeed();
        using var cts = new CancellationTokenSource();
        var listener = new AtasLiveListener(feed, port);
        await listener.StartAsync(cts.Token);
        using (var c = new TcpClient())
        {
            await c.ConnectAsync(IPAddress.Loopback, port);
            var w = new StreamWriter(c.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
            await w.WriteLineAsync(Hello());
            await w.WriteLineAsync(Trade(1, "2026-11-25T14:30:00Z", 100, 1, "Sell"));
            for (int i = 0; i < 50 && feed.Snapshot().Counters.Trades < 1; i++) await Task.Delay(20);
            feed.Snapshot().Status.Should().Be("verbunden");
        }
        for (int i = 0; i < 50 && feed.Snapshot().Status != "getrennt"; i++) await Task.Delay(20);
        feed.Snapshot().Status.Should().Be("getrennt");
        feed.Snapshot().Trades.Should().ContainSingle(t => t.Side == "Sell");
        cts.Cancel();
        await listener.StopAsync(CancellationToken.None);
    }
}

/// <summary>Die ATAS-Bridge ist strukturell nur lesend: kein Order-/Konto-Zugriff, Verbindung ausschließlich lokal.</summary>
public sealed class AtasLiveBridgeSourceGuardTests
{
    private static string Source()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "research", "atas-live-bridge", "OrderFlowLiveBridge.cs"))) dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("Quelltext der Bridge muss im Repo liegen");
        // Nur Code prüfen – Kommentarzeilen (die das Verbot selbst beschreiben) auslassen.
        return string.Join(Environment.NewLine, File.ReadAllLines(Path.Combine(dir!, "research", "atas-live-bridge", "OrderFlowLiveBridge.cs"))
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void Bridge_uses_no_order_or_account_api_and_connects_only_to_loopback()
    {
        var s = Source();
        foreach (var forbidden in new[] { "TradingManager", "OpenOrder", "ModifyOrder", "CancelOrder", "Portfolio", "ChartStrategy", "SendOrder" })
            s.Should().NotContain(forbidden);
        s.Should().Contain("IPAddress.Loopback");
        s.Should().NotContain("Dns.").And.NotContain("http");
        s.Should().Contain(": Indicator");
    }
}
