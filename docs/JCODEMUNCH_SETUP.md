# jCodeMunch MCP — Setup

Token-sparende Code-Exploration für KI-Agenten (Claude Code / Kilo / Cursor): gezielt Klassen,
Methoden und Symbole lesen statt ganze Dateien. Regeln für Agenten: siehe [../AGENTS.md](../AGENTS.md).

> **Noch nicht installiert.** Die folgenden Befehle sind Vorschläge — nichts wird automatisch
> installiert. Keine Secrets/API-Keys nötig.

## Zweck

Agenten sollen für Recherche zuerst per Symbol-/Klassen-/Methodensuche arbeiten und nur die
relevanten Codestellen laden — das spart Tokens gegenüber dem Lesen kompletter Dateien.

## Installation (lokal)

```bash
pip install jcodemunch-mcp
jcodemunch-mcp --version
jcodemunch-mcp init
```

## Claude Code Setup

```bash
claude mcp add -s user jcodemunch jcodemunch-mcp
```

Danach in einer Session: zuerst das Tool **`jcodemunch_guide`** aufrufen und dessen Anweisungen
strikt befolgen.

## Kilo / Cursor / generischer MCP-Client

jCodeMunch als MCP-Server eintragen (Command: `jcodemunch-mcp`) gemäß der MCP-Konfiguration des
jeweiligen Clients. Der Server läuft **lokal**; keine externen Broker-/Netzwerkcalls.

## Repo indexieren

Nur **Quellcode + Doku** indexieren: `src/`, `tests/`, `docs/`, Config-Templates. Der Index bleibt
lokal (nicht committen).

## Excludes — niemals indexieren/committen

- `A:\Projects\MARKET DATA\` (große lokale Marktdaten)
- `samples/sierra/raw/*.{txt,csv,scid,tct}`, `samples/atas/raw/*.{txt,csv,scid,tct}`
- `bin/`, `obj/`
- `.env`, `*.secrets.json`, `config/**/*.local.json`
- Secrets / API-Keys

Diese Pfade sind bereits in `.gitignore`. Große Marktdaten liegen bewusst außerhalb des Repos und
werden nur streamend gelesen (`SierraLargeFileValidator`, `SierraOrderFlowBarBuilder`).

## Troubleshooting

- `command not found` → PATH prüfen bzw. `pip`-Scripts-Verzeichnis im PATH.
- Server startet nicht → `jcodemunch-mcp --version`; Client-MCP-Log prüfen.
- Zu großer Index → sicherstellen, dass Excludes greifen (keine `raw/`-Datendateien).

## Sicherheit

Keine Secrets/API-Keys in den Index oder ins Repo. jCodeMunch ist reine lokale Code-Recherche —
keine Broker-API, keine Live-Execution, keine echten Orders.
