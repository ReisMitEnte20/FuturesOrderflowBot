# Setup für Docker ohne Passwort-Eingabe erforderlich

Wenn Sie Docker verwenden möchten, müssen Sie Docker ohne `sudo` ausführen können.

## Einmalige Setup:

```bash
# Docker-Gruppe erstellen (falls nicht vorhanden)
sudo groupadd docker

# Ihren Benutzer zur docker-Gruppe hinzufügen
sudo usermod -aG docker $USER

# Änderungen übernehmen
newgrp docker

# Testen Sie, ob es funktioniert
docker ps
```

## Danach können Sie direkt folgende Befehle ausführen:

```bash
cd /home/william/repo/FuturesOrderflowBot

# 1. Docker-Image bauen
docker build -f Dockerfile.dev -t tradingbot-dev:latest .

# 2. Volle Stack starten (Elasticsearch, Kibana, Logstash, Filebeat)
docker-compose up -d

# 3. Entwicklungs-Container starten
docker run -it -v $(pwd):/app tradingbot-dev:latest

# 4. Innerhalb des Containers:
dotnet build
dotnet test
dotnet run --project src/TradingBot.DevDashboard
```

## Dashboards zugreifen:
- **Anwendungs-Dashboard**: http://localhost:5000
- **Kibana (Logging/Monitoring)**: http://localhost:5601
- **Elasticsearch**: http://localhost:9200

## Troubleshooting

Falls Sie "Permission denied" erhalten:

**Option 1**: Mit sudo arbeiten (nicht empfohlen)
```bash
sudo docker build -f Dockerfile.dev -t tradingbot-dev:latest .
sudo docker-compose up -d
```

**Option 2**: Docker-Gruppe-Setup überprüfen
```bash
# Überprüfen Sie Gruppen-Mitgliedschaft
groups $USER

# Falls "docker" nicht angezeigt wird, müssen Sie sich abmelden und wieder anmelden
logout
# Dann erneut anmelden
```

**Option 3**: .NET native installieren (Alternative zu Docker)
```bash
# Nur für Fedora
sudo dnf install dotnet-sdk-8.0

# Dann:
./build-linux.sh
```
