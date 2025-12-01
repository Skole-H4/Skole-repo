param(
    [switch]$SkipApps
)
$ErrorActionPreference = 'Stop'
$scriptRoot = $PSScriptRoot
Set-Location $scriptRoot

function Invoke-Compose {
    param(
        [string[]]$ComposeArgs
    )

    $composeFile = Join-Path $scriptRoot 'docker-compose.yml'
    $arguments = @('-f', $composeFile) + $ComposeArgs
    & docker compose @arguments
}

function Wait-ForKafkaHealthy {
    Write-Host 'Waiting for Kafka container to report healthy status...' -ForegroundColor Cyan
    $deadline = (Get-Date).AddMinutes(3)

    while ((Get-Date) -lt $deadline) {
        $statusOutput = Invoke-Compose -ComposeArgs @('ps')
        if ($statusOutput -match 'kafka.*Up.*\(healthy\)') {
            Write-Host 'Kafka is healthy.' -ForegroundColor Green
            return $true
        }

        Write-Host '.' -NoNewline
        Start-Sleep -Seconds 3
    }

    Write-Warning 'Timeout waiting for Kafka to become healthy. Check container logs with `docker compose logs kafka`.'
    return $false
}

function Start-DotNetAppInNewTerminal {
    param(
        [string]$Name,
        [string]$ProjectFolder
    )
    
    Write-Host "Starting $Name..." -ForegroundColor Cyan
    
    # Start in a new PowerShell window that stays open
    $process = Start-Process pwsh -ArgumentList @(
        '-NoExit',
        '-Command',
        "Set-Location '$ProjectFolder'; Write-Host 'Starting $Name...' -ForegroundColor Green; dotnet run"
    ) -PassThru
    
    return $process
}

# Stop any existing dotnet processes for our apps
Write-Host 'Stopping any existing app processes...' -ForegroundColor Yellow
Get-Process -Name 'dotnet' -ErrorAction SilentlyContinue | ForEach-Object {
    $cmdLine = (Get-CimInstance Win32_Process -Filter "ProcessId = $($_.Id)" -ErrorAction SilentlyContinue).CommandLine
    if ($cmdLine -match 'TallyService|WebApp') {
        Write-Host "  Stopping PID $($_.Id): $($_.ProcessName)"
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Magenta
Write-Host '  Kafka Playground Reset' -ForegroundColor Magenta
Write-Host '========================================' -ForegroundColor Magenta
Write-Host ''

Write-Host 'Stopping Kafka playground stack...' -ForegroundColor Yellow
Invoke-Compose -ComposeArgs @('down', '--remove-orphans', '--volumes') | Out-Null

Write-Host 'Starting Kafka playground stack...' -ForegroundColor Yellow
Invoke-Compose -ComposeArgs @('up', '-d') | Out-Null

# Always wait for Kafka to be healthy before starting apps
$kafkaHealthy = Wait-ForKafkaHealthy

if (-not $kafkaHealthy) {
    Write-Host ''
    Write-Warning 'Kafka did not become healthy. Apps will not be started.'
    exit 1
}

Write-Host ''
Write-Host 'Kafka playground reset complete.' -ForegroundColor Green

if (-not $SkipApps) {
    Write-Host ''
    Write-Host '========================================' -ForegroundColor Magenta
    Write-Host '  Starting .NET Applications' -ForegroundColor Magenta
    Write-Host '========================================' -ForegroundColor Magenta
    Write-Host ''
    
    $tallyFolder = Join-Path $scriptRoot 'kafkaApp\TallyService'
    $webAppFolder = Join-Path $scriptRoot 'kafkaApp\WebApp'
    
    # Start TallyService first (it processes votes)
    $tallyProcess = Start-DotNetAppInNewTerminal -Name 'TallyService' -ProjectFolder $tallyFolder
    
    # Give TallyService a moment to initialize
    Start-Sleep -Seconds 5
    
    # Start WebApp
    $webAppProcess = Start-DotNetAppInNewTerminal -Name 'WebApp' -ProjectFolder $webAppFolder
    
    # Wait a moment for WebApp to start listening
    Start-Sleep -Seconds 5
    
    Write-Host ''
    Write-Host '========================================' -ForegroundColor Green
    Write-Host '  All services started!' -ForegroundColor Green
    Write-Host '========================================' -ForegroundColor Green
    Write-Host ''
    Write-Host 'Services running:' -ForegroundColor Cyan
    Write-Host "  - TallyService (PID: $($tallyProcess.Id))" -ForegroundColor White
    Write-Host "  - WebApp (PID: $($webAppProcess.Id))" -ForegroundColor White
    Write-Host ''
    Write-Host 'Open the dashboard at: ' -NoNewline -ForegroundColor Cyan
    Write-Host 'http://localhost:5129' -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'To stop apps, close their terminal windows or run:' -ForegroundColor Gray
    Write-Host '  Get-Process dotnet | Stop-Process' -ForegroundColor Gray
} else {
    Write-Host ''
    Write-Host 'Skipping .NET app startup (use without -SkipApps to start them).' -ForegroundColor Gray
}
