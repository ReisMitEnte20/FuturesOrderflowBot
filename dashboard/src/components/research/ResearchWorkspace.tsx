import { useEffect, useMemo, useState } from "react";
import { FootprintChart, type FpBar } from "@/components/research/FootprintChart";
import { CandleProfileChart, type Bubble } from "@/components/research/CandleProfileChart";
import { FlowPanels } from "@/components/research/FlowPanels";
import type { ChartTrade } from "@/lib/replay";
import {
  type WorkspaceData, type WsTrade, type WsRunIndexEntry, cvdSeries, vwapSeries, volumeProfile, barPriceOccupancy, tradeBubbles, buildSnapshot, isAvailable, tapeCoverage,
} from "@/lib/workspace";

const KEY = "#3cf0a0", RED = "#f0566a";
const eur = (n: number) => (n < 0 ? "−" : "") + "$" + Math.abs(n).toFixed(2);
const sideCol = (s: string) => (s === "Long" ? KEY : RED);
const LS = "research-workspace-layout-v1";

interface Layout { modules: Record<string, boolean>; ind: Record<string, boolean>; maximized: boolean; bubbleMinVol: number; }
const DEFAULT: Layout = {
  modules: { footprint: true, candle: true, flow: true, tape: true, status: true },
  ind: { vp: true, vwap: true, tpo: false, bubbles: true },
  maximized: false, bubbleMinVol: 10,
};

export function ResearchWorkspace() {
  const [d, setD] = useState<WorkspaceData | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [stratIdx, setStratIdx] = useState(1);
  const [selected, setSelected] = useState<number | null>(null);
  const [focusKey, setFocusKey] = useState(0);
  const [src, setSrc] = useState<string>("");
  const [runs, setRuns] = useState<WsRunIndexEntry[]>([]);
  const [runId, setRunId] = useState<string>("");
  const [lay, setLay] = useState<Layout>(() => { try { return { ...DEFAULT, ...JSON.parse(localStorage.getItem(LS) || "{}"), modules: { ...DEFAULT.modules, ...(JSON.parse(localStorage.getItem(LS) || "{}").modules || {}) }, ind: { ...DEFAULT.ind, ...(JSON.parse(localStorage.getItem(LS) || "{}").ind || {}) } }; } catch { return DEFAULT; } });
  useEffect(() => { try { localStorage.setItem(LS, JSON.stringify(lay)); } catch { } }, [lay]);

  const applyData = (data: WorkspaceData, source: string) => {
    const of = data.strategies.findIndex((s) => s.usesOrderflow);
    setD(data); setStratIdx(of >= 0 ? of : 0); setSelected(0); setFocusKey((k) => k + 1); setSrc(source); setErr(null);
  };
  const loadRun = async (id: string) => {
    try {
      const r = await fetch(`/orderflow-runs/${id}.json`);
      if (!r.ok) throw new Error(`HTTP ${r.status}`);
      applyData((await r.json()) as WorkspaceData, `orderflow-runs/${id}.json`); setRunId(id);
    } catch (e) { setErr(`Lauf ${id} nicht ladbar: ${String(e)}`); }
  };

  useEffect(() => {
    let ok = true;
    (async () => {
      // 1) read-only Lauf-Index bevorzugen (Auswahl mehrerer gespeicherter Laeufe).
      try {
        const ri = await fetch("/orderflow-runs/index.json");
        if (ri.ok) {
          const list = (await ri.json()) as WsRunIndexEntry[];
          if (!ok) return;
          if (Array.isArray(list) && list.length > 0) {
            setRuns(list);
            await loadRun((list.find((x) => x.loadable !== false) ?? list[0]).id);
            return;
          }
        }
      } catch { /* kein Index -> Fallback */ }
      // 2) Fallback: fester Zwischenstand (orderflow-run.json) bzw. Demo.
      for (const url of ["/orderflow-run.json", "/research-workspace.json"]) {
        try {
          const r = await fetch(url);
          if (!r.ok) continue;
          if (!ok) return;
          applyData((await r.json()) as WorkspaceData, url.replace("/", "")); return;
        } catch { /* naechste Quelle versuchen */ }
      }
      if (ok) setErr("kein Lauf geladen (orderflow-runs/index.json, orderflow-run.json, research-workspace.json fehlen)");
    })();
    return () => { ok = false; };
  }, []);

  const strat = d?.strategies[stratIdx];
  const selTrade: WsTrade | null = strat && selected != null ? strat.trades.find((t) => t.index === selected) ?? null : null;
  const pick = (i: number | null) => { setSelected(i); setFocusKey((k) => k + 1); };

  const fpBars: FpBar[] = useMemo(() => (d ? d.candles.map((c, i) => ({ candle: c, levels: d.footprint[i] ?? [] })) : []), [d]);
  const chartTrades: ChartTrade[] = useMemo(() => (strat ? strat.trades.map((t) => ({ index: t.index, side: t.side, entryBarIndex: t.entryBarIndex, entryPrice: t.entryPrice, exit: { barIndex: t.exitBarIndex, price: t.exitPrice, reason: t.exitReason, ambiguous: t.ambiguous }, stopLossPrice: 0, takeProfitPrice: 0 })) : []), [strat]);
  const cvd = useMemo(() => (d ? cvdSeries(d.candles, 0) : { cvd: [], anchorBar: 0, anchorTime: 0 }), [d]);
  const vwap = useMemo(() => (d ? vwapSeries(d.candles, d.footprint, 0) : { vwap: [], anchorBar: 0, anchorTime: 0 }), [d]);
  const range = useMemo(() => {
    if (!d) return [0, 0] as [number, number];
    if (selTrade) return [Math.min(selTrade.entryBarIndex, selTrade.exitBarIndex), Math.max(selTrade.entryBarIndex, selTrade.exitBarIndex)] as [number, number];
    return [0, d.candles.length - 1] as [number, number];
  }, [d, selTrade]);
  const vp = useMemo(() => (d ? volumeProfile(d.footprint, range[0], range[1]) : null), [d, range]);
  const tpo = useMemo(() => (d ? barPriceOccupancy(d.candles, range[0], range[1]) : null), [d, range]);
  // Tape kann aus Browser-Schutz GEKAPPT sein (nur erste N Ticks). Bubbles & Tape-Ansicht decken dann nur dieses
  // Fenster ab; VWAP/CVD/Volume-Profil/Bars-Preis/Delta kommen dagegen aus der VOLLSTAENDIGEN Tick-Basis (Kerzen/Footprint).
  const cov = useMemo(() => (d ? tapeCoverage(d) : null), [d]);
  const barOfTime = useMemo(() => {
    const starts = d ? d.candles.map((c) => c.t) : [];
    return (t: number) => { let lo = 0, hi = starts.length - 1, r = 0; while (lo <= hi) { const m = (lo + hi) >> 1; if (starts[m] <= t) { r = m; lo = m + 1; } else hi = m - 1; } return r; };
  }, [d]);
  const coverageEndBar = useMemo(() => (cov?.truncated && cov.endMs != null ? barOfTime(cov.endMs) : null), [cov, barOfTime]);

  const bubbles: Bubble[] = useMemo(() => {
    if (!d) return [];
    return tradeBubbles(d.tape, lay.bubbleMinVol).map((b) => ({ barIndex: barOfTime(b.t), p: b.p, v: b.v, dir: b.dir }));
  }, [d, lay.bubbleMinVol, barOfTime]);

  const tapeWin = useMemo(() => {
    if (!d) return [];
    const t0 = selTrade ? selTrade.entryTimeMs - 5000 : d.meta.marketStartMs;
    const t1 = selTrade ? selTrade.exitTimeMs + 5000 : d.meta.marketStartMs + 20000;
    return d.tape.filter((r) => r[0] >= t0 && r[0] <= t1).slice(0, 400);
  }, [d, selTrade]);

  // Liegt das aktuelle Tape-Anzeigefenster ausserhalb der (gekappten) Tape-Abdeckung? Dann ist das Tape hier
  // NICHT verfuegbar (nicht "keine Trades"). Vollstaendige Indikatoren bleiben verfuegbar.
  const tapeWindowOutside = useMemo(() => {
    if (!d || !cov?.truncated || cov.endMs == null) return false;
    const t0 = selTrade ? selTrade.entryTimeMs - 5000 : d.meta.marketStartMs;
    return t0 > cov.endMs;
  }, [d, cov, selTrade]);
  const hhmmss = (ms: number | null) => (ms == null ? "—" : new Date(ms).toISOString().slice(11, 19));

  function exportSnapshot(pit: boolean) {
    if (!d) return;
    const snap = buildSnapshot(d, stratIdx, selTrade, { vwapAnchorBar: 0, cvdAnchorBar: 0, bubbleMinVol: lay.bubbleMinVol, pointInTimeMs: pit && selTrade ? selTrade.entryTimeMs : null });
    const blob = new Blob([JSON.stringify(snap, null, 2)], { type: "application/json" });
    const a = document.createElement("a"); a.href = URL.createObjectURL(blob);
    a.download = `analysis-snapshot${pit ? "-at-entry" : ""}-${strat?.name}-trade${(selected ?? 0) + 1}.json`; a.click(); URL.revokeObjectURL(a.href);
  }

  if (err) return <Box><p className="text-xs text-[var(--red)] mono">Orderflow-Lauf nicht geladen: {err}. Erzeuge ihn lokal mit <b>dotnet run --project src/TradingBot.OrderflowRun</b> (schreibt dashboard/public/orderflow-run.json).</p></Box>;
  if (!d || !strat) return <Box><p className="text-xs text-[var(--fg-faint)]">Lade Orderflow-Arbeitsfläche…</p></Box>;

  const tgl = (grp: "modules" | "ind", k: string) => setLay((p) => ({ ...p, [grp]: { ...p[grp], [k]: !p[grp][k] } }));
  const Btn = ({ on, onClick, children }: any) => <button onClick={onClick} className={`h-[24px] px-2 rounded text-[10.5px] border ${on ? "bg-[var(--key)] text-black border-transparent font-semibold" : "border-[var(--line-2)] text-[var(--fg)]"}`}>{children}</button>;

  const inner = (
    <div className={`flex flex-col gap-2 ${lay.maximized ? "fixed inset-0 z-50 bg-[#05070b] p-3 overflow-auto" : ""}`}>
      {/* Steuerleiste */}
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-sm font-medium text-[var(--fg)]">Orderflow-Arbeitsfläche · {d.meta.instrument}</span>
        <span className="text-[10px] px-1.5 py-0.5 rounded bg-[rgba(240,195,90,.14)] text-[var(--gold,#f0c35a)] border border-[rgba(240,195,90,.4)]">TECHNISCHER TEST · keine Profitabilitätsaussage</span>
        {runs.length > 0 && (
          <select value={runId} onChange={(e) => loadRun(e.target.value)} title="Gespeicherte Orderflow-Läufe" className="h-[26px] bg-[var(--panel-2,#13202e)] text-[var(--fg)] border border-[var(--line-2)] rounded text-[11px] px-1 max-w-[300px]">
            {runs.map((r) => <option key={r.id} value={r.id} disabled={r.loadable === false}>{(r.generatedUtc || r.id).slice(0, 19).replace("T", " ")} · {r.instrument} · {r.ok ? "ok" : "⚠ " + r.status}{r.loadable === false ? " (nicht ladbar)" : ""}</option>)}
          </select>
        )}
        <div className="flex items-center gap-1">
          {d.strategies.map((s, i) => <Btn key={s.name} on={i === stratIdx} onClick={() => { setStratIdx(i); setSelected(0); setFocusKey((k) => k + 1); }}>{s.name}{s.usesOrderflow ? "·OF" : ""}</Btn>)}
        </div>
        <select value={selected ?? ""} onChange={(e) => pick(e.target.value === "" ? null : +e.target.value)} className="h-[26px] bg-[var(--panel-2,#13202e)] text-[var(--fg)] border border-[var(--line-2)] rounded text-[11px] px-1 max-w-[220px]">
          <option value="">Trade wählen…</option>
          {strat.trades.map((t) => <option key={t.index} value={t.index}>#{t.index + 1} {t.side} {t.entryTime}→{t.exitTime} {eur(t.netPnL)}</option>)}
        </select>
        <div className="ml-auto flex items-center gap-1">
          <Btn on={false} onClick={() => exportSnapshot(false)}>Snapshot ⤓</Btn>
          <Btn on={false} onClick={() => exportSnapshot(true)} >Snapshot @t ⤓</Btn>
          <Btn on={lay.maximized} onClick={() => setLay((p) => ({ ...p, maximized: !p.maximized }))}>{lay.maximized ? "Verkleinern ✕" : "Maximieren ⤢"}</Btn>
        </div>
      </div>

      {/* Ausgewählter Trade */}
      <div className="text-[11px] mono text-[var(--fg)]">
        {selTrade ? <>#{selTrade.index + 1} <b style={{ color: sideCol(selTrade.side) }}>{selTrade.side}</b> · Menge {selTrade.qty} · Bar {selTrade.entryBarIndex}→{selTrade.exitBarIndex} · Entry {selTrade.entryPrice.toFixed(2)} ({selTrade.entryTime}) · Exit {selTrade.exitPrice.toFixed(2)} ({selTrade.exitTime}) · Kosten ${(2 * d.meta.feePerSide).toFixed(2)} · <span style={{ color: selTrade.netPnL >= 0 ? KEY : RED }}>Netto {eur(selTrade.netPnL)}</span></> : <span className="text-[var(--fg-faint)]">Kein Trade gewählt — alle Panels zeigen den gesamten Abschnitt.</span>}
      </div>

      {/* Module + Indikator-Toggles + Anker */}
      <div className="flex flex-wrap items-center gap-1 text-[10px] text-[var(--fg-faint)]">
        <span className="uppercase tracking-wide">Module:</span>
        <Btn on={lay.modules.footprint} onClick={() => tgl("modules", "footprint")}>Footprint</Btn>
        <Btn on={lay.modules.candle} onClick={() => tgl("modules", "candle")}>Kerzen+VP/VWAP</Btn>
        <Btn on={lay.modules.flow} onClick={() => tgl("modules", "flow")}>Vol/Δ/CVD</Btn>
        <Btn on={lay.modules.tape} onClick={() => tgl("modules", "tape")}>Tape+DOM</Btn>
        <Btn on={lay.modules.status} onClick={() => tgl("modules", "status")}>Heatmap-Status</Btn>
        <span className="mx-1">·</span><span className="uppercase tracking-wide">Indikatoren:</span>
        <Btn on={lay.ind.vp} onClick={() => tgl("ind", "vp")}>Volume Profile</Btn>
        <Btn on={lay.ind.vwap} onClick={() => tgl("ind", "vwap")}>VWAP</Btn>
        <Btn on={lay.ind.tpo} onClick={() => tgl("ind", "tpo")}>Bars/Preis</Btn>
        <Btn on={lay.ind.bubbles} onClick={() => tgl("ind", "bubbles")}>Bubbles</Btn>
      </div>

      {/* Daten-/Qualitätsstatus */}
      <div className="text-[10px] text-[var(--fg-faint)] leading-snug border border-[var(--line)] rounded px-2 py-1 bg-[var(--bg-2)]">
        <b>Datenstatus:</b> {Object.entries(d.availability).map(([k, v]) => <span key={k} className="mr-2">{k}: <span style={{ color: isAvailable(v) ? KEY : RED }}>{v}</span></span>)}
        <br /><b>Qualität:</b> {d.quality.priceGranularity} · Drops {d.quality.drops} · {d.quality.aggressor} · {d.quality.footprintSource}
        <br /><b>Anker:</b> VWAP & CVD ab Bar 0 (Datenstart {new Date(d.meta.marketStartMs).toISOString().slice(11, 19)} UTC) · Volume-Profile-Bereich Bars {range[0]}–{range[1]} · Bars/Preis-Einheit 1 Bar = {d.meta.ticksPerBar} Ticks (keine Zeit) · Bubbles ≥ {lay.bubbleMinVol} Kontrakte
        {cov?.truncated && <><br /><b style={{ color: RED }}>Tape gekappt:</b> Bubbles &amp; Tape-Ansicht decken NUR die ersten {cov.count.toLocaleString()} von {cov.totalTicks.toLocaleString()} Ticks ab (bis {hhmmss(cov.endMs)} UTC); außerhalb „nicht verfügbar", keine Nullwerte. <b style={{ color: KEY }}>VWAP · CVD · Volume-Profil · Delta · Bars/Preis</b> nutzen die VOLLSTÄNDIGE Tick-Basis (Kerzen/Footprint) über den gesamten Abschnitt.</>}
        {d.provenance && <><br /><b>Lauf:</b> Quelle {src.replace("/", "") || "—"} · {d.provenance.ticks} Ticks → {d.provenance.fullBars} Bars (+{d.provenance.remainderTicks} Rest) · {d.provenance.ticksPerBar} t/Bar · SHA {d.provenance.dataSha256.slice(0, 10)} · {d.provenance.capabilities}</>}
        {strat?.status && <><br /><b>{strat.name}:</b> {strat.status} · Signale {strat.signalsGenerated} · Orders {strat.ordersSubmitted} (Risk-abgelehnt {strat.ordersRejectedByRisk}) · Pending@Ende {strat.ordersUnfilledAtEnd} · offene Position@Ende <span style={{ color: strat.openPositionSide === "Flat" ? "var(--fg-faint)" : strat.openPositionSide === "Long" ? KEY : RED }}>{strat.openPositionSide} {strat.openPositionQty || 0}{strat.openPositionQty ? ` @ ${strat.openPositionAvgPrice?.toFixed(2)}` : ""}</span></>}
        {strat?.endEquityMarkToMarket !== undefined && <><br /><b>Ergebnis {strat.name}:</b> real. Netto {eur(strat.realizedNetPnL ?? 0)} · unreal.@letzter Tick {eur(strat.unrealizedGrossAtLastTick ?? 0)} · Fees ges. {eur(strat.totalFeesPaid ?? 0)} (offene Eintritts-Fee {eur(strat.openPositionEntryFees ?? 0)}, keine Exit-Fee) · End-Equity real {eur(strat.endEquityRealized ?? 0)} / MtM {eur(strat.endEquityMarkToMarket ?? 0)}</>}
      </div>

      {/* Obere Panel-Reihe */}
      <div className="grid gap-2 grid-cols-1 xl:grid-cols-3">
        {lay.modules.footprint && <Panel title="Footprint (Bid links / Ask rechts, je Kerze)" resize>
          <div className="h-full w-full"><FootprintChart bars={fpBars} trades={chartTrades} selected={selected} focusKey={focusKey} onSelectTrade={pick} className="h-full w-full" /></div>
        </Panel>}
        {lay.modules.status && <Panel title="Orderbuch-Heatmap + Trade-Bubbles" resize>
          <Status items={[["Historische Orderbuch-Heatmap", d.availability.orderbookHeatmap], ["DOM-Snapshots", d.availability.dom]]} note={`${d.quality.domNote}. Trade-Bubbles (große Trades aus dem Tape) werden im Kerzenchart dargestellt (Indikator „Bubbles").${cov?.truncated ? ` Tape gekappt: Bubbles nur für die ersten ${cov.count.toLocaleString()} von ${cov.totalTicks.toLocaleString()} Ticks (bis ${hhmmss(cov.endMs)} UTC); danach nicht verfügbar. VWAP/CVD/Volume-Profil/Delta nutzen die vollständige Tick-Basis.` : ""}`} />
        </Panel>}
        {lay.modules.tape && <Panel title="Time & Sales (Tape) + DOM-Ladder" resize>
          <div className="flex flex-col h-full">
            <div className="flex-1 overflow-y-auto text-[10px] mono">
              <div className="flex px-1 py-0.5 text-[var(--fg-faint)] sticky top-0 bg-[var(--panel)]"><span className="w-20">Zeit</span><span className="flex-1 text-right">Preis</span><span className="w-12 text-right">Vol</span><span className="w-12 text-right">Seite</span></div>
              {tapeWin.map((r, i) => <div key={i} className="flex px-1 py-[1px]"><span className="w-20 text-[var(--fg-faint)]">{new Date(r[0]).toISOString().slice(11, 19)}</span><span className="flex-1 text-right text-[var(--fg)]">{r[1]}</span><span className="w-12 text-right">{r[2]}</span><span className="w-12 text-right" style={{ color: r[3] > 0 ? KEY : r[3] < 0 ? RED : "var(--fg-faint)" }}>{r[3] > 0 ? "Buy" : r[3] < 0 ? "Sell" : "?"}</span></div>)}
              {tapeWin.length === 0 && tapeWindowOutside && <div className="px-1 py-2" style={{ color: RED }}>Tape hier nicht verfügbar — außerhalb des exportierten Tape-Fensters (erste {cov?.count.toLocaleString()} von {cov?.totalTicks.toLocaleString()} Ticks, bis {hhmmss(cov?.endMs ?? null)} UTC). Vollständige Indikatoren (VWAP/CVD/Volume-Profil/Delta) nutzen die vollständige Tick-Basis.</div>}
              {tapeWin.length === 0 && !tapeWindowOutside && <div className="px-1 py-2 text-[var(--fg-faint)]">keine Trades im Fenster</div>}
            </div>
            <div className="border-t border-[var(--line)] px-1 py-1 text-[10px]"><span className="text-[var(--fg-faint)]">DOM-Ladder: </span><span style={{ color: RED }}>{d.availability.dom}</span></div>
          </div>
        </Panel>}
      </div>

      {/* Kerzenchart mit VP/VWAP/TPO/Bubbles */}
      {lay.modules.candle && <Panel title={`Kerzen + Volume Profile + VWAP${lay.ind.tpo ? " + Bars/Preis" : ""}${lay.ind.bubbles ? " + Bubbles" : ""} (Bereich Bars ${range[0]}–${range[1]})`} resize minH={lay.maximized ? 480 : 360}>
        <CandleProfileChart candles={d.candles} trades={chartTrades} vwap={vwap.vwap} vwapAnchorBar={0} vp={lay.ind.vp ? vp : null} tpo={lay.ind.tpo ? tpo : null} bubbles={lay.ind.bubbles ? bubbles : []} bubbleCoverageBar={coverageEndBar} show={{ vp: !!lay.ind.vp, vwap: !!lay.ind.vwap, tpo: !!lay.ind.tpo, bubbles: !!lay.ind.bubbles }} selected={selected} focusKey={focusKey} onSelectTrade={pick} className="h-full w-full" />
      </Panel>}

      {/* Volumen / Delta / CVD */}
      {lay.modules.flow && <Panel title="Volumen · Delta · Cumulative Delta (CVD, Anker Bar 0)" resize minH={220}>
        <FlowPanels candles={d.candles} cvd={cvd.cvd} cvdAnchorBar={0} trades={chartTrades} selected={selected} focusKey={focusKey} className="h-full w-full" />
      </Panel>}

      <div className="text-[9.5px] text-[var(--fg-faint)]">Alle Werte <b>rückblickend</b> (finale Ticks); ein exportierter „Snapshot @t" enthält keine späteren Trades/Profile. Footprint/Delta aus allen Trades der Quelle (Tape-Ansicht &amp; Bubbles ggf. gekappt — siehe Anker-Zeile); VWAP=Preis×Volumen (Anker sichtbar); POC/Value-Area 70 % über den gezeigten Bereich; „Bars/Preis" = Anzahl {d.meta.ticksPerBar}-Tick-Bars, deren Spanne den Preis berührt (kein Zeit-am-Preis/TPO, da Tick-Bars keine feste Dauer haben). DOM/Heatmap unvalidiert → als nicht verfügbar gekennzeichnet. Preisgranularität: {d.quality.priceGranularity}.</div>
    </div>
  );
  return <Box>{inner}</Box>;
}

function Box({ children }: { children: React.ReactNode }) { return <div className="rounded-xl border border-[var(--line)] bg-[var(--panel)] p-3">{children}</div>; }

function Panel({ title, children, resize, minH = 300 }: { title: string; children: React.ReactNode; resize?: boolean; minH?: number }) {
  return (
    <div className="border border-[var(--line)] rounded-lg overflow-hidden flex flex-col bg-[#070b11]" style={resize ? { resize: "vertical", overflow: "hidden", minHeight: minH, height: minH } : undefined}>
      <div className="px-2 py-1 text-[10px] uppercase tracking-wide text-[var(--fg-faint)] border-b border-[var(--line)] bg-[var(--bg-2)] flex-shrink-0">{title}</div>
      <div className="flex-1 min-h-0 relative">{children}</div>
    </div>
  );
}

function Status({ items, note }: { items: [string, string][]; note: string }) {
  return (
    <div className="p-3 text-[11px] h-full overflow-auto">
      {items.map(([k, v]) => <div key={k} className="flex items-center gap-2 mb-1"><span className="text-[var(--fg-faint)] w-48">{k}</span><span className="px-1.5 py-0.5 rounded text-[10px]" style={{ color: RED, border: "1px solid rgba(240,86,106,.4)", background: "rgba(240,86,106,.08)" }}>{v}</span></div>)}
      <div className="text-[10px] text-[var(--fg-faint)] mt-2 leading-snug">{note}</div>
    </div>
  );
}
