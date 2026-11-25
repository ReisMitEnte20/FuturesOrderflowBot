"""Datenqualitäts- und Herkunftsprüfung der ATAS-Aufnahmen für die Orderflow-Strategiesuche (NUR LESEND).

Liest Collections (collection_manifest.json, run_protocol.txt, trades.csv), Aufnahmeordner (meta.json, summary.json,
archive.json und die Streams roh oder als .zst) und optional die ATAS-Protokolle. Erzeugt eine Tabelle mit einer Zeile
je Handelstag, Session und Aufnahme sowie eine Auswertung, wie viele Sessions für welche Vergleichsstufe nutzbar sind:

  A  Trades, Delta und VWAP ab Aufnahmebeginn
  B  zusätzlich kumuliertes Tape (cumtrades)
  C  zusätzlich historisches Bid/Ask (BBO)
  D  zusätzlich historisches DOM (Markttiefe)

Grundsätze:
- Eine vorhandene Datei oder null Drops belegt KEINE historische Herkunft. Herkunft wird nur aus aufnahmebezogenen
  Belegen abgeleitet: (1) Replay-Typ im Sammelprotokoll des Auftrags, dem genau diese Aufnahme zugeordnet ist, (2)
  Signaturen in den Daten selbst (gleichverteilte Tick-Zeitstempel je Minute, DOM mit nur einem Level je Seite).
  Ohne Beleg: „ungeklärt“.
- Kontrakt wird nie aus dem Datum erraten; „unknown“ bleibt „unknown“.
- Überlappende Collections zählen nicht doppelt (Schlüssel = Handelstag + Session); London und New York am selben Tag
  sind zwei Sessions, keine Duplikate.
- Ausgabe (Tabelle, Bericht, JSON) nur lokal; Marktdaten werden nicht kopiert.

Start:
  python atas_data_audit.py --collections-root <...\\Collections> [--collections-root ...] \\
      --recordings-root <...\\Recordings> [--recordings-root ...] [--atas-logs <%APPDATA%\\ATAS\\Logs>] --out <Ordner>
"""
from __future__ import annotations

import argparse
import collections
import concurrent.futures as cf
import csv
import itertools
import json
import re
import sys
from datetime import date, datetime, timedelta, timezone
from decimal import Decimal
from pathlib import Path

import atas_archive as ar

STREAMS = ("ticks.csv", "ticks.ndjson", "cumtrades.ndjson", "bbo.ndjson", "dom.ndjson", "bars.ndjson")
GROUPS = {"Trades": ("ticks.csv", "ticks.ndjson"), "Tape": ("cumtrades.ndjson",), "BBO": ("bbo.ndjson",), "DOM": ("dom.ndjson",)}
LEVELS = (("A", "Trades"), ("B", "Tape"), ("C", "BBO"), ("D", "DOM"))
SESSIONS = ("London", "NewYork")
# Standard-Sessionfenster (Ortszeit) – nur für Aufnahmen OHNE Auftrag: Session aus dem ersten Tick ableiten.
SESSION_WINDOWS = {"London": ("Europe/London", (8, 0), (11, 0)), "NewYork": ("America/New_York", (9, 30), (12, 0))}

# Herkunftsklassen je Stream
HIST = "historisch"
MINUTE = "Minutenaggregat (Ablauf innerhalb der Minute generiert)"
GEN = "generiert"
UNK = "ungeklärt"

# Was ein Replay-Typ für die Streams bedeutet (nur angewandt, wenn der Typ für GENAU diese Aufnahme belegt ist).
REPLAY_TYPE_PROVENANCE = {
    "CandlesAndGeneratedBestPrices": {"Trades": MINUTE, "Tape": GEN, "BBO": GEN, "DOM": GEN},
    "TradesAndDepths": {"Trades": HIST, "Tape": HIST, "BBO": HIST, "DOM": HIST},
}

JOB_ID_RE = re.compile(r"^(?P<session>[A-Za-z]+)_(?P<year>\d{4})_(?P<s>\d{8}T\d{6}Z)_(?P<e>\d{8}T\d{6}Z)$")
REC_ID_RE = re.compile(r"MNQ_\d{8}_\d{6}Z|[A-Z0-9]+_\d{8}_\d{6}Z")


# ---------------------------------------------------------------------------------------------------------------
# Hilfen
# ---------------------------------------------------------------------------------------------------------------

def parse_job_id(job_id: str):
    m = JOB_ID_RE.match(job_id or "")
    if not m:
        return None
    f = "%Y%m%dT%H%M%SZ"
    s = datetime.strptime(m["s"], f).replace(tzinfo=timezone.utc)
    e = datetime.strptime(m["e"], f).replace(tzinfo=timezone.utc)
    return m["session"], s, e


def iso_to_dt(s: str | None):
    """ISO-Zeit (bis 9 Nachkommastellen, 'Z') -> datetime (Mikrosekunden genügen für Fensterprüfungen)."""
    if not s:
        return None
    s = s.strip().replace("Z", "")
    if "." in s:
        head, frac = s.split(".", 1)
        s = head + "." + (frac + "000000")[:6]
    try:
        return datetime.fromisoformat(s).replace(tzinfo=timezone.utc)
    except ValueError:
        return None


def _load_json(p: Path):
    try:
        return json.loads(p.read_text(encoding="utf-8")), None
    except FileNotFoundError:
        return None, "fehlt"
    except (OSError, ValueError) as e:
        return None, f"unlesbar ({type(e).__name__})"


# ---------------------------------------------------------------------------------------------------------------
# Sammelprotokoll: Aufnahme -> Auftrag, Replay-Typ (aufnahmebezogener Beleg)
# ---------------------------------------------------------------------------------------------------------------

PROTO_JOB_RE = re.compile(r"^===== Fenster-Auftrag \d+/\d+: (\S+) =====")
PROTO_REC_RE = re.compile(r"Zugeordnete Instanz: (\S+)")
PROTO_TYPE_RE = re.compile(r"Ladebereich laut ATAS: \[(\S+ \S+)Z, (\S+ \S+)Z\].*? type (\w+)\.")
PROTO_RESULT_RE = re.compile(r"===== ERGEBNIS Auftrag .*?: (\S+)")


def parse_protocol(path: Path) -> dict:
    """recording_id -> {job_id, replay_type, type_window, result, line, protocol}. Der Replay-Typ wird nur der Aufnahme
    zugeordnet, die im SELBEN Auftragsblock zuletzt vor der Typzeile als 'Zugeordnete Instanz' genannt wurde."""
    out: dict = {}
    if not path.is_file():
        return out
    job = rec = None
    with open(path, encoding="utf-8", errors="replace") as f:
        for no, line in enumerate(f, 1):
            m = PROTO_JOB_RE.match(line)
            if m:
                job, rec = m.group(1), None
                continue
            m = PROTO_REC_RE.search(line)
            if m and job:
                rec = m.group(1)
                out.setdefault(rec, {"job_id": job, "replay_type": None, "type_window": None, "result": None,
                                     "line": no, "protocol": str(path)})
                continue
            m = PROTO_TYPE_RE.search(line)
            if m and rec and rec in out and out[rec]["replay_type"] is None:
                out[rec].update(replay_type=m.group(3), type_window=(m.group(1), m.group(2)), line=no)
                continue
            m = PROTO_RESULT_RE.search(line)
            if m and rec and rec in out:
                out[rec]["result"] = m.group(1)
    return out


ATAS_LOG_RE = re.compile(r"Replay DS\s*: Playing from (\d\d\.\d\d\.\d{4} \d\d:\d\d:\d\d) to (\d\d\.\d\d\.\d{4} \d\d:\d\d:\d\d) type (\w+)\.")


def parse_atas_logs(log_dir: Path | None) -> dict:
    """(start, end) im ATAS-Format -> Counter(Replay-Typ). Nur Kontext (fensterbezogen, nicht aufnahmebezogen)."""
    out: dict = collections.defaultdict(collections.Counter)
    if not log_dir or not Path(log_dir).is_dir():
        return out
    for p in sorted(Path(log_dir).glob("*.log")):
        try:
            with open(p, encoding="utf-8", errors="replace") as f:
                for line in f:
                    m = ATAS_LOG_RE.search(line)
                    if m:
                        out[(m.group(1), m.group(2))][m.group(3)] += 1
        except OSError:
            continue
    return out


def _atas_window_key(start: datetime, end: datetime):
    return (start.strftime("%d.%m.%Y %H:%M:%S"), end.strftime("%d.%m.%Y %H:%M:%S"))


# ---------------------------------------------------------------------------------------------------------------
# Aufnahme prüfen (Stichprobe je Stream; nur lesend)
# ---------------------------------------------------------------------------------------------------------------

def _first_time(stream: str, line: str):
    if stream == "ticks.csv":
        return line.split(",", 1)[0]
    try:
        return json.loads(line).get("tMarket")
    except ValueError:
        return None


def _minute_uniformity(rec_dir: Path, minutes: int):
    """Prüft die ersten vollständigen Minuten von ticks.csv: liegen die Ticks exakt gleichverteilt (Abstand 60 s / n)?
    Rückgabe (geprüfte Minuten, davon gleichverteilt) oder (0, 0)."""
    p = rec_dir / "ticks.csv"
    if not ar.exists(p):
        return 0, 0
    by_min: dict = collections.OrderedDict()
    try:
        with ar.open_text(p) as f:
            header = f.readline()
            if not header.lower().startswith("time"):
                return 0, 0
            for line in f:
                t = line.split(",", 1)[0]
                m = t[:16]
                if m not in by_min:
                    if len(by_min) >= minutes + 1:
                        break
                    by_min[m] = []
                try:
                    by_min[m].append(Decimal(t[17:].rstrip("Z") or "0"))
                except ArithmeticError:
                    return 0, 0
    except (OSError, ValueError, UnicodeDecodeError):
        return 0, 0
    keys = list(by_min)[: minutes + 1]
    if len(keys) > minutes:          # letzte (evtl. unvollständige) Minute verwerfen
        keys = keys[:minutes]
    checked = uniform = 0
    for k in keys:
        ts = by_min[k]
        if len(ts) < 20:
            continue
        checked += 1
        gaps = [ts[i + 1] - ts[i] for i in range(len(ts) - 1)]
        common, cnt = collections.Counter(gaps).most_common(1)[0]
        expected = Decimal(60) / len(ts)
        if cnt >= 0.98 * len(gaps) and abs(common - expected) <= Decimal("0.000001"):
            uniform += 1
    return checked, uniform


def inspect_recording(rec_dir, window=None, sample_events: int = 20000, sample_minutes: int = 2) -> dict:
    """Prüft eine Aufnahme. window = (start, end) als UTC-datetime oder None."""
    rec_dir = Path(rec_dir)
    info: dict = {"recording_id": rec_dir.name, "dir": str(rec_dir), "streams": {}, "notes": []}
    meta, meta_err = _load_json(rec_dir / "meta.json")
    summary, sum_err = _load_json(rec_dir / "summary.json")
    info["meta"] = {"instrument": (meta or {}).get("instrument"), "runId": (meta or {}).get("runId"),
                    "startWallUtc": (meta or {}).get("startWallUtc"), "recorder": (meta or {}).get("recorder"), "error": meta_err}
    info["summary"] = {"clean": (summary or {}).get("clean"), "totalDrops": (summary or {}).get("totalDrops"),
                       "runId": (summary or {}).get("runId"), "writerFaulted": (summary or {}).get("writerFaulted"),
                       "error": sum_err}
    per = (summary or {}).get("perStream") or {}
    for s in STREAMS:
        st: dict = {"present": False, "file": None, "readable": None, "sampled": 0, "first_t": None, "window_ok": None,
                    "summary_events": (per.get(s) or {}).get("events"), "drops": (per.get(s) or {}).get("drops"), "content": {}}
        p = rec_dir / s
        r = ar.resolve(p)
        if r is None:
            info["streams"][s] = st
            continue
        st["present"], st["file"] = True, r.name
        try:
            with ar.open_text(p) as f:
                if s == "ticks.csv":
                    f.readline()  # Kopfzeile
                content = collections.Counter()
                book = {"Bid": set(), "Ask": set()}
                max_lv = {"Bid": 0, "Ask": 0}
                for line in itertools.islice(f, sample_events):
                    line = line.strip()
                    if not line:
                        continue
                    st["sampled"] += 1
                    if st["first_t"] is None:
                        st["first_t"] = _first_time(s, line)
                    if s == "ticks.csv":
                        content["aggr:" + line.rsplit(",", 1)[-1]] += 1
                        continue
                    try:
                        e = json.loads(line)
                    except ValueError:
                        content["bad_json"] += 1
                        continue
                    if s == "ticks.ndjson":
                        content["aggr:" + str(e.get("direction"))] += 1
                    elif s == "cumtrades.ndjson":
                        content["event:" + str(e.get("event"))] += 1
                    elif s == "bbo.ndjson":
                        content["side:" + str(e.get("side"))] += 1
                    elif s == "dom.ndjson":
                        side = e.get("side")
                        content["side:" + str(side)] += 1
                        if side in book:
                            (book[side].discard if not e.get("volume") else book[side].add)(e.get("price"))
                            max_lv[side] = max(max_lv[side], len(book[side]))
                    elif s == "bars.ndjson":
                        content["bars"] += 1
                if s == "dom.ndjson":
                    content["max_levels_bid"], content["max_levels_ask"] = max_lv["Bid"], max_lv["Ask"]
                st["content"] = dict(content)
            st["readable"] = True
        except Exception as e:  # noqa: BLE001 – jede Lesestörung ist ein Befund, kein Abbruch der Prüfung
            st["readable"] = False
            info["notes"].append(f"{s}: nicht lesbar ({type(e).__name__}: {e})"[:200])
        if window and st["first_t"] and s != "bars.ndjson":
            t = iso_to_dt(st["first_t"])
            st["window_ok"] = t is not None and window[0] - timedelta(seconds=1) <= t < window[1]
        info["streams"][s] = st
    checked, uniform = _minute_uniformity(rec_dir, sample_minutes)
    info["uniform_minutes"] = {"checked": checked, "uniform": uniform}
    return info


# ---------------------------------------------------------------------------------------------------------------
# Collections
# ---------------------------------------------------------------------------------------------------------------

def _contract_from_csv(csv_path: Path, offset: int | None):
    """contract_or_series der ersten Zeile des Auftrags (Seek auf byteOffsetBefore) – nichts wird erraten."""
    if offset is None or not csv_path.is_file():
        return None, "trades.csv nicht lesbar"
    try:
        with open(csv_path, "rb") as f:
            header = f.readline().decode("utf-8").strip().split(",")
            if "contract_or_series" not in header:
                return None, "Spalte contract_or_series fehlt"
            f.seek(max(offset, f.tell()))      # Offset 0 = Kopfzeile: erste Datenzeile danach lesen
            row = f.readline().decode("utf-8").strip().split(",")
            return row[header.index("contract_or_series")], f"trades.csv contract_or_series @ Byte {offset}"
    except (OSError, ValueError, UnicodeDecodeError, IndexError) as e:
        return None, f"trades.csv nicht lesbar ({type(e).__name__})"


def load_collections(roots) -> tuple[list, list]:
    """Liefert (Collections mit Manifest, Ordner ohne Manifest)."""
    cols, empty = [], []
    for root in roots:
        root = Path(root)
        if not root.is_dir():
            continue
        for d in sorted(p for p in root.iterdir() if p.is_dir()):
            man_p = d / "collection_manifest.json"
            if not man_p.is_file():
                if (d / "run_protocol.txt").is_file() or (d / "coverage_report.txt").is_file():
                    empty.append({"collection": d.name, "dir": str(d), "reason": "kein collection_manifest.json / keine trades.csv – kein Datenbestand"})
                continue
            man, err = _load_json(man_p)
            if man is None:
                empty.append({"collection": d.name, "dir": str(d), "reason": f"Manifest {err}"})
                continue
            cols.append({"collection": d.name, "dir": d, "manifest": man, "protocol": parse_protocol(d / "run_protocol.txt")})
    return cols, empty


# ---------------------------------------------------------------------------------------------------------------
# Bewertung
# ---------------------------------------------------------------------------------------------------------------

def _stream_quality(info: dict, stream: str) -> tuple[str, str | None]:
    st = info["streams"].get(stream) or {}
    if not st.get("present"):
        return "fehlt", "Datei fehlt"
    if st.get("readable") is False:
        return "unlesbar", "Datei nicht lesbar"
    if not st.get("sampled"):
        return "leer", "keine Ereignisse"
    if st.get("drops") is None:
        return "drops?", "summary.json nennt keine Drops für diesen Stream"
    if st.get("drops"):
        return "drops", f"{st['drops']} Drops"
    if st.get("window_ok") is False:
        return "fenster", f"erstes Ereignis {st.get('first_t')} außerhalb des Fensters"
    c = st.get("content") or {}
    if c.get("bad_json"):
        return "unlesbar", f"{c['bad_json']} unlesbare Zeilen in der Stichprobe"
    return "ok", None


def _group_quality(info, group):
    worst = ("ok", None)
    for s in GROUPS[group]:
        q, why = _stream_quality(info, s)
        if q != "ok":
            return q, f"{s}: {why}"
    return worst


def classify(info: dict, replay_type: str | None) -> dict:
    """Herkunft je Stream-Gruppe (nur aus aufnahmebezogenen Belegen)."""
    prov = {g: UNK for g in GROUPS}
    evidence = {g: "kein aufnahmebezogener Beleg" for g in GROUPS}
    if replay_type in REPLAY_TYPE_PROVENANCE:
        for g, v in REPLAY_TYPE_PROVENANCE[replay_type].items():
            prov[g], evidence[g] = v, f"Replay-Typ {replay_type} (Sammelprotokoll, dieser Aufnahme zugeordnet)"
    um = info.get("uniform_minutes") or {}
    if um.get("checked") and um.get("uniform") == um.get("checked"):
        sig = f"Tick-Zeitstempel in {um['uniform']}/{um['checked']} geprüften Minuten exakt gleichverteilt (60 s / n)"
        if prov["Trades"] == HIST:
            evidence["Trades"] += f"; WIDERSPRUCH: {sig}"
            prov["Trades"] = UNK
        else:
            prov["Trades"], evidence["Trades"] = MINUTE, sig + (f"; {evidence['Trades']}" if replay_type else "")
            if prov["Tape"] == UNK:
                prov["Tape"], evidence["Tape"] = GEN, "aus Trades mit generiertem Ablauf abgeleitet"
    dom = (info["streams"].get("dom.ndjson") or {}).get("content") or {}
    if dom.get("max_levels_bid") == 1 and dom.get("max_levels_ask") == 1:
        note = "DOM enthält in der Stichprobe nur 1 Level je Seite (keine Markttiefe)"
        if prov["DOM"] == HIST:
            prov["DOM"], evidence["DOM"] = UNK, f"WIDERSPRUCH: {note}; {evidence['DOM']}"
        else:
            evidence["DOM"] = note + ("; " + evidence["DOM"] if prov["DOM"] != UNK else "")
            if prov["DOM"] == UNK:
                prov["DOM"] = GEN
    return {"provenance": prov, "evidence": evidence}


_ORDER = {"ja": 2, "ungeklärt": 1, "nein": 0}


def evaluate_levels(base_problems: list, quality: dict, prov: dict) -> dict:
    """Stufen A–D je Zeile: 'ja' / 'ungeklärt' / 'nein' + Grund. Stufen sind kumulativ."""
    res = {}
    prev, prev_why = "ja", None
    for lvl, group in LEVELS:
        q, qwhy = quality[group]
        p = prov[group]
        if lvl == "A" and base_problems:
            cur, why = "nein", base_problems[0]
        elif q != "ok":
            cur, why = "nein", f"{group}: {qwhy}"
        elif p in (HIST,) or (group == "Trades" and p == MINUTE):
            cur, why = "ja", None
        elif p == GEN:
            cur, why = "nein", f"{group}: generiert"
        else:
            cur, why = "ungeklärt", f"{group}: Herkunft ungeklärt"
        if _ORDER[prev] < _ORDER[cur]:
            cur, why = prev, prev_why
        res[lvl] = {"status": cur, "reason": why}
        prev, prev_why = cur, why
    return res


# ---------------------------------------------------------------------------------------------------------------
# Hauptlauf
# ---------------------------------------------------------------------------------------------------------------

def _inspect_job(args):
    rec_dir, window, sample_events, sample_minutes = args
    if rec_dir is None or not Path(rec_dir).is_dir():
        return None
    return inspect_recording(rec_dir, window, sample_events, sample_minutes)


def audit(collection_roots, recording_roots, atas_logs=None, sample_events=20000, sample_minutes=2, workers=4) -> dict:
    cols, empty_cols = load_collections(collection_roots)
    logs = parse_atas_logs(Path(atas_logs) if atas_logs else None)

    rec_dirs: dict = {}
    for root in recording_roots:
        root = Path(root)
        if root.is_dir():
            for d in root.iterdir():
                if d.is_dir() and ((d / "meta.json").is_file() or (d / "summary.json").is_file()):
                    rec_dirs.setdefault(d.name, d)

    protocol: dict = {}
    for c in cols:
        for rec, v in c["protocol"].items():
            protocol.setdefault(rec, dict(v, collection=c["collection"]))

    # Zeilen aus den Manifesten: (job_id, recording_id) -> Zeile; überlappende Collections werden zusammengeführt.
    rows: dict = {}
    for c in cols:
        man = c["manifest"]
        csv_path = c["dir"] / "trades.csv"
        for job_id, j in (man.get("jobs") or {}).items():
            rid = j.get("recordingId") or "?"
            key = (job_id, rid)
            if key in rows:
                rows[key]["collections"].append(c["collection"])
                continue
            pj = parse_job_id(job_id)
            win = j.get("window") or {}
            s = iso_to_dt(win.get("start")) or (pj[1] if pj else None)
            e = iso_to_dt(win.get("end")) or (pj[2] if pj else None)
            contract, cev = _contract_from_csv(csv_path, j.get("byteOffsetBefore"))
            rd = rec_dirs.get(rid)
            if rd is None and j.get("recordingDir") and Path(j["recordingDir"]).is_dir():
                rd = Path(j["recordingDir"])
            rows[key] = {"job_id": job_id, "recording_id": rid, "session": pj[0] if pj else man.get("session") or "?",
                         "date": (s.date().isoformat() if s else "?"), "window": (s, e), "collections": [c["collection"]],
                         "in_manifest": True, "job": j, "contract": contract, "contract_evidence": cev, "rec_dir": rd}
    in_manifest = {k[1] for k in rows}
    for rid, rd in rec_dirs.items():
        if rid in in_manifest:
            continue
        p = protocol.get(rid)
        pj = parse_job_id(p["job_id"]) if p else None
        rows[(p["job_id"] if p else "?", rid)] = {
            "job_id": p["job_id"] if p else "", "recording_id": rid, "session": pj[0] if pj else "?",
            "date": pj[1].date().isoformat() if pj else "?", "window": (pj[1], pj[2]) if pj else (None, None),
            "collections": [], "in_manifest": False, "job": {}, "contract": None,
            "contract_evidence": "keine Collection-Zeile", "rec_dir": rd}

    tasks = [(r["rec_dir"], r["window"] if r["window"][0] else None, sample_events, sample_minutes) for r in rows.values()]
    if workers and workers > 1:
        with cf.ProcessPoolExecutor(max_workers=workers) as ex:
            infos = list(ex.map(_inspect_job, tasks, chunksize=4))
    else:
        infos = [_inspect_job(t) for t in tasks]

    for r, info in zip(rows.values(), infos):
        if r["date"] == "?" and info:
            first = next((info["streams"][s]["first_t"] for s in ("ticks.csv", "ticks.ndjson") if info["streams"][s]["first_t"]), None)
            t = iso_to_dt(first)
            if t:
                r["date"] = t.date().isoformat()
                sess, win = session_for_time(t)
                if sess:
                    r["session"], r["window"] = sess, win
                r["derived_from_first_tick"] = first
    table = []
    for r, info in zip(rows.values(), infos):
        table.append(evaluate_row(r, info, protocol.get(r["recording_id"]), logs))
    table.sort(key=lambda x: (x["date"], x["session"], x["recording_id"]))
    return {"rows": table, "empty_collections": empty_cols, "summary": summarize(table),
            "collections": [{"collection": c["collection"], "jobs": len((c["manifest"].get("jobs") or {}))} for c in cols],
            "atas_log_types": {f"{k[0]} – {k[1]}": dict(v) for k, v in logs.items()}}


def session_for_time(t: datetime):
    """Session, in deren Standardfenster (sommerzeitrichtig) der Zeitpunkt liegt; sonst (None, (None, None))."""
    from zoneinfo import ZoneInfo
    for name, (tz, (h0, m0), (h1, m1)) in SESSION_WINDOWS.items():
        local = t.astimezone(ZoneInfo(tz))
        s = local.replace(hour=h0, minute=m0, second=0, microsecond=0)
        e = local.replace(hour=h1, minute=m1, second=0, microsecond=0)
        if s <= local < e:
            return name, (s.astimezone(timezone.utc), e.astimezone(timezone.utc))
    return None, (None, None)


def evaluate_row(r: dict, info: dict | None, proto: dict | None, logs: dict) -> dict:
    j = r["job"]
    s, e = r["window"]
    base: list = []
    if info is None:
        base.append("Aufnahmeordner fehlt")
    if not r["in_manifest"]:
        res = (proto or {}).get("result")
        base.append("nicht in eine Collection übernommen" + (f" (Protokoll: {res})" if res else
                    " (keinem Auftrag zugeordnet; Datum/Session aus erstem Tick)" if r.get("derived_from_first_tick") else ""))
    else:
        if j.get("status") != "committed":
            base.append(f"Auftrag nicht committed ({j.get('status')})")
        if j.get("coverageStatus") != "complete":
            base.append(f"Abdeckung {j.get('coverageStatus')}: {'; '.join(j.get('statusReasons') or [])}")
        rec = j.get("reconciliation") or {}
        if rec.get("summary_clean") is False:
            base.append("Manifest: summary nicht sauber")
        if (j.get("trades") or {}).get("unknownVolume"):
            base.append(f"{j['trades']['unknownVolume']} Kontrakte ohne Aggressor")
    if info is not None:
        sm = info["summary"]
        if sm.get("error"):
            base.append(f"summary.json {sm['error']}")
        elif sm.get("clean") is not True:
            base.append("summary.json meldet keinen sauberen Abschluss")
        if sm.get("writerFaulted"):
            base.append("Recorder-Schreibfehler")
        if r["in_manifest"] and j.get("runId") and sm.get("runId") and j["runId"] != sm["runId"]:
            base.append("runId der Aufnahme passt nicht zum Manifest")
    quality = {g: (_group_quality(info, g) if info else ("fehlt", "Aufnahmeordner fehlt")) for g in GROUPS}
    replay_type = (proto or {}).get("replay_type")
    if proto and r["job_id"] and proto.get("job_id") != r["job_id"]:
        conflict_job = f"Protokoll ordnet die Aufnahme Auftrag {proto.get('job_id')} zu, Manifest {r['job_id']}"
        replay_type = None
    else:
        conflict_job = None
    cls = classify(info, replay_type) if info else {"provenance": {g: UNK for g in GROUPS}, "evidence": {g: "–" for g in GROUPS}}
    levels = evaluate_levels(base, quality, cls["provenance"])
    drops = {st: (info["streams"][st]["drops"] if info else None) for st in STREAMS}
    log_ctx = dict(logs.get(_atas_window_key(s, e), {})) if s and e else {}
    conflict = [conflict_job] if conflict_job else []
    if proto and proto.get("type_window") and s:
        tw0 = iso_to_dt(proto["type_window"][0].replace(" ", "T"))
        if tw0 and abs((tw0 - s).total_seconds()) > 1:
            conflict.append(f"Ladebereich laut ATAS {proto['type_window'][0]} ≠ Fenster {s:%Y-%m-%d %H:%M}")
    tr = j.get("trades") or {}
    return {
        "date": r["date"], "session": r["session"], "window_utc": f"{s:%H:%M}–{e:%H:%M}" if s and e else "?",
        "job_id": r["job_id"], "recording_id": r["recording_id"], "collections": r["collections"],
        "in_manifest": r["in_manifest"], "contract": r["contract"] or "unbekannt",
        "contract_evidence": r["contract_evidence"] + (f"; meta.json instrument={info['meta']['instrument']}" if info else ""),
        "trades": tr.get("rows") if tr else (info["streams"]["ticks.csv"]["summary_events"] if info else None),
        "trade_stats": [tr.get("rows"), tr.get("totalVolume"), tr.get("buyVolume"), tr.get("sellVolume")] if tr else None,
        "quality": {g: q[0] for g, q in quality.items()}, "quality_reason": {g: q[1] for g, q in quality.items()},
        "provenance": cls["provenance"], "evidence": cls["evidence"], "replay_type": replay_type,
        "replay_type_evidence": f"{Path(proto['protocol']).parent.name}/run_protocol.txt Zeile {proto['line']}" if proto and replay_type else None,
        "uniform_minutes": (info or {}).get("uniform_minutes"), "drops": drops,
        "dom_levels": [((info or {}).get("streams", {}).get("dom.ndjson") or {}).get("content", {}).get(k) for k in ("max_levels_bid", "max_levels_ask")],
        "atas_log_types_for_window": log_ctx, "base_problems": base, "conflicts": conflict, "levels": levels,
        "notes": (info or {}).get("notes", []),
    }


def summarize(table: list) -> dict:
    """Je Stufe: nutzbare/ungeklärte/ausgeschlossene Sessions je Session + gemeinsame Handelstage."""
    by_key: dict = collections.defaultdict(list)
    for r in table:
        if r["session"] in SESSIONS and r["date"] != "?":
            by_key[(r["date"], r["session"])].append(r)
    out: dict = {"sessions_with_data": {s: sum(1 for k in by_key if k[1] == s) for s in SESSIONS}, "levels": {}}
    session_status: dict = {}
    for key, rs in by_key.items():
        st = {}
        for lvl, _ in LEVELS:
            best = max(rs, key=lambda r: _ORDER[r["levels"][lvl]["status"]])
            status, reason = best["levels"][lvl]["status"], best["levels"][lvl]["reason"]
            if status == "ja":
                yes = [r for r in rs if r["levels"][lvl]["status"] == "ja"]
                stats = {json.dumps(r["trade_stats"]) for r in yes if r["trade_stats"]}
                if len(stats) > 1:
                    status, reason = "nein", "Konflikt: mehrere geprüfte Aufnahmen mit unterschiedlichem Inhalt"
            st[lvl] = (status, reason, best["recording_id"])
        session_status[key] = st
    for lvl, _ in LEVELS:
        lv: dict = {}
        for s in SESSIONS:
            keys = [k for k in session_status if k[1] == s]
            yes = [k for k in keys if session_status[k][lvl][0] == "ja"]
            unk = [k for k in keys if session_status[k][lvl][0] == "ungeklärt"]
            no = [k for k in keys if session_status[k][lvl][0] == "nein"]
            reasons = collections.Counter(_reason_class(session_status[k][lvl][1]) for k in no + unk)
            lv[s] = {"nutzbar": len(yes), "ungeklärt": len(unk), "ausgeschlossen": len(no),
                     "gründe": reasons.most_common(6), "tage": sorted(k[0] for k in yes)}
        days_l = set(lv["London"]["tage"]); days_n = set(lv["NewYork"]["tage"])
        lv["gemeinsame_tage"] = len(days_l & days_n)
        lv["gemeinsame_tage_liste"] = sorted(days_l & days_n)
        out["levels"][lvl] = lv
    out["session_status"] = {f"{k[0]} {k[1]}": {l: list(v) for l, v in st.items()} for k, st in sorted(session_status.items())}
    return out


def _reason_class(reason: str | None) -> str:
    if not reason:
        return "–"
    r = re.sub(r"\d+", "N", reason)
    r = re.sub(r"\(Protokoll: .*\)", "", r).strip()
    return r[:120]


# ---------------------------------------------------------------------------------------------------------------
# Ausgabe
# ---------------------------------------------------------------------------------------------------------------

def _stream_cell(r, group):
    q, p = r["quality"][group], r["provenance"][group]
    return f"{q} · {p}" if q == "ok" else f"{q} ({r['quality_reason'][group]}) · {p}"


def write_outputs(result: dict, out_dir) -> dict:
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    rows = result["rows"]
    csv_p = out / "datenqualitaet_tabelle.csv"
    with open(csv_p, "w", encoding="utf-8-sig", newline="") as f:
        w = csv.writer(f, delimiter=";")
        w.writerow(["Datum", "Session", "UTC-Fenster", "Aufnahme-ID", "Kontrakt + Beleg", "Trades", "Tape", "BBO", "DOM",
                    "Drops je Stream", "Konflikt/Grund", "Stufe A", "Stufe B", "Stufe C", "Stufe D", "Replay-Typ (Beleg)",
                    "Herkunftsbelege", "Collections", "Job"])
        for r in rows:
            reasons = r["base_problems"] + r["conflicts"] + r["notes"]
            w.writerow([
                r["date"], r["session"], r["window_utc"], r["recording_id"], f"{r['contract']} ({r['contract_evidence']})",
                f"{r['trades'] if r['trades'] is not None else '?'} · {_stream_cell(r, 'Trades')}",
                _stream_cell(r, "Tape"), _stream_cell(r, "BBO"),
                _stream_cell(r, "DOM") + (f" · Level {r['dom_levels'][0]}/{r['dom_levels'][1]}" if r["dom_levels"][0] is not None else ""),
                " · ".join(f"{k.replace('.ndjson', '')} {('?' if v is None else v)}" for k, v in r["drops"].items()),
                "; ".join(reasons) or "–",
                *[f"{r['levels'][l]['status']}" + (f" ({r['levels'][l]['reason']})" if r["levels"][l]["reason"] else "") for l in "ABCD"],
                f"{r['replay_type']} ({r['replay_type_evidence']})" if r["replay_type"] else "kein Beleg",
                " | ".join(f"{g}: {v}" for g, v in r["evidence"].items()),
                ", ".join(r["collections"]) or "–", r["job_id"]])
    json_p = out / "datenqualitaet.json"
    json_p.write_text(json.dumps(result, ensure_ascii=False, indent=1, default=str), encoding="utf-8")
    md_p = out / "datenqualitaet_bericht.md"
    md_p.write_text(render_report(result), encoding="utf-8")
    return {"csv": str(csv_p), "json": str(json_p), "report": str(md_p)}


LEVEL_TEXT = {"A": "Trades, Delta, VWAP ab Aufnahmebeginn", "B": "+ kumuliertes Tape", "C": "+ historisches Bid/Ask",
              "D": "+ historisches DOM"}


def render_report(result: dict) -> str:
    s = result["summary"]
    L = ["# Datenqualität ATAS-Aufnahmen (London / New York)", "",
         f"Zeilen: {len(result['rows'])} (Handelstag × Session × Aufnahme). Sessions mit Daten: London "
         f"{s['sessions_with_data']['London']}, New York {s['sessions_with_data']['NewYork']}.", "",
         "| Stufe | London nutzbar / ungeklärt / ausgeschlossen | New York nutzbar / ungeklärt / ausgeschlossen | gemeinsame Tage |",
         "|---|---|---|---|"]
    for lvl in "ABCD":
        v = s["levels"][lvl]
        L.append(f"| {lvl} {LEVEL_TEXT[lvl]} | {v['London']['nutzbar']} / {v['London']['ungeklärt']} / {v['London']['ausgeschlossen']} | "
                 f"{v['NewYork']['nutzbar']} / {v['NewYork']['ungeklärt']} / {v['NewYork']['ausgeschlossen']} | {v['gemeinsame_tage']} |")
    L.append("")
    for lvl in "ABCD":
        L.append(f"## Stufe {lvl}: {LEVEL_TEXT[lvl]}")
        for sess in SESSIONS:
            v = s["levels"][lvl][sess]
            L.append(f"- {sess}: Gründe für nicht nutzbar/ungeklärt: " + ("; ".join(f"{g} ({n})" for g, n in v["gründe"]) or "–"))
        L.append("")
    if result["empty_collections"]:
        L.append("## Collections ohne Datenbestand")
        L += [f"- {c['collection']}: {c['reason']}" for c in result["empty_collections"]]
        L.append("")
    if result.get("atas_log_types"):
        cnt = collections.Counter()
        for v in result["atas_log_types"].values():
            cnt.update(v)
        L.append("## ATAS-Protokolle (Kontext, fensterbezogen – kein Aufnahmebeleg)")
        L.append("- Wiedergaben je Typ: " + ", ".join(f"{k} {n}" for k, n in cnt.most_common()))
    return "\n".join(L) + "\n"


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--collections-root", action="append", required=True)
    ap.add_argument("--recordings-root", action="append", required=True)
    ap.add_argument("--atas-logs")
    ap.add_argument("--out", required=True)
    ap.add_argument("--sample-events", type=int, default=20000)
    ap.add_argument("--sample-minutes", type=int, default=2)
    ap.add_argument("--workers", type=int, default=4)
    a = ap.parse_args(argv)
    res = audit(a.collections_root, a.recordings_root, a.atas_logs, a.sample_events, a.sample_minutes, a.workers)
    paths = write_outputs(res, a.out)
    sys.stdout.reconfigure(encoding="utf-8")
    print(render_report(res))
    print(json.dumps(paths, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
