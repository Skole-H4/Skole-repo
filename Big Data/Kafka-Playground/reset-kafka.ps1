param(
    [switch]$WaitForHealthy
)
Set-Location "D:\Development\Github\School\Skole-repo\Kafka-Playground"
$ErrorActionPreference = 'Stop'

function Invoke-Compose {
    param(
        [string[]]$ComposeArgs
    )

    $composeFile = Join-Path $PSScriptRoot 'docker-compose.yml'
    $arguments = @('-f', $composeFile) + $ComposeArgs
    & docker compose @arguments
}

Write-Host 'Stopping Kafka playground stack...'
Invoke-Compose -Args @('down', '--remove-orphans', '--volumes') | Out-Null

Write-Host 'Starting Kafka playground stack...'
Invoke-Compose -Args @('up', '-d') | Out-Null

if ($WaitForHealthy) {
    Write-Host 'Waiting for Kafka container to report healthy status...'
    $deadline = (Get-Date).AddMinutes(3)

    while ((Get-Date) -lt $deadline) {
        $statusOutput = Invoke-Compose -Args @('ps')
        if ($statusOutput -match 'kafka-playground-kafka-1\s+.+\s+Up.+\(healthy\)') {
            Write-Host 'Kafka is healthy.'
            break
        }

        Start-Sleep -Seconds 5
    }

    if ((Get-Date) -ge $deadline) {
        Write-Warning 'Timeout waiting for Kafka to become healthy. Check container logs with `docker compose -f Kafka-Playground/docker-compose.yml logs kafka`.'
    }
}

Write-Host 'Kafka playground reset complete.'
