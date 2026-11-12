#!/usr/bin/env python3
"""
ATAS-Sammellauf: viele Aufnahme-Abschnitte -> EINE gemeinsame Trade-CSV je Session+Jahr.

Baut NUR auf der bestehenden Offline-Bruecke (atas_bridge) auf: dieselben verlustfreien
Nanosekunden-Zeitstempel, dieselbe halb-offene [start,end)-Fensterlogik, dieselbe
SHA-256-Stabilitaetspruefung und dieselben konservativen Semantik-Regeln. Dieses Modul
fuegt ausschliesslich das wiederanlauffaehige, atomare ANHAENGEN an eine gemeinsame Datei
hinzu; es erfindet keine Daten und entfernt keine legitimen gleichen Trades.

Leitplanken (Auftrag):
- Pro Session+Jahr GENAU EINE gemeinsame Trade-Datei (trades.csv). Nicht ein Ordner/Tag.
- Jeder Trade bleibt dem Aufnahmeauftrag zuordenbar: session, year, job_id, recording_id,
  run_id, src_seq, recorder_seq, t_market_utc, instrument, contract_or_series, price, volume,
  aggressor_side, exch_order_id, aggr_exch_order_id, open_interest.
- Nur Trade-Ticks (dataType=="Trade"). KEIN DOM/BBO/Bars in dieselbe Tabelle.
- Kontrakt/Serie bleibt 'unknown' (Symbollabel beweist nicht die historische Quellserie).
- Zeitpraezision verlustfrei (ganzzahlige Nanosekunden -> ISO mit .fffffffff).
- Preispraezision verlustfrei: price wird als Decimal (exakte Quellziffern) geparst/geschrieben.
- Wiederanlauf: abgeschlossene Auftraege werden NICHT erneut angehaengt (Auftrags-Dedup ueber
  das Manifest + Eingabe-Hashes). Legitime gleiche Trades (gleiche Zeit/Preis/Menge) bleiben erhalten.
- Unvollstaendiges Anhaengen (Abbruch zwischen Schreiben und Commit) wird erkannt und sicher
  zurueckgesetzt: das Manifest fuehrt den letzten bestaetigten Byte-Stand (committedBytes) als
  Commit-Punkt; ein laengerer File-Rest wird auf diesen Stand abgeschnitten und der Auftrag sauber
  wiederholt -> weder Datenverlust noch doppeltes Anhaengen.
- Originalaufnahmen werden NIE geloescht oder veraendert (nur gelesen).
"""
from __future__ import annotations
import argparse, csv, io, json, os, sys, uuid
from datetime import datetime, timezone
from decimal import Decimal
from pathlib import Path

import atas_bridge as b   # Wiederverwendung der geprueften reinen Helfer (keine Duplizierung)

COLLECTION_SCHEMA = "atas-collection-trades-1"

# Spaltenreihenfolge der gemeinsamen CSV (stabil; Header wird einmalig geschrieben).
CSV_COLUMNS = [
    "session", "year", "job_id", "recording_id", "run_id",
    "src_seq", "recorder_seq", "t_market_utc", "instrument", "contract_or_series",
    "price", "volume", "aggressor_side", "exch_order_id", "aggr_exch_order_id", "open_interest",
]
HEADER_LINE = ",".join(CSV_COLUMNS) + "\r\n"   # csv-Modul schreibt \r\n; Header identisch halten

# Eingaben, deren Stabilitaet (pre==post) waehrend des Lesens geprueft wird (wie Bruecke).
_INPUT_NAMES = ("ticks.ndjson", "ticks.csv", "meta.json", "summary.json")


class CollectionIntegrityError(RuntimeError):
    """Der gemeinsame CSV-Stand widerspricht dem Manifest (committed-Daten fehlen/geschrumpft)."""


class JobConflictError(RuntimeError):
    """job_id ist bereits mit ANDEREM Eingabestand committed -> kein stillschweigendes Ueberschreiben."""


# ---------------------------------------------------------------------------
# Lesen einer Aufnahme -> Fenster-Trades (verlustfreie Zeit + Preis)
# ---------------------------------------------------------------------------

def _iter_trades_ns(ticks_path: Path):
    """Streamt (file_idx, obj) der ticks.ndjson; price als Decimal (exakte Quellziffern)."""
    with open(ticks_path, "r", encoding="utf-8") as f:
        for i, line in enumerate(f):
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line, parse_float=Decimal)
            except json.JSONDecodeError:
                yield i, None
                continue
            yield i, obj


def read_window_trades(recording_dir, start, end):
    """Liest die Trade-Ticks einer Aufnahme im halb-offenen Fenster [start,end) (UTC).

    Rueckgabe: (rows, stats, info) mit
      rows  = Liste von dicts in stabiler Quellreihenfolge (Dateireihenfolge),
      stats = Zaehler/Volumina/Reconciliation,
      info  = Quellmetadaten (recording_id, run_id, instrument, inputHashes, stabil).
    Bricht ab (SourceChangedError), wenn sich eine Eingabe waehrend des Lesens aendert.
    """
    recording_dir = Path(recording_dir)
    start_ns = b.parse_iso_ns(start) if isinstance(start, str) else int(start)
    end_ns = b.parse_iso_ns(end) if isinstance(end, str) else int(end)
    if not (start_ns < end_ns):
        raise ValueError("start muss < end sein (halb-offen [start,end))")

    ticks_path = recording_dir / "ticks.ndjson"
    if not ticks_path.exists():
        raise FileNotFoundError(f"ticks.ndjson fehlt in {recording_dir}")
    meta_path = recording_dir / "meta.json"
    summary_path = recording_dir / "summary.json"
    ticks_csv = recording_dir / "ticks.csv"

    pre = {n: b.sha256_file(recording_dir / n) for n in _INPUT_NAMES if (recording_dir / n).exists()}

    meta = json.loads(meta_path.read_text(encoding="utf-8")) if meta_path.exists() else {}
    summary = json.loads(summary_path.read_text(encoding="utf-8")) if summary_path.exists() else {}
    recording_id = recording_dir.name
    run_id = meta.get("runId") or ""
    instrument = meta.get("instrument") or "unknown"

    rows = []
    n_total = n_window = n_before = n_after = n_bad = n_nontrade = n_lines = 0
    tot = buy = sell = 0
    first_ns = last_ns = None

    for file_idx, obj in _iter_trades_ns(ticks_path):
        if obj is None:
            n_bad += 1
            continue
        n_lines += 1
        if obj.get("dataType") != "Trade":
            n_nontrade += 1
            continue
        n_total += 1
        tm_ns = b.parse_iso_ns(obj["tMarket"])
        if tm_ns < start_ns:
            n_before += 1
            continue
        if tm_ns >= end_ns:
            n_after += 1
            continue
        n_window += 1
        if first_ns is None:
            first_ns = tm_ns
        last_ns = tm_ns
        vol = int(obj.get("volume") or 0)
        aggr = b.aggressor_from(obj.get("direction"))
        price = obj.get("price")
        price_str = format(price, "f") if isinstance(price, Decimal) else str(price)
        tot += vol
        if aggr == "Buy":
            buy += vol
        elif aggr == "Sell":
            sell += vol
        rows.append({
            "src_seq": file_idx,
            "recorder_seq": int(obj.get("seq") or 0),
            "t_market_ns": tm_ns,
            "t_market_utc": b.ns_to_iso(tm_ns),
            "instrument": instrument,
            "contract_or_series": "unknown",
            "price": price_str,
            "volume": vol,
            "aggressor_side": aggr,
            "exch_order_id": obj.get("exchOrderId"),
            "aggr_exch_order_id": obj.get("aggrExchOrderId"),
            "open_interest": int(obj.get("oi") or 0),
        })

    csv_rows = b.csv_data_rows(ticks_csv)

    post = {n: b.sha256_file(recording_dir / n) for n in _INPUT_NAMES if (recording_dir / n).exists()}
    if post != pre:
        changed = sorted(k for k in set(pre) | set(post) if pre.get(k) != post.get(k))
        raise b.SourceChangedError(
            f"Quelle hat sich waehrend des Lesens geaendert ({', '.join(changed)}); kein verlaesslicher Snapshot.")

    summ_ticks = ((summary.get("perStream") or {}).get("ticks.ndjson") or {}).get("events")
    summ_clean = summary.get("clean")
    stats = {
        "rows": n_window, "tradesTotalInFile": n_total,
        "restTicksBefore": n_before, "restTicksAfter": n_after,
        "badJsonLines": n_bad, "nonTradeEventsInTicksStream": n_nontrade,
        "totalVolume": tot, "buyVolume": buy, "sellVolume": sell, "unknownVolume": tot - buy - sell,
        "firstTradeInWindowUtc": b.ns_to_iso(first_ns), "lastTradeInWindowUtc": b.ns_to_iso(last_ns),
        "reconciliation": {
            "ndjson_lines_total": n_lines, "ndjson_trade_events_total": n_total,
            "csv_data_rows": csv_rows, "summary_ticks_events": summ_ticks,
            "csv_eq_trades": (csv_rows == n_total) if csv_rows >= 0 else None,
            "summary_eq_ndjson_lines": (summ_ticks == n_lines) if summ_ticks is not None else None,
            "summary_clean": summ_clean,
        },
    }
    # Status wie Bruecke: Diskrepanzen sichtbar machen, nicht glaetten.
    reasons = []
    if n_bad > 0:
        reasons.append("bad_json_lines")
    if summ_clean is not True:
        reasons.append("summary_not_clean_or_missing")
    if stats["reconciliation"]["csv_eq_trades"] is False:
        reasons.append("csv_trade_count_discrepancy")
    if stats["reconciliation"]["summary_eq_ndjson_lines"] is False:
        reasons.append("summary_ndjson_line_discrepancy")
    status = "empty_window" if n_window == 0 else ("check" if reasons else "complete")
    stats["status"] = status
    stats["statusReasons"] = reasons

    info = {
        "recording_id": recording_id, "run_id": run_id, "instrument": instrument,
        "recordingDir": str(recording_dir), "inputHashes": pre,
        "recorder": meta.get("recorder"),
        "window": {"start": b.ns_to_iso(start_ns), "end": b.ns_to_iso(end_ns),
                   "startNs": start_ns, "endNs": end_ns, "interval": "[start, end)"},
    }
    return rows, stats, info


def _rows_to_bytes(session, year, job_id, run_id, recording_id, rows):
    """Serialisiert Rows deterministisch als CSV-Textbytes (ohne Header)."""
    buf = io.StringIO()
    w = csv.writer(buf)
    for r in rows:
        w.writerow([
            session, year, job_id, recording_id, run_id,
            r["src_seq"], r["recorder_seq"], r["t_market_utc"], r["instrument"], r["contract_or_series"],
            r["price"], r["volume"], r["aggressor_side"],
            "" if r["exch_order_id"] is None else r["exch_order_id"],
            "" if r["aggr_exch_order_id"] is None else r["aggr_exch_order_id"],
            r["open_interest"],
        ])
    return buf.getvalue().encode("utf-8")


# ---------------------------------------------------------------------------
# Gemeinsame Sammeldatei: Manifest + atomares, wiederanlauffaehiges Anhaengen
# ---------------------------------------------------------------------------

def _manifest_path(collection_dir: Path) -> Path:
    return collection_dir / "collection_manifest.json"


def _csv_path(collection_dir: Path) -> Path:
    return collection_dir / "trades.csv"


def load_manifest(collection_dir, session=None, year=None, timezone_name=None):
    collection_dir = Path(collection_dir)
    mp = _manifest_path(collection_dir)
    if mp.exists():
        return json.loads(mp.read_text(encoding="utf-8"))
    return {
        "schemaVersion": COLLECTION_SCHEMA,
        "session": session, "year": year, "timezone": timezone_name,
        "csv": {"path": str(_csv_path(collection_dir)), "committedBytes": 0,
                "committedRows": 0, "header": HEADER_LINE},
        "jobs": {},
        "totals": {"rows": 0, "totalVolume": 0, "buyVolume": 0, "sellVolume": 0, "unknownVolume": 0},
        "updatedAtUtc": None,
        "semantics": {
            "duplicates": "gleiche Zeit/Preis/Menge sind KEIN Duplikatnachweis; Reihenfolge bleibt erhalten.",
            "dedup": "Dedup erfolgt auf Auftragsebene (job_id + Eingabe-Hashes), nicht ueber Trade-Inhalte.",
            "timePrecision": "t_market_utc verlustfrei (ganzzahlige Nanosekunden -> ISO .fffffffff).",
            "contractOrSeries": "unknown; Symbollabel beweist nicht die historische Quellserie.",
            "committedBytes": "letzter bestaetigter CSV-Byte-Stand = Commit-Punkt fuer Wiederanlauf.",
        },
    }


def _write_manifest_atomic(collection_dir: Path, manifest: dict):
    manifest["updatedAtUtc"] = datetime.now(timezone.utc).isoformat()
    mp = _manifest_path(collection_dir)
    tmp = collection_dir / (".manifest_" + uuid.uuid4().hex + ".tmp")
    data = json.dumps(manifest, indent=2, ensure_ascii=False).encode("utf-8")
    with open(tmp, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, mp)   # atomarer Commit-Punkt


def recover(collection_dir, manifest=None):
    """Setzt einen unvollstaendigen Anhaenge-Vorgang sicher zurueck.

    committedBytes im Manifest ist der letzte bestaetigte Stand. Ist die CSV laenger, wurde nach
    dem letzten Commit geschrieben, aber der Commit (Manifest-Update) kam nicht zustande -> der
    Rest wird abgeschnitten. Ist die CSV kuerzer/fehlt sie trotz committedBytes>0 -> Integritaetsfehler
    (committed-Daten duerfen nie verloren gehen; wir loeschen/kuerzen Originale nicht).
    Rueckgabe: dict mit {action, truncatedBytes}.
    """
    collection_dir = Path(collection_dir)
    m = manifest if manifest is not None else load_manifest(collection_dir)
    committed = int(m["csv"]["committedBytes"])
    csvp = _csv_path(collection_dir)
    if not csvp.exists():
        if committed > 0:
            raise CollectionIntegrityError(
                f"CSV fehlt, Manifest erwartet {committed} committed Bytes -> committed-Daten verloren.")
        return {"action": "noop", "truncatedBytes": 0}
    size = csvp.stat().st_size
    if size < committed:
        raise CollectionIntegrityError(
            f"CSV ({size} B) kuerzer als committedBytes ({committed}) -> committed-Daten geschrumpft.")
    if size > committed:
        with open(csvp, "r+b") as f:
            f.truncate(committed)
            f.flush()
            os.fsync(f.fileno())
        return {"action": "truncated", "truncatedBytes": size - committed}
    return {"action": "noop", "truncatedBytes": 0}


def append_job(collection_dir, recording_dir, job_id, start, end,
               session=None, year=None, timezone_name=None, allow_check_status=False):
    """Haengt die Fenster-Trades EINER Aufnahme atomar und wiederanlauffaehig an die Sammel-CSV an.

    Idempotent: ein bereits committeter job_id mit identischem Eingabestand wird uebersprungen.
    Rueckgabe: dict {result, rowsAppended, ...}. result in {committed, skipped, conflict, empty, check}.
    """
    collection_dir = Path(collection_dir)
    collection_dir.mkdir(parents=True, exist_ok=True)
    m = load_manifest(collection_dir, session, year, timezone_name)

    # 1) IMMER zuerst einen etwaigen unvollstaendigen Rest zuruecksetzen (sicherer Wiederanlauf).
    rec = recover(collection_dir, m)

    rows, stats, info = read_window_trades(recording_dir, start, end)

    # 2) Auftrags-Dedup: bereits committed?
    prev = m["jobs"].get(job_id)
    if prev and prev.get("status") == "committed":
        if prev.get("inputHashes") == info["inputHashes"]:
            return {"result": "skipped", "jobId": job_id, "rowsAppended": 0,
                    "recovered": rec, "committedRows": m["csv"]["committedRows"]}
        raise JobConflictError(
            f"job_id {job_id!r} ist bereits mit anderem Eingabestand committed "
            f"(recording {prev.get('recordingId')} -> jetzt {info['recording_id']}).")

    if stats["status"] == "empty_window":
        # Leeres Fenster wird als Auftrag vermerkt (nachvollziehbarer Status), aber keine Zeilen angehaengt.
        m["jobs"][job_id] = {
            "status": "committed", "rowsAppended": 0, "window": info["window"],
            "recordingId": info["recording_id"], "runId": info["run_id"],
            "recordingDir": info["recordingDir"], "inputHashes": info["inputHashes"],
            "trades": {"rows": 0, "totalVolume": 0, "buyVolume": 0, "sellVolume": 0, "unknownVolume": 0},
            "coverageStatus": "empty_window", "statusReasons": stats["statusReasons"],
            "byteOffsetBefore": m["csv"]["committedBytes"], "byteOffsetAfter": m["csv"]["committedBytes"],
            "appendedAtUtc": datetime.now(timezone.utc).isoformat(),
        }
        _write_manifest_atomic(collection_dir, m)
        return {"result": "empty", "jobId": job_id, "rowsAppended": 0, "recovered": rec}

    if stats["status"] == "check" and not allow_check_status:
        return {"result": "check", "jobId": job_id, "rowsAppended": 0,
                "statusReasons": stats["statusReasons"], "recovered": rec,
                "note": "Abschnitt NICHT uebernommen (Diskrepanz). Mit allow_check_status erzwingbar."}

    # 3) Atomar anhaengen + committen.
    csvp = _csv_path(collection_dir)
    before = int(m["csv"]["committedBytes"])
    payload = _rows_to_bytes(session if session is not None else m.get("session"),
                             year if year is not None else m.get("year"),
                             job_id, info["run_id"], info["recording_id"], rows)

    with open(csvp, "ab") as f:
        if before == 0 and f.tell() == 0:
            f.write(HEADER_LINE.encode("utf-8"))
        f.write(payload)
        f.flush()
        os.fsync(f.fileno())
    after = csvp.stat().st_size

    m["csv"]["committedBytes"] = after
    m["csv"]["committedRows"] = int(m["csv"]["committedRows"]) + len(rows)
    m["jobs"][job_id] = {
        "status": "committed", "rowsAppended": len(rows), "window": info["window"],
        "recordingId": info["recording_id"], "runId": info["run_id"],
        "recordingDir": info["recordingDir"], "inputHashes": info["inputHashes"],
        "recorder": info["recorder"],
        "trades": {"rows": stats["rows"], "totalVolume": stats["totalVolume"],
                   "buyVolume": stats["buyVolume"], "sellVolume": stats["sellVolume"],
                   "unknownVolume": stats["unknownVolume"]},
        "coverageStatus": stats["status"], "statusReasons": stats["statusReasons"],
        "coverage": {"restTicksBefore": stats["restTicksBefore"], "restTicksAfter": stats["restTicksAfter"],
                     "firstTradeInWindowUtc": stats["firstTradeInWindowUtc"],
                     "lastTradeInWindowUtc": stats["lastTradeInWindowUtc"]},
        "reconciliation": stats["reconciliation"],
        "byteOffsetBefore": before, "byteOffsetAfter": after,
        "appendedAtUtc": datetime.now(timezone.utc).isoformat(),
    }
    t = m["totals"]
    t["rows"] += stats["rows"]; t["totalVolume"] += stats["totalVolume"]
    t["buyVolume"] += stats["buyVolume"]; t["sellVolume"] += stats["sellVolume"]
    t["unknownVolume"] += stats["unknownVolume"]
    _write_manifest_atomic(collection_dir, m)   # <-- Commit-Punkt

    return {"result": "committed", "jobId": job_id, "rowsAppended": len(rows),
            "trades": m["jobs"][job_id]["trades"], "status": stats["status"],
            "recovered": rec, "committedRows": m["csv"]["committedRows"]}


# ---------------------------------------------------------------------------
# 1x vs 100x Vergleich (aufnahmeunabhaengiger Inhalt + Reihenfolge)
# ---------------------------------------------------------------------------

def _comparable(rows):
    """Aufnahmeunabhaengiger Trade-Schluessel: t_market, price, volume, aggressor, exch-ids, oi.
    Ausgeschlossen: t_recv, run_id, recording_id, recorder_seq (aufnahmeabhaengig). src_seq = Position."""
    return [(r["t_market_ns"], r["price"], r["volume"], r["aggressor_side"],
             r["exch_order_id"], r["aggr_exch_order_id"], r["open_interest"]) for r in rows]


def compare_recordings(recording_a, recording_b, start, end, max_diffs=10):
    """Vergleicht zwei Aufnahmen desselben Fensters inhaltlich + in Reihenfolge.

    Gibt ein dict zurueck mit Zaehlern/Volumina beider Seiten, equal-Flags und (falls ungleich)
    den ersten abweichenden Positionen. Verliert KEINE Unterschiede und behauptet 100x nicht
    pauschal als verlustfrei.
    """
    ra, sa, ia = read_window_trades(recording_a, start, end)
    rb, sb, ib = read_window_trades(recording_b, start, end)
    ca, cb = _comparable(ra), _comparable(rb)

    content_equal = (ca == cb)
    diffs = []
    if not content_equal:
        n = min(len(ca), len(cb))
        for i in range(n):
            if ca[i] != cb[i]:
                diffs.append({"index": i, "a": _fmt_cmp(ca[i]), "b": _fmt_cmp(cb[i])})
                if len(diffs) >= max_diffs:
                    break
        if len(diffs) < max_diffs and len(ca) != len(cb):
            diffs.append({"index": n, "a": f"<{len(ca)} rows total>", "b": f"<{len(cb)} rows total>"})

    def vol(s):
        return {"rows": s["rows"], "totalVolume": s["totalVolume"], "buyVolume": s["buyVolume"],
                "sellVolume": s["sellVolume"], "unknownVolume": s["unknownVolume"]}

    va, vb = vol(sa), vol(sb)
    return {
        "window": ia["window"],
        "a": {"recordingId": ia["recording_id"], "runId": ia["run_id"], **va, "status": sa["status"]},
        "b": {"recordingId": ib["recording_id"], "runId": ib["run_id"], **vb, "status": sb["status"]},
        "equal": {
            "rows": va["rows"] == vb["rows"],
            "totalVolume": va["totalVolume"] == vb["totalVolume"],
            "buyVolume": va["buyVolume"] == vb["buyVolume"],
            "sellVolume": va["sellVolume"] == vb["sellVolume"],
            "unknownVolume": va["unknownVolume"] == vb["unknownVolume"],
            "contentAndOrder": content_equal,
        },
        "lossless": content_equal and va == vb,
        "firstDiffs": diffs,
        "excludedFromComparison": ["t_recv_utc", "run_id", "recording_id", "recorder_seq"],
    }


def _fmt_cmp(t):
    return {"t_market_utc": b.ns_to_iso(t[0]), "price": t[1], "volume": t[2], "aggressor_side": t[3],
            "exch_order_id": t[4], "aggr_exch_order_id": t[5], "open_interest": t[6]}


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main(argv=None):
    ap = argparse.ArgumentParser(description="ATAS-Sammellauf: Abschnitt -> gemeinsame Trade-CSV (append/status/compare/recover)")
    sub = ap.add_subparsers(dest="cmd", required=True)

    pa = sub.add_parser("append", help="einen Aufnahme-Abschnitt an die gemeinsame CSV anhaengen")
    pa.add_argument("--collection", required=True, help="Sammelordner, z.B. F:\\ATAS_Data\\Collections\\London_2026")
    pa.add_argument("--recording", required=True, help="Aufnahmeordner (ticks.ndjson, ...)")
    pa.add_argument("--job-id", required=True)
    pa.add_argument("--start", required=True, help="Fensterstart UTC ISO")
    pa.add_argument("--end", required=True, help="Fensterende UTC ISO (exklusiv)")
    pa.add_argument("--session", default=None)
    pa.add_argument("--year", type=int, default=None)
    pa.add_argument("--timezone", dest="tz", default=None)
    pa.add_argument("--allow-check", action="store_true", help="Abschnitt trotz Diskrepanz-Status uebernehmen")

    ps = sub.add_parser("status", help="Manifest-Zusammenfassung ausgeben")
    ps.add_argument("--collection", required=True)

    pr = sub.add_parser("recover", help="unvollstaendiges Anhaengen sicher zuruecksetzen")
    pr.add_argument("--collection", required=True)

    pc = sub.add_parser("compare", help="zwei Aufnahmen desselben Fensters vergleichen (z.B. 1x vs 100x)")
    pc.add_argument("--a", required=True)
    pc.add_argument("--b", required=True)
    pc.add_argument("--start", required=True)
    pc.add_argument("--end", required=True)

    a = ap.parse_args(argv)
    if a.cmd == "append":
        r = append_job(a.collection, a.recording, a.job_id, a.start, a.end,
                       session=a.session, year=a.year, timezone_name=a.tz, allow_check_status=a.allow_check)
        print(json.dumps(r, ensure_ascii=False))
        return 0 if r["result"] in ("committed", "skipped", "empty") else 2
    if a.cmd == "status":
        m = load_manifest(a.collection)
        print(json.dumps({"session": m.get("session"), "year": m.get("year"),
                          "jobs": {k: v.get("status") for k, v in m["jobs"].items()},
                          "csv": m["csv"], "totals": m["totals"]}, ensure_ascii=False, indent=2))
        return 0
    if a.cmd == "recover":
        print(json.dumps(recover(a.collection), ensure_ascii=False))
        return 0
    if a.cmd == "compare":
        r = compare_recordings(a.a, a.b, a.start, a.end)
        print(json.dumps(r, ensure_ascii=False, indent=2))
        return 0 if r["lossless"] else 3
    return 1


if __name__ == "__main__":
    sys.exit(main())
