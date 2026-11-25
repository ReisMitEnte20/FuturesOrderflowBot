# Unabhaengige 5-Min-Aggregation direkt aus der ORIGINAL-CSV (nur lesend), exakte ganzzahlige Nanosekunden.
import csv, io, json, sys
coll = r"F:\ATAS_Data\Collections\MNQ_NewYork_0930-1200_America-New-York_2026-10-26_bis_2026-10-30"
man = json.load(open(coll + r"\collection_manifest.json", encoding="utf-8"))
job_id = sys.argv[1]; job = man["jobs"][job_id]
start_ns = job["window"]["startNs"]; BUCKET = 300 * 10**9
a, b = job["byteOffsetBefore"], job["byteOffsetAfter"]
def ns_of(s):  # 2026-10-26T13:30:00.004152800Z -> ns seit Tagesbeginn (UTC), exakt
    return ((int(s[11:13]) * 60 + int(s[14:16])) * 60 + int(s[17:19])) * 10**9 + int(s[20:29])
day_start_ns = start_ns - ns_of(job["window"]["start"])
candles = {}; order = []; n = 0
with open(coll + r"\trades.csv", "rb") as f:
    header = f.readline().decode().strip().split(",")
    idx = {k: i for i, k in enumerate(header)}
    f.seek(a); raw = f.read(b - a)
for row in csv.reader(io.StringIO(raw.decode("utf-8"))):
    if not row or row[idx["job_id"]] != job_id: continue
    ns = day_start_ns + ns_of(row[idx["t_market_utc"]])
    bk = (ns - start_ns) // BUCKET
    px = float(row[idx["price"]]); v = int(row[idx["volume"]]); side = row[idx["aggressor_side"]]
    c = candles.get(bk)
    if c is None:
        c = candles[bk] = {"b": bk, "o": px, "h": px, "l": px, "c": px, "v": 0, "bv": 0, "sv": 0, "n": 0}; order.append(bk)
    c["h"] = max(c["h"], px); c["l"] = min(c["l"], px); c["c"] = px; c["v"] += v; c["n"] += 1
    if side == "Buy": c["bv"] += v
    elif side == "Sell": c["sv"] += v
    n += 1
print(json.dumps({"count": n, "candles": [candles[k] for k in sorted(order)]}))
