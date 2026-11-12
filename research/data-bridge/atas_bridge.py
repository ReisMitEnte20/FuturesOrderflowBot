#!/usr/bin/env python3
"""
ATAS -> neutrales Research-Format (Offline-Datenbrücke).

Wandelt eine EINZELNE ATAS-Recorder-Aufnahme (Ordner mit ticks.ndjson, meta.json,
summary.json, ...) verlustarm in Parquet (trades) + ein separates Manifest um.

Leitplanken (bewusst konservativ, siehe Auftrag):
- Rohdateien bleiben die unveränderte Quelle; dieses Skript LIEST nur, schreibt nie in die Quelle.
- Das gewünschte Fenster ist halb-offen [start, end). Vor-/Nachlauf bleibt in den Rohdaten;
  der Parquet-Output enthält NUR das Fenster, Rest-Ticks werden gezählt (Erhaltung in Rohdaten).
- Zeitstempel/Fenstergrenzen werden VERLUSTFREI als ganzzahlige Nanosekunden geparst (keine
  datetime-Mikrosekunden-Kürzung); so entscheiden Sub-Mikrosekunden-Grenzen korrekt (Finding 5.1).
- t_recv ist die lokale Empfangs-Wanduhr beim Abspielen eines historischen Replays. Das ist KEINE
  historische Börsen-Empfangszeit; t_recv - t_market ist KEINE gemessene Feed-Latenz. Keine künstliche Latenz.
- Aggressorseite kommt aus dem Quellfeld 'direction'; fehlt sie -> 'Unknown'. Nichts hinzuerfunden.
- Reihenfolge gleichzeitiger Events bleibt erhalten (stabile Quellreihenfolge = Dateireihenfolge/lokale seq).
- Keine Exchange-Sequenz erfunden; vorhandene lokale seq wird als 'src_seq' geführt (nicht als Börsensequenz).
- Kontrakt/Serienart bleibt 'unknown' (Symbollabel beweist nicht die historische Quellserie).
- Instrument-TickSize (aus meta) und beobachtetes Preisraster werden getrennt ausgewiesen.
- DOM/Quotes sind eigene, hier NICHT qualifizierte Datentypen (kein künstliches L2/L3 aus Trades).
- Ausgeführtes Bid/Ask-Volumen ist keine ruhende Orderbuchgröße.
- Deterministischer Output bei identischem Input (stabile Sortierung, keine 'now'-Werte in den Daten).
- Integrität (Finding 5): stabiler Eingabestand wird vor UND nach dem Lesen per SHA-256 geprüft
  (Änderung während der Konvertierung -> Abbruch, nichts wird veröffentlicht). Fehler/Diskrepanzen
  fließen in den Export-Status ein. Veröffentlichung erfolgt ATOMAR (temp -> rename); ein bestehendes
  Ziel wird standardmäßig abgelehnt (kein ungefragtes Überschreiben, keine partiellen Artefakte).
"""
from __future__ import annotations
import argparse, hashlib, json, os, re, shutil, sys, uuid
from datetime import datetime, timezone
from pathlib import Path

import pyarrow as pa
import pyarrow.parquet as pq

SCHEMA_VERSION = "atas-bridge-trades-1"


class SourceChangedError(RuntimeError):
    """Eingabedatei hat sich während der Konvertierung geändert -> kein verlässlicher Snapshot."""


class TargetExistsError(RuntimeError):
    """Zielausgabe existiert bereits -> standardmäßig kein Überschreiben (force=True nötig)."""


# ---------------------------------------------------------------------------
# reine Hilfsfunktionen (unit-testbar, keine ATAS-/IO-Abhängigkeit außer Datei)
# ---------------------------------------------------------------------------

def sha256_file(path: Path) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


_ISO_RE = re.compile(
    r"^(\d{4}-\d{2}-\d{2})[T ](\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?(Z|[+-]\d{2}:?\d{2})?$"
)
_NS = 1_000_000_000


def parse_iso_ns(s: str) -> int:
    """ISO-8601 UTC -> ganzzahlige Nanosekunden seit Epoch, VERLUSTFREI bis Nanosekunde.

    Vermeidet den datetime-Mikrosekunden-Verlust: die Sekundenbasis wird über datetime bestimmt,
    die Bruchteilziffern werden separat als Nanosekunden geführt (auf 9 Stellen aufgefüllt/begrenzt).
    """
    if s is None:
        raise ValueError("leerer Zeitstempel")
    s = s.strip()
    m = _ISO_RE.match(s)
    if not m:
        raise ValueError(f"unparsebarer Zeitstempel: {s!r}")
    date, hh, mm, ss, frac, off = m.groups()
    if off in (None, "Z"):
        off_str = "+00:00"
    else:
        off_str = off if ":" in off else off[:3] + ":" + off[3:]
    # Sekundenbasis (ohne Bruchteil) -> Epoch-Sekunden (ganzzahlig, exakt in float64 für ~1.8e9).
    base = f"{date}T{hh}:{mm}:{ss}{off_str}"
    dt = datetime.fromisoformat(base)
    epoch_s = int(round(dt.timestamp()))
    frac = frac or ""
    if len(frac) > 9:
        frac = frac[:9]            # jenseits Nanosekunde wird abgeschnitten (Recorder liefert .fffffff)
    ns_frac = int(frac.ljust(9, "0")) if frac else 0
    return epoch_s * _NS + ns_frac


def ns_to_iso(ns: int | None) -> str | None:
    """Ganzzahlige ns seit Epoch -> ISO-UTC mit Nanosekunden (für Manifest-Anzeige, verlustfrei)."""
    if ns is None:
        return None
    sec, frac = divmod(ns, _NS)
    dt = datetime.fromtimestamp(sec, timezone.utc)
    return dt.strftime("%Y-%m-%dT%H:%M:%S") + f".{frac:09d}Z"


def parse_iso(s: str) -> datetime:
    """Kompatibilitäts-Helfer (aware UTC datetime). Für Fenster-/Datenentscheidungen wird
    ausschließlich parse_iso_ns verwendet (verlustfrei); diese Funktion bleibt nur für Anzeige/API."""
    return datetime.fromtimestamp(parse_iso_ns(s) / _NS, timezone.utc)


def in_window_ns(t_ns: int, start_ns: int, end_ns: int) -> bool:
    """Halb-offenes Fenster [start, end) in ganzzahligen Nanosekunden."""
    return start_ns <= t_ns < end_ns


def observed_price_grid(prices) -> float | None:
    """Kleinster positiver Abstand zwischen DISTINKTEN Preisen (beobachtetes Raster).
    Getrennt von der Instrument-TickSize zu melden. None, wenn <2 distinkte Preise."""
    uniq = sorted(set(prices))
    if len(uniq) < 2:
        return None
    step = None
    for a, b in zip(uniq, uniq[1:]):
        d = round(b - a, 10)
        if d > 0 and (step is None or d < step):
            step = d
    return step


def off_grid_count(prices, tick: float) -> int:
    """Anzahl Preise, die kein ganzzahliges Vielfaches von tick sind."""
    if not tick or tick <= 0:
        return 0
    bad = 0
    for p in prices:
        if abs(round(p / tick) * tick - p) > 1e-9:
            bad += 1
    return bad


def iter_ndjson(path: Path):
    """Streamt eine NDJSON-Datei zeilenweise (keine Vollmaterialisierung)."""
    with open(path, "r", encoding="utf-8") as f:
        for i, line in enumerate(f):
            line = line.strip()
            if not line:
                continue
            try:
                yield i, json.loads(line)
            except json.JSONDecodeError:
                yield i, {"__bad_json__": True}


def aggressor_from(direction) -> str:
    if direction in ("Buy", "Sell"):
        return direction
    return "Unknown"


def csv_data_rows(path: Path) -> int:
    """Datenzeilen (ohne Header) in ticks.csv; -1 wenn fehlt."""
    if not path.exists():
        return -1
    n = 0
    with open(path, "r", encoding="utf-8") as f:
        for _ in f:
            n += 1
    return max(0, n - 1)


# ---------------------------------------------------------------------------
# Konvertierung einer Aufnahme
# ---------------------------------------------------------------------------

TRADE_FIELDS = [
    ("run_id", pa.string()), ("recording_id", pa.string()),
    ("src_seq", pa.int64()),            # Dateireihenfolge (stabil) - KEINE Börsensequenz
    ("recorder_seq", pa.int64()),       # lokale Recorder-seq (vorhanden, nicht Anbieter-vollständig)
    ("t_market_utc", pa.timestamp("ns", tz="UTC")),
    ("t_recv_utc", pa.timestamp("ns", tz="UTC")),   # lokale Empfangs-Wanduhr, KEINE Börsen-Empfangszeit
    ("price", pa.float64()), ("volume", pa.int64()),
    ("aggressor_side", pa.string()),    # Buy/Sell/Unknown aus Quellfeld
    ("exch_order_id", pa.string()), ("aggr_exch_order_id", pa.string()),
    ("open_interest", pa.int64()),
    ("data_type", pa.string()), ("schema_version", pa.string()),
]

# Finding 5 (Recheck): ALLE tatsächlich ausgewerteten Eingaben gehören in dieselbe Stabilitätsprüfung
# (nicht nur ticks/csv/meta/summary, sondern auch die otherStreams, die gehasht werden).
_INPUT_NAMES = ("ticks.ndjson", "ticks.csv", "meta.json", "summary.json",
                "dom.ndjson", "bbo.ndjson", "cumtrades.ndjson", "bars.ndjson")


def _hash_inputs(recording_dir: Path) -> dict:
    return {name: sha256_file(recording_dir / name)
            for name in _INPUT_NAMES if (recording_dir / name).exists()}


def _build_table(cols):
    """Arrow-Tabelle. ns-Zeitstempel werden aus int64-ns gebaut (verlustfrei), dann gecastet."""
    arrays = {}
    for name, typ in TRADE_FIELDS:
        if name in ("t_market_utc", "t_recv_utc"):
            arrays[name] = pa.array(cols[name], type=pa.int64()).cast(typ)
        else:
            arrays[name] = pa.array(cols[name], type=typ)
    return pa.table(arrays, schema=pa.schema(TRADE_FIELDS))


def convert(recording_dir, window_start, window_end, out_dir, run_label=None,
            instrument_hint=None, force=False):
    recording_dir = Path(recording_dir)
    out_dir = Path(out_dir)
    start_ns = parse_iso_ns(window_start) if isinstance(window_start, str) else int(window_start)
    end_ns = parse_iso_ns(window_end) if isinstance(window_end, str) else int(window_end)
    if not (start_ns < end_ns):
        raise ValueError("window_start muss < window_end sein (halb-offen [start,end))")

    meta_path = recording_dir / "meta.json"
    summary_path = recording_dir / "summary.json"
    ticks_path = recording_dir / "ticks.ndjson"
    ticks_csv = recording_dir / "ticks.csv"
    if not ticks_path.exists():
        raise FileNotFoundError(f"ticks.ndjson fehlt in {recording_dir}")

    # Finding 5.4: bestehendes Ziel standardmäßig ablehnen (kein ungefragtes Überschreiben).
    target_parquet = out_dir / "trades.parquet"
    target_manifest = out_dir / "manifest.json"
    if not force and (target_parquet.exists() or target_manifest.exists()):
        raise TargetExistsError(
            f"Ziel existiert bereits ({out_dir}); force=True erforderlich, um zu ersetzen.")

    # Finding 5.2: Eingabestand VOR dem Lesen snapshotten.
    pre_hashes = _hash_inputs(recording_dir)

    meta = json.loads(meta_path.read_text(encoding="utf-8")) if meta_path.exists() else {}
    summary = json.loads(summary_path.read_text(encoding="utf-8")) if summary_path.exists() else {}
    recording_id = recording_dir.name
    run_id = meta.get("runId") or ""
    instrument = meta.get("instrument") or instrument_hint or "unknown"
    tick_size = float(meta.get("tickSize") or 0.0)

    cols = {name: [] for name, _ in TRADE_FIELDS}
    n_trades_total = 0
    n_trades_window = 0
    n_before = n_after = 0
    n_bad_json = 0
    n_nontrade = 0
    n_lines_total = 0
    first_in_ns = last_in_ns = None
    prices_window = []

    for file_idx, obj in iter_ndjson(ticks_path):
        if obj.get("__bad_json__"):
            n_bad_json += 1
            continue
        n_lines_total += 1
        if obj.get("dataType") != "Trade":
            n_nontrade += 1
            continue
        n_trades_total += 1
        tm_ns = parse_iso_ns(obj["tMarket"])
        if tm_ns < start_ns:
            n_before += 1
            continue
        if tm_ns >= end_ns:
            n_after += 1
            continue
        n_trades_window += 1
        if first_in_ns is None:
            first_in_ns = tm_ns
        last_in_ns = tm_ns
        price = float(obj.get("price"))
        prices_window.append(price)
        cols["run_id"].append(run_id)
        cols["recording_id"].append(recording_id)
        cols["src_seq"].append(file_idx)
        cols["recorder_seq"].append(int(obj.get("seq") or 0))
        cols["t_market_utc"].append(tm_ns)
        trecv = obj.get("tRecv")
        cols["t_recv_utc"].append(parse_iso_ns(trecv) if trecv else None)
        cols["price"].append(price)
        cols["volume"].append(int(obj.get("volume") or 0))
        cols["aggressor_side"].append(aggressor_from(obj.get("direction")))
        cols["exch_order_id"].append(obj.get("exchOrderId"))
        cols["aggr_exch_order_id"].append(obj.get("aggrExchOrderId"))
        cols["open_interest"].append(int(obj.get("oi") or 0))
        cols["data_type"].append("Trade")
        cols["schema_version"].append(SCHEMA_VERSION)

    # Finding 5 (Recheck): CSV wird INNERHALB des Stabilitätsfensters gelesen (vor der post-Hash-Prüfung).
    csv_rows = csv_data_rows(ticks_csv)

    # Eingabestand NACH dem Lesen ALLER ausgewerteten Dateien erneut prüfen (ticks, csv, meta, summary,
    # dom/bbo/cumtrades/bars). Jede Änderung während der Konvertierung -> Abbruch, nichts veröffentlichen.
    post_hashes = _hash_inputs(recording_dir)
    if post_hashes != pre_hashes:
        changed = sorted(k for k in set(pre_hashes) | set(post_hashes)
                         if pre_hashes.get(k) != post_hashes.get(k))
        raise SourceChangedError(
            f"Quelle hat sich während der Konvertierung geändert ({', '.join(changed)}); "
            f"kein verlässlicher Snapshot -> Abbruch, kein Export.")

    table = _build_table(cols)

    total_volume = int(sum(cols["volume"]))
    buy_vol = int(sum(v for v, a in zip(cols["volume"], cols["aggressor_side"]) if a == "Buy"))
    sell_vol = int(sum(v for v, a in zip(cols["volume"], cols["aggressor_side"]) if a == "Sell"))
    unk_vol = total_volume - buy_vol - sell_vol
    grid = observed_price_grid(prices_window)
    off_grid = off_grid_count(prices_window, tick_size)
    price_decimals = max((len(str(p).split(".")[1]) if "." in str(p) else 0) for p in prices_window) if prices_window else 0

    summ_ticks = ((summary.get("perStream") or {}).get("ticks.ndjson") or {}).get("events")
    summ_clean = summary.get("clean")  # None bei alter Summary ohne Statusfeld
    csv_eq = (csv_rows == n_trades_total) if csv_rows >= 0 else None
    summ_eq = (summ_ticks == n_lines_total) if summ_ticks is not None else None
    reconcile = {
        "ndjson_lines_total": n_lines_total,
        "ndjson_trade_events_total": n_trades_total,
        "csv_data_rows": csv_rows,
        "summary_ticks_events": summ_ticks,
        "csv_eq_trades": csv_eq,
        "summary_eq_ndjson_lines": summ_eq,
        "summary_clean": summ_clean,
        "note": "Diskrepanzen werden sichtbar gemacht, nicht geglaettet. summary.clean==true erst mit neuem Recorder (A3); "
                "fehlt das Feld, ist der saubere Abschluss NICHT belegt. summary zaehlt NDJSON-Zeilen, ticks.csv zaehlt Trades.",
    }

    # Finding 5.3: Status bezieht Fehler/Diskrepanzen ein (nicht nur 'Trades + Preisraster vorhanden').
    reasons = []
    if n_bad_json > 0:
        reasons.append("bad_json_lines")
    if off_grid > 0:
        reasons.append("off_grid_prices")
    if summ_clean is not True:
        reasons.append("summary_not_clean_or_missing")  # None (alt/fehlt) ODER False (fehlgeschlagen)
    if csv_eq is False:
        reasons.append("csv_trade_count_discrepancy")
    if summ_eq is False:
        reasons.append("summary_ndjson_line_discrepancy")
    if n_trades_window == 0:
        status = "empty_window"
    elif reasons:
        status = "check"     # nicht als 'complete'/qualifiziert ausweisen
    else:
        status = "complete"

    manifest = {
        "schemaVersion": SCHEMA_VERSION,
        "generatedAtUtc": datetime.now(timezone.utc).isoformat(),  # Metadaten (nicht Teil des Datendeterminismus)
        "source": {
            "type": "ATAS replay recording",
            "recorder": meta.get("recorder"),
            "recordingId": recording_id,
            "runId": run_id,
            "recordingDir": str(recording_dir),
        },
        "instrument": {
            "symbolLabel": instrument,
            "venue": "unknown",            # NICHT aus Symbol abgeleitet (nicht belegt)
            "contractOrSeries": "unknown", # Symbollabel beweist nicht die historische Quellserie
            "tickSize": tick_size,
            "observedPriceGrid": grid,     # separat von tickSize
            "priceDecimals": price_decimals,
            "offGridVsTickSize": off_grid,
        },
        "requestedWindow": {
            "start": ns_to_iso(start_ns),
            "end": ns_to_iso(end_ns),
            "startNs": start_ns,
            "endNs": end_ns,
            "interval": "[start, end)",
            "timezoneNote": "Fenstergrenzen sind UTC. Europe/Berlin-Umrechnung erfolgt im Steuerungslauf, hier UTC-Eingabe.",
        },
        "coverage": {
            "tradesInWindow": n_trades_window,
            "tradesTotalInFile": n_trades_total,
            "restTicksBefore": n_before,     # Vorlauf (in Rohdaten erhalten)
            "restTicksAfter": n_after,       # Nachlauf (in Rohdaten erhalten)
            "firstTradeInWindowUtc": ns_to_iso(first_in_ns),
            "lastTradeInWindowUtc": ns_to_iso(last_in_ns),
            "note": "Erster/letzter Trade ist NICHT die exakte Fenstergrenze; Enderreichung belegt der Steuerungslauf "
                    "über Marktfortschritt, nicht dieses Skript. Lokale Vollständigkeit != Anbieter-Vollständigkeit.",
        },
        "trades": {
            "rows": n_trades_window,
            "totalVolume": total_volume,
            "buyVolume": buy_vol, "sellVolume": sell_vol, "unknownVolume": unk_vol,
            "aggressorSource": "Quellfeld 'direction' (Buy/Sell); sonst Unknown",
        },
        "qualityFlags": {
            "badJsonLines": n_bad_json,
            "nonTradeEventsInTicksStream": n_nontrade,
            "status": status,
            "statusReasons": reasons,
        },
        "reconciliation": reconcile,
        "otherStreams": {
            # sha256 aus DEMSELBEN Snapshot (pre_hashes), nicht separat nachgehasht (Finding 5 Recheck).
            name: {
                "present": (recording_dir / name).exists(),
                "sha256": pre_hashes.get(name),
                "summaryEvents": ((summary.get("perStream") or {}).get(name) or {}).get("events"),
                "qualified": False,
            }
            for name in ("dom.ndjson", "bbo.ndjson", "cumtrades.ndjson", "bars.ndjson")
        },
        "inputHashes": pre_hashes,
        "inputSnapshotStable": True,     # pre==post über ALLE ausgewerteten Dateien geprüft (sonst oben abgebrochen)
        "inputSnapshotFiles": sorted(pre_hashes.keys()),
        "output": {
            "tradesParquet": str(target_parquet),
            "parquetCompression": "zstd",
            "publish": "eine Generation: Staging-Verzeichnis -> atomarer Verzeichnis-Swap (Parquet+Manifest gemeinsam).",
        },
        "semantics": {
            "tRecv": "lokale Empfangs-Wanduhr beim Abspielen eines historischen Replays; KEINE historische "
                     "Börsen-Empfangszeit. t_recv - t_market ist KEINE gemessene Feed-Latenz.",
            "srcSeq": "Dateireihenfolge (stabil). KEINE Börsensequenz. recorder_seq ist lokal, nicht Anbieter-vollständig.",
            "timePrecision": "Zeitstempel verlustfrei als ganzzahlige Nanosekunden geparst (keine datetime-Kürzung).",
            "aggressor": "aus Quellfeld; nichts hinzuerfunden.",
            "dom_quotes": "DOM/Quotes sind eigene Typen, hier unqualifiziert; kein künstliches L2/L3 aus Trades.",
            "executedVolume": "ausgeführtes Bid/Ask-Volumen ist KEINE ruhende Orderbuchgröße.",
            "dashboardCap": "Research-Datensatz ist NICHT durch Dashboard-Tape-Caps begrenzt.",
            "duplicates": "gleiche Zeit/Preis/Menge sind KEIN Duplikatnachweis; Reihenfolge bleibt erhalten.",
        },
        "provenance": {
            "bridgeTool": "research/data-bridge/atas_bridge.py",
            "runLabel": run_label,
            "pyarrow": pa.__version__,
        },
    }

    # Finding 5 (Recheck): Veröffentlichung als EINE Generation. Beide Dateien werden in ein exklusives,
    # eindeutiges Staging-Verzeichnis geschrieben und per atomarem VERZEICHNIS-Swap sichtbar — nie zwei
    # Einzel-Renames als Pseudo-Transaktion. Bei Fehler bleibt entweder der alte oder der neue Stand, nie eine Mischung.
    parent = out_dir.parent
    parent.mkdir(parents=True, exist_ok=True)
    staging = parent / (".stage_" + uuid.uuid4().hex)   # pro Aufruf eindeutig -> parallele Exporte isoliert
    staging.mkdir()
    try:
        sp = staging / "trades.parquet"
        sm = staging / "manifest.json"
        pq.write_table(table, sp, compression="zstd")
        if pq.read_table(sp).num_rows != table.num_rows:
            raise RuntimeError("Parquet-Validierung fehlgeschlagen (Zeilenzahl weicht ab).")
        sm.write_text(json.dumps(manifest, indent=2, ensure_ascii=False), encoding="utf-8")
        if out_dir.exists():
            if not force:
                raise TargetExistsError(f"Ziel existiert bereits ({out_dir}); force=True erforderlich.")
            backup = parent / (".old_" + uuid.uuid4().hex)
            os.replace(out_dir, backup)              # altes Ziel atomar beiseite
            try:
                os.replace(staging, out_dir)         # neue Generation atomar an denselben Pfad
            except Exception:
                os.replace(backup, out_dir)          # Rollback: konsistenter Alt-Stand
                raise
            shutil.rmtree(backup, ignore_errors=True)
        else:
            # Verzeichnis-Rename schlägt fehl, falls das Ziel inzwischen (konkurrierend) existiert -> kein Überschreiben.
            os.replace(staging, out_dir)
    finally:
        if staging.exists():
            shutil.rmtree(staging, ignore_errors=True)

    return manifest, table


def main(argv=None):
    ap = argparse.ArgumentParser(description="ATAS-Aufnahme -> trades.parquet + manifest.json")
    ap.add_argument("recording_dir")
    ap.add_argument("--start", required=True, help="Fensterstart UTC ISO, z.B. 2026-10-26T08:00:00Z")
    ap.add_argument("--end", required=True, help="Fensterende UTC ISO (exklusiv)")
    ap.add_argument("--out", required=True, help="Ausgabeordner (auf F:)")
    ap.add_argument("--label", default=None)
    ap.add_argument("--force", action="store_true", help="bestehendes Ziel ersetzen (sonst Abbruch)")
    a = ap.parse_args(argv)
    m, _ = convert(a.recording_dir, a.start, a.end, a.out, run_label=a.label, force=a.force)
    print(json.dumps({"status": m["qualityFlags"]["status"],
                      "statusReasons": m["qualityFlags"]["statusReasons"],
                      "tradesInWindow": m["coverage"]["tradesInWindow"],
                      "out": m["output"]["tradesParquet"]}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    sys.exit(main())
