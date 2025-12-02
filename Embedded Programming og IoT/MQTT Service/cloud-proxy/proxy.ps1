# Simple HTTP Proxy for Arduino -> Cloudflare MQTT API
# This accepts HTTP requests from Arduino and forwards them to Cloudflare with proper auth headers

param(
    [int]$Port = 8080
)

# Cloudflare Access credentials
$CF_CLIENT_ID = "7b6731f1db1f5c39eabc0af704ca3d4d.access"
$CF_CLIENT_SECRET = "21c58b5f4bd128f140b2c16ec92e4656a0cdad36cce5589b51275640433712dd"
$CLOUD_API_URL = "https://mqtt-api.asgaard.online/publish"

Write-Host "Starting MQTT Cloud Proxy on port $Port..." -ForegroundColor Green
Write-Host "Arduino should POST to: http://<your-pc-ip>:$Port/publish" -ForegroundColor Cyan

$listener = [System.Net.HttpListener]::new()
$listener.Prefixes.Add("http://+:$Port/")

try {
    $listener.Start()
    Write-Host "Proxy running. Press Ctrl+C to stop." -ForegroundColor Green
    
    while ($true) {
        $context = $listener.GetContext()
        $request = $context.Request
        $response = $context.Response
        
        Write-Host "`n$(Get-Date -Format 'HH:mm:ss') - $($request.HttpMethod) $($request.Url.LocalPath)" -ForegroundColor Yellow
        
        if ($request.Url.LocalPath -eq "/publish" -and $request.HttpMethod -eq "POST") {
            # Read the request body
            $reader = [System.IO.StreamReader]::new($request.InputStream)
            $body = $reader.ReadToEnd()
            $reader.Close()
            
            Write-Host "Received: $body" -ForegroundColor Gray
            
            try {
                # Forward to Cloudflare with auth headers
                $headers = @{
                    "CF-Access-Client-Id" = $CF_CLIENT_ID
                    "CF-Access-Client-Secret" = $CF_CLIENT_SECRET
                    "Content-Type" = "application/json"
                }
                
                $cloudResponse = Invoke-RestMethod -Uri $CLOUD_API_URL -Method POST -Headers $headers -Body $body -ContentType "application/json"
                
                Write-Host "Forwarded to Cloudflare successfully!" -ForegroundColor Green
                
                # Send success response to Arduino
                $responseBytes = [System.Text.Encoding]::UTF8.GetBytes("OK")
                $response.StatusCode = 200
                $response.ContentLength64 = $responseBytes.Length
                $response.OutputStream.Write($responseBytes, 0, $responseBytes.Length)
            }
            catch {
                Write-Host "Error forwarding to Cloudflare: $_" -ForegroundColor Red
                $errorBytes = [System.Text.Encoding]::UTF8.GetBytes("Error: $_")
                $response.StatusCode = 500
                $response.ContentLength64 = $errorBytes.Length
                $response.OutputStream.Write($errorBytes, 0, $errorBytes.Length)
            }
        }
        elseif ($request.Url.LocalPath -eq "/health") {
            # Health check endpoint
            $healthBytes = [System.Text.Encoding]::UTF8.GetBytes("Proxy OK")
            $response.StatusCode = 200
            $response.ContentLength64 = $healthBytes.Length
            $response.OutputStream.Write($healthBytes, 0, $healthBytes.Length)
        }
        else {
            # Unknown endpoint
            $notFoundBytes = [System.Text.Encoding]::UTF8.GetBytes("Not Found")
            $response.StatusCode = 404
            $response.ContentLength64 = $notFoundBytes.Length
            $response.OutputStream.Write($notFoundBytes, 0, $notFoundBytes.Length)
        }
        
        $response.Close()
    }
}
catch {
    Write-Host "Error: $_" -ForegroundColor Red
}
finally {
    $listener.Stop()
    Write-Host "Proxy stopped." -ForegroundColor Yellow
}
