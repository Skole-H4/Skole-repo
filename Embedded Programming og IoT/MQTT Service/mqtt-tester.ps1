<#
.SYNOPSIS
    Test the MQTT HTTP API endpoint with Cloudflare Service Token authentication.

.DESCRIPTION
    Sends a test message to the MQTT API (mqtt-api.asgaard.online) to verify:
    1. Cloudflare Access Service Token authentication works
    2. HTTP-to-MQTT bridge is functioning
    3. Message is published to Mosquitto broker

.PARAMETER Topic
    The MQTT topic to publish to. Default: "school/classroom/environment"

.PARAMETER Message
    The message payload to send. Default: simulated classroom sensor data

.PARAMETER ShowResponse
    Display the full response body

.PARAMETER SimulateSensors
    Send realistic classroom environmental sensor data (temperature, humidity, light, noise, dust, gas, motion)

.EXAMPLE
    .\Test-MqttApi.ps1
    # Sends simulated classroom sensor data

.EXAMPLE
    .\Test-MqttApi.ps1 -SimulateSensors
    # Same as default - sends full sensor payload

.EXAMPLE
    .\Test-MqttApi.ps1 -Topic "sensors/temperature" -Message '{"value": 23.5, "unit": "C"}'
    # Sends a custom temperature reading

.EXAMPLE
    .\Test-MqttApi.ps1 -Topic "home/livingroom/motion" -Message '{"detected": true}'
    # Sends a motion detection event
#>

param(
    [string]$Topic = "school/classroom/environment",
    [string]$Message = "",
    [switch]$ShowResponse,
    [switch]$SimulateSensors
)

# Load secrets from secrets.env
$repoRoot = (Get-Item $PSScriptRoot).Parent.Parent.FullName
#$secretsFile = Join-Path $repoRoot "secrets.env"
#
#if (-not (Test-Path $secretsFile)) {
#    Write-Error "secrets.env not found at: $secretsFile"
#    exit 1
#}
#
## Parse secrets.env
#$secrets = @{}
#Get-Content $secretsFile | ForEach-Object {
#    if ($_ -match '^\s*([^#][^=]+)=(.*)$') {
#        $secrets[$matches[1].Trim()] = $matches[2].Trim()
#    }
#}

$clientId = "7b6731f1db1f5c39eabc0af704ca3d4d.access"
$clientSecret = "21c58b5f4bd128f140b2c16ec92e4656a0cdad36cce5589b51275640433712dd"

if (-not $clientId -or -not $clientSecret) {
    Write-Error "CF_IOT_SERVICE_TOKEN_ID or CF_IOT_SERVICE_TOKEN_SECRET not found in secrets.env"
    exit 1
}

# Build payload
if (-not $Message) {
    # Generate randomized sensor values within realistic ranges
    $temperature = [math]::Round((Get-Random -Minimum 180 -Maximum 320) / 10, 1)  # 18.0 - 32.0 °C
    $humidity = Get-Random -Minimum 25 -Maximum 75                                  # 25 - 75 %
    $light = Get-Random -Minimum 100 -Maximum 1500                                  # 100 - 1500 lux
    $noise = Get-Random -Minimum 1 -Maximum 10                                      # 1 - 10 level
    $dust = [math]::Round((Get-Random -Minimum 1 -Maximum 20) / 10, 1)             # 0.1 - 2.0 mg/m³
    $gasDetected = (Get-Random -Minimum 0 -Maximum 100) -lt 5                       # 5% chance
    $motionDetected = (Get-Random -Minimum 0 -Maximum 100) -lt 30                   # 30% chance
   
    $sensorData = @{
        temperature = @{
            value = $temperature
            unit = "C"
        }
        humidity = @{
            value = $humidity
            unit = "%"
        }
        light = @{
            value = $light
            unit = "lux"
        }
        noise = @{
            value = $noise
            unit = "level"
        }
        dust = @{
            value = $dust
            unit = "mg/m3"
        }
        gas = @{
            detected = $gasDetected
        }
        motion = @{
            detected = $motionDetected
        }
        timestamp = (Get-Date -Format "o")
        device_id = "classroom-sensor-01"
    }
    $Message = $sensorData | ConvertTo-Json -Compress -Depth 3
}

$payload = @{
    topic = $Topic
    message = $Message   # API expects 'message' not 'payload'
} | ConvertTo-Json

# API endpoint
$url = "https://mqtt-api.asgaard.online/publish"

Write-Host "`n📡 MQTT API Test" -ForegroundColor Cyan
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor DarkGray
Write-Host "URL:     " -NoNewline; Write-Host $url -ForegroundColor Yellow
Write-Host "Topic:   " -NoNewline; Write-Host $Topic -ForegroundColor Yellow
Write-Host "Payload: " -NoNewline; Write-Host $Message -ForegroundColor Yellow
Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor DarkGray

try {
    $headers = @{
        "CF-Access-Client-Id" = $clientId
        "CF-Access-Client-Secret" = $clientSecret
        "Content-Type" = "application/json"
    }

    $response = Invoke-WebRequest -Uri $url -Method POST -Headers $headers -Body $payload -ErrorAction Stop
   
    # Check if we got the Cloudflare Access login page instead of actual success
    if ($response.Content -match "cloudflareaccess.com" -or $response.Content -match "Sign in with") {
        Write-Host "`n❌ FAILED" -ForegroundColor Red
        Write-Host "Got Cloudflare Access login page instead of API response" -ForegroundColor Red
        Write-Host "The Service Token may not be configured correctly in Cloudflare Access" -ForegroundColor Yellow
        Write-Host "`nCheck:" -ForegroundColor Cyan
        Write-Host "  1. Service Token exists in Cloudflare Zero Trust → Access → Service Auth" -ForegroundColor Gray
        Write-Host "  2. The mqtt-api Access policy allows this Service Token" -ForegroundColor Gray
        Write-Host "  3. CF_IOT_SERVICE_TOKEN_ID and CF_IOT_SERVICE_TOKEN_SECRET are correct" -ForegroundColor Gray
        exit 1
    }

    Write-Host "`n✅ SUCCESS" -ForegroundColor Green
    Write-Host "Message published to MQTT broker!" -ForegroundColor Green
   
    if ($ShowResponse -or $response.Content) {
        Write-Host "`nResponse:" -ForegroundColor Cyan
        Write-Host $response.Content -ForegroundColor Gray
    }
}
catch {
    $statusCode = $null
    $responseBody = $null
   
    if ($_.Exception.Response) {
        $statusCode = [int]$_.Exception.Response.StatusCode
        try {
            $stream = $_.Exception.Response.GetResponseStream()
            $reader = [System.IO.StreamReader]::new($stream)
            $responseBody = $reader.ReadToEnd()
            $reader.Close()
            $stream.Close()
        } catch {}
    }
   
    Write-Host "`n❌ FAILED" -ForegroundColor Red
   
    if ($statusCode -eq 403) {
        Write-Host "Authentication failed (403 Forbidden)" -ForegroundColor Red
        Write-Host "Check that CF_IOT_SERVICE_TOKEN_ID and CF_IOT_SERVICE_TOKEN_SECRET are correct" -ForegroundColor Yellow
    }
    elseif ($statusCode -eq 401) {
        Write-Host "Unauthorized (401)" -ForegroundColor Red
        Write-Host "Service token may be expired or revoked" -ForegroundColor Yellow
    }
    elseif ($statusCode) {
        Write-Host "HTTP $statusCode" -ForegroundColor Red
    }
   
    Write-Host "`nError: $($_.Exception.Message)" -ForegroundColor Red
   
    if ($responseBody) {
        Write-Host "`nResponse body:" -ForegroundColor Yellow
        Write-Host $responseBody -ForegroundColor Gray
    }
   
    exit 1
}

Write-Host "`n💡 To see this message in Cedalo Management Center:" -ForegroundColor Cyan
Write-Host "   1. Open https://mqtt.asgaard.online" -ForegroundColor Gray
Write-Host "   2. Subscribe to topic: $Topic" -ForegroundColor Gray
Write-Host ""
