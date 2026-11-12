/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{js,ts,jsx,tsx}"],
  theme: {
    extend: {
      fontFamily: {
        mono: ["var(--mono)", "ui-monospace", "SFMono-Regular", "JetBrains Mono", "Menlo", "monospace"],
        sans: ["var(--sans)", "ui-sans-serif", "system-ui", "-apple-system", "sans-serif"],
        display: ["var(--display)", "ui-sans-serif", "system-ui", "sans-serif"],
      },
      // Semantische Namen auf die Marken-Tokens aus index.css abgebildet (inkl. der auf einigen
      // Seiten verwendeten Kurzformen wie bg-card / text-muted-foreground).
      colors: {
        background: "#020d1e",
        foreground: "#eefcf6",
        card: "#0a1a30",
        muted: { DEFAULT: "#0e2139", foreground: "#6b8399" },
        border: "#1d3654",
        primary: { DEFAULT: "#3cf0a0", foreground: "#020d1e" },
        success: "#3cf0a0",
        destructive: "#f0566a",
        brand: {
          green: "#3cf0a0",
          mint: "#9ff0d0",
          cyan: "#22b8f0",
          blue: "#2b8cf5",
        },
      },
      backgroundImage: {
        "grad-brand": "var(--grad-brand)",
        "grad-brand-v": "var(--grad-brand-v)",
      },
      boxShadow: {
        brand: "var(--glow-brand)",
        panel: "var(--shadow-panel)",
      },
    },
  },
  plugins: [],
};
