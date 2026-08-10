#!/bin/bash
# FuturesOrderflowBot - Development Environment Summary
# Shows what's available and how to get started

cat << 'EOF'

╔══════════════════════════════════════════════════════════════════════════════╗
║                                                                              ║
║           FuturesOrderflowBot - Linux Development Environment               ║
║                                                                              ║
╚══════════════════════════════════════════════════════════════════════════════╝

📊 ENVIRONMENT STATUS:
═══════════════════════════════════════════════════════════════════════════════

✓ .NET SDK:           NOT installed (requires setup)
✓ Docker:             AVAILABLE ✓
✓ Docker Compose:     AVAILABLE ✓
✓ Git:                AVAILABLE ✓

═══════════════════════════════════════════════════════════════════════════════
🚀 RECOMMENDED: Docker Development (5 minutes to start)
═══════════════════════════════════════════════════════════════════════════════

This is the easiest path without installing .NET SDK.

STEP 1: Grant Docker permissions (one-time)
──────────────────────────────────────────
    sudo groupadd docker 2>/dev/null
    sudo usermod -aG docker $USER
    newgrp docker
    docker ps                    # Test if working

STEP 2: Build the development container
─────────────────────────────────────────
    cd /home/william/repo/FuturesOrderflowBot
    docker build -f Dockerfile.dev -t tradingbot-dev:latest .

STEP 3: Start the monitoring stack
──────────────────────────────────────
    docker-compose up -d

    This starts:
    ✓ Elasticsearch (port 9200)  - Log storage
    ✓ Kibana (port 5601)         - Visualization
    ✓ Logstash (port 5044)       - Log processing
    ✓ Filebeat                   - Log shipping

STEP 4: Run the development container with access to the project
──────────────────────────────────────────────────────────────────
    docker run -it -v $(pwd):/app tradingbot-dev:latest

STEP 5: Inside the container, build and run
──────────────────────────────────────────────
    cd /app
    
    # Build the project
    dotnet build
    
    # Run all tests
    dotnet test
    
    # Start the web dashboard
    dotnet run --project src/TradingBot.DevDashboard

STEP 6: Access the dashboards
──────────────────────────────
    • Application Dashboard:     http://localhost:5000
    • Paper Trading Monitor:     http://localhost:5000/paper
    • Research Dashboard:        http://localhost:5000/research
    • Kibana (Logs/Monitoring):  http://localhost:5601

═══════════════════════════════════════════════════════════════════════════════
🔧 ALTERNATIVE: Native .NET Development (10 minutes to start)
═══════════════════════════════════════════════════════════════════════════════

If you prefer native development:

STEP 1: Install .NET 8 SDK
──────────────────────────
    Fedora/RHEL:
        sudo dnf install dotnet-sdk-8.0
    
    Ubuntu/Debian:
        wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh
        chmod +x dotnet-install.sh
        ./dotnet-install.sh --version 8.0
        export PATH="$HOME/.dotnet:$PATH"

STEP 2: Build the project
──────────────────────────
    cd /home/william/repo/FuturesOrderflowBot
    chmod +x build-linux.sh
    ./build-linux.sh

This script will:
    ✓ Verify .NET SDK installation
    ✓ Restore dependencies
    ✓ Compile the entire solution
    ✓ Run unit tests
    ✓ Build the DevDashboard

STEP 3: Run components
──────────────────────
    # Start web dashboard with live reload
    dotnet watch --project src/TradingBot.DevDashboard run
    
    # Run tests in watch mode
    dotnet test --watch
    
    # Run backtest console
    dotnet run --project src/TradingBot.Console

═══════════════════════════════════════════════════════════════════════════════
📁 PROJECT STRUCTURE
═══════════════════════════════════════════════════════════════════════════════

src/
  ├── TradingBot.Domain              # Data models & enums
  ├── TradingBot.Core                # Interfaces & abstractions
  ├── TradingBot.Application         # Business logic
  ├── TradingBot.Infrastructure      # Config, logging, I/O
  ├── TradingBot.Execution           # Broker adapters
  ├── TradingBot.Backtesting         # Backtest engine
  ├── TradingBot.PaperTrading        # Paper trading engine
  ├── TradingBot.Console             # CLI entry point
  ├── TradingBot.DevDashboard        # Web UI (ASP.NET Core)
  └── TradingBot.Research            # Analytics & research

tests/
  └── TradingBot.Tests               # Unit tests (xUnit)

config/                              # JSON profiles
  ├── brokers/                       # Broker configs
  ├── instruments/                   # Instrument definitions
  ├── fees/                          # Fee structures
  ├── risk/                          # Risk parameters
  └── dashboard/                     # Dashboard settings

docs/
  ├── ARCHITECTURE.md                # System design
  ├── PROJECT_STATUS.md              # Current progress
  ├── FILEBEAT_INTEGRATION.md        # Monitoring setup
  ├── STRATEGY_FRAMEWORK.md          # Strategy development
  └── ORDERFLOW_STRATEGY_TEMPLATE.md # Strategy template

═══════════════════════════════════════════════════════════════════════════════
🛠️  COMMON DEVELOPMENT COMMANDS
═══════════════════════════════════════════════════════════════════════════════

Build:
    dotnet build                                      # Build all
    dotnet build --configuration Release             # Release build
    dotnet build --no-restore                        # Skip restore

Test:
    dotnet test                                      # Run all tests
    dotnet test -k FilebeatMonitorTests             # Run specific tests
    dotnet test --watch                             # Watch mode
    dotnet test --verbosity detailed                # Detailed output

Run:
    dotnet run --project src/TradingBot.DevDashboard    # Start dashboard
    dotnet watch --project src/TradingBot.DevDashboard run  # With hot reload
    dotnet run --project src/TradingBot.Console          # Run CLI

Debug:
    # In VS Code: F5 to debug (with C# Dev Kit installed)
    # Or use breakpoints in DevDashboard

Format & Analyze:
    dotnet format                                    # Format code
    dotnet analyzers                                 # Run code analysis

═══════════════════════════════════════════════════════════════════════════════
📚 DOCUMENTATION FILES YOU SHOULD READ
═══════════════════════════════════════════════════════════════════════════════

1. LINUX_DEVELOPMENT_GUIDE.md
   → Complete setup and development guide
   → Troubleshooting section
   → All available commands

2. docs/ARCHITECTURE.md
   → System design and data flow
   → Component relationships
   → Design patterns

3. docs/PROJECT_STATUS.md
   → Current progress (Phase 8A complete)
   → What's implemented
   → What's still needed

4. docs/FILEBEAT_INTEGRATION.md
   → Filebeat/ELK Stack setup
   → Monitoring and logging
   → Kibana dashboards

5. docs/STRATEGY_FRAMEWORK.md
   → How to develop new strategies
   → Signal generation
   → Risk management

6. DOCKER_SETUP.md
   → Docker-specific configuration
   → Docker Compose stack details
   → Troubleshooting Docker issues

═══════════════════════════════════════════════════════════════════════════════
✅ NEXT STEPS
═══════════════════════════════════════════════════════════════════════════════

IMMEDIATE (Today):
  1. Choose Docker or Native .NET
  2. Run the build process
  3. Start the DevDashboard
  4. Explore /paper and /research routes

SHORT TERM (This week):
  1. Review docs/ARCHITECTURE.md to understand the system
  2. Read docs/PROJECT_STATUS.md to see what's done
  3. Look at example strategies in docs/STRATEGY_FRAMEWORK.md
  4. Run some backtests with sample data

MEDIUM TERM (This month):
  1. Develop your own custom strategy
  2. Integrate with Filebeat for monitoring
  3. Create Kibana dashboards for your trading metrics
  4. Set up automated testing

═══════════════════════════════════════════════════════════════════════════════

Questions? Check the documentation files listed above, or explore the code!

Happy trading! 🚀

═══════════════════════════════════════════════════════════════════════════════
EOF
