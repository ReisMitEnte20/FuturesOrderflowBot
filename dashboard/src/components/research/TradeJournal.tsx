import type { WsCandle, WsSignal, WsTrade } from "@/lib/workspace";
import { tradeMath, tradeRef, type TradeCosts } from "@/lib/tradeReplay";

// Trade-Journal des Orderflow-Laufs (rueckblickend: abgeschlossene Trades mit Ergebnis) + Ergebnisrechnung je Trade.
// Zeiten in Marktzeit America/New_York; Werte ausschliesslich aus der Laufdatei. Fehlendes heisst "nicht gespeichert".

const KEY = "#3cf0a0", RED = "#f0566a", GOLD = "#e8c069";
export const ET_TZ = "America/New_York";
const etFmt = new Intl.DateTimeFormat("de-DE", { timeZone: ET_TZ, hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" });
const tzFmt = new Intl.DateTimeFormat("en-US", { timeZone: ET_TZ, timeZoneName: "short" });
const tzShort = (ms: number) => tzFmt.formatToParts(ms).find((p) => p.type === "timeZoneName")?.value ?? "";
const usd = (n: number | null | undefined) => (n == null || Number.isNaN(n) ? "—" : (n < 0 ? "−" : "") + "$" + Math.abs(n).toFixed(2));
const px = (n: number | null | undefined) => (n == null ? "—" : String(n));
const sideCol = (s: string) => (s === "Long" ? KEY : s === "Short" ? RED : "var(--fg-dim)");

/** Marktzeit ET; exakte Nachkommastellen aus ISO (100 ns), sonst ms. */
export function fmtEt(iso: string | null | undefined, ms: number): string {
  if (iso) {
    const m = /^(.*?)(?:\.(\d+))?Z$/.exec(iso);
    const base = Date.parse((m?.[1] ?? iso) + "Z");
    return `${etFmt.format(base)}.${(m?.[2] ?? "").padEnd(7, "0").slice(0, 7)}`;
  }
  return `${etFmt.format(ms)}.${String(((ms % 1000) + 1000) % 1000).padStart(3, "0")}`;
}
export const etZone = (ms: number) => tzShort(ms);

export function TradeJournal({ trades, candidate, selected, onSelect, onReplay, replayBlocked }: {
  trades: WsTrade[]; candidate: string; selected: number | null;
  onSelect: (i: number) => void; onReplay: (t: WsTrade) => void; replayBlocked: string | null;
}) {
  const z = trades.length ? etZone(trades[0].entryTimeMs) : "";
  return (
    <div className="h-full overflow-auto text-[10.5px] mono" data-testid="trade-journal">
      <table className="w-full border-collapse">
        <thead className="sticky top-0 bg-[var(--panel)] text-[var(--fg-faint)] text-left">
          <tr>
            <th className="px-1 py-1">#</th><th className="px-1">Seite</th><th className="px-1 text-right">Menge</th>
            <th className="px-1">Entry ({ET_TZ}{z ? `, ${z}` : ""})</th><th className="px-1 text-right">Entry-Fill</th>
            <th className="px-1">Exit</th><th className="px-1 text-right">Exit-Fill</th>
            <th className="px-1 text-right">Brutto</th><th className="px-1 text-right">Gebühren</th><th className="px-1 text-right">Netto</th>
            <th className="px-1">Aktion</th>
          </tr>
        </thead>
        <tbody>
          {trades.map((t) => (
            <tr key={t.index} onClick={() => onSelect(t.index)} data-trade-index={t.index}
              className={`border-t border-[var(--line)] cursor-pointer ${selected === t.index ? "bg-[rgba(60,240,160,.08)]" : "hover:bg-[rgba(255,255,255,.03)]"}`}>
              <td className="px-1 py-[2px]">{t.index + 1}</td>
              <td className="px-1" style={{ color: sideCol(t.side) }}>{t.side}</td>
              <td className="px-1 text-right">{t.qty}</td>
              <td className="px-1 text-[var(--fg-dim)]">{fmtEt(t.entryTimeUtc, t.entryTimeMs).slice(0, 12)}</td>
              <td className="px-1 text-right">{t.entryPrice}</td>
              <td className="px-1 text-[var(--fg-dim)]">{fmtEt(t.exitTimeUtc, t.exitTimeMs).slice(0, 12)}</td>
              <td className="px-1 text-right">{t.exitPrice}</td>
              <td className="px-1 text-right">{t.grossPnL == null ? <span className="text-[var(--fg-faint)]" title="je Trade nicht gespeichert">n. gesp.</span> : usd(t.grossPnL)}</td>
              <td className="px-1 text-right">{t.fees == null ? <span className="text-[var(--fg-faint)]" title="je Trade nicht gespeichert">n. gesp.</span> : usd(t.fees)}</td>
              <td className="px-1 text-right" style={{ color: t.netPnL >= 0 ? KEY : RED }}>{usd(t.netPnL)}</td>
              <td className="px-1">
                <button disabled={!!replayBlocked} title={replayBlocked ?? `${candidate} #${t.index + 1} im Tick-Replay öffnen (pausiert vor dem Signal)`}
                  onClick={(e) => { e.stopPropagation(); onSelect(t.index); onReplay(t); }} data-testid="journal-replay"
                  className="h-[20px] px-1.5 rounded border border-[var(--line-2)] text-[var(--key)] disabled:opacity-40 whitespace-nowrap">Im Replay ansehen</button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
      {trades.length === 0 && <div className="p-2 text-[var(--fg-faint)]">keine abgeschlossenen Trades</div>}
    </div>
  );
}

/** Was die Strategie beim Signal kannte: die gespeicherte Signal-Bar (Schluss = Signalzeitpunkt). */
export function SignalFacts({ s, bar, ticksPerBar, label }: { s: WsSignal | null | undefined; bar: WsCandle | null | undefined; ticksPerBar: number; label: string }) {
  if (!s) return <div><b>{label}:</b> <span className="text-[var(--fg-faint)]">Signalzeitpunkt nicht gespeichert (Laufdatei ohne Signal-Mitschnitt) – kein Marker, keine Schätzung</span></div>;
  return (
    <div>
      <b>{label}:</b> <span style={{ color: sideCol(s.direction) }}>{s.direction}</span> · Schluss Signal-Bar #{s.barIndex} ({ticksPerBar} Ticks) · {fmtEt(s.timeUtc, 0)} {etZone(Date.parse(s.timeUtc.replace(/\.\d+Z$/, "Z")))}
      {" "}· Referenzpreis (Bar-Close) {s.referencePrice} · Position davor {s.positionBefore}
      {bar && <> · Signal-Bar O {bar.o} H {bar.h} L {bar.l} C {bar.c} · Vol {bar.v} · Δ {bar.delta > 0 ? "+" : ""}{bar.delta}</>}
      {" "}· Grund: {s.reason ? <span className="text-[var(--fg)]">{s.reason}</span> : <span className="text-[var(--fg-faint)]">nicht gespeichert</span>}
      {s.triggeredConditions && s.triggeredConditions.length > 0 && <> · Bedingungen: {s.triggeredConditions.join(", ")}</>}
    </div>
  );
}

/** Ergebnisrechnung eines ABGESCHLOSSENEN Trades: Fills, Brutto, Gebühren, Netto + Abgleich mit dem Journal. */
export function TradeCalc({ t, costs, runId, candidate, withRef = true }: { t: WsTrade; costs: TradeCosts; runId: string; candidate: string; withRef?: boolean }) {
  const m = tradeMath(t, costs);
  const dirTxt = m.dir === 1 ? "+1 (Long)" : "−1 (Short)";
  return (
    <div className="space-y-0.5 leading-snug" data-testid="trade-calc"
      data-calc={JSON.stringify({ gross: m.grossFromFills, grossStored: m.grossStored, fees: m.fees, net: m.netComputed, netStored: m.netStored, ok: m.netMatches, slip: m.slippage })}>
      {withRef && <div className="text-[var(--fg-faint)]">Trade-Referenz: {tradeRef(runId, candidate, t)}</div>}
      <div>Menge <b>{m.qty}</b> · Richtung {dirTxt} · Punktwert {m.pointValue ?? "nicht gespeichert"} $ · Tickgröße {costs.tickSize ?? "nicht gespeichert"}</div>
      <div>Entry-Fill <b>{t.entryPrice}</b>{t.entryTickPrice != null ? <> (Tickpreis {t.entryTickPrice}, Slippage {m.slippage?.entryTicks} Tick adverse – im Fill enthalten)</> : <span className="text-[var(--fg-faint)]"> (Tickpreis vor Slippage nicht gespeichert)</span>}
        {" "}· Exit-Fill <b>{t.exitPrice}</b>{t.exitTickPrice != null ? <> (Tickpreis {t.exitTickPrice}, Slippage {m.slippage?.exitTicks} Tick adverse – im Fill enthalten)</> : <span className="text-[var(--fg-faint)]"> (Tickpreis nicht gespeichert)</span>}</div>
      <div>Brutto = ({t.exitPrice} − {t.entryPrice}) × {m.dir} × {m.qty} × {px(m.pointValue)} = <b>{usd(m.grossFromFills)}</b>
        {m.grossStored != null && <> · Engine-Brutto {usd(m.grossStored)} {m.grossStored === m.grossFromFills ? <span style={{ color: KEY }}>✓</span> : <span style={{ color: RED }}>≠</span>}</>}</div>
      <div>Gebühren <b>{usd(m.fees)}</b> <span className="text-[var(--fg-faint)]">({m.feesSource})</span></div>
      <div>Netto = Brutto − Gebühren = <b style={{ color: (m.netComputed ?? 0) >= 0 ? KEY : RED }}>{usd(m.netComputed)}</b> · Journal {usd(m.netStored)} →{" "}
        {m.netMatches == null ? <span className="text-[var(--fg-faint)]">nicht prüfbar</span> : m.netMatches ? <span style={{ color: KEY }}>übereinstimmend</span> : <span style={{ color: RED }}>ABWEICHUNG</span>}</div>
      <div className="text-[var(--fg-faint)]">
        Slippage ({costs.slippageTicks ?? "?"} Tick/Seite laut Lauf) steckt bereits in den Fillpreisen (FillModel: Market = Tickpreis ± Slippage) und wird NICHT zusätzlich abgezogen
        {m.slippage && !Number.isNaN(m.slippage.money) ? <> – entspricht {usd(m.slippage.money)} in diesem Trade (nur Information).</> : "."}
        {" "}Kostenquelle: {costs.source}{costs.feePerSide != null ? ` (${costs.feePerSide} $/Seite)` : ""}.
        {m.missing.length > 0 && <> Nicht gespeichert: {m.missing.join(", ")}.</>}
      </div>
    </div>
  );
}

export function ExitFacts({ t, bar, ticksPerBar }: { t: WsTrade; bar: WsCandle | null | undefined; ticksPerBar: number }) {
  return (
    <>
      <SignalFacts s={t.exitSignal} bar={bar} ticksPerBar={ticksPerBar} label="Exit-Auslöser (Strategiesignal)" />
      <div><b>Exitgrund-Text:</b> {t.exitReason ? t.exitReason : <span className="text-[var(--fg-faint)]">nicht gespeichert</span>}
        {t.signalLink && t.signalLink !== "verknuepft" && <span className="text-[var(--fg-faint)]"> · Signal-Zuordnung: {t.signalLink}</span>}</div>
    </>
  );
}

export { GOLD };
