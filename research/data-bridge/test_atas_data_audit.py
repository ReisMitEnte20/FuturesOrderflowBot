"""Offline-Tests der Datenqualitäts-/Herkunftsprüfung (nur synthetische Dateien in tmp_path, nie echte Aufnahmen).

Abgedeckt: generierter Ablauf (gleichverteilte Ticks, Replay-Typ CandlesAndGeneratedBestPrices, DOM mit einem Level)
→ nur Stufe A; belegt historische Aufnahme (TradesAndDepths, unregelmäßige Ticks, mehrere DOM-Level) → A–D; ohne Beleg
→ ungeklärt; Drops je Stream; fehlender Stream; .zst-Stream lesbar; Replay-Typ nur für die im Auftrag genannte Aufnahme;
überlappende Collections zählen nicht doppelt; London und New York am selben Tag sind keine Duplikate; widersprüchliche
Fassungen = Konflikt; Kontrakt wird nicht aus dem Datum erraten; Collection ohne Manifest.
"""
import json
from datetime import datetime, timedelta, timezone
from pathlib import Path

import pytest

import atas_archive as ar
import atas_data_audit as au

UTC = timezone.utc


def _iso(t: datetime, frac7: int = 0) -> str:
    return t.strftime("%Y-%m-%dT%H:%M:%S") + f".{frac7:07d}Z"


def make_recording(root: Path, rec_id: str, start: datetime, *, uniform=True, minutes=3, per_min=60, dom_levels=1,
                   drops=None, missing=(), run_id=None, clean=True, zst=()):
    d = root / rec_id
    d.mkdir(parents=True)
    run_id = run_id or rec_id.lower()
    ticks_csv, ticks_nd, cum, bbo, dom = ["Time,Instrument,Price,Volume,AggressorSide"], [], [], [], []
    seq = 0
    prev_levels = []
    for m in range(minutes):
        base = start + timedelta(minutes=m)
        for i in range(per_min):
            if uniform:
                units = i * (600_000_000 // per_min)                 # 60 s / n in 100-ns-Einheiten
            else:
                units = (i * i * 7919 + i * 104729) % 600_000_000    # unregelmäßig
            sec, frac = divmod(units, 10_000_000)
            t = base + timedelta(seconds=sec)
            ts = _iso(t, frac)
            side = "Buy" if i % 3 else "Sell"
            price = 20000 + (i % 7) * 0.25
            ticks_csv.append(f"{ts},MNQ,{price:.2f},1,{side}")
            seq += 1
            ticks_nd.append(json.dumps({"seq": seq, "tMarket": ts, "price": price, "volume": 1, "direction": side}))
            cum.append(json.dumps({"seq": seq, "cumId": seq, "event": "new", "tMarket": ts}))
            bbo.append(json.dumps({"seq": seq, "tMarket": ts, "price": price, "volume": 2, "side": "Bid"}))
            for p_old in prev_levels:                                   # alte Level löschen (Volumen 0) wie im echten DOM
                dom.append(json.dumps({"seq": seq, "tMarket": ts, "side": p_old[0], "price": p_old[1], "volume": 0}))
            prev_levels = [("Bid", price - lv * 0.25) for lv in range(dom_levels)] + [("Ask", price + 0.25 + lv * 0.25) for lv in range(dom_levels)]
            for lv in range(dom_levels):
                dom.append(json.dumps({"seq": seq, "tMarket": ts, "side": "Bid", "price": price - lv * 0.25, "volume": 3}))
                dom.append(json.dumps({"seq": seq, "tMarket": ts, "side": "Ask", "price": price + 0.25 + lv * 0.25, "volume": 3}))
    ticks_csv = sorted(ticks_csv[1:])
    files = {"ticks.csv": "\n".join(["Time,Instrument,Price,Volume,AggressorSide"] + ticks_csv) + "\n",
             "ticks.ndjson": "\n".join(ticks_nd) + "\n", "cumtrades.ndjson": "\n".join(cum) + "\n",
             "bbo.ndjson": "\n".join(bbo) + "\n", "dom.ndjson": "\n".join(dom) + "\n",
             "bars.ndjson": json.dumps({"bar": 1, "tMarket": _iso(start)}) + "\n"}
    drops = drops or {}
    per = {}
    for name, text in files.items():
        if name in missing:
            continue
        (d / name).write_text(text, encoding="utf-8")
        per[name] = {"events": text.count("\n"), "drops": drops.get(name, 0)}
    (d / "meta.json").write_text(json.dumps({"instrument": "MNQ", "runId": run_id, "startWallUtc": "2026-11-25T01:00:00Z"}))
    (d / "summary.json").write_text(json.dumps({"runId": run_id, "clean": clean, "totalDrops": sum(drops.values()),
                                                "writerFaulted": False, "perStream": per}))
    for name in zst:
        ar.archive_file(d, name)
    return d, run_id


def job_id(session, s: datetime, e: datetime):
    return f"{session}_{s.year}_{s:%Y%m%dT%H%M%SZ}_{e:%Y%m%dT%H%M%SZ}"


def make_collection(root: Path, name: str, session: str, jobs: list, protocol_blocks: list, contract="unknown"):
    """jobs: (job_id, rec_id, run_id, (rows, vol, buy, sell)); protocol_blocks: (job_id, rec_id, replay_type or None)."""
    d = root / name
    d.mkdir(parents=True)
    header = "session,year,job_id,recording_id,run_id,src_seq,recorder_seq,t_market_utc,instrument,contract_or_series,price,volume,aggressor_side\n"
    data = header.encode()
    man_jobs = {}
    for jid, rid, run, stats in jobs:
        _, s, e = au.parse_job_id(jid)
        off = len(data)
        data += f"{session},2026,{jid},{rid},{run},0,1,{_iso(s)},MNQ,{contract},20000.00,1,Buy\n".encode()
        man_jobs[jid] = {"status": "committed", "recordingId": rid, "runId": run, "coverageStatus": "complete", "statusReasons": [],
                         "window": {"start": _iso(s), "end": _iso(e)}, "byteOffsetBefore": off, "byteOffsetAfter": len(data),
                         "trades": {"rows": stats[0], "totalVolume": stats[1], "buyVolume": stats[2], "sellVolume": stats[3], "unknownVolume": 0},
                         "reconciliation": {"summary_clean": True}}
    (d / "trades.csv").write_bytes(data)
    (d / "collection_manifest.json").write_text(json.dumps({"session": session, "jobs": man_jobs}))
    lines = []
    for i, (jid, rid, typ) in enumerate(protocol_blocks, 1):
        _, s, e = au.parse_job_id(jid)
        lines.append(f"===== Fenster-Auftrag {i}/{len(protocol_blocks)}: {jid} =====")
        lines.append(f"[7] Zugeordnete Instanz: {rid}  runId=x  (genau EINE neue gesperrte MNQ-Instanz)")
        if typ:
            lines.append(f"[9] Ladebereich laut ATAS: [{s:%Y-%m-%d %H:%M:%S}Z, {e:%Y-%m-%d %H:%M:%S}Z]  (Zeile: x INFO  Replay DS      : "
                         f"Playing from {s:%d.%m.%Y %H:%M:%S} to {e:%d.%m.%Y %H:%M:%S} type {typ}.)")
        lines.append(f"===== ERGEBNIS Auftrag {i}/{len(protocol_blocks)} [x): GESAMT-PASS (rc=0) =====")
    (d / "run_protocol.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    return d


L_S = datetime(2026, 11, 17, 8, 0, tzinfo=UTC); L_E = datetime(2026, 11, 17, 11, 0, tzinfo=UTC)
N_S = datetime(2026, 11, 17, 14, 30, tzinfo=UTC); N_E = datetime(2026, 11, 17, 17, 0, tzinfo=UTC)
STATS = (180, 180, 120, 60)


def run_audit(tmp_path, cols, recs, **kw):
    return au.audit([cols], [recs], sample_events=5000, sample_minutes=2, workers=1, **kw)


def row(res, rid):
    return next(r for r in res["rows"] if r["recording_id"] == rid)


def test_generated_candle_replay_is_only_usable_at_level_a_on_minute_basis(tmp_path):
    recs, cols = tmp_path / "rec", tmp_path / "col"
    _, run = make_recording(recs, "MNQ_L1", L_S, uniform=True, dom_levels=1)
    jl = job_id("London", L_S, L_E)
    make_collection(cols, "London_c", "London", [(jl, "MNQ_L1", run, STATS)], [(jl, "MNQ_L1", "CandlesAndGeneratedBestPrices")])
    r = row(run_audit(tmp_path, cols, recs), "MNQ_L1")

    assert r["provenance"]["Trades"] == au.MINUTE
    assert "gleichverteilt" in r["evidence"]["Trades"]
    assert r["provenance"]["BBO"] == au.GEN and r["provenance"]["DOM"] == au.GEN and r["provenance"]["Tape"] == au.GEN
    assert r["dom_levels"] == [1, 1]
    assert r["replay_type"] == "CandlesAndGeneratedBestPrices" and "run_protocol.txt Zeile" in r["replay_type_evidence"]
    assert [r["levels"][l]["status"] for l in "ABCD"] == ["ja", "nein", "nein", "nein"]
    assert r["levels"]["B"]["reason"] == "Tape: generiert"


def test_recording_with_proven_trades_and_depths_reaches_level_d(tmp_path):
    recs, cols = tmp_path / "rec", tmp_path / "col"
    _, run = make_recording(recs, "MNQ_N1", N_S, uniform=False, dom_levels=5)
    jn = job_id("NewYork", N_S, N_E)
    make_collection(cols, "NY_c", "NewYork", [(jn, "MNQ_N1", run, STATS)], [(jn, "MNQ_N1", "TradesAndDepths")])
    r = row(run_audit(tmp_path, cols, recs), "MNQ_N1")
    assert r["provenance"] == {"Trades": au.HIST, "Tape": au.HIST, "BBO": au.HIST, "DOM": au.HIST}
    assert [r["levels"][l]["status"] for l in "ABCD"] == ["ja", "ja", "ja", "ja"]


def test_without_recording_specific_evidence_provenance_stays_unclear(tmp_path):
    """Datei vorhanden + null Drops beweist keine historische Herkunft."""
    recs, cols = tmp_path / "rec", tmp_path / "col"
    _, run = make_recording(recs, "MNQ_N2", N_S, uniform=False, dom_levels=5)
    jn = job_id("NewYork", N_S, N_E)
    make_collection(cols, "NY_c", "NewYork", [(jn, "MNQ_N2", run, STATS)], [(jn, "MNQ_N2", None)])
    r = row(run_audit(tmp_path, cols, recs), "MNQ_N2")
    assert all(r["quality"][g] == "ok" for g in au.GROUPS)
    assert r["provenance"]["DOM"] == au.UNK and r["provenance"]["Trades"] == au.UNK
    assert [r["levels"][l]["status"] for l in "ABCD"] == ["ungeklärt"] * 4
    s = run_audit(tmp_path, cols, recs)["summary"]["levels"]["D"]["NewYork"]
    assert (s["nutzbar"], s["ungeklärt"]) == (0, 1)


def test_replay_type_is_assigned_only_to_the_recording_named_in_the_job_block(tmp_path):
    recs, cols = tmp_path / "rec", tmp_path / "col"
    _, run_a = make_recording(recs, "MNQ_A", N_S, uniform=False, dom_levels=5)
    _, run_b = make_recording(recs, "MNQ_B", N_S, uniform=False, dom_levels=5)
    jn = job_id("NewYork", N_S, N_E)
    make_collection(cols, "NY_c", "NewYork", [(jn, "MNQ_A", run_a, STATS)], [(jn, "MNQ_A", "TradesAndDepths"), (jn, "MNQ_B", None)])
    res = run_audit(tmp_path, cols, recs)
    assert row(res, "MNQ_A")["replay_type"] == "TradesAndDepths"
    b = row(res, "MNQ_B")
    assert b["replay_type"] is None and b["provenance"]["DOM"] == au.UNK
    assert b["in_manifest"] is False and b["levels"]["A"]["status"] == "nein"
    assert "nicht in eine Collection übernommen" in b["levels"]["A"]["reason"]


def test_drops_missing_stream_and_zst_are_reported_per_stream(tmp_path):
    recs, cols = tmp_path / "rec", tmp_path / "col"
    _, run1 = make_recording(recs, "MNQ_D1", N_S, uniform=False, dom_levels=5, drops={"bbo.ndjson": 7}, zst=("ticks.csv", "dom.ndjson"))
    _, run2 = make_recording(recs, "MNQ_D2", L_S, uniform=False, dom_levels=5, missing=("dom.ndjson",))
    jn, jl = job_id("NewYork", N_S, N_E), job_id("London", L_S, L_E)
    make_collection(cols, "NY_c", "NewYork", [(jn, "MNQ_D1", run1, STATS)], [(jn, "MNQ_D1", "TradesAndDepths")])
    make_collection(cols, "L_c", "London", [(jl, "MNQ_D2", run2, STATS)], [(jl, "MNQ_D2", "TradesAndDepths")])
    res = run_audit(tmp_path, cols, recs)
    d1 = row(res, "MNQ_D1")
    assert d1["drops"]["bbo.ndjson"] == 7 and d1["quality"]["BBO"] == "drops"
    assert d1["quality"]["Trades"] == "ok" and d1["quality"]["DOM"] == "ok", ".zst-Streams sind lesbar"
    assert [d1["levels"][l]["status"] for l in "ABCD"] == ["ja", "ja", "nein", "nein"]
    d2 = row(res, "MNQ_D2")
    assert d2["quality"]["DOM"] == "fehlt" and d2["levels"]["D"]["status"] == "nein" and d2["levels"]["C"]["status"] == "ja"


def test_overlapping_collections_do_not_add_days_and_london_new_york_same_day_are_not_duplicates(tmp_path):
    recs, cols = tmp_path / "rec", tmp_path / "col"
    _, rl = make_recording(recs, "MNQ_L", L_S, uniform=True)
    _, rn = make_recording(recs, "MNQ_N", N_S, uniform=True)
    jl, jn = job_id("London", L_S, L_E), job_id("NewYork", N_S, N_E)
    make_collection(cols, "L_c", "London", [(jl, "MNQ_L", rl, STATS)], [(jl, "MNQ_L", "CandlesAndGeneratedBestPrices")])
    make_collection(cols, "NY_c1", "NewYork", [(jn, "MNQ_N", rn, STATS)], [(jn, "MNQ_N", "CandlesAndGeneratedBestPrices")])
    make_collection(cols, "NY_c2_overlap", "NewYork", [(jn, "MNQ_N", rn, STATS)], [])
    res = run_audit(tmp_path, cols, recs)
    assert len(res["rows"]) == 2
    assert sorted(row(res, "MNQ_N")["collections"]) == ["NY_c1", "NY_c2_overlap"]
    a = res["summary"]["levels"]["A"]
    assert (a["London"]["nutzbar"], a["NewYork"]["nutzbar"], a["gemeinsame_tage"]) == (1, 1, 1)


def test_two_clean_recordings_with_different_content_for_one_session_are_a_conflict(tmp_path):
    recs, cols = tmp_path / "rec", tmp_path / "col"
    _, r1 = make_recording(recs, "MNQ_X1", N_S, uniform=True)
    _, r2 = make_recording(recs, "MNQ_X2", N_S, uniform=True)
    jn = job_id("NewYork", N_S, N_E)
    make_collection(cols, "NY_a", "NewYork", [(jn, "MNQ_X1", r1, STATS)], [(jn, "MNQ_X1", "CandlesAndGeneratedBestPrices")])
    make_collection(cols, "NY_b", "NewYork", [(jn, "MNQ_X2", r2, (181, 181, 120, 61))], [(jn, "MNQ_X2", "CandlesAndGeneratedBestPrices")])
    s = run_audit(tmp_path, cols, recs)["summary"]
    assert s["levels"]["A"]["NewYork"]["nutzbar"] == 0 and s["levels"]["A"]["NewYork"]["ausgeschlossen"] == 1
    assert "Konflikt" in s["levels"]["A"]["NewYork"]["gründe"][0][0]


def test_contract_is_not_guessed_and_empty_collections_are_listed(tmp_path):
    recs, cols = tmp_path / "rec", tmp_path / "col"
    _, run = make_recording(recs, "MNQ_C", N_S, uniform=True)
    jn = job_id("NewYork", N_S, N_E)
    make_collection(cols, "NY_c", "NewYork", [(jn, "MNQ_C", run, STATS)], [(jn, "MNQ_C", "CandlesAndGeneratedBestPrices")])
    (cols / "NY_aborted").mkdir()
    (cols / "NY_aborted" / "run_protocol.txt").write_text("abgebrochen\n")
    res = run_audit(tmp_path, cols, recs)
    r = row(res, "MNQ_C")
    assert r["contract"] == "unknown" and "contract_or_series" in r["contract_evidence"]
    assert "Z" not in r["contract"] and "2026" not in r["contract"]
    assert res["empty_collections"][0]["collection"] == "NY_aborted"


def test_outputs_contain_one_row_per_day_session_recording(tmp_path):
    recs, cols = tmp_path / "rec", tmp_path / "col"
    _, run = make_recording(recs, "MNQ_O", L_S, uniform=True)
    jl = job_id("London", L_S, L_E)
    make_collection(cols, "L_c", "London", [(jl, "MNQ_O", run, STATS)], [(jl, "MNQ_O", "CandlesAndGeneratedBestPrices")])
    res = run_audit(tmp_path, cols, recs)
    paths = au.write_outputs(res, tmp_path / "out")
    text = Path(paths["csv"]).read_text(encoding="utf-8-sig").splitlines()
    assert text[0].startswith("Datum;Session;UTC-Fenster;Aufnahme-ID;Kontrakt + Beleg;Trades;Tape;BBO;DOM;Drops je Stream;Konflikt/Grund")
    assert len(text) == 2 and "MNQ_O" in text[1] and "2026-11-17;London;08:00–11:00" in text[1]
    assert "Stufe A" in Path(paths["report"]).read_text(encoding="utf-8")


def test_recording_without_any_job_gets_date_and_session_from_its_first_tick_and_stays_excluded(tmp_path):
    recs, cols = tmp_path / "rec", tmp_path / "col"
    make_recording(recs, "MNQ_ORPHAN", datetime(2026, 7, 8, 13, 30, tzinfo=UTC), uniform=True)   # 09:30 New York (EDT)
    cols.mkdir()
    res = run_audit(tmp_path, cols, recs)
    r = row(res, "MNQ_ORPHAN")
    assert (r["date"], r["session"], r["window_utc"]) == ("2026-07-08", "NewYork", "13:30–16:00")
    assert r["levels"]["A"]["status"] == "nein" and "keinem Auftrag zugeordnet" in r["levels"]["A"]["reason"]
    assert res["summary"]["levels"]["A"]["NewYork"]["ausgeschlossen"] == 1


def test_contract_is_read_from_the_first_data_row_even_at_byte_offset_zero(tmp_path):
    p = tmp_path / "trades.csv"
    p.write_text("session,contract_or_series,price\nNewYork,unknown,1\n", encoding="utf-8")
    assert au._contract_from_csv(p, 0)[0] == "unknown"
