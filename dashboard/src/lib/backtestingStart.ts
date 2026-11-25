// Reine Darstellungslogik des Backtesting-Startbildschirms (ohne React): Startentscheidung aus der Vorschau, gruppierte
// Prüfliste, kurze Fakten. Keine Berechnung von Kennzahlen — nur Zusammenfassung dessen, was das Backend vorab meldet.
import type { BacktestingPreview, PlannedCheck } from "./backtestingApi";

export type DecisionKind = "loading" | "ready" | "limited" | "blocked";

export interface StartDecision {
  kind: DecisionKind;
  title: string;
  /** Fehlende Pflichtdaten (Start gesperrt) — direkt sichtbar. */
  missing: { label: string; detail: string }[];
  /** Nur eingeschränkt auswertbare Analysen (zu wenig Historie) — Start möglich. */
  limited: { label: string; detail: string }[];
}

export interface CheckGroup {
  key: string;
  label: string;
  status: PlannedCheck["status"] | "partial";
  reason?: string | null;
  /** Untervarianten (nur bei Walk-forward). */
  items?: PlannedCheck[];
}

const WF_PREFIX = "oos-";

/** Gruppiert die Prüfliste: die drei Walk-forward-Varianten werden zu EINEM Punkt mit Untereinträgen. */
export function groupChecks(checks: PlannedCheck[]): CheckGroup[] {
  const out: CheckGroup[] = [];
  const wf = checks.filter((c) => c.key.startsWith(WF_PREFIX));
  let wfAdded = false;
  for (const c of checks) {
    if (c.key.startsWith(WF_PREFIX)) {
      if (wfAdded) continue;
      wfAdded = true;
      const ok = wf.filter((x) => x.status === "possible").length;
      const status: CheckGroup["status"] = ok === wf.length ? "possible" : ok === 0 ? (wf.every((x) => x.status === "notApplicable") ? "notApplicable" : "insufficient") : "partial";
      out.push({
        key: "oos", label: `06 Walk-forward-Testdaten (${wf.length} Varianten)`, status, items: wf,
        reason: status === "possible" ? null : `${ok} von ${wf.length} Varianten auswertbar`,
      });
      continue;
    }
    out.push({ key: c.key, label: c.label, status: c.status, reason: c.reason });
  }
  return out;
}

/** Der finale Holdout ist nie eine Prüfung des normalen Laufs — er bleibt unbenutzt. */
export const isHoldout = (g: { key: string }) => g.key === "holdout";

/** Text des Status-Badges VOR dem Start: „geplant“, nicht „wird geprüft“. */
export function plannedText(status: CheckGroup["status"]): string {
  switch (status) {
    case "possible": return "geplant";
    case "partial": return "teilweise";
    case "insufficient": return "zu wenig Historie";
    case "reserved": return "reserviert, unbenutzt";
    default: return "nicht anwendbar";
  }
}

/** Startentscheidung, ausschließlich aus dem tatsächlichen Vorschauzustand abgeleitet. */
export function startDecision(preview: BacktestingPreview | null): StartDecision {
  if (!preview) return { kind: "loading", title: "Daten werden geprüft …", missing: [], limited: [] };
  if (!preview.ok)
    return { kind: "blocked", title: "Start nicht möglich", missing: [{ label: "Datensatz nicht ladbar", detail: preview.error ?? "Unbekannter Fehler." }], limited: [] };
  const missing = (preview.requirements ?? []).filter((r) => !r.met).map((r) => ({ label: r.requirement, detail: r.detail }));
  if (!preview.canStart || missing.length > 0)
    return { kind: "blocked", title: "Start nicht möglich", missing: missing.length ? missing : [{ label: "Datenanforderungen", detail: "Nicht erfüllt." }], limited: [] };
  const limited = groupChecks(preview.checks ?? [])
    .filter((g) => !isHoldout(g) && (g.status === "insufficient" || g.status === "partial"))
    .map((g) => ({ label: g.label, detail: g.status === "partial" ? `${g.reason}; ${(g.items ?? []).filter((i) => i.status !== "possible").map((i) => `${i.label.replace(/^06 Walk-forward-Testdaten:\s*/, "")}: ${i.reason ?? "zu wenig Historie"}`).join(" · ")}` : g.reason ?? "zu wenig Historie" }));
  return limited.length > 0
    ? { kind: "limited", title: "Mit Einschränkungen startbereit", missing: [], limited }
    : { kind: "ready", title: "Startbereit", missing: [], limited: [] };
}

/** Zählung für die Kopfzeile der Prüfliste — der Holdout zählt nie als (geplante oder erledigte) Prüfung. */
export function checkCounts(checks: PlannedCheck[]): { planned: number; limited: number; notApplicable: number; total: number; holdoutReserved: boolean } {
  const g = groupChecks(checks).filter((x) => !isHoldout(x));
  return {
    planned: g.filter((x) => x.status === "possible").length,
    limited: g.filter((x) => x.status === "insufficient" || x.status === "partial").length,
    notApplicable: g.filter((x) => x.status === "notApplicable").length,
    total: g.length,
    holdoutReserved: checks.some((c) => isHoldout(c) && c.status === "reserved"),
  };
}

const de = (v: number, d = 2) => v.toLocaleString("de-DE", { minimumFractionDigits: 0, maximumFractionDigits: d });

/** Die drei entscheidenden Fakten unter der Startentscheidung. */
export function previewFacts(p: BacktestingPreview, ticksPerBar?: string | null, applyFees = true): string[] {
  const tick = p.engine === "orderflow-ticks";
  const bars = tick ? `${ticksPerBar ?? "500"}-Tick-Orderflow-Bars` : `${p.timeframeMinutes}-Minuten-Kerzen`;
  const kind = tick ? "Trade-Ticks mit gemessenem Aggressor, ohne Quotes"
    : /Last|Trade-Ticks/i.test(p.dataKind) ? "aus gehandelten Preisen, ohne Quotes" : p.dataKind;
  const split = `${p.sessions} Handelstage · ${p.developmentSessions} Entwicklung · ` +
    (p.holdoutSessions > 0 ? `${p.holdoutSessions} für den finalen Holdout reserviert` : "kein finaler Holdout");
  const c = p.costs;
  const cost = !c ? "Kosten: –"
    : `${p.feeIsExample ? "Beispielkosten" : "Kosten"}: ${applyFees ? `${de(c.feePerSide)} je Seite` : "Gebühren aus (brutto)"} · ${de(c.slippageTicks)} ${c.slippageTicks === 1 ? "Tick" : "Ticks"} Slippage`;
  return [`${p.symbol} · ${bars} · ${kind}`, split, cost];
}

/** Kurzfassung der Strategieparameter, z. B. „Schneller SMA 9 · Langsamer SMA 21“. */
export function paramSummary(params: { key: string; label: string; default: string }[], values: Record<string, string>): string {
  if (params.length === 0) return "keine einstellbaren Parameter";
  const labels = params.map((p) => p.label.replace(/\s*\(.*?\)\s*/g, " ").trim());
  const vals = params.map((p) => values[p.key] ?? p.default);
  // Gemeinsames Stichwort aller Parameter (z. B. „SMA“ in „Schneller SMA“/„Langsamer SMA“) → „SMA 9/21“.
  const words = labels.map((l) => new Set(l.split(/\s+/)));
  const common = params.length > 1 ? [...words[0]].find((w) => w.length > 1 && words.every((ws) => ws.has(w))) : undefined;
  if (common) return `${common} ${vals.join("/")}`;
  return labels.map((l, i) => `${l} ${vals[i]}`).join(" · ");
}

/** Getesteter Zeitraum eines gespeicherten Laufs (Ende exklusiv → letzter enthaltener Tag). */
export function testedPeriod(fromUtc?: string | null, toUtc?: string | null, dataFrom?: string | null, dataTo?: string | null): string {
  // Bevorzugt der tatsächlich geladene Datenbereich; sonst der angefragte Zeitraum.
  if (dataFrom || dataTo) { fromUtc = dataFrom ?? fromUtc; toUtc = dataTo ?? toUtc; }
  const d = (s: string) => { const x = new Date(s); return Number.isNaN(x.getTime()) ? null : x; };
  const f = fromUtc ? d(fromUtc) : null, t = toUtc ? d(toUtc) : null;
  if (!f && !t) return "ganzer Datensatz";
  const fmt = (x: Date) => `${String(x.getUTCDate()).padStart(2, "0")}.${String(x.getUTCMonth() + 1).padStart(2, "0")}.${x.getUTCFullYear()}`;
  // Exklusives Ende um Mitternacht → der Vortag ist der letzte getestete Tag.
  const last = t && t.getUTCHours() === 0 && t.getUTCMinutes() === 0 ? new Date(t.getTime() - 86_400_000) : t;
  return `${f ? fmt(f) : "Beginn"} – ${last ? fmt(last) : "Ende"}`;
}
