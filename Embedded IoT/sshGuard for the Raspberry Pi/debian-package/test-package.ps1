# test-package.ps1 - Build and test the .deb package in Docker (Windows version)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "===================================" -ForegroundColor Cyan
Write-Host "sshGuard Package Test Environment" -ForegroundColor Cyan
Write-Host "===================================" -ForegroundColor Cyan
Write-Host ""

# Check Docker is available
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    Write-Host "ERROR: Docker not found. Install Docker Desktop for Windows." -ForegroundColor Red
    exit 1
}

Set-Location $ScriptDir

# Step 1: Copy source files to package directory
Write-Host "[1/4] Copying source files..." -ForegroundColor Yellow
$SourceDir = Split-Path -Parent $ScriptDir
$PackageDir = Join-Path $ScriptDir "sshguard-face_1.0.0_all"

Copy-Item "$SourceDir\sshguard.py" "$PackageDir\opt\sshGuard\" -Force
Copy-Item "$SourceDir\huskylens_reader.py" "$PackageDir\opt\sshGuard\" -Force
Copy-Item "$SourceDir\create-user.sh" "$PackageDir\opt\sshGuard\" -Force
Copy-Item "$SourceDir\sshguard.service" "$PackageDir\etc\systemd\system\" -Force

Write-Host "  Files copied to package directory" -ForegroundColor Green

# Step 2: Build the package in Docker (need Linux to run dpkg-deb)
Write-Host ""
Write-Host "[2/4] Building .deb package in Docker..." -ForegroundColor Yellow

# Use a separate shell script to avoid CRLF issues - convert it first inline
docker run --rm -v "${ScriptDir}:/build" -w /build debian:bookworm-slim bash -c "sed -i 's/\r$//' build-in-docker.sh && chmod +x build-in-docker.sh && ./build-in-docker.sh"

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to build .deb package" -ForegroundColor Red
    exit 1
}
Write-Host "  Package built: sshguard-face_1.0.0_all.deb" -ForegroundColor Green

# Step 3: Build Docker test image
Write-Host ""
Write-Host "[3/4] Building Docker test image..." -ForegroundColor Yellow
docker build -t sshguard-test .

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Failed to build test image" -ForegroundColor Red
    exit 1
}
Write-Host "  Test image built" -ForegroundColor Green

# Step 4: Run installation test
Write-Host ""
Write-Host "[4/4] Running installation test..." -ForegroundColor Yellow
docker run --rm sshguard-test

Write-Host ""
Write-Host "===================================" -ForegroundColor Green
Write-Host "All tests passed!" -ForegroundColor Green
Write-Host "===================================" -ForegroundColor Green
Write-Host ""
Write-Host "For interactive testing:" -ForegroundColor Cyan
Write-Host "  docker run --rm -it sshguard-test bash"
Write-Host ""
Write-Host "Test removal:" -ForegroundColor Cyan
Write-Host "  docker run --rm -it sshguard-test bash -c 'dpkg -r sshguard-face && echo Removed OK'"
Write-Host ""
Write-Host "Test purge:" -ForegroundColor Cyan
Write-Host "  docker run --rm -it sshguard-test bash -c 'dpkg -P sshguard-face && echo Purged OK'"
