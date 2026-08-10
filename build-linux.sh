#!/bin/bash
# FuturesOrderflowBot - Linux Development Setup & Build Script
# Automatically sets up the development environment and builds the project

set -e

echo "========================================"
echo "FuturesOrderflowBot - Linux Setup"
echo "========================================"
echo ""

# Color codes for output
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

# Function to print colored output
print_status() {
    echo -e "${GREEN}✓${NC} $1"
}

print_step() {
    echo -e "${YELLOW}→${NC} $1"
}

print_error() {
    echo -e "${RED}✗${NC} $1"
}

# Step 1: Check prerequisites
echo "Step 1: Checking prerequisites..."
print_step "Checking for .NET SDK..."

if command -v dotnet &> /dev/null; then
    DOTNET_VERSION=$(dotnet --version 2>/dev/null || echo "broken")
    if [[ "$DOTNET_VERSION" != "broken" ]]; then
        print_status ".NET SDK found: $DOTNET_VERSION"
    else
        print_error ".NET installation is broken"
        echo ""
        echo "Fix: Run this to reinstall .NET 8:"
        echo "  sudo dnf remove dotnet-*"
        echo "  sudo dnf install dotnet-sdk-8.0"
        exit 1
    fi
else
    print_error ".NET SDK not found"
    echo ""
    echo "Installation instructions:"
    echo ""
    echo "Option 1: Via dnf (Fedora/RHEL)"
    echo "  sudo dnf install dotnet-sdk-8.0"
    echo ""
    echo "Option 2: Via Docker (No installation needed)"
    echo "  docker run -it -v \$(pwd):/app mcr.microsoft.com/dotnet/sdk:8.0"
    exit 1
fi

print_step "Checking for git..."
command -v git &> /dev/null && print_status "Git found" || print_error "Git not found"

echo ""
echo "Step 2: Preparing project..."
print_step "Creating logs directory..."
mkdir -p logs

print_step "Verifying project structure..."
if [ -f "TradingBot.sln" ]; then
    print_status "Solution file found: TradingBot.sln"
else
    print_error "Solution file not found"
    exit 1
fi

echo ""
echo "Step 3: Building solution..."
print_step "Running 'dotnet restore'..."
if dotnet restore 2>&1 | grep -E "(Restored|error)"; then
    print_status "Dependencies restored"
else
    print_error "Failed to restore dependencies"
    exit 1
fi

echo ""
print_step "Running 'dotnet build'..."
if dotnet build --configuration Debug 2>&1 | tail -20; then
    print_status "Build completed successfully"
else
    print_error "Build failed"
    exit 1
fi

echo ""
echo "Step 4: Running tests..."
print_step "Executing unit tests (this may take a moment)..."
if dotnet test tests/TradingBot.Tests/TradingBot.Tests.csproj --verbosity normal 2>&1 | tail -30; then
    print_status "Tests completed"
else
    print_error "Tests failed"
fi

echo ""
echo "Step 5: Building individual components..."
print_step "Building DevDashboard..."
dotnet build src/TradingBot.DevDashboard/TradingBot.DevDashboard.csproj --configuration Debug > /dev/null 2>&1
print_status "DevDashboard ready"

echo ""
echo "========================================"
echo "Build Complete!"
echo "========================================"
echo ""
echo "Next steps:"
echo ""
echo "1. Start the Dev Dashboard (Paper Trading Monitor):"
echo "   dotnet run --project src/TradingBot.DevDashboard/TradingBot.DevDashboard.csproj"
echo "   → Open: http://localhost:5000"
echo ""
echo "2. Run Backtesting:"
echo "   dotnet run --project src/TradingBot.Console/TradingBot.Console.csproj"
echo ""
echo "3. Run Tests (Watch mode):"
echo "   dotnet watch --project tests/TradingBot.Tests test"
echo ""
echo "4. Development with Live Reload:"
echo "   dotnet watch --project src/TradingBot.DevDashboard run"
echo ""
echo "Documentation:"
echo "  - Architecture: docs/ARCHITECTURE.md"
echo "  - Status: docs/PROJECT_STATUS.md"
echo "  - Filebeat Integration: docs/FILEBEAT_INTEGRATION.md"
echo ""
