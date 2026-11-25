"""Offline-Tests der Archivierung (nur synthetische Testdateien in tmp_path, nie echte Aufnahmen).

Abgedeckt: exakter Roundtrip, identischer Bridge-Import roh vs .zst, beschaedigtes Archiv, Abbruch + Fortsetzen
(Stopp, Absturz zwischen Umbenennen und Loeschen, Temp-Rest), fehlender Speicher, gesperrte Datei, fehlerhafte /
nicht zugeordnete Aufnahme, bereits vorhandenes Archiv, nicht kleiner, Manifest-Hash-Abweichung, geaenderte Quelle,
bytegenaue Wiederherstellung.
"""
import hashlib
import json
import os
import random
from pathlib import Path

import pytest

import atas_archive as ar
import atas_collect as c
from test_atas_collect import _make_recording, _job_lines, START, END


@pytest.fixture(autouse=True)
def small_blocks(monkeypatch):
    monkeypatch.setattr(ar, "BLOCK_BYTES", 4096)          # viele Frames auch bei kleinen Testdateien
    monkeypatch.setattr(ar, "FREE_RESERVE_BYTES", 0)
    monkeypatch.setattr(ar, "QUIET_SECONDS", 0)
    monkeypatch.setattr(ar, "replay_run_active", lambda: False)


def _text(n_lines=4000, seed=1):
    rnd = random.Random(seed)
    lines = [json.dumps({"seq": i, "tMarket": f"2026-01-02T14:30:{i % 60:02d}.{rnd.randint(0, 9999999):07d}Z",
                         "price": 21000 + rnd.randint(0, 400) * 0.25, "volume": rnd.randint(1, 5)})
             for i in range(n_lines)]
    return ("\n".join(lines) + "\n").encode()


def _sha(b):
    return hashlib.sha256(b).hexdigest()


def test_roundtrip_exact_and_transparent_read(tmp_path):
    data = _text()
    (tmp_path / "dom.ndjson").write_bytes(data)
    r = ar.archive_file(tmp_path, "dom.ndjson")
    assert r["result"] == "archived" and r["zstSize"] < r["origSize"] == len(data)
    assert not (tmp_path / "dom.ndjson").exists() and (tmp_path / "dom.ndjson.zst").exists()
    with ar.open_binary(tmp_path / "dom.ndjson") as f:
        assert f.read() == data                               # bytegenau, mehrere Frames
    assert ar.sha256_file(tmp_path / "dom.ndjson") == _sha(data)   # Hash = Original-Bytes
    assert ar.original_size(tmp_path / "dom.ndjson") == len(data)
    with ar.open_text(tmp_path / "dom.ndjson") as f:
        assert sum(1 for _ in f) == data.count(b"\n")
    e = ar.load_archive_manifest(tmp_path)["files"]["dom.ndjson"]
    assert e["state"] == "archived" and e["sha256"] == _sha(data) and e["size"] == len(data)
    assert ar.verify_recording(tmp_path)["ok"]


def test_bridge_import_identical_raw_vs_zst(tmp_path):
    lines = _job_lines(100.0, 6, 0)
    lines.append(lines[-1])    # legitimer identischer Trade (gleiche Zeit/Preis/Menge) bleibt erhalten
    rec = _make_recording(tmp_path / "rec", lines, run_id="R1")
    ra = c.append_job(tmp_path / "A", rec, "job-1", START, END, session="NY", year=2026)
    for n in ar.compressible_names(rec):
        assert ar.archive_file(rec, n)["result"] in ("archived", "kept_not_smaller")
    assert (rec / "ticks.ndjson.zst").exists() and not (rec / "ticks.ndjson").exists()
    rb = c.append_job(tmp_path / "B", rec, "job-1", START, END, session="NY", year=2026)
    assert ra["result"] == rb["result"] == "committed" and ra["rowsAppended"] == rb["rowsAppended"] == 7
    assert (tmp_path / "A" / "trades.csv").read_bytes() == (tmp_path / "B" / "trades.csv").read_bytes()
    ma, mb = c.load_manifest(tmp_path / "A"), c.load_manifest(tmp_path / "B")
    assert ma["jobs"]["job-1"]["inputHashes"] == mb["jobs"]["job-1"]["inputHashes"]   # 'Fortsetzen'-Dedup bleibt
    # Wiederholung gegen A (wie 'Fortsetzen' nach der Archivierung): skipped, nichts doppelt
    assert c.append_job(tmp_path / "A", rec, "job-1", START, END, session="NY", year=2026)["result"] == "skipped"


def test_damaged_archive_detected_and_restore_refuses(tmp_path):
    data = _text()
    (tmp_path / "bbo.ndjson").write_bytes(data)
    ar.archive_file(tmp_path, "bbo.ndjson")
    z = tmp_path / "bbo.ndjson.zst"
    b = bytearray(z.read_bytes()); b[len(b) // 2] ^= 0xFF; z.write_bytes(bytes(b))
    assert not ar.verify_recording(tmp_path)["ok"]
    r = ar.restore_recording(tmp_path)
    assert not r["ok"] and r["files"][0]["result"] == "verify_failed"
    assert not (tmp_path / "bbo.ndjson").exists() and z.exists()          # nichts Falsches hergestellt
    assert not list(tmp_path.glob("*.tmp"))


def test_failed_verification_keeps_original(tmp_path, monkeypatch):
    data = _text()
    (tmp_path / "dom.ndjson").write_bytes(data)
    real = ar.open_binary_zst
    import io
    monkeypatch.setattr(ar, "open_binary_zst", lambda p: io.BytesIO(real(p).read()[:-1]))   # 1 Byte fehlt
    r = ar.archive_file(tmp_path, "dom.ndjson")
    assert r["result"] == "verify_failed"
    assert (tmp_path / "dom.ndjson").read_bytes() == data
    assert not (tmp_path / "dom.ndjson.zst").exists() and not (tmp_path / "dom.ndjson.zst.tmp").exists()


def test_stop_mid_file_then_resume(tmp_path):
    data = _text(8000)
    raw = tmp_path / "cumtrades.ndjson"
    raw.write_bytes(data)
    stop = tmp_path / "stop.request"

    def prog(ev):
        if ev.get("event") == "block" and ev["done"] >= 3 * 4096:
            stop.write_text("x")
    with pytest.raises(ar.Stopped):
        ar.archive_file(tmp_path, raw.name, stop_file=stop, progress=prog)
    assert raw.read_bytes() == data and not list(tmp_path.glob("*.zst*"))
    stop.unlink()
    assert ar.archive_file(tmp_path, raw.name, stop_file=stop)["result"] == "archived"
    with ar.open_binary(raw) as f:
        assert f.read() == data


def test_crash_between_rename_and_delete_resumes_safely(tmp_path):
    data = _text()
    raw = tmp_path / "ticks.ndjson"
    raw.write_bytes(data)
    ar.archive_file(tmp_path, raw.name)
    # Zustand nach Absturz: Archiv umbenannt + Eintrag 'verified', Original noch da
    raw.write_bytes(data)
    m = ar.load_archive_manifest(tmp_path); m["files"][raw.name]["state"] = "verified"
    ar._write_archive_manifest(tmp_path, m)
    r = ar.archive_file(tmp_path, raw.name)
    assert r["result"] == "already_archived" and r.get("resumed")
    assert not raw.exists() and ar.load_archive_manifest(tmp_path)["files"][raw.name]["state"] == "archived"


def test_raw_and_archive_side_by_side_but_different_never_deleted(tmp_path):
    data = _text()
    raw = tmp_path / "ticks.ndjson"
    raw.write_bytes(data)
    ar.archive_file(tmp_path, raw.name)
    raw.write_bytes(data + b"anders\n")                     # Rohdatei weicht vom Pruefwert ab
    r = ar.archive_file(tmp_path, raw.name)
    assert r["result"] == "unclear"
    assert raw.exists() and (tmp_path / "ticks.ndjson.zst").exists()


def test_existing_archive_without_entry_is_unclear(tmp_path):
    data = _text()
    (tmp_path / "x.ndjson").write_bytes(data)
    (tmp_path / "x.ndjson.zst").write_bytes(b"fremd")
    r = ar.archive_file(tmp_path, "x.ndjson")
    assert r["result"] == "unclear" and (tmp_path / "x.ndjson").read_bytes() == data
    assert (tmp_path / "x.ndjson.zst").read_bytes() == b"fremd"


def test_leftover_tmp_is_discarded_and_original_archived(tmp_path):
    data = _text()
    (tmp_path / "bbo.ndjson").write_bytes(data)
    (tmp_path / "bbo.ndjson.zst.tmp").write_bytes(b"halber Rest")
    assert ar.archive_file(tmp_path, "bbo.ndjson")["result"] == "archived"
    assert not (tmp_path / "bbo.ndjson.zst.tmp").exists()


def test_out_of_space_keeps_original(tmp_path, monkeypatch):
    data = _text()
    (tmp_path / "dom.ndjson").write_bytes(data)
    monkeypatch.setattr(ar, "free_bytes", lambda p: 10)
    with pytest.raises(ar.OutOfSpace):
        ar.archive_file(tmp_path, "dom.ndjson")
    assert (tmp_path / "dom.ndjson").read_bytes() == data and not list(tmp_path.glob("*.zst*"))


def test_disk_full_during_write_keeps_original(tmp_path, monkeypatch):
    data = _text()
    (tmp_path / "dom.ndjson").write_bytes(data)
    import pyarrow as pa
    calls = {"n": 0}
    real_codec = pa.Codec("zstd", compression_level=3)     # echtes Objekt VOR dem Ersetzen anlegen

    class FullCodec:
        def __init__(self, *a, **k):
            pass

        def compress(self, b, asbytes=True):
            calls["n"] += 1
            if calls["n"] == 2:
                raise OSError(28, "No space left on device")
            return real_codec.compress(b, asbytes=True)
    monkeypatch.setattr(pa, "Codec", FullCodec)
    with pytest.raises(ar.OutOfSpace):
        ar.archive_file(tmp_path, "dom.ndjson")
    assert (tmp_path / "dom.ndjson").read_bytes() == data and not list(tmp_path.glob("*.zst*"))


@pytest.mark.skipif(os.name != "nt", reason="Windows-Dateisperre")
def test_locked_file_is_skipped(tmp_path):
    data = _text()
    raw = tmp_path / "ticks.ndjson"
    raw.write_bytes(data)
    with open(raw, "ab"):
        assert ar.is_locked(raw)
        r = ar.archive_file(tmp_path, raw.name)
    assert r["result"] == "locked" and raw.read_bytes() == data and not list(tmp_path.glob("*.zst*"))
    assert not ar.is_locked(raw)


def test_not_smaller_keeps_original(tmp_path):
    data = os.urandom(50_000)
    (tmp_path / "gaps.ndjson").write_bytes(data)
    assert ar.archive_file(tmp_path, "gaps.ndjson")["result"] == "kept_not_smaller"
    assert (tmp_path / "gaps.ndjson").read_bytes() == data and not list(tmp_path.glob("*.zst*"))


def test_manifest_hash_mismatch_keeps_original(tmp_path):
    data = _text()
    (tmp_path / "ticks.ndjson").write_bytes(data)
    r = ar.archive_file(tmp_path, "ticks.ndjson", expected_sha="0" * 64)
    assert r["result"] == "hash_mismatch_manifest" and (tmp_path / "ticks.ndjson").read_bytes() == data


def test_source_changed_while_reading_keeps_original(tmp_path):
    data = _text()
    raw = tmp_path / "dom.ndjson"
    raw.write_bytes(data)

    def prog(ev):
        if ev.get("event") == "block" and ev["done"] == 4096:
            with open(raw, "ab") as f:
                f.write(b"neu\n")
    r = ar.archive_file(tmp_path, raw.name, progress=prog)
    assert r["result"] == "source_changed" and raw.read_bytes() == data + b"neu\n"
    assert not list(tmp_path.glob("*.zst*"))


def test_restore_is_byte_exact_and_marks_folder(tmp_path):
    files = {"ticks.ndjson": _text(3000, 2), "dom.ndjson": _text(3000, 3), "ticks.csv": b"Time,Price\n1,2\n" * 2000}
    for n, b in files.items():
        (tmp_path / n).write_bytes(b)
        ar.archive_file(tmp_path, n)
    r = ar.restore_recording(tmp_path)
    assert r["ok"]
    for n, b in files.items():
        assert (tmp_path / n).read_bytes() == b and not (tmp_path / (n + ".zst")).exists()
    assert ar.load_archive_manifest(tmp_path).get("restoredAtUtc")


# ---------------------------------------------------------------- Auswahl + Gesamtlauf
def _data_dir(tmp_path, *, drops=0, clean=True, run_meta="R1", run_job="R1", committed=True, coverage="complete"):
    data = tmp_path / "Daten"
    rec = _make_recording(data / "Recordings" / "MNQ_TEST", _job_lines(100.0, 55, 0), run_id=run_meta,
                          clean=clean, drops={"ticks.csv": drops, "ticks.ndjson": drops} if drops else None)
    coll = data / "Collections" / "MNQ_NY"
    c.append_job(coll, rec, "job-1", START, END, session="NY", year=2026, allow_check_status=True)
    m = c.load_manifest(coll)
    j = m["jobs"]["job-1"]
    j["runId"] = run_job
    j["coverageStatus"] = coverage
    if not committed:
        j["status"] = "open"
    c._write_manifest_atomic(coll, m)
    return data, rec, coll


def test_compress_all_archives_only_clean_imported_and_never_touches_collection(tmp_path):
    data, rec, coll = _data_dir(tmp_path)
    (data / "Recordings" / "UNBEKANNT").mkdir()
    (data / "Recordings" / "UNBEKANNT" / "ticks.ndjson").write_bytes(_text())
    before = {p.name: p.read_bytes() for p in coll.iterdir()}
    a = ar.analyze(data)
    assert [e["recording"] for e in a["eligible"]] == ["MNQ_TEST"]
    assert {s["recording"]: s["reason"] for s in a["skipped"]}["UNBEKANNT"].startswith("keine summary")
    r = ar.compress_all(data)
    assert r["status"] == "completed" and r["recordings"][0]["recording"] == "MNQ_TEST"
    assert (rec / "ticks.ndjson.zst").exists() and (data / "Recordings" / "UNBEKANNT" / "ticks.ndjson").exists()
    assert {p.name: p.read_bytes() for p in coll.iterdir()} == before          # Collection unveraendert
    assert ar.analyze(data)["archived"][0]["recording"] == "MNQ_TEST"
    assert ar.compress_all(data)["recordings"] == []                           # erneuter Lauf: nichts mehr zu tun


@pytest.mark.parametrize("kw, reason", [
    ({"drops": 5}, "Recorder-Drops"),
    ({"clean": False}, "summary.clean"),
    ({"run_job": "ANDERS"}, "runId"),
    ({"committed": False}, "nicht committed"),
    ({"coverage": "check"}, "coverageStatus"),
])
def test_faulty_or_unclear_recordings_are_skipped(tmp_path, kw, reason):
    data, rec, _ = _data_dir(tmp_path, **kw)
    ok, why, _ = ar.classify_recording(rec, ar.load_job_refs(data))
    assert not ok and reason in why
    assert ar.compress_all(data)["recordings"] == [] and (rec / "ticks.ndjson").exists()


def test_refuses_while_replay_run_active(tmp_path, monkeypatch):
    data, rec, _ = _data_dir(tmp_path)
    monkeypatch.setattr(ar, "replay_run_active", lambda: True)
    with pytest.raises(ar.ArchiveError):
        ar.compress_all(data)
    assert (rec / "ticks.ndjson").exists()


def test_restored_recording_is_not_recompressed_automatically(tmp_path):
    data, rec, _ = _data_dir(tmp_path)
    ar.compress_all(data)
    assert ar.restore_recording(rec)["ok"]
    ok, why, _ = ar.classify_recording(rec, ar.load_job_refs(data))
    assert not ok and "wiederhergestellt" in why


def test_bridge_parquet_export_identical_raw_vs_zst(tmp_path):
    """atas_bridge.convert (Parquet-Export, Pruefskript-Pfad) liefert aus .zst dieselbe Tabelle und dieselben Hashes."""
    import atas_bridge as b
    rec = _make_recording(tmp_path / "rec", _job_lines(100.0, 30, 0), run_id="R1")
    (rec / "dom.ndjson").write_bytes(_text(500))
    ma, ta = b.convert(rec, START, END, tmp_path / "out_raw")
    for n in ar.compressible_names(rec):
        ar.archive_file(rec, n)
    assert not (rec / "dom.ndjson").exists() and not (rec / "ticks.ndjson").exists()
    mb, tb = b.convert(rec, START, END, tmp_path / "out_zst")
    assert ta.equals(tb)
    assert ma["inputHashes"] == mb["inputHashes"] and ma["reconciliation"] == mb["reconciliation"]
    assert mb["otherStreams"]["dom.ndjson"]["present"] is True


def test_unexpected_file_error_is_reported_and_run_continues(tmp_path, monkeypatch):
    data, rec, _ = _data_dir(tmp_path)
    def boom(*a, **k):
        raise PermissionError(5, "Zugriff verweigert (z. B. Virenscanner)")
    monkeypatch.setattr(ar, "archive_file", boom)
    r = ar.compress_all(data)
    assert r["status"] == "completed" and "PermissionError" in r["recordings"][0]["error"]
    assert (rec / "ticks.ndjson").exists()
