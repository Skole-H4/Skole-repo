#!/bin/bash
# test-package.sh - Build and test the .deb package in Docker
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "==================================="
echo "sshGuard Package Test Environment"
echo "==================================="
echo ""

# Step 1: Build the package
echo "[1/3] Building .deb package..."
./build-deb.sh

# Step 2: Build Docker image
echo ""
echo "[2/3] Building Docker test image..."
docker build -t sshguard-test .

# Step 3: Run tests
echo ""
echo "[3/3] Running installation test..."
docker run --rm sshguard-test

echo ""
echo "==================================="
echo "All tests passed!"
echo "==================================="
echo ""
echo "For interactive testing:"
echo "  docker run --rm -it sshguard-test bash"
echo ""
echo "Test removal:"
echo "  docker run --rm -it sshguard-test bash -c 'dpkg -r sshguard-face && echo Removed OK'"
echo ""
echo "Test purge:"
echo "  docker run --rm -it sshguard-test bash -c 'dpkg -P sshguard-face && echo Purged OK'"
