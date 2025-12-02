# Cloud API Requirements for Home Assistant Integration

## Overview
The Arduino sends sensor data to `mqtt-api.asgaard.online` via HTTPS POST. The cloud API must republish this data to an MQTT broker that the remote Home Assistant subscribes to.

## Current Arduino Cloud Payload Format

The Arduino sends a POST request to `/publish` with this JSON structure:

```json
{
  "topic": "school/classroom/environment",
  "message": {
    "temperature": {"value": 21.7, "unit": "C"},
    "humidity": {"value": 33, "unit": "%"},
    "light": {"value": 924, "unit": "lux"},
    "noise": {"value": 5, "unit": "level"},
    "dust": {"value": 0.001, "unit": "mg/m3"},
    "gas": {"detected": false},
    "motion": {"detected": true},
    "clap": {"detected": false},
    "co2": {"value": 650, "unit": "ppm"},
    "device_id": "arduino-classroom-01"
  }
}
```

## Required MQTT Output Format

The cloud API must publish to **individual MQTT topics** with **simple values** (not nested JSON).

Home Assistant expects these exact topics and payload formats:

| Topic | Payload Format | Example |
|-------|----------------|---------|
| `sensors/temperature` | Float as string | `"21.7"` |
| `sensors/humidity` | Float as string | `"33.0"` |
| `sensors/light` | Float as string | `"924.0"` |
| `sensors/sound` | Integer as string | `"5"` |
| `sensors/dust` | Float as string (µg/m³) | `"1.0"` |
| `sensors/co2` | Integer as string | `"650"` |
| `sensors/gas` | Integer as string (raw) | `"106"` |
| `sensors/motion` | `"ON"` or `"OFF"` | `"ON"` |
| `sensors/clap` | `"ON"` or `"OFF"` | `"OFF"` |
| `sensors/gas_detected` | `"ON"` or `"OFF"` | `"OFF"` |

### Important Notes:

1. **Dust unit conversion**: Arduino sends `mg/m³`, but HA expects `µg/m³`. Multiply by 1000.
   - Input: `0.001 mg/m³` → Output: `"1.0"` (as µg/m³)

2. **Sound/Noise**: Arduino sends as `noise.value` (1-10 scale), publish as `sensors/sound`

3. **Gas sensor**: Arduino only sends `gas.detected` boolean. The raw value is not available in cloud mode.
   - Option A: Publish a dummy value like `"0"` for gas raw
   - Option B: Only publish `sensors/gas_detected` as `"ON"`/`"OFF"`

4. **Binary sensors**: Convert boolean to string
   - `true` → `"ON"`
   - `false` → `"OFF"`

## Transformation Logic (Pseudocode)

```python
def transform_and_publish(payload):
    msg = payload["message"]
    
    # Numeric sensors
    mqtt_publish("sensors/temperature", str(msg["temperature"]["value"]))
    mqtt_publish("sensors/humidity", str(msg["humidity"]["value"]))
    mqtt_publish("sensors/light", str(msg["light"]["value"]))
    mqtt_publish("sensors/sound", str(msg["noise"]["value"]))
    mqtt_publish("sensors/co2", str(msg["co2"]["value"]))
    
    # Dust: convert mg/m³ to µg/m³
    dust_ug = msg["dust"]["value"] * 1000
    mqtt_publish("sensors/dust", str(dust_ug))
    
    # Binary sensors
    mqtt_publish("sensors/motion", "ON" if msg["motion"]["detected"] else "OFF")
    mqtt_publish("sensors/clap", "ON" if msg["clap"]["detected"] else "OFF")
    mqtt_publish("sensors/gas_detected", "ON" if msg["gas"]["detected"] else "OFF")
    
    # Gas raw value not available - publish placeholder or skip
    mqtt_publish("sensors/gas", "0")
```

## Authentication

The Arduino sends Cloudflare Access headers:
- `CF-Access-Client-Id`: `7b6731f1db1f5c39eabc0af704ca3d4d.access`
- `CF-Access-Client-Secret`: (stored in Arduino code)

## Home Assistant MQTT Configuration

See `configuration.yaml` in this folder for the complete HA sensor configuration that expects the above topic/payload format.

## Testing

Use this PowerShell script to simulate Arduino payloads to the cloud API:

```powershell
$body = @{
    topic = "school/classroom/environment"
    message = @{
        temperature = @{ value = 21.7; unit = "C" }
        humidity = @{ value = 33; unit = "%" }
        light = @{ value = 924; unit = "lux" }
        noise = @{ value = 5; unit = "level" }
        dust = @{ value = 0.001; unit = "mg/m3" }
        gas = @{ detected = $false }
        motion = @{ detected = $true }
        clap = @{ detected = $false }
        co2 = @{ value = 650; unit = "ppm" }
        device_id = "arduino-classroom-01"
    }
} | ConvertTo-Json -Depth 3

Invoke-RestMethod -Uri "https://mqtt-api.asgaard.online/publish" `
    -Method POST `
    -ContentType "application/json" `
    -Headers @{
        "CF-Access-Client-Id" = "7b6731f1db1f5c39eabc0af704ca3d4d.access"
        "CF-Access-Client-Secret" = "YOUR_SECRET_HERE"
    } `
    -Body $body
```

## Verify with MQTT Subscribe

After the API publishes, verify with:

```bash
mosquitto_sub -h YOUR_MQTT_BROKER -t "sensors/#" -v
```

Expected output:
```
sensors/temperature 21.7
sensors/humidity 33.0
sensors/light 924.0
sensors/sound 5
sensors/co2 650
sensors/dust 1.0
sensors/motion ON
sensors/clap OFF
sensors/gas_detected OFF
sensors/gas 0
```
