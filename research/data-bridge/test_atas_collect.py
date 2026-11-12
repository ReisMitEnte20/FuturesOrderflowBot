"""Offline-Tests des Sammel-Collectors (synthetische Fixtures, keine ATAS-/Netz-Abhaengigkeit).

Abnahme (Auftrag Abschnitt 6/7):
- mehrere Abschnitte landen korrekt in EINER gemeinsamen CSV (Header genau einmal)
- CSV-Zeilenzahl + Volumen stimmen mit den geprueften Einzelabschnitten/Manifest ueberein
- erneuter Start erledigter Auftraege fuegt KEINE Daten hinzu (Idempotenz)
- Abbruch waehrend des Anhaengens: Wiederanlauf verliert nichts und haengt nicht doppelt an
- Auftrags-Dedup, NICHT ueber gleiche Trade-Inhalte (legitime gleiche Trades bleiben erhalten)
- Zeit- + Preispraezision bleiben erhalten
- 1x vs 100x: inhaltlich/Reihenfolge gleich -> lossless; Unterschiede werden gemeldet
- leeres Fenster + Diskrepanz-Status ("check") werden nachvollziehbar behandelt
"""
import csv
import json
from decimal import Decimal
from pathlib import Path

import pytest

import atas_bridge as b
import atas_collect as c

START = "2026-10-26T08:00:00Z"
END = "2026-10-26T08:01:00Z"


def _line(seq, tmarket, price, vol, direction="Buy", dtype="Trade",
          trecv="2026-10-29T23:20:03.0Z", exch=None, aggr=None, oi=0):
    return json.dumps({
        "seq": seq, "tMarket": tmarket, "tRecv": trecv, "dataType": dtype,
        "price": price, "volume": vol, "direction": direction,
        "isBid": False, "isAsk": False, "exchOrderId": exch, "aggrExchOrderId": aggr, "oi": oi,
    })


def _make_recording(d: Path, lines, *, run_id="RID-1", instrument="MNQ", clean=True,
                    summary_events=None, csv_rows=None):
    d.mkdir(parents=True, exist_ok=True)
    (d / "ticks.ndjson").write_text("\n".join(lines) + "\n", encoding="utf-8")
    # ticks.csv Datenzeilenzahl steuerbar (Default = Anzahl Trade-Zeilen)
    trades = [l for l in lines if '"dataType": "Trade"' in l or '"dataType":"Trade"' in l]
    n = csv_rows if csv_rows is not None else len(trades)
    rows = "\n".join(f"t,{instrument},100.25,1,Buy" for _ in range(n))
    (d / "ticks.csv").write_text("Time,Instrument,Price,Volume,AggressorSide\n" + (rows + "\n" if n else ""),
                                 encoding="utf-8")
    (d / "meta.json").write_text(json.dumps({
        "recorder": "test", "runId": run_id, "instrument": instrument, "tickSize": 0.25,
        "startWallUtc": "2026-10-29T23:20:00Z"}), encoding="utf-8")
    ev = summary_events if summary_events is not None else len([l for l in lines if "bad json" not in l])
    (d / "summary.json").write_text(json.dumps({
        "schemaVersion": "ofrec-summary-2", "clean": clean, "totalDrops": 0,
        "perStream": {"ticks.ndjson": {"events": ev, "drops": 0}}}), encoding="utf-8")
    return d


def _job_lines(base_price=100.0, n=3, start_sec=0, direction_cycle=("Buy", "Sell")):
    out = []
    for i in range(n):
        sec = start_sec + i
        d = direction_cycle[i % len(direction_cycle)]
        out.append(_line(i + 1, f"2026-10-26T08:00:{sec:02d}.000Z", base_price + i * 0.25, i + 1, d))
    return out


def _read_csv(collection_dir: Path):
    p = collection_dir / "trades.csv"
    with open(p, newline="", encoding="utf-8") as f:
        r = list(csv.reader(f))
    return r[0], r[1:]


# --------------------------------------------------------------------------

def test_append_multiple_jobs_one_shared_csv(tmp_path):
    coll = tmp_path / "London_2026"
    r1 = _make_recording(tmp_path / "rec1", _job_lines(100.0, 3, 0), run_id="A")
    r2 = _make_recording(tmp_path / "rec2", _job_lines(200.0, 4, 10), run_id="B")

    res1 = c.append_job(coll, r1, "job-1", START, END, session="London", year=2026)
    res2 = c.append_job(coll, r2, "job-2", START, END, session="London", year=2026)
    assert res1["result"] == "committed" and res1["rowsAppended"] == 3
    assert res2["result"] == "committed" and res2["rowsAppended"] == 4

    header, rows = _read_csv(coll)
    assert header == c.CSV_COLUMNS
    assert len(rows) == 7                      # genau ein Header, 3+4 Datenzeilen
    # Zuordnung zum Auftrag bleibt erhalten
    jidx = c.CSV_COLUMNS.index("job_id")
    assert [r[jidx] for r in rows].count("job-1") == 3
    assert [r[jidx] for r in rows].count("job-2") == 4

    m = c.load_manifest(coll)
    assert m["csv"]["committedRows"] == 7
    assert m["totals"]["rows"] == 7
    # Volumen stimmt mit Einzelabschnitten ueberein
    assert m["totals"]["totalVolume"] == (1 + 2 + 3) + (1 + 2 + 3 + 4)
    assert m["jobs"]["job-1"]["trades"]["rows"] == 3
    assert m["jobs"]["job-2"]["trades"]["rows"] == 4


def test_rerun_completed_job_adds_nothing(tmp_path):
    coll = tmp_path / "London_2026"
    r1 = _make_recording(tmp_path / "rec1", _job_lines(100.0, 3, 0), run_id="A")
    c.append_job(coll, r1, "job-1", START, END, session="London", year=2026)
    size1 = (coll / "trades.csv").stat().st_size
    rows1 = c.load_manifest(coll)["csv"]["committedRows"]

    res = c.append_job(coll, r1, "job-1", START, END, session="London", year=2026)
    assert res["result"] == "skipped" and res["rowsAppended"] == 0
    assert (coll / "trades.csv").stat().st_size == size1
    assert c.load_manifest(coll)["csv"]["committedRows"] == rows1


def test_same_job_id_different_input_conflicts(tmp_path):
    coll = tmp_path / "London_2026"
    r1 = _make_recording(tmp_path / "rec1", _job_lines(100.0, 3, 0), run_id="A")
    r2 = _make_recording(tmp_path / "rec2", _job_lines(200.0, 4, 10), run_id="B")
    c.append_job(coll, r1, "job-1", START, END, session="London", year=2026)
    with pytest.raises(c.JobConflictError):
        c.append_job(coll, r2, "job-1", START, END, session="London", year=2026)


def test_abort_during_publish_recovers_exactly_once(tmp_path):
    """Simuliert einen Abbruch NACH dem Schreiben der Zeilen, aber VOR dem Manifest-Commit."""
    coll = tmp_path / "London_2026"
    r1 = _make_recording(tmp_path / "rec1", _job_lines(100.0, 3, 0), run_id="A")
    c.append_job(coll, r1, "job-1", START, END, session="London", year=2026)
    good_size = (coll / "trades.csv").stat().st_size
    good_rows = c.load_manifest(coll)["csv"]["committedRows"]

    # --- Abbruch simulieren: job-2 Zeilen an die CSV schreiben, Manifest NICHT aktualisieren ---
    r2 = _make_recording(tmp_path / "rec2", _job_lines(200.0, 4, 10), run_id="B")
    rows, stats, info = c.read_window_trades(r2, START, END)
    payload = c._rows_to_bytes("London", 2026, "job-2", info["run_id"], info["recording_id"], rows)
    with open(coll / "trades.csv", "ab") as f:
        f.write(payload)          # unvollstaendig: kein Commit (committedBytes bleibt alt)
    assert (coll / "trades.csv").stat().st_size > good_size   # Rest vorhanden

    # --- Wiederanlauf: derselbe job-2 wird sauber angehaengt (Recovery schneidet den Rest ab) ---
    res = c.append_job(coll, r2, "job-2", START, END, session="London", year=2026)
    assert res["result"] == "committed"
    assert res["recovered"]["action"] == "truncated"

    header, csv_rows = _read_csv(coll)
    assert len(csv_rows) == good_rows + 4                 # NICHT doppelt (nicht 3+4+4)
    m = c.load_manifest(coll)
    assert m["csv"]["committedRows"] == good_rows + 4
    assert m["csv"]["committedBytes"] == (coll / "trades.csv").stat().st_size
    jidx = c.CSV_COLUMNS.index("job_id")
    assert [r[jidx] for r in csv_rows].count("job-2") == 4   # genau einmal


def test_abort_leaves_previous_job_intact(tmp_path):
    """Nach Abbruch+Recovery bleibt der zuvor committete Auftrag unveraendert erhalten."""
    coll = tmp_path / "NewYork_2026"
    r1 = _make_recording(tmp_path / "rec1", _job_lines(100.0, 2, 0), run_id="A")
    c.append_job(coll, r1, "job-1", START, END, session="NewYork", year=2026)
    with open(coll / "trades.csv", "ab") as f:
        f.write(b"GARBAGE-UNCOMMITTED-TAIL\r\n")
    rec = c.recover(coll)
    assert rec["action"] == "truncated"
    header, rows = _read_csv(coll)
    assert len(rows) == 2
    jidx = c.CSV_COLUMNS.index("job_id")
    assert all(r[jidx] == "job-1" for r in rows)


def test_window_is_half_open_and_order_preserved(tmp_path):
    coll = tmp_path / "London_2026"
    lines = [
        _line(1, "2026-10-26T07:59:59.500Z", 100.00, 3, "Buy"),   # Vorlauf -> aus
        _line(2, "2026-10-26T08:00:00.000Z", 100.25, 1, "Buy"),   # start inklusiv
        _line(3, "2026-10-26T08:00:00.000Z", 100.25, 2, "Sell"),  # gleicher Zeitstempel -> Reihenfolge
        _line(4, "2026-10-26T08:01:00.000Z", 101.00, 9, "Buy"),   # end exklusiv -> aus
    ]
    r = _make_recording(tmp_path / "rec", lines, run_id="A")
    res = c.append_job(coll, r, "job-1", START, END, session="London", year=2026)
    assert res["rowsAppended"] == 2
    _, rows = _read_csv(coll)
    aidx = c.CSV_COLUMNS.index("aggressor_side")
    assert [r[aidx] for r in rows] == ["Buy", "Sell"]            # Reihenfolge erhalten
    # legitime gleiche Zeit/Preis bleiben beide erhalten
    assert len(rows) == 2


def test_price_and_time_precision_preserved(tmp_path):
    coll = tmp_path / "London_2026"
    # ROHE NDJSON-Zeilen: Preis-Token exakt wie in der Datei (trailing zero / viele Stellen),
    # damit der Decimal-Pfad die Quellziffern nachweislich verlustfrei weitergibt (kein float-Rauschen).
    lines = [
        '{"seq":1,"tMarket":"2026-10-26T08:00:00.123456789Z","tRecv":"2026-10-29T23:20:03.0Z",'
        '"dataType":"Trade","price":21050.750,"volume":1,"direction":"Buy","oi":0}',
        '{"seq":2,"tMarket":"2026-10-26T08:00:30.000000500Z","tRecv":"2026-10-29T23:20:03.0Z",'
        '"dataType":"Trade","price":21050.2500000001,"volume":2,"direction":"Sell","oi":0}',
    ]
    r = _make_recording(tmp_path / "rec", lines, run_id="A", summary_events=2, csv_rows=2)
    c.append_job(coll, r, "job-1", START, END, session="London", year=2026)
    _, rows = _read_csv(coll)
    tidx = c.CSV_COLUMNS.index("t_market_utc")
    pidx = c.CSV_COLUMNS.index("price")
    assert rows[0][tidx] == "2026-10-26T08:00:00.123456789Z"     # ns verlustfrei
    assert rows[1][tidx] == "2026-10-26T08:00:30.000000500Z"
    assert rows[0][pidx] == "21050.750"                          # trailing zero erhalten (Decimal)
    assert rows[1][pidx] == "21050.2500000001"                   # sub-cent Stellen erhalten (kein float-Rauschen)


def test_empty_window_recorded_no_rows(tmp_path):
    coll = tmp_path / "London_2026"
    lines = [_line(1, "2026-10-26T09:00:00.000Z", 100.0, 1, "Buy")]   # ausserhalb Fenster
    r = _make_recording(tmp_path / "rec", lines, run_id="A")
    res = c.append_job(coll, r, "job-1", START, END, session="London", year=2026)
    assert res["result"] == "empty" and res["rowsAppended"] == 0
    assert c.load_manifest(coll)["jobs"]["job-1"]["coverageStatus"] == "empty_window"
    assert not (coll / "trades.csv").exists() or (coll / "trades.csv").stat().st_size == 0


def test_check_status_not_appended_without_override(tmp_path):
    coll = tmp_path / "London_2026"
    # Diskrepanz erzwingen: summary.clean=False
    r = _make_recording(tmp_path / "rec", _job_lines(100.0, 3, 0), run_id="A", clean=False)
    res = c.append_job(coll, r, "job-1", START, END, session="London", year=2026)
    assert res["result"] == "check" and res["rowsAppended"] == 0
    assert "summary_not_clean_or_missing" in res["statusReasons"]
    # mit Override uebernehmbar
    res2 = c.append_job(coll, r, "job-1", START, END, session="London", year=2026, allow_check_status=True)
    assert res2["result"] == "committed" and res2["rowsAppended"] == 3


def test_compare_identical_content_is_lossless(tmp_path):
    """Gleiche Trades, nur aufnahmeabhaengige Metadaten (run_id, t_recv) verschieden -> lossless."""
    lines_a = [_line(1, "2026-10-26T08:00:01Z", 100.25, 1, "Buy", trecv="2026-10-29T23:00:00.0Z"),
               _line(2, "2026-10-26T08:00:02Z", 100.50, 2, "Sell", trecv="2026-10-29T23:00:00.1Z")]
    lines_b = [_line(1, "2026-10-26T08:00:01Z", 100.25, 1, "Buy", trecv="2026-11-04T10:00:00.0Z"),
               _line(2, "2026-10-26T08:00:02Z", 100.50, 2, "Sell", trecv="2026-11-04T10:00:00.9Z")]
    ra = _make_recording(tmp_path / "recA", lines_a, run_id="SLOW-1x")
    rb = _make_recording(tmp_path / "recB", lines_b, run_id="FAST-100x")
    r = c.compare_recordings(ra, rb, START, END)
    assert r["lossless"] is True
    assert r["equal"]["contentAndOrder"] is True
    assert r["firstDiffs"] == []
    assert r["a"]["runId"] != r["b"]["runId"]      # Metadaten verschieden, aber ausgenommen


def test_compare_detects_content_difference(tmp_path):
    lines_a = [_line(1, "2026-10-26T08:00:01Z", 100.25, 1, "Buy"),
               _line(2, "2026-10-26T08:00:02Z", 100.50, 2, "Sell")]
    lines_b = [_line(1, "2026-10-26T08:00:01Z", 100.25, 1, "Buy"),
               _line(2, "2026-10-26T08:00:02Z", 100.50, 3, "Sell")]   # Volumen weicht ab
    ra = _make_recording(tmp_path / "recA", lines_a)
    rb = _make_recording(tmp_path / "recB", lines_b)
    r = c.compare_recordings(ra, rb, START, END)
    assert r["lossless"] is False
    assert r["equal"]["sellVolume"] is False
    assert r["firstDiffs"] and r["firstDiffs"][0]["index"] == 1


def test_compare_detects_missing_trade(tmp_path):
    lines_a = [_line(1, "2026-10-26T08:00:01Z", 100.25, 1, "Buy"),
               _line(2, "2026-10-26T08:00:02Z", 100.50, 2, "Sell")]
    lines_b = [_line(1, "2026-10-26T08:00:01Z", 100.25, 1, "Buy")]    # ein Trade fehlt (lokaler Drop)
    ra = _make_recording(tmp_path / "recA", lines_a)
    rb = _make_recording(tmp_path / "recB", lines_b)
    r = c.compare_recordings(ra, rb, START, END)
    assert r["lossless"] is False
    assert r["equal"]["rows"] is False
    assert r["a"]["rows"] == 2 and r["b"]["rows"] == 1


def test_source_change_during_read_aborts(tmp_path, monkeypatch):
    r = _make_recording(tmp_path / "rec", _job_lines(100.0, 3, 0), run_id="A")
    orig = c.b.sha256_file
    calls = {"n": 0}

    def flaky(path):
        calls["n"] += 1
        # beim POST-Hash einen anderen Wert liefern -> Aenderung waehrend des Lesens vortaeuschen
        return "POST" if calls["n"] > 4 else orig(path)

    monkeypatch.setattr(c.b, "sha256_file", flaky)
    with pytest.raises(b.SourceChangedError):
        c.read_window_trades(r, START, END)
