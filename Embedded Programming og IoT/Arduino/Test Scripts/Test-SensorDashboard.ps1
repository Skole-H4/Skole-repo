<#
.SYNOPSIS
    Test the Home Assistant sensor dashboard with simulated Arduino data.

.DESCRIPTION
    Sends test sensor data through the MQTT API to verify the dashboard displays
    correctly. Cycles through different binary sensor combinations (motion, clap, gas)
    with 2-second intervals between each test.

.EXAMPLE
    .\Test-SensorDashboard.ps1
    # Runs all 5 test cycles

.EXAMPLE
    .\Test-SensorDashboard.ps1 -DelaySeconds 5
    # Runs tests with 5 seconds between each

.NOTES
    Requires Cloudflare Access service token credentials.
    Dashboard: https://ha.asgaard.online (ZBC - D15 Classroom)
#>

param(
    [int]$DelaySeconds = 2
)

# Cloudflare Access Service Token
$clientId = "7b6731f1db1f5c39eabc0af704ca3d4d.access"
$clientSecret = "21c58b5f4bd128f140b2c16ec92e4656a0cdad36cce5589b51275640433712dd"

$headers = @{
    "CF-Access-Client-Id" = $clientId
    "CF-Access-Client-Secret" = $clientSecret
    "Content-Type" = "application/json"
}

$url = "https://mqtt-api.asgaard.online/publish"

function Send-SensorData {
    param(
        [double]$Temperature,
        [int]$Humidity,
        [int]$Light,
        [int]$Sound,
        [int]$CO2,
        [double]$Dust,
        [bool]$Motion,
        [bool]$Clap,
        [bool]$GasDetected
    )
    
    # Build nested JSON matching Arduino Cloud Mode format
    $sensorData = @{
        topic = "school/classroom/environment"
        message = @{
            temperature = @{ value = $Temperature; unit = "C" }
            humidity = @{ value = $Humidity; unit = "%" }
            light = @{ value = $Light; unit = "lux" }
            noise = @{ value = $Sound; unit = "level" }
            co2 = @{ value = $CO2; unit = "ppm" }
            dust = @{ value = $Dust; unit = "mg/m3" }
            gas = @{ detected = $GasDetected }
            motion = @{ detected = $Motion }
            clap = @{ detected = $Clap }
            timestamp = (Get-Date -Format "o")
            device_id = "test-script-01"
        }
    }
    
    $body = $sensorData | ConvertTo-Json -Depth 4 -Compress
    
    try {
        $response = Invoke-RestMethod -Uri $url -Method POST -Headers $headers -Body $body -ErrorAction Stop
        Write-Host "  ✓ Sent successfully" -ForegroundColor DarkGray
    }
    catch {
        Write-Host "  ✗ Failed: $($_.Exception.Message)" -ForegroundColor Red
    }
}

# Header
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║         🔬 Sensor Dashboard Test Suite                       ║" -ForegroundColor Cyan
Write-Host "║         Testing: Motion, Clap, Gas Detection                 ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "Delay between tests: $DelaySeconds seconds" -ForegroundColor DarkGray
Write-Host "Dashboard: https://ha.asgaard.online" -ForegroundColor DarkGray
Write-Host ""

# Test 1: Motion ON
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor DarkGray
Write-Host "Test 1/5: " -NoNewline; Write-Host "MOTION ON" -ForegroundColor Green
Write-Host "         Temp: 22.5°C | Humidity: 45% | CO2: 650 ppm" -ForegroundColor DarkGray
Send-SensorData -Temperature 22.5 -Humidity 45 -Light 500 -Sound 3 -CO2 650 -Dust 0.015 -Motion $true -Clap $false -GasDetected $false
Start-Sleep -Seconds $DelaySeconds

# Test 2: Clap ON
Write-Host ""
Write-Host "Test 2/5: " -NoNewline; Write-Host "CLAP ON" -ForegroundColor Cyan
Write-Host "         Temp: 23.0°C | Humidity: 46% | CO2: 700 ppm" -ForegroundColor DarkGray
Send-SensorData -Temperature 23.0 -Humidity 46 -Light 520 -Sound 8 -CO2 700 -Dust 0.018 -Motion $false -Clap $true -GasDetected $false
Start-Sleep -Seconds $DelaySeconds

# Test 3: Gas Detected (ALERT!)
Write-Host ""
Write-Host "Test 3/5: " -NoNewline; Write-Host "⚠️  GAS DETECTED!" -ForegroundColor Red
Write-Host "         Temp: 24.0°C | Humidity: 50% | CO2: 800 ppm" -ForegroundColor DarkGray
Send-SensorData -Temperature 24.0 -Humidity 50 -Light 480 -Sound 4 -CO2 800 -Dust 0.025 -Motion $false -Clap $false -GasDetected $true
Start-Sleep -Seconds $DelaySeconds

# Test 4: Motion + Clap ON
Write-Host ""
Write-Host "Test 4/5: " -NoNewline; Write-Host "MOTION + CLAP ON" -ForegroundColor Yellow
Write-Host "         Temp: 22.0°C | Humidity: 48% | CO2: 720 ppm" -ForegroundColor DarkGray
Send-SensorData -Temperature 22.0 -Humidity 48 -Light 550 -Sound 9 -CO2 720 -Dust 0.020 -Motion $true -Clap $true -GasDetected $false
Start-Sleep -Seconds $DelaySeconds

# Test 5: All OFF (normal state)
Write-Host ""
Write-Host "Test 5/5: " -NoNewline; Write-Host "ALL OFF (Normal)" -ForegroundColor White
Write-Host "         Temp: 21.5°C | Humidity: 44% | CO2: 600 ppm" -ForegroundColor DarkGray
Send-SensorData -Temperature 21.5 -Humidity 44 -Light 500 -Sound 2 -CO2 600 -Dust 0.012 -Motion $false -Clap $false -GasDetected $false

# Summary
Write-Host ""
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor DarkGray
Write-Host "✅ All tests complete!" -ForegroundColor Green
Write-Host ""
Write-Host "Check the dashboard to verify:" -ForegroundColor Cyan
Write-Host "  • Sensor values updated" -ForegroundColor Gray
Write-Host "  • Motion/Clap/Gas cards lit up during tests" -ForegroundColor Gray
Write-Host "  • Graphs show data points" -ForegroundColor Gray
Write-Host ""
