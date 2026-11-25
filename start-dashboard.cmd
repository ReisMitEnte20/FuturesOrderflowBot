@echo off
rem Startet das lokale Dashboard (nur lesend, Simulation, keine Orders):
rem   1) .NET-API-Host auf http://localhost:5034  (eigenes Fenster)
rem   2) React-Oberflaeche auf http://127.0.0.1:5899 (eigenes Fenster)
rem   3) oeffnet Backtesting -> Chart ^& Replay im Browser
rem Beenden: die beiden Fenster schliessen.
setlocal
cd /d "%~dp0"
start "FuturesOrderflowBot API :5034" cmd /k dotnet run --project src\TradingBot.DevDashboard\TradingBot.DevDashboard.csproj --launch-profile http
start "FuturesOrderflowBot UI :5899" cmd /k "cd /d dashboard && npm run dev"
echo Warte auf das Dashboard ...
powershell -NoProfile -Command "for($i=0;$i -lt 90;$i++){try{$null=Invoke-WebRequest -UseBasicParsing http://127.0.0.1:5899 -TimeoutSec 2;break}catch{Start-Sleep 1}}"
start "" "http://127.0.0.1:5899/backtesting?view=chart"
endlocal
