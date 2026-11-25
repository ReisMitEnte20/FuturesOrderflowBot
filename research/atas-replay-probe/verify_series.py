import json, os, sys

# Aufnahmeordner der damaligen Phase-1-Serie (29.10.2026). Standard: %USERPROFILE%\ATAS_OF_Recordings;
# anderer Ordner als erstes Argument.
BASE = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.expanduser("~"), "ATAS_OF_Recordings")
WINS = [
    ("F1 26.10 09:00-09:01 Berlin (08:00-08:01 UTC)", "MNQ_20261029_214831Z"),
    ("F2 26.10 15:30-15:31 Berlin (14:30-14:31 UTC)", "MNQ_20261029_215017Z"),
    ("F3 27.10 09:00-09:01 Berlin (08:00-08:01 UTC)", "MNQ_20261029_215149Z"),
    ("F4 27.10 15:30-15:31 Berlin (14:30-14:31 UTC)", "MNQ_20261029_215328Z"),
]
TICK = 0.25

def meta_locked(p):
    try:
        f = open(p, 'r+b'); f.close(); return False
    except Exception:
        return True

for label, folder in WINS:
    d = os.path.join(BASE, folder)
    print("="*70)
    print(label, "->", folder)
    sp = os.path.join(d, "summary.json")
    has_sum = os.path.exists(sp)
    locked = meta_locked(os.path.join(d, "meta.json"))
    print(f"  summary.json={has_sum}  writer_beendet(unlocked)={not locked}")
    summ = json.load(open(sp, encoding='utf-8')) if has_sum else {}
    ps = summ.get("perStream", {})
    # parse ALL ticks
    tn = os.path.join(d, "ticks.ndjson")
    n=0; buys=0; sells=0; unknown=0; offgrid=0; mn=1e18; mx=-1e18
    first_mt=last_mt=None; first_price=last_price=None; bad_json=0; recv_ok=True
    with open(tn, encoding='utf-8') as fh:
        for line in fh:
            line=line.strip()
            if not line: continue
            try: o=json.loads(line)
            except Exception: bad_json+=1; continue
            n+=1
            dr=o.get("direction")
            if dr=="Buy": buys+=1
            elif dr=="Sell": sells+=1
            else: unknown+=1
            pr=o.get("price")
            if pr is not None:
                mn=min(mn,pr); mx=max(mx,pr)
                # off-grid if not multiple of tick
                r=round(pr/TICK)
                if abs(r*TICK-pr)>1e-9: offgrid+=1
            mt=o.get("tMarket")
            if mt:
                if first_mt is None: first_mt=mt; first_price=pr
                last_mt=mt; last_price=pr
            rc=o.get("tRecv","")
            if not rc.startswith("2026-10-29"): recv_ok=False
    # csv line count (minus header)
    cn=os.path.join(d,"ticks.csv")
    csv_rows = sum(1 for _ in open(cn, encoding='utf-8'))-1 if os.path.exists(cn) else -1
    # reconcile
    tnd = ps.get("ticks.ndjson",{}); tcsv = ps.get("ticks.csv",{})
    sum_tick_ev = tnd.get("events"); sum_tick_drops = tnd.get("drops")
    file_bytes = os.path.getsize(tn)
    sum_bytes = tnd.get("bytes")
    recon_bytes = (sum_bytes + n) == file_bytes if sum_bytes is not None else None
    total_drops = sum(v.get("drops",0) for v in ps.values())
    print(f"  Ticks: ndjson_Zeilen={n}  csv_Datenzeilen={csv_rows}  summary_events={sum_tick_ev}  (gleich={n==csv_rows==sum_tick_ev})  bad_json={bad_json}")
    print(f"  Marktzeit: {first_mt} -> {last_mt}")
    print(f"  Aggressor: Buy={buys} Sell={sells} UNBEKANNT={unknown}")
    print(f"  Preisraster(0.25): min={mn} max={mx} off_grid={offgrid}")
    print(f"  Drops gesamt(alle Streams)={total_drops}  ticks_drops={sum_tick_drops}  bad_json={bad_json}")
    print(f"  tRecv alle heute(29.10)={recv_ok}")
    print(f"  Byte-Reconcile ticks.ndjson: summary_bytes({sum_bytes})+zeilen({n})==datei({file_bytes}) -> {recon_bytes}")
    print(f"  summary: durationSec={summ.get('durationSec'):.1f} totalEvents={summ.get('totalEvents')} queueHighWater={summ.get('queueHighWater')}")
    # other streams events/drops
    for s in ("cumtrades.ndjson","dom.ndjson","bbo.ndjson"):
        v=ps.get(s,{}); print(f"    {s}: events={v.get('events')} drops={v.get('drops')} bytes={v.get('bytes')}")
