#!/bin/bash
# FuturesOrderflowBot - Quick Start Guide
# Detects environment and provides optimal setup instructions

clear

echo "╔════════════════════════════════════════════════════════════════╗"
echo "║     FuturesOrderflowBot - Linux Development Setup              ║"
echo "║     Choose your development environment                        ║"
echo "╚════════════════════════════════════════════════════════════════╝"
echo ""

# Detect system capabilities
HAS_DOTNET=false
HAS_DOCKER=false
HAS_DOCKER_COMPOSE=false

if command -v dotnet &> /dev/null; then
    DOTNET_VERSION=$(dotnet --version 2>/dev/null)
    if [[ $DOTNET_VERSION == 8.* ]] || [[ $DOTNET_VERSION == 9.* ]] || [[ $DOTNET_VERSION == 10.* ]]; then
        HAS_DOTNET=true
        echo "✓ .NET SDK $DOTNET_VERSION detected"
    fi
fi

if command -v docker &> /dev/null; then
    HAS_DOCKER=true
    echo "✓ Docker detected"
fi

if command -v docker-compose &> /dev/null; then
    HAS_DOCKER_COMPOSE=true
    echo "✓ Docker Compose detected"
fi

echo ""
echo "═══════════════════════════════════════════════════════════════"
echo ""

# Choose best option
if [ "$HAS_DOTNET" = true ]; then
    echo "🎯 RECOMMENDED: Native .NET Development"
    echo ""
    echo "You have .NET SDK installed. This is the fastest way to develop."
    echo ""
    echo "Quick Start:"
    echo "  1. Build the project:"
    echo "     chmod +x build-linux.sh"
    echo "     ./build-linux.sh"
    echo ""
    echo "  2. Start development:"
    echo "     dotnet run --project src/TradingBot.DevDashboard"
    echo "     → Open http://localhost:5000"
    echo ""
    echo "  3. Run tests:"
    echo "     dotnet test"
    echo ""
    echo "More info: Read LINUX_DEVELOPMENT_GUIDE.md"
    echo ""

elif [ "$HAS_DOCKER" = true ] && [ "$HAS_DOCKER_COMPOSE" = true ]; then
    echo "🐳 RECOMMENDED: Docker Development"
    echo ""
    echo "Docker and Docker Compose detected. Perfect for containerized dev!"
    echo ""
    echo "Quick Start:"
    echo "  1. Start the full stack (Elasticsearch + Kibana + Logstash + Filebeat):"
    echo "     docker-compose up -d"
    echo ""
    echo "  2. Start development container:"
    echo "     docker run -it -v \$(pwd):/app tradingbot-dev:latest"
    echo ""
    echo "  3. Inside container:"
    echo "     dotnet build"
    echo "     dotnet test"
    echo "     dotnet run --project src/TradingBot.DevDashboard"
    echo ""
    echo "  4. Access dashboards:"
    echo "     - Application: http://localhost:5000"
    echo "     - Kibana (logs): http://localhost:5601"
    echo ""

elif [ "$HAS_DOCKER" = true ]; then
    echo "🐳 Docker Detected (but no Docker Compose)"
    echo ""
    echo "You can still use Docker, but Docker Compose is recommended."
    echo ""
    echo "Option 1: Install Docker Compose"
    echo "  sudo curl -L \"https://github.com/docker/compose/releases/download/2.25.0/docker-compose-\$(uname -s)-\$(uname -m)\" -o /usr/local/bin/docker-compose"
    echo "  sudo chmod +x /usr/local/bin/docker-compose"
    echo ""
    echo "Option 2: Use Docker directly"
    echo "  docker build -f Dockerfile.dev -t tradingbot-dev ."
    echo "  docker run -it -v \$(pwd):/app tradingbot-dev"
    echo ""

else
    echo "⚠️  No suitable development environment detected"
    echo ""
    echo "You have two options:"
    echo ""
    echo "═══════════════════════════════════════════════════════════════"
    echo "Option 1: Install .NET 8 SDK (Recommended)"
    echo "═══════════════════════════════════════════════════════════════"
    echo ""
    echo "Fedora/RHEL:"
    echo "  sudo dnf install dotnet-sdk-8.0"
    echo ""
    echo "Ubuntu/Debian:"
    echo "  wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh"
    echo "  chmod +x dotnet-install.sh"
    echo "  ./dotnet-install.sh --version 8.0"
    echo "  export PATH=\"\$HOME/.dotnet:\$PATH\""
    echo ""
    echo "Then run: ./build-linux.sh"
    echo ""
    echo "═══════════════════════════════════════════════════════════════"
    echo "Option 2: Install Docker (Alternative)"
    echo "═══════════════════════════════════════════════════════════════"
    echo ""
    echo "Fedora:"
    echo "  sudo dnf install docker docker-compose"
    echo "  sudo systemctl start docker"
    echo ""
    echo "Ubuntu:"
    echo "  sudo apt install docker.io docker-compose"
    echo "  sudo systemctl start docker"
    echo ""
    echo "Then run: docker-compose up -d"
    echo ""
fi

echo ""
echo "═══════════════════════════════════════════════════════════════"
echo ""
echo "📚 For detailed information:"
echo "   - Read: LINUX_DEVELOPMENT_GUIDE.md"
echo "   - Arch: docs/ARCHITECTURE.md"
echo "   - Status: docs/PROJECT_STATUS.md"
echo "   - Filebeat: docs/FILEBEAT_INTEGRATION.md"
echo ""
echo "═══════════════════════════════════════════════════════════════"
