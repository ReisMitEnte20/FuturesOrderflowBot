/// <reference types="vite/client" />

declare module "*.scss" {
  const content: string;
  export default content;
}

interface ImportMetaEnv {
  readonly VITE_API_URL: string;
  readonly VITE_BACKTEST_API?: string;
  readonly VITE_RITHMIC_ENABLED?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}