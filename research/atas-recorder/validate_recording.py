#!/usr/bin/env python3
"""
Validator fuer OrderFlowReplayRecorder-Aufnahmen (isoliert, nur lesend).

Cross-Checks EINER Aufnahme:
  - Tick-Summe (DataType==Trade): Anzahl, Volumen, Delta(Buy-Sell)
  - Bar-Summe: Anzahl, Volumen, Delta  + Footprint-Volumen je Bar
  - Cumulative-Trades: Volumen OHNE Doppelzaehlung (pro cumId letzter Record)
  - Konsistenz: Sigma(tick vol) ~ Sigma(bar vol) ~ Sigma(footprint vol); Sigma(tick delta) ~ Sigma(bar delta)
  - Lueckencheck: lokale Seq monoton/ohne Loch; Replay-Marktzeit (tMarket) nicht ruecklaeufig; Drops aus summary.json

Vergleich ZWEIER Aufnahmen (1x vs beschleunigt):
  python validate_recording.py DIR_1x --compare DIR_fast

WICHTIG: Gleiche Totale sind ein Hinweis, KEIN Beweis fuer Verlustfreiheit beim Datenanbieter.
"""
from __future__ import annotations
import argparse, json, os, sys
from collections import defaultdict

def _read_ndjson(path):
    if not os.path.exists(path): return []
    out = []
    with open(path, "r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                try: out.append(json.loads(line))
                except json.JSONDecodeError: pass
    return out

def analyze(d):
    ticks = _read_ndjson(os.path.join(d, "ticks.ndjson"))
    bars  = _read_ndjson(os.path.join(d, "bars.ndjson"))
    cums  = _read_ndjson(os.path.join(d, "cumtrades.ndjson"))
    dom   = _read_ndjson(os.path.join(d, "dom.ndjson"))
    summary = {}
    sp = os.path.join(d, "summary.json")
    if os.path.exists(sp):
        try: summary = json.load(open(sp, encoding="utf-8"))
        except Exception: pass

    trades = [t for t in ticks if t.get("dataType") == "Trade"]
    tick_vol = sum(float(t["volume"]) for t in trades)
    tick_delta = sum((float(t["volume"]) if t["direction"] == "Buy" else -float(t["volume"]) if t["direction"] == "Sell" else 0.0) for t in trades)

    bar_vol = sum(float(b["volume"]) for b in bars)
    bar_delta = sum(float(b["delta"]) for b in bars)
    fp_vol = 0.0
    for b in bars:
        for lv in b.get("footprint", []):
            fp_vol += float(lv["vol"])

    cum_last = {}
    for c in cums:
        cum_last[c["cumId"]] = c          # letzter Record je cumId gewinnt (volume ist kumuliert)
    cum_vol = sum(float(c["volume"]) for c in cum_last.values())

    # Luecken/Ordnung
    def seq_gaps(rows):
        seqs = sorted(int(r["seq"]) for r in rows if "seq" in r)
        holes = 0
        for a, b in zip(seqs, seqs[1:]):
            if b - a > 1: holes += (b - a - 1)
        return holes
    def time_backsteps(rows):
        last = None; back = 0
        for r in rows:
            tm = r.get("tMarket")
            if tm is None: continue
            if last is not None and tm < last: back += 1
            last = tm
        return back

    return {
        "dir": d,
        "ticks_all": len(ticks), "trades": len(trades), "tick_vol": tick_vol, "tick_delta": tick_delta,
        "bars": len(bars), "bar_vol": bar_vol, "bar_delta": bar_delta, "footprint_vol": fp_vol,
        "cumtrade_records": len(cums), "cumtrade_unique": len(cum_last), "cum_vol": cum_vol,
        "dom_updates": len(dom),
        "seq_holes_ticks": seq_gaps(ticks), "time_backsteps_ticks": time_backsteps(ticks),
        "summary": summary,
    }

def dir_bytes(d):
    tot = 0
    for fn in os.listdir(d):
        p = os.path.join(d, fn)
        if os.path.isfile(p): tot += os.path.getsize(p)
    return tot

def pct(a, b):
    if b == 0: return "n/a"
    return f"{(a-b)/b*100:+.4f}%"

def report(a):
    print(f"\n=== {a['dir']} ===")
    print(f"  Trades (DataType=Trade): {a['trades']}  (alle tick-Events: {a['ticks_all']})")
    print(f"  Sigma Tick-Volumen      : {a['tick_vol']:.2f}")
    print(f"  Sigma Tick-Delta        : {a['tick_delta']:.2f}")
    print(f"  Bars                    : {a['bars']}   Sigma Bar-Volumen: {a['bar_vol']:.2f}   Sigma Bar-Delta: {a['bar_delta']:.2f}")
    print(f"  Sigma Footprint-Volumen : {a['footprint_vol']:.2f}")
    print(f"  CumTrades (unique/records): {a['cumtrade_unique']}/{a['cumtrade_records']}   Sigma CumVolumen: {a['cum_vol']:.2f}")
    print(f"  DOM-Updates             : {a['dom_updates']}")
    print(f"  -- Konsistenz --")
    print(f"     Tick vs Bar Volumen  : {pct(a['tick_vol'], a['bar_vol'])}")
    print(f"     Footprint vs Bar Vol : {pct(a['footprint_vol'], a['bar_vol'])}")
    print(f"     Tick vs Bar Delta    : diff {a['tick_delta']-a['bar_delta']:.2f}")
    print(f"     CumTrade vs Tick Vol : {pct(a['cum_vol'], a['tick_vol'])}")
    print(f"  -- Integritaet --")
    print(f"     Seq-Loecher (ticks)  : {a['seq_holes_ticks']}")
    print(f"     tMarket rueckwaerts  : {a['time_backsteps_ticks']}")
    drops = 0
    per = (a["summary"].get("perStream") or {})
    for s, v in per.items(): drops += int(v.get("drops", 0))
    print(f"     Drops gesamt (summary.json): {drops}")
    if a["summary"]:
        print(f"     Durchsatz: {a['summary'].get('eventsPerSec','?')} ev/s, {a['summary'].get('bytesPerSec','?')} B/s, "
              f"QueueHighWater {a['summary'].get('queueHighWater','?')}")
    if os.path.isdir(a["dir"]):
        print(f"     Dateigroesse gesamt : {dir_bytes(a['dir'])/1024:.1f} KB")
    # Beispiel-Datensaetze
    for fn in ("ticks.ndjson", "bars.ndjson", "dom.ndjson"):
        rows = _read_ndjson(os.path.join(a["dir"], fn))
        if rows:
            print(f"  Beispiel {fn}: {json.dumps(rows[0], ensure_ascii=False)[:240]}")

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dir")
    ap.add_argument("--compare", default=None, help="zweite Aufnahme (beschleunigt) zum Vergleich")
    args = ap.parse_args()

    a = analyze(args.dir); report(a)
    if args.compare:
        b = analyze(args.compare); report(b)
        print("\n=== VERGLEICH 1x vs beschleunigt ===")
        for k in ("trades", "tick_vol", "tick_delta", "bars", "bar_vol", "bar_delta", "footprint_vol", "cum_vol", "dom_updates"):
            print(f"  {k:16}: 1x={a[k]:.2f}  fast={b[k]:.2f}  diff={pct(b[k], a[k])}")
        print("  HINWEIS: Abweichungen offen interpretieren; identische Totale != verlustfrei beim Anbieter.")

if __name__ == "__main__":
    sys.exit(main())
