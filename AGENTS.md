# AGENTS.md

Regeln für KI-Agenten (Claude Code / Kilo Code / Cursor) in diesem Repo. Ergänzt `CLAUDE.md`.

## jCodeMunch / Token-Saving Rules

- **Nutze jCodeMunch für Code-Recherche, bevor ganze Dateien gelesen werden.**
- Rufe zuerst das Tool **`jcodemunch_guide`** auf und folge seinen Anweisungen strikt.
- Bevorzuge **Symbol-/Klassen-/Methoden-Suche** statt komplette Dateien zu öffnen.
- Verwende gezielte **Symbolquellen** (nur die relevante Methode/Klasse) statt großer Datei-Reads.
- Ist jCodeMunch nicht verfügbar: normal weiterarbeiten, aber **nur relevante Dateien** öffnen.
- **Keine** langen Architektur-Zusammenfassungen.
- **Keine** vollständigen Dateien in den Chat ausgeben.
- Build/Test nur **kurz** zusammenfassen (Ergebniszeile), keine langen Logs.

## Niemals indexieren oder committen

Große Marktdaten und Secrets gehören **nicht** ins Repo und **nicht** in den jCodeMunch-Index:

- `A:\Projects\MARKET DATA\` (große lokale Marktdaten — außerhalb des Repos)
- `samples/sierra/raw/*.txt`, `samples/sierra/raw/*.csv`, `samples/sierra/raw/*.scid`, `samples/sierra/raw/*.tct`
- `samples/atas/raw/*.txt`, `samples/atas/raw/*.csv`, `samples/atas/raw/*.scid`, `samples/atas/raw/*.tct`
- `bin/`, `obj/`
- `.env`, `*.secrets.json`, `config/**/*.local.json`
- Jegliche Secrets / API-Keys / Zugangsdaten

Nur `.gitkeep` bleibt in den `raw/`-Ordnern (siehe `.gitignore`). jCodeMunch soll nur **Quellcode**
(`src/`, `tests/`) und Doku indexieren — keine Datendateien.

## Projekt-Leitplanken (Kurzform)

Research/Simulation-only. Keine Broker-API, keine Live-Execution, keine echten Orders, keine
Phase 13 ohne Freigabe. Strategy erzeugt nur `TradeSignal`; `RiskManager` ist Gatekeeper;
Dashboard/Research ohne `TradingBot.Execution`-Referenz; kein Fake-Orderflow (`InsufficientData`).
Details: `CLAUDE.md`, `docs/COLLABORATOR_ONBOARDING.md`. **Kein Commit ohne Freigabe.**
