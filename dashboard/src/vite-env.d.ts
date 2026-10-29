/// <reference types="vite/client" />

declare module "*.scss" {
  const content: string;
  export default content;
}

interface ImportMetaEnv {
  // Zentrale Backend-Wurzel; abgeleitet in src/lib/apiBase.ts. Standard: http://127.0.0.1:5034
  readonly VITE_API_URL?: string;
  // Optionale, feiner granulare Overrides einzelner APIs (normalerweise ungesetzt).
  readonly VITE_BACKTEST_API?: string;
  readonly VITE_QUANT_API?: string;
  readonly VITE_RITHMIC_API?: string;
  readonly VITE_RITHMIC_ENABLED?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}