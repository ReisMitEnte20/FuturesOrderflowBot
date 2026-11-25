# Synthetische Tick-Fixtures (keine Marktdaten)

`mnq-deltaflow-synthetic.csv` — 3.000 künstlich erzeugte MNQ-Trade-Ticks im ATAS-Tick-Format
(`config/import-profiles/atas-tick.example.json`): 6 Bars à 500 Ticks im Muster
buy / sell / buy / neutral / sell / buy, Preisschritt 0,25, Aggressor je Trade.
Dient ausschließlich der technischen Abnahme des Tick-Orderflow-Pfads im Backtesting
(Auswahl „DeltaFlow“ + Datensatz „Trade-Ticks (ATAS-Format)“). Keine Aussage über echte Märkte.
