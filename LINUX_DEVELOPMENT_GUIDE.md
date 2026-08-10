# FuturesOrderflowBot - Linux Development Setup Guide

Complete guide for building and developing the FuturesOrderflowBot on Linux.

## Quick Start (3 Options)

### Option 1: Native Build (Recommended if .NET 8 SDK is installed)

```bash
cd /home/william/repo/FuturesOrderflowBot
chmod +x build-linux.sh
./build-linux.sh
```

This script will:
- ✓ Check .NET SDK installation
- ✓ Restore NuGet packages
- ✓ Build the entire solution
- ✓ Run unit tests
- ✓ Prepare DevDashboard

### Option 2: Docker Development (No installation required)

```bash
# Build development image
docker build -f Dockerfile.dev -t tradingbot-dev .

# Run container with bash access
docker run -it -v $(pwd):/app tradingbot-dev

# Inside container, you can now run:
dotnet build
dotnet test
dotnet run --project src/TradingBot.DevDashboard
```

### Option 3: Docker Compose (Easiest)

```bash
docker-compose up -d
```

This starts a full stack including Elasticsearch, Kibana, and Logstash.

## System Requirements

### For Native Build:
- **OS**: Linux (Ubuntu, Fedora, Debian, etc.)
- **.NET SDK**: 8.0 or higher
- **RAM**: 4GB minimum (8GB recommended)
- **Disk**: 5GB free space
- **Build time**: 2-5 minutes

### For Docker:
- **Docker**: 20.10+
- **Docker Compose**: 2.0+
- **RAM**: 6GB minimum (8GB+ recommended for full stack)
- **Disk**: 10GB free space

## Installation Steps

### Step 1: Install .NET 8 SDK

#### On Fedora/RHEL:
```bash
sudo dnf install dotnet-sdk-8.0
```

#### On Ubuntu/Debian:
```bash
wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --version 8.0
export PATH="$HOME/.dotnet:$PATH"
```

#### On Other Linux:
Visit https://dotnet.microsoft.com/download for distribution-specific instructions.

### Step 2: Verify Installation

```bash
dotnet --version      # Should show 8.0.x or higher
dotnet --info         # Shows detailed SDK info
```

If you get: `Error: [/usr/lib64/dotnet/host/fxr] does not contain any version-numbered child folders`

**Fix**: 
```bash
# Option A: Reinstall .NET
sudo dnf remove dotnet-*
sudo dnf install dotnet-sdk-8.0

# Option B: Use Docker instead
docker build -f Dockerfile.dev -t tradingbot-dev .
```

### Step 3: Clone and Navigate

```bash
cd /home/william/repo/FuturesOrderflowBot
```

## Build Commands

### Full Build
```bash
dotnet build
```

### Debug Build (optimized for development)
```bash
dotnet build --configuration Debug
```

### Release Build (optimized for performance)
```bash
dotnet build --configuration Release
```

### Restore Dependencies Only
```bash
dotnet restore
```

## Testing

### Run All Tests
```bash
dotnet test
```

### Run Specific Test Class
```bash
dotnet test -k FilebeatMonitorTests
```

### Run Tests with Detailed Output
```bash
dotnet test --verbosity detailed
```

### Watch Mode (Re-run tests on file changes)
```bash
dotnet watch --project tests/TradingBot.Tests test
```

### Test Coverage
```bash
dotnet test /p:CollectCoverage=true /p:CoverageFormat=opencover
```

## Running Components

### 1. DevDashboard (Web Interface)
```bash
# Standard
dotnet run --project src/TradingBot.DevDashboard

# With Live Reload (watches for changes)
dotnet watch --project src/TradingBot.DevDashboard run

# Custom port
dotnet run --project src/TradingBot.DevDashboard -- --urls "http://localhost:8080"
```

Then open: http://localhost:5000

**Features:**
- `/` - System Status Dashboard
- `/paper` - Paper Trading Monitor
- `/research` - Research & Analysis Dashboard

### 2. Console Application (Backtest Runner)
```bash
dotnet run --project src/TradingBot.Console

# With arguments
dotnet run --project src/TradingBot.Console -- --backtest-config config/backtest.example.json
```

### 3. Individual Project Build
```bash
# Build only one project
dotnet build src/TradingBot.Application

# Run only one project's tests
dotnet test src/TradingBot.Application
```

## Project Structure

```
FuturesOrderflowBot/
├── src/
│   ├── TradingBot.Domain/              # Core domain models
│   ├── TradingBot.Core/                # Interfaces & abstractions
│   ├── TradingBot.Application/         # Business logic (Risk, Order, MarketData)
│   ├── TradingBot.Infrastructure/      # Config, Logging, CSV IO
│   ├── TradingBot.Execution/           # Broker adapters
│   ├── TradingBot.Backtesting/         # Backtest engine
│   ├── TradingBot.PaperTrading/        # Paper trading simulation
│   ├── TradingBot.Console/             # CLI entry point
│   ├── TradingBot.DevDashboard/        # Web dashboard (ASP.NET Core)
│   └── TradingBot.Research/            # Research analytics
├── tests/
│   └── TradingBot.Tests/               # Unit tests (xUnit)
├── config/                             # JSON configuration profiles
├── docs/                               # Documentation
└── samples/                            # Example data (CSV, etc.)
```

## Development Workflow

### 1. Making Code Changes

```bash
# Open in VS Code with C# support
code .

# Or use your preferred editor
```

### 2. Building and Testing

```bash
# Quick build
dotnet build

# Run specific test
dotnet test -k YourTestClass

# Watch mode (auto-rebuild on changes)
dotnet watch build
```

### 3. Running Components

```bash
# Start web dashboard
dotnet run --project src/TradingBot.DevDashboard

# In another terminal, run tests
dotnet test --watch
```

### 4. Debugging

**In VS Code:**
1. Install "C# Dev Kit" extension
2. Open `.vscode/launch.json`
3. Add breakpoints
4. Press F5 to debug

**From CLI:**
```bash
dotnet run --configuration Debug
```

## Troubleshooting

### Problem: "dotnet: command not found"
```bash
# Check installation
which dotnet

# If not found, install .NET SDK
sudo dnf install dotnet-sdk-8.0  # Fedora
sudo apt install dotnet-sdk-8.0  # Ubuntu
```

### Problem: "Error: [/usr/lib64/dotnet/host/fxr] does not contain any version-numbered child folders"
```bash
# Reinstall .NET
sudo dnf remove dotnet-\*
sudo dnf install dotnet-sdk-8.0

# OR use Docker
docker build -f Dockerfile.dev -t tradingbot-dev .
```

### Problem: Build fails with NuGet errors
```bash
# Clear NuGet cache
dotnet nuget locals all --clear

# Restore again
dotnet restore
```

### Problem: Tests timeout or hang
```bash
# Run with longer timeout
dotnet test --blame-hang-timeout 60000

# Or skip slow tests
dotnet test -k "not Performance"
```

### Problem: Port 5000 already in use
```bash
# Use different port
dotnet run --project src/TradingBot.DevDashboard -- --urls "http://localhost:5001"
```

## Performance Tips

### Faster Builds
```bash
# Build only changed projects
dotnet build --no-restore

# Use Release configuration
dotnet build --configuration Release

# Parallel build
dotnet build -m:4  # Use 4 cores
```

### Faster Testing
```bash
# Run tests in parallel
dotnet test --parallel 4

# Run only modified tests
dotnet test --filter "Category=Unit"
```

## Integration with Filebeat/ELK Stack

### Start Local ELK Stack
```bash
docker-compose up -d

# Access:
# - Elasticsearch: http://localhost:9200
# - Kibana: http://localhost:5601
```

### Configure Filebeat
```bash
# Edit filebeat.yml if needed
# Start Filebeat
filebeat -c filebeat.yml
```

### View Logs in Kibana
1. Open http://localhost:5601
2. Create index pattern: `tradingbot-*`
3. Go to Discover tab
4. Watch real-time logs

## Useful Commands Summary

```bash
# Build & Test
dotnet restore                          # Download packages
dotnet build                            # Compile code
dotnet test                             # Run tests
dotnet watch build                      # Rebuild on changes

# Run Components
dotnet run --project src/TradingBot.DevDashboard          # Start web UI
dotnet run --project src/TradingBot.Console               # Run backtest

# Development
code .                                  # Open VS Code
dotnet format                           # Format code
dotnet analyzers                        # Run code analysis

# Docker
docker build -f Dockerfile.dev -t tradingbot-dev .        # Build image
docker run -it -v $(pwd):/app tradingbot-dev              # Run container
docker-compose up -d                                      # Start full stack
```

## Next Steps for Development

1. **Understand Architecture**: Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)
2. **Check Project Status**: Read [docs/PROJECT_STATUS.md](docs/PROJECT_STATUS.md)
3. **Filebeat Integration**: Read [docs/FILEBEAT_INTEGRATION.md](docs/FILEBEAT_INTEGRATION.md)
4. **Example Strategies**: Look in `samples/` directory
5. **Run Tests**: `dotnet test` to verify setup
6. **Start Dashboard**: `dotnet run --project src/TradingBot.DevDashboard`

## Support & Issues

- Documentation: `docs/` folder
- Issues: GitHub Issues
- Architecture questions: See `docs/ARCHITECTURE.md`
- Strategy development: See `docs/STRATEGY_FRAMEWORK.md`

---

**Happy developing! 🚀**
