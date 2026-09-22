import { useState, useEffect } from "react";
import { Card, CardHeader, CardBody } from "@/components/common/Card";
import { Button } from "@/components/common/Button";
import { Badge } from "@/components/common/Badge";
import { loadRithmicSettings, saveRithmicSettings, clearRithmicSettings, type RithmicSettings } from "@/lib/rithmicSettings";
import { Settings, Trash2, CheckCircle } from "lucide-react";

export function SettingsPage() {
  const [settings, setSettings] = useState<RithmicSettings>(() => loadRithmicSettings());
  const [saved, setSaved] = useState(false);
  const [showSecret, setShowSecret] = useState(false);

  useEffect(() => {
    if (saved) {
      const timer = setTimeout(() => setSaved(false), 3000);
      return () => clearTimeout(timer);
    }
  }, [saved]);

  const handleSave = () => {
    saveRithmicSettings(settings);
    setSaved(true);
  };

  const handleClear = () => {
    clearRithmicSettings();
    setSettings({ apiKey: "", apiSecret: "", baseUrl: "https://api.rithmic.com" });
    setSaved(true);
  };

  const hasCredentials = settings.apiKey.length > 0 || settings.apiSecret.length > 0;

  return (
    <div className="p-4 space-y-4 max-w-xl mx-auto">
      <div className="flex items-center justify-between">
        <h1 className="font-mono text-lg font-bold flex items-center gap-2">
          <Settings className="h-5 w-5" />
          Rithmic Credentials
        </h1>
        {hasCredentials && <Badge variant="success" dot>Configured</Badge>}
      </div>

      <Card>
        <CardHeader>
          <p className="text-sm font-mono text-[var(--fg)]">API Credentials</p>
        </CardHeader>
        <CardBody className="space-y-4">
          <div>
            <label className="block text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider mb-1">
              API Key
            </label>
            <input
              type="text"
              value={settings.apiKey}
              onChange={(e) => setSettings({ ...settings, apiKey: e.target.value })}
              placeholder="Enter your Rithmic API key"
              className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] placeholder:text-[var(--fg-faint)] focus:border-[var(--key)] focus:outline-none"
            />
          </div>

          <div>
            <label className="block text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider mb-1">
              API Secret
            </label>
            <div className="flex gap-2">
              <input
                type={showSecret ? "text" : "password"}
                value={settings.apiSecret}
                onChange={(e) => setSettings({ ...settings, apiSecret: e.target.value })}
                placeholder="Enter your Rithmic API secret"
                className="flex-1 bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] placeholder:text-[var(--fg-faint)] focus:border-[var(--key)] focus:outline-none"
              />
              <Button variant="default" size="sm" onClick={() => setShowSecret(!showSecret)}>
                {showSecret ? "Hide" : "Show"}
              </Button>
            </div>
          </div>

          <div>
            <label className="block text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider mb-1">
              Base URL
            </label>
            <input
              type="text"
              value={settings.baseUrl}
              onChange={(e) => setSettings({ ...settings, baseUrl: e.target.value })}
              placeholder="https://api.rithmic.com"
              className="w-full bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm font-mono text-[var(--fg)] placeholder:text-[var(--fg-faint)] focus:border-[var(--key)] focus:outline-none"
            />
          </div>

          {saved && (
            <div className="flex items-center gap-2 text-[var(--key)] text-sm">
              <CheckCircle className="h-4 w-4" />
              Saved successfully
            </div>
          )}

          <div className="flex gap-2 pt-2">
            <Button variant="primary" onClick={handleSave}>
              Save Credentials
            </Button>
            <Button variant="danger" onClick={handleClear}>
              <Trash2 className="h-3 w-3" />
              Clear
            </Button>
          </div>
        </CardBody>
      </Card>

      <Card>
        <CardHeader>
          <p className="text-sm font-mono text-[var(--fg)]">Security Notes</p>
        </CardHeader>
        <CardBody>
          <ul className="space-y-2 text-xs text-[var(--fg-dim)]">
            <li className="flex items-start gap-2">
              <span className="text-[var(--gold)]">•</span>
              Credentials are stored in localStorage (browser only)
            </li>
            <li className="flex items-start gap-2">
              <span className="text-[var(--gold)]">•</span>
              Never commit credentials to version control
            </li>
            <li className="flex items-start gap-2">
              <span className="text-[var(--gold)]">•</span>
              Use environment variables for production deployments
            </li>
            <li className="flex items-start gap-2">
              <span className="text-[var(--gold)]">•</span>
              Clear credentials when switching prop firm accounts
            </li>
          </ul>
        </CardBody>
      </Card>
    </div>
  );
}

export default SettingsPage;