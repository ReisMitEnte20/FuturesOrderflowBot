#!/bin/bash
# Vergleicht Engine-Kerzen (API+Engine) mit der unabhaengigen Aggregation der Originalticks fuer einen Tag.
JOB=$1
node engine_candles.mjs "$JOB" > "eng_$JOB.json" && python independent.py "$JOB" > "ind_$JOB.json" && python - "$JOB" <<'PY'
import json, sys
j = sys.argv[1]
e = json.load(open(f"eng_{j}.json")); i = json.load(open(f"ind_{j}.json"))
diff = [ (x, y) for x, y in zip(e["candles"], i["candles"]) if x != y ]
same = e["count"] == i["count"] and len(e["candles"]) == len(i["candles"]) and not diff
tv = sum(c["v"] for c in i["candles"])
print(f"{j}: trades engine={e['count']} unabh={i['count']} | kerzen {len(e['candles'])}/{len(i['candles'])} | vol {tv} | {'IDENTISCH' if same else 'ABWEICHUNG'}")
for x, y in diff[:3]: print("  engine", x, "\n  unabh ", y)
PY
