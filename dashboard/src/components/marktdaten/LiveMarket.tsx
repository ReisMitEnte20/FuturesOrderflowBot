// Marktdaten → Live: was der Bot gerade aus ATAS empfängt (ATAS → Bridge → Backend → hier). Nur lesend – keine Orders,
// keine Strategie. Die Anzeige fragt gedrosselt ab (zeichnet seltener als Daten eintreffen); verlorene Ereignisse werden
// über die Zähler sichtbar, nie still verworfen. Jede Kachel trägt ihre Herkunft.
import { useEffect, useMemo, useRef, useState } from "react";
import { cn } from "@/lib/utils";
import { TickReplayChart, type ReplaySlot } from "@/components/research/TickReplayChart";
import { FootprintLadder } from "@/components/chartreplay/DayReplay";
import {
  marketdataApi, valueText, candlesToSlots, slotDeltaCvd, STATUS_TEXT, BOOK_STATE_TEXT,
  type LiveStateResponse, type LiveComparison, type LiveCandle,
} from "@/lib/marketdataApi";
import { ProvChip, ProvLegend } from "./Provenance";

const POLL_MS = 400;
const nf = new Intl.NumberFormat("de-DE", { maximumFractionDigits: 4 });
const SESSION_TZ: Record<string, string> = { London: "Europe/London", "New York": "America/New_York" };
const readLs = (k: string) => { try { return localStorage.getItem(k); } catch { return null; } };
const writeLs = (k: string, v: string) => { try { localStorage.setItem(k, v); } catch { /* ohne Speicher */ } };

function useLiveState() {
  const [data, setData] = useState<LiveStateResponse | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [polls, setPolls] = useState(0);
  useEffect(() => {
    let stop = false, timer: ReturnType<typeof setTimeout> | null = null;
    const ac = new AbortController();
    const tick = async () => {
      try { const d = await marketdataApi.liveState(ac.signal); if (!stop) { setData(d); setErr(null); setPolls((n) => n + 1); } }
      catch (e: any) { if (!stop && !ac.signal.aborted) setErr(`Backend nicht erreichbar (${e?.message ?? e}). API-Host auf :5034 starten.`); }
      if (!stop) timer = setTimeout(tick, POLL_MS);
    };
    tick();
    return () => { stop = true; ac.abort(); if (timer) clearTimeout(timer); };
  }, []);
  return { data, err, polls };
}

function fmtTime(iso: string | null | undefined, tz: string) {
  if (!iso) return "–";
  const ms = Date.parse(iso);
  const s = new Intl.DateTimeFormat("de-DE", { timeZone: tz, hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" }).format(ms);
  const frac = iso.match(/\.(\d{3})/)?.[1] ?? "000";
  const z = new Intl.DateTimeFormat(tz.startsWith("Europe/") ? "en-GB" : "en-US", { timeZone: tz, timeZoneName: "short" }).formatToParts(ms).find((p) => p.type === "timeZoneName")?.value ?? tz;
  return `${s}.${frac} ${z}`;
}

export function LiveMarket() {
  const { data, err, polls } = useLiveState();
  const s = data?.state ?? null;
  const tz = (s && SESSION_TZ[s.session]) || "UTC";
  const [show, setShow] = useState<Record<string, boolean>>(() => { try { return JSON.parse(readLs("marktdaten:live:show") ?? "") ?? {}; } catch { return {}; } });
  const on = (k: string) => show[k] !== false;
  const toggle = (k: string) => setShow((p) => { const n = { ...p, [k]: !on(k) }; writeLs("marktdaten:live:show", JSON.stringify(n)); return n; });
  const [fpSel, setFpSel] = useState<number | null>(null);
  const [fpSource, setFpSource] = useState<"derived" | "atas">("atas");
  const gen = s?.generation;
  useEffect(() => setFpSel(null), [gen]);

  // Datenrate (empfangene Zeilen/s) vs. Anzeigerate (Abrufe/s)
  const rate = useRef<{ t: number; lines: number; polls: number; dps: number; pps: number }>({ t: Date.now(), lines: 0, polls: 0, dps: 0, pps: 0 });
  if (s) {
    const now = Date.now(), r = rate.current;
    if (now - r.t >= 2000) {
      r.dps = Math.max(0, (s.counters.linesReceived - r.lines) / ((now - r.t) / 1000));
      r.pps = (polls - r.polls) / ((now - r.t) / 1000);
      r.t = now; r.lines = s.counters.linesReceived; r.polls = polls;
    }
  }

  const candles: LiveCandle[] = s?.candles ?? [];
  const slots = useMemo(() => candlesToSlots(candles, tz) as unknown as ReplaySlot[], [candles, tz]);
  const dc = useMemo(() => slotDeltaCvd(candles), [candles]);
  const currentSlot = candles.length && !candles[candles.length - 1].closed ? candles.length - 1 : null;
  const fpIdx = fpSel ?? (candles.length ? candles.length - 1 : null);
  const fpCandle = fpIdx != null ? candles[fpIdx] : null;
  const atasBar = fpCandle && s ? s.atasBars.find((b) => b.tStart === fpCandle.tStart) ?? null : null;
  const fpUse = fpSource === "atas" && atasBar ? atasBar : fpCandle;
  const fpLevels = (fpUse?.footprint ?? []).map((l) => ({ price: l.price, bid: l.bid, ask: l.ask, trades: 0 }));

  const st = s ? STATUS_TEXT[s.status] ?? { text: s.status, color: "#8b9bb0" } : null;
  const isTest = s?.sourceKind === "test";
  const box = "rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2";
  const chip = (k: string, l: string) => (
    <button key={k} onClick={() => toggle(k)} aria-pressed={on(k)} data-testid={`live-toggle-${k}`}
      className={cn("h-[24px] rounded-md border px-2 text-[11px]", on(k) ? "border-[var(--cyan)] text-[var(--fg)]" : "border-[var(--line-2)] text-[var(--fg-faint)]")}>{l}</button>
  );

  return (
    <div className="space-y-3" data-testid="live-market" data-status={s?.status ?? "keine_api"} data-generation={s?.generation ?? ""} data-source-kind={s?.sourceKind ?? ""}
      data-book-state={s?.book.state ?? ""} data-counters={s ? JSON.stringify({ r: s.counters.linesReceived, p: s.counters.processed, gaps: s.counters.seqMissing, be: s.counters.backendDropped, br: s.counters.bridgeDropped, trades: s.trades.length, tape: s.tape.length, candles: s.candles.length, bids: s.book.bids.length }) : ""}>
      {/* ---------------- Statusleiste: Quelle · Instrument · Session · letztes Ereignis · Verbindung ---------------- */}
      <section className={cn("rounded-lg border bg-[var(--panel)] p-3", isTest ? "border-[#c77dff]" : "border-[var(--line)]")} data-testid="live-statusbar">
        {isTest && <div className="mb-2 rounded border border-[#c77dff] px-2 py-1 text-[12px] font-semibold text-[#c77dff]" data-testid="live-testdata">TESTDATEN – diese Werte stammen aus testfeed.py, nicht aus ATAS. Live-Abnahme steht noch aus.</div>}
        <div className="grid gap-2 text-[11px] sm:grid-cols-2 lg:grid-cols-5">
          <div><div className="text-[var(--fg-faint)]">Quelle</div>
            <div className="flex items-center gap-1.5 text-[var(--fg)]">{s?.source ? <>{s.source === "test" ? "Testfeed" : "ATAS"} <ProvChip source={s.sourceKind} testid="live-source-chip" /></> : "–"}</div>
            <div className="text-[var(--fg-faint)]">{s?.bridge ?? ""}{s?.atasVersion ? ` · ATAS ${s.atasVersion}` : ""}</div></div>
          <div><div className="text-[var(--fg-faint)]">Instrument / Kontrakt</div>
            <div className="font-mono text-[var(--fg)]" data-testid="live-instrument">{s?.instrument ?? "–"}{s?.exchange ? ` · ${s.exchange}` : ""}</div>
            <div className="text-[var(--fg-faint)]">{s?.instrument ? "Name laut ATAS-InstrumentInfo (nicht umgedeutet)" : ""}</div></div>
          <div><div className="text-[var(--fg-faint)]">Session</div><div className="text-[var(--fg)]" data-testid="live-session">{s?.session ?? "–"}</div>
            <div className="text-[var(--fg-faint)]">{s ? `Modus: ${s.mode === "live" ? "live" : s.mode === "replay_oder_historie" ? "Replay/Historie" : "unbekannt"}` : ""}</div></div>
          <div><div className="text-[var(--fg-faint)]">Letztes Marktereignis</div><div className="font-mono text-[var(--fg)]" data-testid="live-last-event">{fmtTime(s?.lastMarketTime, tz)}</div>
            <div className="text-[var(--fg-faint)]">Empfang zuletzt {fmtTime(s?.lastMessageUtc, tz)}</div></div>
          <div><div className="text-[var(--fg-faint)]">Verbindung</div>
            <div className="font-semibold" style={{ color: st?.color }} data-testid="live-connection">{err ? "Backend nicht erreichbar" : st?.text ?? "…"}</div>
            <div className="text-[var(--fg-faint)]">{s?.statusNote}</div></div>
        </div>
        {err && <p className="mt-1 text-xs text-[var(--red)]">{err}</p>}
        {data?.listener.error && <p className="mt-1 text-xs text-[var(--red)]">Bridge-Empfang: {data.listener.error}</p>}
        <div className="mt-2"><ProvLegend /></div>
      </section>

      {s?.status === "nie_verbunden" && <SetupHelp port={data?.listener.port ?? 5036} />}

      {/* ---------------- Preis + Footprint im Mittelpunkt ---------------- */}
      <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
        <div className="flex flex-wrap items-center gap-1.5">
          <b className="text-[12px] text-[var(--fg)]">Preis · 1-Minuten-Kerzen</b><ProvChip source={candles[0]?.source ?? "derived"} />
          <span className="text-[10px] text-[var(--fg-faint)]">von uns aus empfangenen Trades berechnet (UTC-Minutenraster){s?.timeFrame ? ` · ATAS-Chart: ${s.chartType} ${s.timeFrame}` : ""}</span>
          <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
          {chip("delta", "Delta/CVD")}{chip("dom", "DOM")}{chip("tape", "Tape")}{chip("trades", "Trades")}
        </div>
        <div className="grid gap-2 xl:grid-cols-[minmax(0,1fr)_300px]">
          <div>
            {candles.length ? (
              <TickReplayChart slots={slots} currentSlot={currentSlot} lastPrice={s?.lastPrice.value ?? null} follow markers={[]} delta={on("delta") ? dc : null} height={460}
                onSlotClick={(i) => setFpSel(i)} selectedSlot={fpIdx} />
            ) : <div className="flex h-[200px] items-center justify-center text-[12px] text-[var(--fg-dim)]" data-testid="live-no-candles">noch keine Trades empfangen – keine Kerze (nichts wird erfunden)</div>}
            {on("delta") && s && <div className="mt-1 flex flex-wrap gap-3 text-[11px] text-[var(--fg-dim)]">
              <span>CVD {valueText(s.cvd)} <ProvChip source={s.cvd.source} /> <span className="text-[var(--fg-faint)]">{s.cvdAnchor}</span></span>
              <span>VWAP {valueText(s.sessionVwap, 2)} <ProvChip source={s.sessionVwap.source} /> <span className="text-[var(--fg-faint)]">{s.vwapAnchor}</span></span>
            </div>}
          </div>
          <div className="space-y-1">
            <div className="flex items-center gap-1 text-[10px]">
              <span className="text-[var(--fg-faint)]">Footprint aus</span>
              {(["atas", "derived"] as const).map((k) => (
                <button key={k} onClick={() => setFpSource(k)} aria-pressed={fpSource === k} data-testid={`live-fp-${k}`}
                  className={cn("rounded border px-1.5", fpSource === k ? "border-[var(--cyan)] text-[var(--fg)]" : "border-[var(--line-2)] text-[var(--fg-faint)]")}>
                  {k === "atas" ? "ATAS-Chartkerze" : "unseren Trades"}</button>
              ))}
              <ProvChip source={fpUse?.source} />
            </div>
            {fpSource === "atas" && fpCandle && !atasBar && <div className="text-[10px] text-[var(--gold)]">Für diese Minute keine ATAS-Chartkerze empfangen (anderer Chart-Zeitrahmen oder Bridge sendet keine Kerzen) – zeige unsere Berechnung.</div>}
            <FootprintLadder levels={fpLevels} title={fpCandle ? `${fmtTime(fpCandle.tStart, tz).slice(0, 5)}` : ""} partial={!!fpCandle && !fpCandle.closed} />
          </div>
        </div>
      </section>

      {/* ---------------- Bid/Ask, DOM, Tape, Trades ---------------- */}
      {s && <section className="grid gap-2 text-[11px] lg:grid-cols-3">
        {on("dom") && <div className={box} data-testid="live-dom">
          <div className="mb-1 flex items-center gap-1.5"><b className="text-[var(--fg)]">Bid/Ask &amp; DOM</b><ProvChip source={s.book.source} /></div>
          <div className="font-mono">Bid {valueText(s.bid)} · Ask {valueText(s.ask)}</div>
          <div className={cn("mt-1", s.book.state === "vollstaendig" ? "text-[var(--fg-dim)]" : "text-[var(--gold)]")} data-testid="live-book-state">Buch: {BOOK_STATE_TEXT[s.book.state] ?? s.book.state} · beobachtete Level Bid {s.book.bidLevels} / Ask {s.book.askLevels}</div>
          <div className="text-[10px] text-[var(--fg-faint)]">{s.book.note}{s.book.bidLevels <= 1 && s.book.askLevels <= 1 && s.counters.domUpdates > 0 ? " Nur eine Preisstufe je Seite – Feed liefert offenbar nur Top-of-Book." : ""}</div>
          {(s.book.state === "vollstaendig" || s.book.state === "unvollstaendig") && (
            <table className="mt-1 w-full font-mono"><tbody>
              {[...s.book.asks].slice(0, 8).reverse().map((l) => <tr key={`a${l.price}`}><td /><td className="text-center">{l.price}</td><td className="text-[var(--red)]">{nf.format(l.volume)}</td></tr>)}
              {s.book.bids.slice(0, 8).map((l) => <tr key={`b${l.price}`}><td className="text-right text-[var(--key)]">{nf.format(l.volume)}</td><td className="text-center">{l.price}</td><td /></tr>)}
            </tbody></table>
          )}
        </div>}
        {on("tape") && <div className={box} data-testid="live-tape">
          <div className="mb-1 flex items-center gap-1.5"><b className="text-[var(--fg)]">Tape (kumulierte Trades)</b><ProvChip source={s.sourceKind} /></div>
          <div className="text-[var(--fg-dim)]">Volumen {valueText(s.tapeVolume)} · neu {s.counters.cumNew} · Updates {s.counters.cumUpdates} <span className="text-[var(--fg-faint)]">(Updates ersetzen den Stand, kein Doppelzählen)</span></div>
          <div className="max-h-[220px] overflow-y-auto"><table className="w-full font-mono"><tbody>
            {s.tape.map((c) => <tr key={c.id}><td className="pr-1 text-[var(--fg-faint)]">{fmtTime(c.tMarket, tz).slice(0, 12)}</td><td className="pr-1" style={{ color: c.side === "Buy" ? "#3cf0a0" : c.side === "Sell" ? "#f0566a" : undefined }}>{c.side}</td>
              <td className="pr-1">{c.firstPrice}{c.lastPrice !== c.firstPrice ? `→${c.lastPrice}` : ""}</td><td className="pr-1 text-right">{nf.format(c.volume)}</td><td className="text-[var(--fg-faint)]">{c.updates ? `${c.updates} Upd.` : ""}</td></tr>)}
          </tbody></table></div>
        </div>}
        {on("trades") && <div className={box} data-testid="live-trades">
          <div className="mb-1 flex items-center gap-1.5"><b className="text-[var(--fg)]">Trades</b><ProvChip source={s.sourceKind} /><span className="text-[var(--fg-faint)]">Marktzeit · Preis · Volumen · Aggressor</span></div>
          <div className="max-h-[220px] overflow-y-auto"><table className="w-full font-mono"><tbody>
            {s.trades.map((t) => <tr key={t.seq}><td className="pr-1 text-[var(--fg-faint)]">{fmtTime(t.tMarket, tz).slice(0, 12)}</td><td className="pr-1">{t.price}</td><td className="pr-1 text-right">{nf.format(t.volume)}</td>
              <td style={{ color: t.side === "Buy" ? "#3cf0a0" : t.side === "Sell" ? "#f0566a" : "#8b9bb0" }}>{t.side === "Unknown" ? "Richtung unbekannt" : t.side}</td></tr>)}
          </tbody></table>{!s.trades.length && <div className="text-[var(--fg-dim)]">noch kein Trade</div>}</div>
        </div>}
      </section>}

      {s && <Diagnostics s={s} dps={rate.current.dps} pps={rate.current.pps} polls={polls} />}
    </div>
  );
}

function SetupHelp({ port }: { port: number }) {
  return (
    <section className="rounded-lg border border-[var(--gold)] bg-[var(--panel)] p-3 text-[11px] text-[var(--fg-dim)] space-y-1" data-testid="live-setup">
      <b className="text-[var(--fg)]">Kein Live-Feed verbunden</b>
      <ol className="list-decimal pl-4 space-y-0.5">
        <li>Bridge bauen: <code>powershell -ExecutionPolicy Bypass -File research\atas-live-bridge\build.ps1</code></li>
        <li>DLL nach <code>%APPDATA%\ATAS\Indicators</code> kopieren (ATAS lädt neue Indikatoren ggf. erst nach Neustart).</li>
        <li>In ATAS einen MNQ-Chart öffnen → Indikatoren → „OF Live Bridge (read-only)“ hinzufügen. Port {port}, nur 127.0.0.1.</li>
      </ol>
      <div>Oberfläche ohne ATAS prüfen: <code>python research\atas-live-bridge\testfeed.py --seconds 150</code> (wird als TESTDATEN gekennzeichnet).</div>
    </section>
  );
}

function Diagnostics({ s, dps, pps, polls }: { s: LiveStateResponse["state"]; dps: number; pps: number; polls: number }) {
  const [open, setOpen] = useState(false);
  const [cmp, setCmp] = useState<LiveComparison[] | null>(null);
  useEffect(() => {
    if (!open) return;
    let stop = false;
    const load = () => marketdataApi.liveCompare().then((c) => !stop && setCmp(c)).catch(() => !stop && setCmp([]));
    load();
    const id = setInterval(load, 5000);
    return () => { stop = true; clearInterval(id); };
  }, [open, s.generation]);
  const c = s.counters;
  const lost = c.seqMissing + c.backendDropped + c.bridgeDropped + c.parseErrors;
  return (
    <details className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 text-[11px]" onToggle={(e) => setOpen((e.target as HTMLDetailsElement).open)} data-testid="live-diagnostics">
      <summary className="cursor-pointer text-[var(--fg)]">
        Diagnose · empfangen {nf.format(c.linesReceived)} · verarbeitet {nf.format(c.processed)} · verloren/verworfen <span className={lost ? "text-[var(--red)]" : ""} data-testid="live-lost">{nf.format(lost)}</span>
        <span className="text-[var(--fg-faint)]"> · Daten {dps.toFixed(0)}/s · Anzeige {pps.toFixed(1)} Abrufe/s</span>
      </summary>
      <div className="mt-2 grid gap-2 md:grid-cols-2">
        <table className="font-mono"><tbody>
          {[["Zeilen empfangen", c.linesReceived], ["verarbeitet", c.processed], ["Sequenzlücken (fehlende Nr.)", `${c.seqGaps} (${c.seqMissing})`], ["Backend-Puffer verworfen", c.backendDropped],
            ["Bridge verworfen (Puffer/offline)", `${c.bridgeDropped} ${Object.keys(c.bridgeDropsByStream).length ? JSON.stringify(c.bridgeDropsByStream) : ""}`], ["Parse-Fehler", c.parseErrors], ["unbekannte Typen", c.unknownTypes],
            ["Trades / BBO / DOM / Snapshots", `${c.trades} / ${c.bboUpdates} / ${c.domUpdates} / ${c.domSnapshots}`], ["kumul. neu / Updates", `${c.cumNew} / ${c.cumUpdates}`], ["ATAS-Kerzen / Heartbeats", `${c.bars} / ${c.heartbeats}`],
            ["Anzeige-Abrufe gesamt", polls]].map(([k, v]) => <tr key={String(k)}><td className="pr-3 text-[var(--fg-faint)]">{k}</td><td>{String(v)}</td></tr>)}
        </tbody></table>
        <div className="space-y-1 text-[var(--fg-dim)]">
          <div>Generation {s.generation} · Bridge-Sitzung {s.bridgeSessionId ?? "–"} · verbunden seit {s.connectedUtc ?? "–"}{s.disconnectedUtc ? ` · zuletzt getrennt ${s.disconnectedUtc}` : ""}</div>
          <div>Modus: {s.modeEvidence}</div>
          {s.coverageBreaks.length > 0 && <div className="text-[var(--gold)]">Unterbrechungen: {s.coverageBreaks.join(" · ")}</div>}
        </div>
      </div>
      <div className="mt-3">
        <b className="text-[var(--fg)]">Indikatoren – Herkunft</b>
        <table className="mt-1 w-full"><tbody>{s.indicators.map((i) => <tr key={i.name} className="align-top"><td className="pr-3 text-[var(--fg)]">{i.name}</td><td className="pr-3 font-mono text-[var(--cyan)]">{i.category}</td><td className="text-[var(--fg-dim)]">{i.detail}</td></tr>)}</tbody></table>
      </div>
      <div className="mt-3" data-testid="live-compare">
        <b className="text-[var(--fg)]">Vergleich ATAS-Chartkerze ↔ Dashboard (gleiches Intervall, gleiche Trades)</b>
        {!cmp ? <div className="text-[var(--fg-dim)]">lädt …</div> : !cmp.length ? <div className="text-[var(--fg-dim)]">Noch keine geschlossene ATAS-Kerze empfangen.</div> : cmp.slice(-3).reverse().map((k) => (
          <div key={k.bar} className="mt-1">
            <div className="text-[var(--fg-dim)]">Kerze #{k.bar} {k.tStart} – {k.tEnd} · <span className={k.status.startsWith("verglichen") ? "text-[var(--key)]" : "text-[var(--gold)]"}>{k.status}</span></div>
            {k.rows.length > 0 && <table className="font-mono"><thead><tr className="text-left text-[var(--fg-faint)]"><th className="pr-3 font-normal">Feld</th><th className="pr-3 font-normal">ATAS</th><th className="pr-3 font-normal">Dashboard</th><th className="font-normal">Abweichung</th></tr></thead>
              <tbody>{k.rows.map((r) => <tr key={r.field}><td className="pr-3">{r.field}</td><td className="pr-3">{r.atas ?? "–"}</td><td className="pr-3">{r.dashboard ?? "–"}</td><td className={r.difference ? "text-[var(--red)]" : "text-[var(--key)]"}>{r.difference ?? "–"}</td></tr>)}</tbody></table>}
          </div>
        ))}
      </div>
    </details>
  );
}
