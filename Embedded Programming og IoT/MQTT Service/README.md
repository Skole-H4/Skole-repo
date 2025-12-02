# MQTT Service - Docker Setup

## Quick Start

1. **Generate password file** (run once):
   ```powershell
   cd "d:\Skole\Skole-repo\Embedded Programming og IoT\MQTT Service"
   
   # Create a user (replace 'arduino' and 'yourpassword' with your credentials)
   docker run --rm -v "${PWD}/mosquitto/config:/mosquitto/config" eclipse-mosquitto:2 mosquitto_passwd -b /mosquitto/config/passwd arduino yourpassword
   ```

2. **Start the MQTT broker**:
   ```powershell
   docker compose up -d
   ```

3. **Test the connection**:
   ```powershell
   # Subscribe to a topic (in one terminal)
   docker exec -it mqtt-broker mosquitto_sub -h localhost -u arduino -P yourpassword -t "sensors/#" -v
   
   # Publish a message (in another terminal)
   docker exec -it mqtt-broker mosquitto_pub -h localhost -u arduino -P yourpassword -t "sensors/temp" -m "25.5"
   ```

## Connection Details

| Setting | Value |
|---------|-------|
| Host | Your PC's IP address (e.g., `192.168.1.x`) |
| MQTT Port | `1883` |
| WebSocket Port | `9001` |
| Username | `arduino` (or your chosen username) |
| Password | Your chosen password |

## Arduino Connection Example

```cpp
#include <WiFiS3.h>
#include <PubSubClient.h>

// MQTT Broker settings
const char* mqtt_server = "192.168.1.100";  // Your PC's IP
const int mqtt_port = 1883;
const char* mqtt_user = "arduino";
const char* mqtt_password = "yourpassword";

WiFiClient wifiClient;
PubSubClient mqttClient(wifiClient);

void setup() {
  // ... WiFi setup ...
  
  mqttClient.setServer(mqtt_server, mqtt_port);
  mqttClient.setCallback(callback);
}

void reconnect() {
  while (!mqttClient.connected()) {
    if (mqttClient.connect("ArduinoClient", mqtt_user, mqtt_password)) {
      mqttClient.subscribe("commands/#");
    } else {
      delay(5000);
    }
  }
}

void loop() {
  if (!mqttClient.connected()) {
    reconnect();
  }
  mqttClient.loop();
  
  // Publish sensor data
  mqttClient.publish("sensors/temperature", "25.5");
}
```

## Add More Users

```powershell
# Add another user
docker exec -it mqtt-broker mosquitto_passwd -b /mosquitto/config/passwd newuser newpassword

# Restart to apply changes
docker compose restart
```

## View Logs

```powershell
docker logs -f mqtt-broker
```

## Stop the Service

```powershell
docker compose down
```
