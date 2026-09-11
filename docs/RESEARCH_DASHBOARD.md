# Research Dashboard

Das visuelle Research Dashboard (vormals `TradingBot.DevDashboard`, Route `/research`) wurde
entfernt. Als Trading-Dashboard wird stattdessen das externe Projekt
**[HKUDS/Vibe-Trading](https://github.com/HKUDS/Vibe-Trading)** referenziert — ein eigenständiges
Repo, nicht Teil dieser .NET-Solution.

Die zugrunde liegende Research-Analytics-Schicht (`TradingBot.Research`: Monte Carlo, Walk Forward,
Ranking, Robustness, Sensitivity) bleibt im Code erhalten und ist unabhängig von jeder Dashboard-UI
nutz- und testbar — siehe [RESEARCH_ANALYTICS.md](RESEARCH_ANALYTICS.md).
