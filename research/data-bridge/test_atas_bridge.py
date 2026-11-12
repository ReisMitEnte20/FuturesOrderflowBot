"""Offline-Tests der Datenbrücke (synthetische Fixtures). Keine ATAS-/Netz-Abhängigkeit.

Abnahme (Auftrag Abschnitt 6):
- Anzahl übernommener Trades, Gesamtvolumen, Buy/Sell/Unknown-Volumen
- Preispräzision + beobachtetes Raster getrennt von tickSize
- Zeitgrenzen + explizite [start,end)-Fensterfilterung (Ende exklusiv)
- Reihenfolge bei identischen Zeitstempeln bleibt erhalten
- Rest-Ticks (Vor-/Nachlauf) gezählt; Rohdatei-Hash unverändert
- deterministischer logischer Output bei identischem Input
- widersprüchliche CSV/NDJSON/Summary-Zahlen werden sichtbar
"""
import hashlib
import json
from pathlib import Path

import pyarrow.parquet as pq
import pytest

import atas_bridge as b

START = "2026-10-26T08:00:00Z"
END = "2026-10-26T08:01:00Z"


def _line(seq, tmarket, price, vol, direction="Buy", dtype="Trade", trecv="2026-10-29T23:20:03.0Z"):
    return json.dumps({
        "seq": seq, "tMarket": tmarket, "tRecv": trecv, "dataType": dtype,
        "price": price, "volume": vol, "direction": direction,
        "isBid": False, "isAsk": False, "exchOrderId": None, "aggrExchOrderId": None, "oi": 0,
    })


@pytest.fixture
def recording(tmp_path):
    d = tmp_path / "MNQ_20261029_999999Z"
    d.mkdir()
    lines = [
        _line(1, "2026-10-26T07:59:59.500Z", 100.00, 3),          # VORLAUF (vor start) -> ausgeschlossen
        _line(2, "2026-10-26T08:00:00.000Z", 100.25, 1, "Buy"),   # im Fenster (start inklusiv)
        _line(3, "2026-10-26T08:00:00.000Z", 100.25, 2, "Sell"),  # gleicher Zeitstempel -> Reihenfolge erhalten
        _line(4, "2026-10-26T08:00:30.000Z", 100.75, 4, "Buy"),
        '{"seq":5,"tMarket":"2026-10-26T08:00:31Z","dataType":"Quote","price":100.5,"volume":0,"direction":null}',  # Nicht-Trade
        "{bad json line",                                           # bad json
        _line(6, "2026-10-26T08:00:45.000Z", 100.50, 2, "Xyz"),   # unbekannter Aggressor -> Unknown
        _line(7, "2026-10-26T08:01:00.000Z", 101.00, 9, "Buy"),   # EXAKT am Ende -> ausgeschlossen (halb-offen)
        _line(8, "2026-10-26T08:01:05.000Z", 101.25, 5, "Sell"),  # NACHLAUF (nach end) -> ausgeschlossen
    ]
    (d / "ticks.ndjson").write_text("\n".join(lines) + "\n", encoding="utf-8")
    # ticks.csv mit ABSICHTLICHER Diskrepanz (nur 3 Datenzeilen statt 5 Trades gesamt) -> muss sichtbar werden
    (d / "ticks.csv").write_text("Time,Instrument,Price,Volume,AggressorSide\n"
                                 "a,MNQ,100.25,1,Buy\nb,MNQ,100.25,2,Sell\nc,MNQ,100.75,4,Buy\n", encoding="utf-8")
    (d / "meta.json").write_text(json.dumps({
        "recorder": "test", "runId": "RID-TEST-1", "instrument": "MNQ", "tickSize": 0.25,
        "startWallUtc": "2026-10-29T23:20:00Z"}), encoding="utf-8")
    # summary zaehlt NDJSON-ZEILEN: seq1-4 (4) + Quote seq5 (1) + seq6-8 (3) = 8 gueltige Zeilen (bad json zaehlt nicht).
    (d / "summary.json").write_text(json.dumps({
        "schemaVersion": "ofrec-summary-2", "clean": True, "totalDrops": 0,
        "perStream": {"ticks.ndjson": {"events": 8, "drops": 0}}}), encoding="utf-8")
    return d


def test_window_filtering_and_counts(recording, tmp_path):
    m, table = b.convert(recording, START, END, tmp_path / "out")
    # 7 Trades gesamt (seq 1,2,3,4,6,7,8; seq5=Quote, bad-json zaehlt nicht),
    # davon im Fenster: 2,3,4,6 (seq7 ==end exklusiv, seq1 Vorlauf, seq8 Nachlauf ausgeschlossen)
    assert m["coverage"]["tradesTotalInFile"] == 7
    assert m["coverage"]["tradesInWindow"] == 4
    assert m["coverage"]["restTicksBefore"] == 1   # seq1
    assert m["coverage"]["restTicksAfter"] == 2     # seq7 (==end) + seq8
    assert table.num_rows == 4
    assert m["qualityFlags"]["badJsonLines"] == 1
    assert m["qualityFlags"]["nonTradeEventsInTicksStream"] == 1


def test_end_exclusive(recording, tmp_path):
    # Trade exakt bei END (08:01:00) darf NICHT im Fenster sein (halb-offen).
    m, table = b.convert(recording, START, END, tmp_path / "out")
    last = m["coverage"]["lastTradeInWindowUtc"]
    assert last.startswith("2026-10-26T08:00:45")


def test_volumes(recording, tmp_path):
    m, _ = b.convert(recording, START, END, tmp_path / "out")
    t = m["trades"]
    # im Fenster: Buy 1 (seq2) + 4 (seq4) = 5; Sell 2 (seq3) = 2; Unknown 2 (seq6)
    assert t["totalVolume"] == 1 + 2 + 4 + 2
    assert t["buyVolume"] == 5
    assert t["sellVolume"] == 2
    assert t["unknownVolume"] == 2


def test_unknown_aggressor(recording, tmp_path):
    _, table = b.convert(recording, START, END, tmp_path / "out")
    sides = table.column("aggressor_side").to_pylist()
    assert "Unknown" in sides
    assert sides.count("Unknown") == 1


def test_order_preserved_same_timestamp(recording, tmp_path):
    _, table = b.convert(recording, START, END, tmp_path / "out")
    seqs = table.column("recorder_seq").to_pylist()
    src = table.column("src_seq").to_pylist()
    # seq2 (Buy) muss vor seq3 (Sell) stehen, obwohl identischer tMarket
    assert seqs[0] == 2 and seqs[1] == 3
    assert src == sorted(src)  # Dateireihenfolge stabil monoton


def test_price_grid_separate_from_ticksize(recording, tmp_path):
    m, _ = b.convert(recording, START, END, tmp_path / "out")
    inst = m["instrument"]
    assert inst["tickSize"] == 0.25
    # beobachtetes Raster aus {100.25,100.75,100.50}=0.25
    assert abs(inst["observedPriceGrid"] - 0.25) < 1e-9
    assert inst["offGridVsTickSize"] == 0
    assert inst["contractOrSeries"] == "unknown"
    assert inst["venue"] == "unknown"


def test_reconciliation_discrepancy_visible(recording, tmp_path):
    m, _ = b.convert(recording, START, END, tmp_path / "out")
    r = m["reconciliation"]
    # CSV hat 3 Zeilen, NDJSON 7 Trades -> Diskrepanz SICHTBAR (nicht geglaettet).
    assert r["csv_data_rows"] == 3
    assert r["ndjson_trade_events_total"] == 7
    assert r["csv_eq_trades"] is False
    # summary zaehlt NDJSON-Zeilen (8) == gezaehlte Zeilen -> konsistent
    assert r["ndjson_lines_total"] == 8
    assert r["summary_ticks_events"] == 8
    assert r["summary_eq_ndjson_lines"] is True
    assert r["summary_clean"] is True


def test_raw_unchanged(recording, tmp_path):
    before = hashlib.sha256((recording / "ticks.ndjson").read_bytes()).hexdigest()
    b.convert(recording, START, END, tmp_path / "out")
    after = hashlib.sha256((recording / "ticks.ndjson").read_bytes()).hexdigest()
    assert before == after  # Quelle unverändert


def test_deterministic_logical_output(recording, tmp_path):
    _, t1 = b.convert(recording, START, END, tmp_path / "o1")
    _, t2 = b.convert(recording, START, END, tmp_path / "o2")
    # logisch identisch
    assert t1.equals(t2)
    # Parquet zurücklesen -> identisch
    r1 = pq.read_table(tmp_path / "o1" / "trades.parquet")
    r2 = pq.read_table(tmp_path / "o2" / "trades.parquet")
    assert r1.equals(r2)


def test_tRecv_not_latency_semantics_documented(recording, tmp_path):
    m, _ = b.convert(recording, START, END, tmp_path / "out")
    # Keine künstliche Latenz gespeichert; Semantik dokumentiert.
    assert "Latenz" in m["semantics"]["tRecv"]
    # trades-Tabelle enthält KEINE latency-Spalte
    _, table = b.convert(recording, START, END, tmp_path / "out2")
    assert "latency" not in [n.lower() for n in table.schema.names]


def test_empty_window(recording, tmp_path):
    m, table = b.convert(recording, "2026-10-26T09:00:00Z", "2026-10-26T09:01:00Z", tmp_path / "out")
    assert m["coverage"]["tradesInWindow"] == 0
    assert m["qualityFlags"]["status"] == "empty_window"
    assert table.num_rows == 0


# ============================================================================
# Finding 5 (Codex) — Regressionstests: Datenintegrität der Veröffentlichung.
# Jeder Test kodiert ein Codex-Gegenbeispiel und läuft gegen den echten convert()-Ablauf.
# ============================================================================

def _trade(seq, tm, price=100.25, vol=1, direction="Buy",
           trecv="2026-10-30T12:00:00.0000001Z"):
    return json.dumps({"seq": seq, "dataType": "Trade", "tMarket": tm, "tRecv": trecv,
                       "price": price, "volume": vol, "direction": direction})


def _rec(tmp_path, name, lines, meta='{"instrument":"MNQ","tickSize":0.25}',
         csv=None, summary=None):
    d = tmp_path / name
    d.mkdir()
    (d / "ticks.ndjson").write_text("\n".join(lines) + "\n", encoding="utf-8")
    (d / "meta.json").write_text(meta, encoding="utf-8")
    if csv is not None:
        (d / "ticks.csv").write_text(csv, encoding="utf-8")
    if summary is not None:
        (d / "summary.json").write_text(summary, encoding="utf-8")
    return d


# --- 5.1 Sub-Mikrosekunden-Präzision + Fenstergrenze ------------------------

def test_subsecond_ns_precision_preserved(tmp_path):
    # VORHER (datetime-µs): alle drei .000000X kollabieren auf denselben ns-Wert.
    d = _rec(tmp_path, "rec", [
        _trade(1, "2026-10-26T08:00:00.0000001Z"),
        _trade(2, "2026-10-26T08:00:00.0000002Z"),
        _trade(3, "2026-10-26T08:00:00.0000003Z"),
    ])
    _, t = b.convert(d, START, END, tmp_path / "o1")
    ns = t["t_market_utc"].cast("int64").to_pylist()
    assert len(set(ns)) == 3            # verlustfrei: drei distinkte Nanosekundenwerte
    assert ns == sorted(ns)
    # t_recv ebenfalls ns-genau (7. Nachkommastelle erhalten)
    r = t["t_recv_utc"].cast("int64").to_pylist()
    assert r[0] % 1000 == 100           # .0000001s = 100 ns


def test_fractional_window_boundary_excludes_below(tmp_path):
    # Fensterstart bei .0000002 -> .0000001 ausgeschlossen (halb-offen), zwei verbleiben.
    d = _rec(tmp_path, "rec", [
        _trade(1, "2026-10-26T08:00:00.0000001Z"),
        _trade(2, "2026-10-26T08:00:00.0000002Z"),
        _trade(3, "2026-10-26T08:00:00.0000003Z"),
    ])
    _, t = b.convert(d, "2026-10-26T08:00:00.0000002Z", END, tmp_path / "o")
    assert t.num_rows == 2              # VORHER: 3 (µs-Kürzung ließ .0000001 fälschlich rein)


# --- 5.2 Stabiler Eingabe-Snapshot -----------------------------------------

def test_source_changed_during_read_refuses_publish(tmp_path, monkeypatch):
    d = _rec(tmp_path, "rec", [_trade(1, "2026-10-26T08:00:05.000Z")])
    real = b.iter_ndjson

    def wrapped(path):
        yield from real(path)                       # alles lesen ...
        with open(path, "a", encoding="utf-8") as f:  # ... dann Quelle ändern (vor post-hash)
            f.write(_trade(2, "2026-10-26T08:00:06.000Z") + "\n")

    monkeypatch.setattr(b, "iter_ndjson", wrapped)
    out = tmp_path / "out"
    with pytest.raises(b.SourceChangedError):
        b.convert(d, START, END, out)
    # Nichts veröffentlicht, kein temp-Rest.
    assert not (out / "trades.parquet").exists()
    assert not (out / ".tmp_publish").exists()


# --- 5.3 Status bezieht Fehler/Diskrepanzen ein -----------------------------

def test_status_check_on_bad_json_and_missing_summary(tmp_path):
    d = _rec(tmp_path, "rec", [_trade(1, "2026-10-26T08:00:05.000Z"), "{broken json"])
    m, _ = b.convert(d, START, END, tmp_path / "out")
    assert m["qualityFlags"]["status"] == "check"      # VORHER: "complete"
    reasons = m["qualityFlags"]["statusReasons"]
    assert "bad_json_lines" in reasons
    assert "summary_not_clean_or_missing" in reasons


def test_status_check_on_csv_discrepancy(tmp_path):
    # CSV (1 Datenzeile) widerspricht NDJSON (2 Trades) -> Status check, Diskrepanz sichtbar.
    d = _rec(tmp_path, "rec",
             [_trade(1, "2026-10-26T08:00:05.000Z"), _trade(2, "2026-10-26T08:00:06.000Z")],
             csv="Time,Instrument,Price,Volume,AggressorSide\na,MNQ,100.25,1,Buy\n",
             summary='{"schemaVersion":"ofrec-summary-2","clean":true,'
                     '"perStream":{"ticks.ndjson":{"events":2}}}')
    m, _ = b.convert(d, START, END, tmp_path / "out")
    assert m["reconciliation"]["csv_eq_trades"] is False
    assert m["qualityFlags"]["status"] == "check"
    assert "csv_trade_count_discrepancy" in m["qualityFlags"]["statusReasons"]


def test_status_complete_only_when_fully_clean(tmp_path):
    # Alles konsistent: clean-Summary (A3-Schema), CSV==Trades, on-grid, kein bad json.
    d = _rec(tmp_path, "rec",
             [_trade(1, "2026-10-26T08:00:05.000Z"), _trade(2, "2026-10-26T08:00:06.000Z", price=100.50)],
             csv="Time,Instrument,Price,Volume,AggressorSide\na,MNQ,100.25,1,Buy\nb,MNQ,100.50,1,Buy\n",
             summary='{"schemaVersion":"ofrec-summary-2","clean":true,'
                     '"perStream":{"ticks.ndjson":{"events":2}}}')
    m, _ = b.convert(d, START, END, tmp_path / "out")
    assert m["qualityFlags"]["status"] == "complete"
    assert m["qualityFlags"]["statusReasons"] == []


# --- 5.4 Atomare Veröffentlichung / bestehendes Ziel ------------------------

def test_existing_target_rejected_without_force(tmp_path):
    d = _rec(tmp_path, "rec", [_trade(1, "2026-10-26T08:00:05.000Z")])
    out = tmp_path / "out"
    b.convert(d, START, END, out)                   # erstes Publish ok
    with pytest.raises(b.TargetExistsError):
        b.convert(d, START, END, out)               # zweites ohne force -> abgelehnt
    b.convert(d, START, END, out, force=True)       # mit force erlaubt


def test_snapshot_covers_csv_read(tmp_path, monkeypatch):
    # Recheck: CSV-Änderung während des Lese-/Snapshot-Fensters muss erkannt werden (nicht erst nach post-hash).
    d = _rec(tmp_path, "rec", [_trade(1, "2026-10-26T08:00:05.000Z")],
             csv="Time,Instrument,Price,Volume,AggressorSide\na,MNQ,100.25,1,Buy\n")
    real = b.csv_data_rows

    def change_then_count(p):
        if Path(p).name == "ticks.csv":
            with open(p, "a", encoding="utf-8") as f:
                f.write("b,MNQ,100.25,1,Buy\n")   # Quelle ändert sich im Snapshot-Fenster
        return real(p)

    monkeypatch.setattr(b, "csv_data_rows", change_then_count)
    out = tmp_path / "out"
    with pytest.raises(b.SourceChangedError):
        b.convert(d, START, END, out)
    assert not out.exists()


def test_snapshot_covers_other_streams(tmp_path, monkeypatch):
    # Recheck: auch dom/bbo/cumtrades/bars gehören in dieselbe Stabilitätsprüfung.
    d = _rec(tmp_path, "rec", [_trade(1, "2026-10-26T08:00:05.000Z")],
             csv="Time,Instrument,Price,Volume,AggressorSide\na,MNQ,100.25,1,Buy\n")
    (d / "dom.ndjson").write_text('{"seq":1,"tMarket":"2026-10-26T08:00:05.000Z"}\n', encoding="utf-8")
    real = b.csv_data_rows

    def touch_dom(p):
        with open(d / "dom.ndjson", "a", encoding="utf-8") as f:
            f.write('{"seq":2,"tMarket":"2026-10-26T08:00:06.000Z"}\n')
        return real(p)

    monkeypatch.setattr(b, "csv_data_rows", touch_dom)
    out = tmp_path / "out"
    with pytest.raises(b.SourceChangedError) as ei:
        b.convert(d, START, END, out)
    assert "dom.ndjson" in str(ei.value)
    assert not out.exists()


def test_publish_one_generation_rollback_on_swap_failure(tmp_path, monkeypatch):
    # Recheck: schlägt der finale Verzeichnis-Swap fehl, bleibt die ALTE Generation konsistent (kein Mix).
    d = _rec(tmp_path, "rec", [_trade(1, "2026-10-26T08:00:05.000Z")])
    out = tmp_path / "out"
    b.convert(d, START, END, out)                       # Generation 1 (1 Trade)
    old_parquet = (out / "trades.parquet").read_bytes()
    old_manifest = (out / "manifest.json").read_bytes()
    (d / "ticks.ndjson").write_text(_trade(1, "2026-10-26T08:00:05.000Z") + "\n"
                                    + _trade(2, "2026-10-26T08:00:06.000Z") + "\n", encoding="utf-8")
    real = b.os.replace

    def fail_stage_swap(src, dst):
        if Path(src).name.startswith(".stage_"):
            raise OSError("synthetic directory swap failure")
        return real(src, dst)

    monkeypatch.setattr(b.os, "replace", fail_stage_swap)
    with pytest.raises(OSError):
        b.convert(d, START, END, out, force=True)
    # Altes Ziel vollständig und konsistent (1 Trade in Parquet UND Manifest), keine Mischgeneration.
    assert (out / "trades.parquet").read_bytes() == old_parquet
    assert (out / "manifest.json").read_bytes() == old_manifest
    assert pq.read_table(out / "trades.parquet").num_rows == 1
    assert json.loads((out / "manifest.json").read_text())["trades"]["rows"] == 1


def test_no_partial_generation_on_fresh_target(tmp_path, monkeypatch):
    # Recheck: schlägt der Swap beim frischen Ziel fehl, darf KEIN halbes Ziel (Parquet ohne Manifest) entstehen.
    d = _rec(tmp_path, "rec", [_trade(1, "2026-10-26T08:00:05.000Z")])
    out = tmp_path / "out"
    real = b.os.replace

    def fail_fresh(src, dst):
        if Path(src).name.startswith(".stage_"):
            raise OSError("synthetic fresh swap failure")
        return real(src, dst)

    monkeypatch.setattr(b.os, "replace", fail_fresh)
    with pytest.raises(OSError):
        b.convert(d, START, END, out)
    assert not out.exists()            # weder Parquet noch Manifest allein


def test_concurrent_target_not_overwritten(tmp_path, monkeypatch):
    # Recheck: entsteht das Ziel konkurrierend zwischen Prüfung und Swap, wird es NICHT überschrieben.
    d = _rec(tmp_path, "rec", [_trade(1, "2026-10-26T08:00:05.000Z")])
    out = tmp_path / "out"
    real = b.os.replace
    state = {"injected": False}

    def inject(src, dst):
        if Path(src).name.startswith(".stage_") and not state["injected"]:
            # fremder Prozess legt das Ziel an, bevor unser Swap erfolgt
            Path(dst).mkdir(parents=True)
            (Path(dst) / "trades.parquet").write_bytes(b"CONCURRENT-OWNER")
            state["injected"] = True
        return real(src, dst)

    monkeypatch.setattr(b.os, "replace", inject)
    with pytest.raises(OSError):
        b.convert(d, START, END, out)
    assert state["injected"]
    assert (out / "trades.parquet").read_bytes() == b"CONCURRENT-OWNER"   # fremder Besitz erhalten


def test_interrupted_write_keeps_target_intact(tmp_path):
    d = _rec(tmp_path, "rec", [_trade(1, "2026-10-26T08:00:05.000Z")])
    out = tmp_path / "out"
    b.convert(d, START, END, out)                   # gültiges Ziel anlegen
    old_parquet = (out / "trades.parquet").read_bytes()
    old_manifest = (out / "manifest.json").read_bytes()
    orig = b.pq.write_table

    def boom(table, path, **kw):
        from pathlib import Path as _P
        _P(path).write_bytes(b"PARTIAL"); raise OSError("synthetic interrupted write")

    b.pq.write_table = boom
    try:
        with pytest.raises(OSError):
            b.convert(d, START, END, out, force=True)
    finally:
        b.pq.write_table = orig
    # Ziel unverändert; kein PARTIAL-Artefakt, kein temp-Rest.
    assert (out / "trades.parquet").read_bytes() == old_parquet
    assert (out / "manifest.json").read_bytes() == old_manifest
    assert not (out / ".tmp_publish").exists()
