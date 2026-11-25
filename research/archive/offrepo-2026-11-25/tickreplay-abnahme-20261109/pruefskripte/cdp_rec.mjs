// Ferngesteuerter Abnahme-Lauf im echten Chromium (Edge headless) ueber das DevTools-Protokoll.
import { writeFileSync, mkdirSync } from "node:fs";
const OUT = "F:/ATAS_Data/Research/TickReplay_Abnahme_20261109";
const FRAMES = OUT + "/screencast_frames";
mkdirSync(FRAMES, { recursive: true });
const tgt = await (await fetch("http://127.0.0.1:9333/json/new?http://127.0.0.1:5899/research", { method: "PUT" })).json();
const ws = new WebSocket(tgt.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener("open", r, { once: true }));
let id = 0; const pending = new Map(); const handlers = [];
ws.addEventListener("message", (ev) => { const m = JSON.parse(ev.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m); pending.delete(m.id); } else if (m.method) handlers.forEach((h) => h(m)); });
const send = (method, params = {}) => new Promise((res) => { const i = ++id; pending.set(i, res); ws.send(JSON.stringify({ id: i, method, params })); });
const ev = async (expression) => { const r = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true }); if (r.result?.exceptionDetails) throw new Error(JSON.stringify(r.result.exceptionDetails)); return r.result?.result?.value; };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
await send("Page.enable"); await send("Runtime.enable");
await send("Emulation.setDeviceMetricsOverride", { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false });
await send("Page.navigate", { url: "http://127.0.0.1:5899/research" });
const S = `JSON.parse(document.querySelector('[data-testid="tick-replay"]').dataset.replayState||'null')`;
for (let i = 0; i < 100; i++) { await sleep(300); const s = await ev(`(()=>{const e=document.querySelector('[data-testid="tick-replay"]');return e&&e.dataset.replayState?${S}:null})()`); if (s && s.loaded === s.total) break; }
await ev(`(()=>{const e=document.querySelector('[data-testid="tick-replay"]'); e.scrollIntoView({block:'start'}); return 1})()`);
const shot = async (name) => {
  const r = await ev(`(()=>{const b=document.querySelector('[data-testid="tick-replay"]').getBoundingClientRect(); return {x:Math.max(0,b.left),y:Math.max(0,b.top),w:b.width,h:Math.min(b.height,1000-Math.max(0,b.top))}})()`);
  const s = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: false, clip: { x: r.x, y: r.y, width: r.w, height: r.h, scale: 1 } });
  writeFileSync(`${OUT}/${name}.png`, Buffer.from(s.result.data, "base64"));
};
const click = (tid) => ev(`document.querySelector('[data-testid="${tid}"]').click()`);
const measure = async (speedTid, ms) => ev(`(async()=>{const st=()=>${S}; document.querySelector('[data-testid="${speedTid}"]').click(); document.querySelector('[data-testid="tr-play"]')?.click();
  await new Promise(r=>setTimeout(r,400)); const lt=[]; const po=new PerformanceObserver(l=>l.getEntries().forEach(e=>lt.push(Math.round(e.duration)))); po.observe({type:'longtask'});
  const c0=st().clock, p0=st().p, w0=performance.now(); let n=0; await new Promise(res=>{const f=()=>{n++; if(performance.now()-w0<${ms}) requestAnimationFrame(f); else res();}; requestAnimationFrame(f);});
  const c1=st().clock, p1=st().p, w1=performance.now(); po.disconnect(); document.querySelector('[data-testid="tr-pause"]')?.click();
  return {fps: Math.round(n/((w1-w0)/1000)), speed: +((c1-c0)/10000/(w1-w0)).toFixed(2), trades: p1-p0, marketSec: +((c1-c0)/1e7).toFixed(1), longTasks: lt}})()`);

const REC = OUT + "/aufnahme_frames"; mkdirSync(REC, { recursive: true });
const gotoNy = (t) => ev(`(()=>{const i=document.querySelector('[data-testid="tr-goto"]'); const set=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set; set.call(i,'${t}'); i.dispatchEvent(new Event('input',{bubbles:true})); i.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true})); return 1})()`);
let k = 0; const log = [];
const scene = async (startNy, speedTid, n, gapMs) => {
  await click("tr-pause").catch(() => {}); await gotoNy(startNy); await sleep(250);
  await click(speedTid); await click("tr-play");
  for (let i = 0; i < n; i++) {
    const r = await ev(`(()=>{const b=document.querySelector('[data-testid="tick-replay"]').getBoundingClientRect(); return {x:Math.max(0,b.left),y:Math.max(0,b.top),w:b.width,h:Math.min(b.height,1000-Math.max(0,b.top))}})()`);
    const s = await send("Page.captureScreenshot", { format: "png", captureBeyondViewport: false, clip: { x: r.x, y: r.y, width: r.w, height: r.h, scale: 1 } });
    writeFileSync(`${REC}/f${String(k++).padStart(3, "0")}.png`, Buffer.from(s.result.data, "base64"));
    log.push(await ev(`document.querySelector('[data-testid="tr-clock"]').innerText.split(String.fromCharCode(10))[1] + ' | ' + document.querySelector('[data-testid="tr-candle"]').innerText.split(String.fromCharCode(10))[0]`));
    await sleep(gapMs);
  }
  await click("tr-pause");
};
await scene("10:19:52", "tr-speed-10", 24, 250);   // ~1 Marktminute ueber die 10:20-Grenze
await scene("10:42:00", "tr-speed-100", 24, 250);  // ~10 Marktminuten, zwei Kerzenwechsel
console.log(JSON.stringify({ frames: k, first: log[0], boundary: log.filter((l, i) => i > 0 && l.split('|')[1] !== log[i - 1].split('|')[1]), last: log[log.length - 1] }, null, 1));
ws.close();
