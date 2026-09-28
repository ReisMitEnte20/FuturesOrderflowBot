import { useState, useEffect } from "react";
import { Card, CardHeader, CardBody } from "@/components/common/Card";
import { Button } from "@/components/common/Button";
import { Badge } from "@/components/common/Badge";
import {
  loadMarketDataSettings,
  saveMarketDataSettings,
  clearMarketDataSettings,
  DEFAULT_SETTINGS,
  type MarketDataSettings,
  type MarketDataProvider,
} from "@/lib/marketdataSettings";
import { RithmicConformanceCard } from "@/components/trading/RithmicConformanceCard";
import { Settings, Trash2, CheckCircle, Database, Clock, ChevronDown, AlertTriangle } from "lucide-react";

const PROVIDERS: { value: MarketDataProvider; label: string }[] = [
  { value: "rithmic", label: "Rithmic" },
  { value: "csv", label: "CSV Import" },
  { value: "sierra", label: "Sierra Chart" },
  { value: "atas", label: "ATAS" },
];

const SYMBOLS = ["NQ", "MNQ", "MES", "CL", "GC", "ES", "RTY", "ZN"];
const INTERVALS = ["1m", "5m", "15m", "30m", "1h", "4h", "1d"];

export function SettingsPage() {
  const [settings, setSettings] = useState<MarketDataSettings>(() => loadMarketDataSettings());
  const [saved, setSaved] = useState(false);
  const [providerOpen, setProviderOpen] = useState(false);

  useEffect(() => {
    if (saved) {
      const timer = setTimeout(() => setSaved(false), 3000);
      return () => clearTimeout(timer);
    }
  }, [saved]);

  const handleSave = () => {
    saveMarketDataSettings(settings);
    setSaved(true);
  };

  const handleClear = () => {
    clearMarketDataSettings();
    setSettings({ ...DEFAULT_SETTINGS });
    setSaved(true);
  };

  return (
    <div className="p-4 space-y-4 max-w-xl mx-auto">
      <div className="flex items-center justify-between">
        <h1 className="font-mono text-lg font-bold flex items-center gap-2">
          <Settings className="h-5 w-5" />
          Market Data
        </h1>
        {settings.provider && <Badge variant="info">{settings.provider}</Badge>}
      </div>

      {/* Provider Selection */}
      <Card>
        <CardHeader>
          <p className="text-sm font-mono text-[var(--fg)] flex items-center gap-2">
            <Database className="h-4 w-4" />
            Data Source
          </p>
        </CardHeader>
        <CardBody>
          <div className="relative">
            <button
              onClick={() => setProviderOpen(!providerOpen)}
              className="w-full flex items-center justify-between bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] hover:border-[var(--line-2)]"
            >
              <span>
                {PROVIDERS.find((p) => p.value === settings.provider)?.label || "Select data source"}
              </span>
              <ChevronDown className={`h-4 w-4 text-[var(--fg-faint)] transition-transform ${providerOpen ? "rotate-180" : ""}`} />
            </button>
            {providerOpen && (
              <div className="absolute z-10 w-full mt-1 bg-[var(--panel)] border border-[var(--line)] rounded-md shadow-lg">
                {PROVIDERS.map((p) => (
                  <button
                    key={p.value}
                    onClick={() => {
                      setSettings({ ...settings, provider: p.value });
                                        setProviderOpen(false);
                    }}
                    className={`w-full text-left px-3 py-2 text-sm font-mono hover:bg-[var(--panel-2)] ${
                      settings.provider === p.value ? "text-[var(--key)]" : "text-[var(--fg-dim)]"
                    }`}
                  >
                    {p.label}
                  </button>
                ))}
              </div>
            )}
          </div>
        </CardBody>
      </Card>

      {/* Rithmic */}
      {settings.provider === "rithmic" && (
        <Card>
          <CardHeader>
            <p className="text-sm font-mono text-[var(--fg)] flex items-center gap-2">
              <AlertTriangle className="h-4 w-4 text-[var(--gold)]" />
              Rithmic (R|Protocol, nur Marktdaten)
            </p>
          </CardHeader>
          <CardBody className="space-y-2 text-xs font-mono text-[var(--fg-dim)]">
            <p>
              Login über „Connect“ oben rechts. Verbunden werden nur Ticker- und History-Plant (Live-Trades mit
              Aggressor, BBO, Minuten-Bars) – kein Order-Plant, keine Orders.
            </p>
            <p>
              Backend: <code>Rithmic:Enabled=true</code>, Gateways und App-Name in{" "}
              <code>config/marketdata/rithmic.local.json</code>. Für echte Konten (z. B. LucidTrading) ist eine
              Rithmic-Conformance der App nötig; zuerst gegen „Rithmic Test“ testen.
            </p>
            <p>Zugangsdaten werden hier nicht gespeichert.</p>
          </CardBody>
        </Card>
      )}

      {settings.provider === "rithmic" && <RithmicConformanceCard />}

      {/* General Data Settings */}
      <Card>
        <CardHeader>
          <p className="text-sm font-mono text-[var(--fg)] flex items-center gap-2">
            <Clock className="h-4 w-4" />
            Data Settings
          </p>
        </CardHeader>
        <CardBody className="grid grid-cols-2 gap-4">
          <div>
            <label className="block text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider mb-1">
              Default Symbol
            </label>
            <select
              value={settings.defaultSymbol}
              onChange={(e) => setSettings({ ...settings, defaultSymbol: e.target.value })}
              className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] disabled:opacity-50 focus:border-[var(--key)] focus:outline-none"
            >
              {SYMBOLS.map((s) => <option key={s} value={s}>{s}</option>)}
            </select>
          </div>
          <div>
            <label className="block text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider mb-1">
              Default Interval
            </label>
            <select
              value={settings.defaultInterval}
              onChange={(e) => setSettings({ ...settings, defaultInterval: e.target.value })}
              className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] disabled:opacity-50 focus:border-[var(--key)] focus:outline-none"
            >
              {INTERVALS.map((i) => <option key={i} value={i}>{i}</option>)}
            </select>
          </div>
          <div>
            <label className="block text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider mb-1">
              Lookback (Candles)
            </label>
            <input
              type="number"
              min={10}
              max={10000}
              value={settings.lookbackCandles}
              onChange={(e) => setSettings({ ...settings, lookbackCandles: Number(e.target.value) })}
              className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] disabled:opacity-50 focus:border-[var(--key)] focus:outline-none"
            />
          </div>
          <div>
            <label className="block text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider mb-1">
              Timeout (sec)
            </label>
            <input
              type="number"
              min={5}
              max={300}
              value={settings.timeoutSeconds}
              onChange={(e) => setSettings({ ...settings, timeoutSeconds: Number(e.target.value) })}
              className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] disabled:opacity-50 focus:border-[var(--key)] focus:outline-none"
            />
          </div>
          <div>
            <label className="block text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider mb-1">
              Max Retries
            </label>
            <input
              type="number"
              min={0}
              max={10}
              value={settings.maxRetries}
              onChange={(e) => setSettings({ ...settings, maxRetries: Number(e.target.value) })}
              className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] disabled:opacity-50 focus:border-[var(--key)] focus:outline-none"
            />
          </div>
        </CardBody>
      </Card>

      {/* Actions */}
      <div className="flex items-center gap-2">
        <Button variant="primary" onClick={handleSave}>
          Save
        </Button>
        <Button variant="danger" onClick={handleClear}>
          <Trash2 className="h-3 w-3" />
          Reset
        </Button>
        {saved && (
          <span className="flex items-center gap-1 text-[var(--key)] text-sm">
            <CheckCircle className="h-4 w-4" />
            Saved
          </span>
        )}
      </div>

      {/* Summary */}
      {settings.provider && (
        <Card>
          <CardHeader>
            <p className="text-xs font-mono text-[var(--fg-faint)] uppercase tracking-wider">Summary</p>
          </CardHeader>
          <CardBody>
            <div className="grid grid-cols-2 gap-2 text-xs font-mono">
              <div className="text-[var(--fg-faint)]">Source:</div>
              <div className="text-[var(--fg)]">{settings.provider}</div>
              <div className="text-[var(--fg-faint)]">Symbol:</div>
              <div className="text-[var(--fg)]">{settings.defaultSymbol}</div>
              <div className="text-[var(--fg-faint)]">Interval:</div>
              <div className="text-[var(--fg)]">{settings.defaultInterval}</div>
              <div className="text-[var(--fg-faint)]">Status:</div>
              <div className="text-[var(--fg-faint)]">
                {settings.provider === "rithmic" ? "Login über Connect" : "Lokal"}
              </div>
            </div>
          </CardBody>
        </Card>
      )}
    </div>
  );
}

export default SettingsPage;