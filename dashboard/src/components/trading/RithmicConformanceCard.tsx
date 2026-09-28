import { useEffect, useState } from "react";
import { Card, CardHeader, CardBody } from "@/components/common/Card";
import { Button } from "@/components/common/Button";
import { ShieldCheck } from "lucide-react";

const API_BASE = "/api/rithmic";

interface ConformanceStatus {
  isRunning: boolean;
  username?: string | null;
  gatewayUrl?: string | null;
  startedAt?: string | null;
  lastError?: string | null;
}

const inputClass =
  "w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] placeholder:text-[var(--fg-faint)] focus:border-[var(--key)] focus:outline-none disabled:opacity-50";

/**
 * Rithmic-Conformance-Test: Login am Order Plant von "Rithmic Test" und eingeloggt bleiben (nur Heartbeat,
 * keine Orders). Rithmic gibt danach Produktions-Gateways für Prop-Firm-Konten (z. B. LucidTrading) frei.
 */
export function RithmicConformanceCard() {
  const [userId, setUserId] = useState("");
  const [password, setPassword] = useState("");
  const [gateways, setGateways] = useState<string[]>([]);
  const [gateway, setGateway] = useState("");
  const [status, setStatus] = useState<ConformanceStatus | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  // Nur lokale Backend-Konfiguration/Status – kein Rithmic-Netzwerkzugriff.
  useEffect(() => {
    fetch(`${API_BASE}/options`)
      .then((r) => r.json())
      .then((data) => {
        if (data.enabled === false) {
          setMessage(data.message || "Rithmic ist im Backend deaktiviert.");
          return;
        }
        const list: string[] = data.options?.gateways ?? [];
        setGateways(list);
        setGateway(list.find((g) => g === "Rithmic Test") ?? list[0] ?? "");
      })
      .catch(() => setMessage("Backend nicht erreichbar."));
    loadStatus();
  }, []);

  // Solange die Session läuft, Status regelmäßig lokal abfragen (z. B. ForcedLogout erkennen).
  useEffect(() => {
    if (!status?.isRunning) return;
    const timer = setInterval(loadStatus, 5000);
    return () => clearInterval(timer);
  }, [status?.isRunning]);

  async function loadStatus() {
    try {
      const r = await fetch(`${API_BASE}/conformance/status`);
      if (r.ok) setStatus(await r.json());
    } catch {
      /* Backend nicht erreichbar */
    }
  }

  async function start() {
    if (!userId.trim() || !password) {
      setMessage("User ID und Passwort des Rithmic-Test-Zugangs sind erforderlich.");
      return;
    }
    setBusy(true);
    setMessage(null);
    try {
      const r = await fetch(`${API_BASE}/conformance/start`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ userId: userId.trim(), password, gateway }),
      });
      const result = await r.json();
      setMessage(result.message ?? null);
      if (r.ok && result.success) setPassword("");
    } catch (err) {
      setMessage(err instanceof Error ? err.message : "Network error");
    } finally {
      setBusy(false);
      loadStatus();
    }
  }

  async function stop() {
    setBusy(true);
    try {
      await fetch(`${API_BASE}/conformance/stop`, { method: "POST" });
      setMessage("Conformance-Session beendet.");
    } finally {
      setBusy(false);
      loadStatus();
    }
  }

  const running = status?.isRunning === true;

  return (
    <Card>
      <CardHeader>
        <p className="text-sm font-mono text-[var(--fg)] flex items-center gap-2">
          <ShieldCheck className="h-4 w-4 text-[var(--key)]" />
          Rithmic Conformance (Rithmic Test)
        </p>
      </CardHeader>
      <CardBody className="space-y-3">
        <p className="text-xs font-mono text-[var(--fg-dim)]">
          Meldet die App am Order Plant von „Rithmic Test“ an und hält sie per Heartbeat eingeloggt – ohne Orders.
          Laufen lassen, bis Rithmic die Conformance bestätigt und die Produktions-Gateways mitteilt.
        </p>

        {!running && (
          <>
            <input className={inputClass} placeholder="Rithmic-Test User ID" value={userId} onChange={(e) => setUserId(e.target.value)} disabled={busy} />
            <input className={inputClass} type="password" placeholder="Passwort" value={password} onChange={(e) => setPassword(e.target.value)} disabled={busy} />
            <select className={inputClass} value={gateway} onChange={(e) => setGateway(e.target.value)} disabled={busy}>
              {gateways.length === 0 && <option value="">– keins konfiguriert –</option>}
              {gateways.map((g) => (
                <option key={g} value={g}>
                  {g}
                </option>
              ))}
            </select>
          </>
        )}

        <div className="grid grid-cols-2 gap-2 text-xs font-mono">
          <div className="text-[var(--fg-faint)]">Status:</div>
          <div className={running ? "text-[var(--key)]" : "text-[var(--fg-faint)]"}>{running ? "Läuft" : "Gestoppt"}</div>
          {running && (
            <>
              <div className="text-[var(--fg-faint)]">User:</div>
              <div className="text-[var(--fg)]">{status?.username}</div>
              <div className="text-[var(--fg-faint)]">Seit:</div>
              <div className="text-[var(--fg)]">{status?.startedAt ? new Date(status.startedAt).toLocaleString() : "–"}</div>
            </>
          )}
        </div>

        {(message || status?.lastError) && (
          <p className={`text-xs font-mono ${running ? "text-[var(--fg-dim)]" : "text-[var(--red)]"}`}>{status?.lastError || message}</p>
        )}

        {running ? (
          <Button variant="danger" onClick={stop} disabled={busy} className="w-full">
            Conformance stoppen
          </Button>
        ) : (
          <Button variant="primary" onClick={start} disabled={busy || !gateway} className="w-full">
            {busy ? "Verbinde..." : "Conformance starten"}
          </Button>
        )}
      </CardBody>
    </Card>
  );
}
