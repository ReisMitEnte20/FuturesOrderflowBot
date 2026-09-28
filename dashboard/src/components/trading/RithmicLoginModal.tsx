import { useEffect, useState } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { Button } from "@/components/common/Button";
import { X } from "lucide-react";

// Vorschläge; maßgeblich ist die System-Liste, die das Gateway beim Connect meldet (Fehlermeldung listet sie).
const SYSTEM_SUGGESTIONS = ["Rithmic Test", "Rithmic Paper Trading", "Rithmic 01", "LucidTrading"];

const API_BASE = "/api/rithmic";

interface RithmicLoginModalProps {
  open: boolean;
  onClose: () => void;
}

export function RithmicLoginModal({ open, onClose }: RithmicLoginModalProps) {
  const creds = useTradingStore((s) => s.rithmicCredentials);
  const setCredentials = useTradingStore((s) => s.setRithmicCredentials);
  const setConnectionStatus = useTradingStore((s) => s.setConnectionStatus);

  const [userId, setUserId] = useState(creds?.userId || "");
  const [password, setPassword] = useState("");
  const [system, setSystem] = useState(creds?.system || SYSTEM_SUGGESTIONS[0]);
  const [gateway, setGateway] = useState(creds?.gateway || "");
  const [gateways, setGateways] = useState<string[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  // Nur lokale Backend-Konfiguration (konfigurierte Gateways) – kein Rithmic-Netzwerkzugriff.
  useEffect(() => {
    if (!open) return;
    fetch(`${API_BASE}/options`)
      .then((r) => r.json())
      .then((data) => {
        if (data.enabled === false) {
          setError(data.message || "Rithmic ist im Backend deaktiviert.");
          return;
        }
        const list: string[] = data.options?.gateways ?? [];
        setGateways(list);
        setGateway((g) => (g && list.includes(g) ? g : list[0] ?? ""));
        if (data.options && !data.options.appConfigured) setError("Rithmic:AppName ist im Backend nicht konfiguriert.");
      })
      .catch(() => setError("Backend nicht erreichbar."));
  }, [open]);

  if (!open) return null;

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    if (!userId.trim() || !password.trim() || !system.trim()) {
      setError("User ID, Password und System sind erforderlich.");
      return;
    }
    if (!gateway) {
      setError("Kein Gateway konfiguriert.");
      return;
    }
    setLoading(true);
    setConnectionStatus("connecting");

    try {
      const response = await fetch(`${API_BASE}/connect`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          userId: userId.trim(),
          password,
          system: system.trim(),
          gateway,
        }),
      });

      const result = await response.json();

      if (response.ok && result.success) {
        // Passwort wird bewusst NICHT gespeichert (nur im Formular-State für den einen Request).
        setCredentials({
          userId: userId.trim(),
          system,
          gateway,
        });
        setConnectionStatus("connected");
        onClose();
      } else {
        setError(result.message || "Connection failed");
        setConnectionStatus("error");
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Network error");
      setConnectionStatus("error");
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/70" onClick={onClose}>
      <div
        className="bg-[var(--panel)] border border-[var(--line)] rounded-lg w-[440px] max-w-[95vw]"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="flex items-center justify-between px-4 py-3 border-b border-[var(--line)]">
          <h3 className="text-sm font-mono font-medium">Rithmic Login</h3>
          <Button variant="default" size="sm" onClick={onClose}>
            <X className="h-4 w-4" />
          </Button>
        </div>

        <form onSubmit={handleSubmit} className="p-4 space-y-4">
          <div className="space-y-1.5">
            <label className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">
              User ID
            </label>
            <input
              type="text"
              value={userId}
              onChange={(e) => setUserId(e.target.value)}
              autoFocus
              className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] focus:outline focus:outline-2 focus:outline-[var(--key)]"
              placeholder="Enter user ID"
            />
          </div>

          <div className="space-y-1.5">
            <label className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">
              Password
            </label>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] focus:outline focus:outline-2 focus:outline-[var(--key)]"
              placeholder="Enter password"
            />
          </div>

          <div className="grid grid-cols-2 gap-4">
            <div className="space-y-1.5">
              <label className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">
                System
              </label>
              <input
                list="rithmic-systems"
                value={system}
                onChange={(e) => setSystem(e.target.value)}
                className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] focus:outline focus:outline-2 focus:outline-[var(--key)]"
              />
              <datalist id="rithmic-systems">
                {SYSTEM_SUGGESTIONS.map((s) => (
                  <option key={s} value={s} />
                ))}
              </datalist>
            </div>

            <div className="space-y-1.5">
              <label className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">
                Gateway
              </label>
              <select
                value={gateway}
                onChange={(e) => setGateway(e.target.value)}
                className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] focus:outline focus:outline-2 focus:outline-[var(--key)]"
              >
                {gateways.length === 0 && <option value="">– keins konfiguriert –</option>}
                {gateways.map((g) => (
                  <option key={g} value={g}>
                    {g}
                  </option>
                ))}
              </select>
            </div>
          </div>

          {error && (
            <p className="text-xs text-[var(--red)] font-mono">{error}</p>
          )}

          <div className="flex items-center justify-end gap-2 pt-2">
            <Button variant="default" size="md" onClick={onClose} disabled={loading}>
              Cancel
            </Button>
            <Button variant="primary" size="md" type="submit" disabled={loading}>
              {loading ? "Connecting..." : "Login"}
            </Button>
          </div>
        </form>
      </div>
    </div>
  );
}