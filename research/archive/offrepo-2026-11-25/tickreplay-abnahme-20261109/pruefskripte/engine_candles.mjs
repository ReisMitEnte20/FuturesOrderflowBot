// Spielt eine Session ueber die ECHTE API (portionsweise) + die Dashboard-Engine komplett ab und gibt die Kerzen aus.
import { createStore, appendChunk, decodeChunk, TickReplayEngine } from "file:///A:/Projects/FuturesOrderflowBot/dashboard/src/lib/tickReplay.ts";
const job = process.argv[2];
const base = "http://127.0.0.1:5034/api/tickreplay/sessions/" + job;
const meta = await (await fetch(base + "/meta")).json();
const st = createStore(meta.count);
while (st.loaded < st.total) {
  const buf = await (await fetch(`${base}/ticks?offset=${st.loaded}&count=200000`)).arrayBuffer();
  appendChunk(st, st.loaded, decodeChunk(buf));
}
const e = new TickReplayEngine(st, meta.bucketUnits);
e.advanceTo(st.total);
console.log(JSON.stringify({ count: meta.count, candles: e.candles().map(c => ({ b: c.bucket, o: c.open, h: c.high, l: c.low, c: c.close, v: c.volume, bv: c.buyVolume, sv: c.sellVolume, n: c.trades })) }));
