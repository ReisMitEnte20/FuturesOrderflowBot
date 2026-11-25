// Herkunft einer geöffneten Aufnahme laut Datenqualitäts-Audit: klarer Hinweis im Chart, wenn die Tickfolge im ATAS-Replay
// aus Minutenkerzen generiert wurde. Abspielen bleibt möglich; BBO/DOM werden nicht als historische Quotes/Markttiefe gezeigt.
import { useEffect, useState } from "react";
import { marketdataApi, type RecordingProvenance } from "@/lib/marketdataApi";
import { ProvChip } from "./Provenance";

export function useRecordingProvenance(recordingId: string | null | undefined) {
  const [p, setP] = useState<RecordingProvenance | null>(null);
  const [err, setErr] = useState<string | null>(null);
  useEffect(() => {
    setP(null); setErr(null);
    if (!recordingId) return;
    const ac = new AbortController();
    marketdataApi.provenance(recordingId, ac.signal).then(setP).catch((e) => !ac.signal.aborted && setErr(String(e?.message ?? e)));
    return () => ac.abort();
  }, [recordingId]);
  return { p, err };
}

/** Bezeichnung des Ereigniszählers: nie „echte Börsentrades“, solange die Herkunft das nicht belegt. */
export function eventLabel(p: RecordingProvenance | null): string {
  if (!p) return "Ereignisse";
  return p.streams?.Trades === "historisch" ? "Trades" : "Replay-Ereignisse";
}

export function RecordingProvenanceBanner({ recordingId, compact }: { recordingId: string | null | undefined; compact?: boolean }) {
  const { p, err } = useRecordingProvenance(recordingId);
  if (!recordingId) return null;
  const generated = p?.banner.startsWith("ATAS-Minutenhistorie");
  const color = generated ? "#f0c35a" : p?.streams?.Trades === "historisch" ? "#22b8f0" : "#8b9bb0";
  return (
    <div className="rounded-md border px-2 py-1.5 text-[11px]" style={{ borderColor: color }} data-testid="recording-provenance"
      data-banner={p?.banner ?? (err ? "fehler" : "lädt")} data-recording={recordingId}>
      <div className="flex flex-wrap items-center gap-2">
        <b style={{ color }} data-testid="recording-provenance-banner">{p ? p.banner : err ? "Herkunft nicht abrufbar" : "Herkunft wird geprüft …"}</b>
        <span className="text-[var(--fg-faint)]">Aufnahme {recordingId}</span>
        {p?.replayType && <span className="text-[var(--fg-faint)]">· ATAS-Replay-Typ {p.replayType}</span>}
      </div>
      {p && !compact && (
        <div className="mt-1 flex flex-wrap gap-x-3 gap-y-0.5 text-[var(--fg-dim)]">
          <span>Minutenwerte (OHLC, Volumen, Bid/Ask-Volumen) <ProvChip source={generated ? "Minutenaggregat" : p.streams?.Trades} /></span>
          <span>Reihenfolge/Zeitpunkt innerhalb der Minute <ProvChip cls={generated ? "computed" : p.streams?.Trades === "historisch" ? "history" : "unavailable"} /></span>
          <span>Bid/Ask-Quotes <ProvChip cls={p.streams?.BBO === "historisch" ? "history" : p.streams?.BBO === "generiert" ? "computed" : "unavailable"} /> {p.streams?.BBO === "historisch" ? "" : "– nicht als historische Quotes angezeigt"}</span>
          <span>DOM <ProvChip cls={p.streams?.DOM === "historisch" ? "history" : p.streams?.DOM === "generiert" ? "computed" : "unavailable"} /> {p.streams?.DOM === "historisch" ? "" : "– eine Preisstufe je Seite, keine historische Markttiefe"}</span>
          <span>Delta, CVD, VWAP, Footprint im Chart <ProvChip cls="computed" /> von uns aus den Replay-Ereignissen berechnet</span>
          {!p.auditFound && <span className="text-[var(--gold)]">{p.notes.join(" ")}</span>}
        </div>
      )}
    </div>
  );
}
