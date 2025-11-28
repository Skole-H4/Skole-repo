// Combined Sensor Dashboard - Arduino UNO R4 WiFi
// Displays Light, Sound, Temperature and Humidity on LED Matrix columns
// 
// Sensors:
//   - LDR Light Sensor LM393: A0 -> Column 0
//   - KY-038 Sound Sensor: A1 -> Column 2
//   - DS18B20 Temperature: Digital Pin 1 -> Column 4
//   - DHT11 Humidity: Digital Pin 2 -> Column 6

#include "Arduino_LED_Matrix.h"
#include <OneWire.h>
#include <DallasTemperature.h>
#include <WiFiS3.h>
#include <DHT.h>

// ============== WIFI CONFIGURATION ==============
// Access Point mode (Arduino creates its own WiFi network)
const char* AP_SSID = "ArduinoSensors";     // Name of the WiFi network Arduino will create
const char* AP_PASSWORD = "sensor1234";     // Password (min 8 characters)

// Client mode (Arduino connects to existing WiFi) - not used in AP mode
// const char* WIFI_SSID = "prog";
// const char* WIFI_PASSWORD = "Alvorlig5And";

// ============== PIN CONFIGURATION ==============
const int LDR_PIN = A0;           // Light sensor analog pin
const int SOUND_PIN = A1;         // Sound sensor analog pin
#define ONE_WIRE_BUS 1            // Temperature sensor digital pin
#define DHT_PIN 2                 // DHT11 humidity sensor digital pin
#define DHT_TYPE DHT11            // DHT sensor type
const int MODE_BUTTON_PIN = 3;    // Push button for mode selection

// ============== MODE CONFIGURATION ==============
bool calibrationMode = false;     // true = calibration, false = AP + webserver

// ============== LED MATRIX CONFIGURATION ==============
ArduinoLEDMatrix matrix;
const int MATRIX_HEIGHT = 8;
const int MATRIX_WIDTH = 12;

// Column assignments for each sensor
const int LIGHT_COLUMN = 0;
const int SOUND_COLUMN = 2;
const int TEMP_COLUMN = 4;
const int HUMIDITY_COLUMN = 6;

// Shared frame buffer (8 rows x 12 columns)
byte frame[8][12] = {
  { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
  { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
  { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
  { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
  { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
  { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
  { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
  { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }
};

// ============== LIGHT SENSOR CALIBRATION ==============
// LM393 Light Sensor Module - outputs LOWER voltage when brighter (inverted)
// Calibration values - adjust based on your readings
const float LIGHT_VOLTAGE_DARK = 4.5;    // Voltage when dark (high value)
const float LIGHT_VOLTAGE_BRIGHT = 1.0;  // Voltage when bright (low value)
const float MIN_LUX = 0.0;               // Minimum lux (dark)
const float MAX_LUX = 900.0;             // Maximum lux (bright)

// ============== SOUND SENSOR CALIBRATION ==============
const int MIN_SOUND = 79;         // Minimum expected analog reading
const int MAX_SOUND = 85;         // Maximum analog reading (narrower = more sensitive)

// ============== TEMPERATURE CALIBRATION ==============
const float MIN_TEMP = 15.0;      // Minimum temperature (°C)
const float MAX_TEMP = 35.0;      // Maximum temperature (°C)

// ============== HUMIDITY CALIBRATION ==============
const float MIN_HUMIDITY = 20.0;  // Minimum humidity (%)
const float MAX_HUMIDITY = 80.0;  // Maximum humidity (%)

// ============== TEMPERATURE SENSOR SETUP ==============
OneWire oneWire(ONE_WIRE_BUS);
DallasTemperature tempSensor(&oneWire);

// ============== HUMIDITY SENSOR SETUP ==============
DHT dhtSensor(DHT_PIN, DHT_TYPE);

// ============== WEB SERVER ==============
WiFiServer server(80);  // HTTP server on port 80

// ============== TIMING ==============
unsigned long lastTempRead = 0;
const unsigned long TEMP_INTERVAL = 1000;  // Read temperature every 1 second
float lastTemperature = 0;
float lastHumidity = 0;

// Global sensor values for web server
float currentLightLux = 0;
int currentSoundValue = 0;

void setup() {
  Serial.begin(9600);
  delay(2000);  // Wait for Serial to be ready
  
  // Setup mode button with internal pull-up
  pinMode(MODE_BUTTON_PIN, INPUT_PULLUP);
  
  // Check button state at startup (LOW = pressed because of pull-up)
  calibrationMode = (digitalRead(MODE_BUTTON_PIN) == LOW);
  
  Serial.println();
  Serial.println("=== Arduino UNO R4 WiFi Starting ===");
  Serial.println();
  
  if (calibrationMode) {
    Serial.println("*** CALIBRATION MODE ***");
    Serial.println("Button was held during startup");
    Serial.println("WiFi and web server DISABLED");
    Serial.println("Raw sensor values will be printed for calibration");
    Serial.println();
  }
  
  // Initialize LED Matrix
  matrix.begin();
  
  // ============== CREATE ACCESS POINT (only in normal mode) ==============
  if (!calibrationMode) {
    Serial.println("Creating WiFi Access Point...");
    Serial.print("SSID: ");
    Serial.println(AP_SSID);
    Serial.print("Password: ");
    Serial.println(AP_PASSWORD);
    Serial.println();
    
    // Create the Access Point
    int status = WiFi.beginAP(AP_SSID, AP_PASSWORD);
    
    if (status != WL_AP_LISTENING) {
      Serial.println("Creating Access Point failed!");
      Serial.print("Status: ");
      Serial.println(status);
      // Continue anyway, sensors will still work
    } else {
      Serial.println("Access Point created successfully!");
      delay(2000);  // Give AP time to start
      
      Serial.print("AP IP Address: ");
      Serial.println(WiFi.localIP());
      
      // Start web server
      server.begin();
      Serial.println();
      Serial.println("Web server started!");
      Serial.println();
      Serial.println("===========================================");
      Serial.println("Connect your device to WiFi network:");
      Serial.print("  SSID: ");
      Serial.println(AP_SSID);
      Serial.print("  Password: ");
      Serial.println(AP_PASSWORD);
      Serial.println();
      Serial.print("Then open: http://");
      Serial.println(WiFi.localIP());
      Serial.println("===========================================");
    }
    Serial.println();
  }
  
  // Initialize temperature sensor
  tempSensor.begin();
  
  // Initialize humidity sensor
  dhtSensor.begin();
  
  Serial.println("=== Combined Sensor Dashboard ===");
  Serial.println("Column 0: Light Level");
  Serial.println("Column 2: Sound Level");
  Serial.println("Column 4: Temperature");
  Serial.println("Column 6: Humidity");
  Serial.println();
  
  // Check for temperature sensor
  int deviceCount = tempSensor.getDeviceCount();
  Serial.print("Temperature sensors found: ");
  Serial.println(deviceCount);
  if (deviceCount == 0) {
    Serial.println("WARNING: No DS18B20 sensor found!");
  }
  Serial.println();
}

void loop() {
  // ============== HANDLE WEB CLIENTS (only in normal mode) ==============
  if (!calibrationMode) {
    handleWebClient();
  }
  
  // ============== READ LIGHT SENSOR ==============
  int lightRaw = analogRead(LDR_PIN);
  float lightVoltage = (lightRaw / 1023.0) * 5.0;
  // LM393: INVERTED - lower voltage = more light
  // Map from voltage range to lux (inverted: dark voltage -> 0 lux, bright voltage -> max lux)
  float lightLux = ((LIGHT_VOLTAGE_DARK - lightVoltage) / (LIGHT_VOLTAGE_DARK - LIGHT_VOLTAGE_BRIGHT)) * MAX_LUX;
  lightLux = constrain(lightLux, MIN_LUX, MAX_LUX);
  currentLightLux = lightLux;  // Store for web server
  int lightLeds = map(lightLux, MIN_LUX, MAX_LUX, 0, MATRIX_HEIGHT);
  lightLeds = constrain(lightLeds, 0, MATRIX_HEIGHT);
  
  // ============== READ SOUND SENSOR ==============
  int soundValue = analogRead(SOUND_PIN);
  currentSoundValue = soundValue;  // Store for web server
  int soundLeds = map(soundValue, MIN_SOUND, MAX_SOUND, 0, MATRIX_HEIGHT);
  soundLeds = constrain(soundLeds, 0, MATRIX_HEIGHT);
  
  // ============== READ TEMPERATURE SENSOR ==============
  // Only read temperature every TEMP_INTERVAL ms (it's slow)
  if (millis() - lastTempRead >= TEMP_INTERVAL) {
    tempSensor.requestTemperatures();
    float temp = tempSensor.getTempCByIndex(0);
    if (temp != DEVICE_DISCONNECTED_C) {
      lastTemperature = temp;
    }
    lastTempRead = millis();
  }
  int tempLeds = map(lastTemperature * 10, MIN_TEMP * 10, MAX_TEMP * 10, 0, MATRIX_HEIGHT);
  tempLeds = constrain(tempLeds, 0, MATRIX_HEIGHT);
  
  // ============== READ HUMIDITY SENSOR ==============
  // DHT11 is also slow, read together with temperature
  static unsigned long lastHumidityRead = 0;
  if (millis() - lastHumidityRead >= TEMP_INTERVAL) {
    float humidity = dhtSensor.readHumidity();
    if (!isnan(humidity)) {
      lastHumidity = humidity;
    }
    lastHumidityRead = millis();
  }
  int humidityLeds = map(lastHumidity * 10, MIN_HUMIDITY * 10, MAX_HUMIDITY * 10, 0, MATRIX_HEIGHT);
  humidityLeds = constrain(humidityLeds, 0, MATRIX_HEIGHT);
  
  // ============== UPDATE FRAME BUFFER ==============
  updateColumn(LIGHT_COLUMN, lightLeds);
  updateColumn(SOUND_COLUMN, soundLeds);
  updateColumn(TEMP_COLUMN, tempLeds);
  updateColumn(HUMIDITY_COLUMN, humidityLeds);
  
  // ============== RENDER TO MATRIX ==============
  matrix.renderBitmap(frame, 8, 12);
  
  // ============== DEBUG OUTPUT ==============
  static unsigned long lastPrint = 0;
  unsigned long printInterval = calibrationMode ? 200 : 500;  // Faster in calibration mode
  
  if (millis() - lastPrint >= printInterval) {
    if (calibrationMode) {
      // Detailed calibration output
      Serial.println("-------- CALIBRATION VALUES --------");
      Serial.print("LIGHT:    RAW=");
      Serial.print(lightRaw);
      Serial.print("  V=");
      Serial.print(lightVoltage, 3);
      Serial.print("  -> ");
      Serial.print(lightLux, 1);
      Serial.println(" lux");
      
      Serial.print("SOUND:    RAW=");
      Serial.println(soundValue);
      
      Serial.print("TEMP:     ");
      Serial.print(lastTemperature, 2);
      Serial.println(" C");
      
      Serial.print("HUMIDITY: ");
      Serial.print(lastHumidity, 2);
      Serial.println(" %");
      Serial.println();
    } else {
      // Normal compact output
      Serial.print("Light: RAW=");
      Serial.print(lightRaw);
      Serial.print(" V=");
      Serial.print(lightVoltage, 2);
      Serial.print(" -> ");
      Serial.print(lightLux, 0);
      Serial.print(" lux | Sound: ");
      Serial.print(soundValue);
      Serial.print(" | Temp: ");
      Serial.print(lastTemperature, 1);
      Serial.print("C | Humidity: ");
      Serial.print(lastHumidity, 1);
      Serial.println("%");
    }
    lastPrint = millis();
  }
  
  delay(10);  // Fast update for responsive sound
}

// Calculate number of LEDs based on voltage thresholds
int calculateLeds(float value, float minVal, float maxVal) {
  if (value >= maxVal) return MATRIX_HEIGHT;
  if (value <= minVal) return 0;
  float normalized = (value - minVal) / (maxVal - minVal);
  return (int)(normalized * MATRIX_HEIGHT);
}

// Update a single column in the frame buffer (bottom to top)
void updateColumn(int column, int ledCount) {
  for (int row = 0; row < MATRIX_HEIGHT; row++) {
    // Light from bottom (row 7) to top (row 0)
    int actualRow = MATRIX_HEIGHT - 1 - row;
    frame[actualRow][column] = (row < ledCount) ? 1 : 0;
  }
}

// Handle incoming web requests
void handleWebClient() {
  WiFiClient client = server.available();
  
  if (client) {
    Serial.println(">>> Client connected!");
    String currentLine = "";
    String requestPath = "";
    unsigned long timeout = millis() + 3000;  // 3 second timeout
    
    while (client.connected() && millis() < timeout) {
      if (client.available()) {
        char c = client.read();
        
        if (c == '\n') {
          // End of HTTP header line
          if (currentLine.length() == 0) {
            // Empty line = end of headers, send response
            Serial.print(">>> Request path: ");
            Serial.println(requestPath);
            
            if (requestPath.indexOf("/api/sensors") >= 0) {
              // JSON API endpoint
              sendJsonResponse(client);
            } else {
              // HTML page
              sendHtmlPage(client);
            }
            Serial.println(">>> Response sent");
            break;
          } else {
            // Parse the request line to get the path
            if (currentLine.startsWith("GET ")) {
              int pathStart = 4;
              int pathEnd = currentLine.indexOf(' ', pathStart);
              requestPath = currentLine.substring(pathStart, pathEnd);
            }
            currentLine = "";
          }
        } else if (c != '\r') {
          currentLine += c;
        }
      }
    }
    
    delay(1);  // Give client time to receive data
    client.stop();
  }
}

// Send JSON response with sensor data
void sendJsonResponse(WiFiClient& client) {
  String json = "{";
  json += "\"light_lux\":" + String(currentLightLux, 0) + ",";
  json += "\"sound_raw\":" + String(currentSoundValue) + ",";
  json += "\"temperature_c\":" + String(lastTemperature, 1) + ",";
  json += "\"temperature_f\":" + String(lastTemperature * 9.0 / 5.0 + 32, 1) + ",";
  json += "\"humidity\":" + String(lastHumidity, 1);
  json += "}";
  
  client.println("HTTP/1.1 200 OK");
  client.println("Content-Type: application/json");
  client.println("Access-Control-Allow-Origin: *");
  client.println("Connection: close");
  client.println();
  client.println(json);
}

// Send HTML dashboard page with JavaScript live updates
void sendHtmlPage(WiFiClient& client) {
  client.println("HTTP/1.1 200 OK");
  client.println("Content-Type: text/html");
  client.println("Connection: close");
  client.println();
  
  client.println("<!DOCTYPE html>");
  client.println("<html><head>");
  client.println("<title>Arduino Sensor Dashboard</title>");
  client.println("<meta name='viewport' content='width=device-width, initial-scale=1'>");
  client.println("<style>");
  client.println("body { font-family: Arial, sans-serif; max-width: 600px; margin: 50px auto; padding: 20px; background: #1a1a2e; color: #eee; }");
  client.println("h1 { color: #00d4ff; text-align: center; }");
  client.println(".sensor { background: #16213e; border-radius: 10px; padding: 20px; margin: 15px 0; }");
  client.println(".sensor h2 { margin: 0 0 10px 0; color: #00d4ff; font-size: 1.1em; }");
  client.println(".value { font-size: 2.5em; font-weight: bold; transition: all 0.3s ease; }");
  client.println(".light { color: #ffd700; }");
  client.println(".sound { color: #ff6b6b; }");
  client.println(".temp { color: #4ecdc4; }");
  client.println(".humidity { color: #a29bfe; }");
  client.println(".bar { background: #0f3460; border-radius: 5px; height: 20px; margin-top: 10px; overflow: hidden; }");
  client.println(".bar-fill { height: 100%; border-radius: 5px; transition: width 0.3s ease; }");
  client.println(".bar-light { background: linear-gradient(90deg, #ffd700, #ffed4a); }");
  client.println(".bar-sound { background: linear-gradient(90deg, #ff6b6b, #ee5a5a); }");
  client.println(".bar-temp { background: linear-gradient(90deg, #4ecdc4, #44a08d); }");
  client.println(".bar-humidity { background: linear-gradient(90deg, #a29bfe, #6c5ce7); }");
  client.println(".status { text-align: center; color: #666; margin-top: 30px; font-size: 0.9em; }");
  client.println(".status.connected { color: #4ecdc4; }");
  client.println(".status.error { color: #ff6b6b; }");
  client.println("</style></head><body>");
  
  client.println("<h1>Sensor Dashboard</h1>");
  
  // Light sensor
  client.println("<div class='sensor'>");
  client.println("<h2>Light Level</h2>");
  client.println("<div class='value light' id='light-value'>-- lux</div>");
  client.println("<div class='bar'><div class='bar-fill bar-light' id='light-bar' style='width:0%'></div></div></div>");
  
  // Sound sensor
  client.println("<div class='sensor'>");
  client.println("<h2>Sound Level</h2>");
  client.println("<div class='value sound' id='sound-value'>--</div>");
  client.println("<div class='bar'><div class='bar-fill bar-sound' id='sound-bar' style='width:0%'></div></div></div>");
  
  // Temperature sensor
  client.println("<div class='sensor'>");
  client.println("<h2>Temperature</h2>");
  client.println("<div class='value temp' id='temp-value'>-- &deg;C</div>");
  client.println("<div class='bar'><div class='bar-fill bar-temp' id='temp-bar' style='width:0%'></div></div></div>");
  
  // Humidity sensor
  client.println("<div class='sensor'>");
  client.println("<h2>Humidity</h2>");
  client.println("<div class='value humidity' id='humidity-value'>-- %</div>");
  client.println("<div class='bar'><div class='bar-fill bar-humidity' id='humidity-bar' style='width:0%'></div></div></div>");
  
  client.println("<div class='status' id='status'>Connecting...</div>");
  
  // JavaScript for live updates
  client.println("<script>");
  client.println("const config = {");
  client.print("  lightMin: "); client.print(MIN_LUX); client.println(",");
  client.print("  lightMax: "); client.print(MAX_LUX); client.println(",");
  client.print("  soundMin: "); client.print(MIN_SOUND); client.println(",");
  client.print("  soundMax: "); client.print(MAX_SOUND); client.println(",");
  client.print("  tempMin: "); client.print(MIN_TEMP); client.println(",");
  client.print("  tempMax: "); client.print(MAX_TEMP); client.println(",");
  client.print("  humidityMin: "); client.print(MIN_HUMIDITY); client.println(",");
  client.print("  humidityMax: "); client.print(MAX_HUMIDITY);
  client.println("};");
  
  client.println("const EventBus = {");
  client.println("  listeners: {},");
  client.println("  on(event, callback) {");
  client.println("    if (!this.listeners[event]) this.listeners[event] = [];");
  client.println("    this.listeners[event].push(callback);");
  client.println("  },");
  client.println("  emit(event, data) {");
  client.println("    if (this.listeners[event]) {");
  client.println("      this.listeners[event].forEach(cb => cb(data));");
  client.println("    }");
  client.println("  }");
  client.println("};");
  
  client.println("function clamp(val, min, max) { return Math.max(min, Math.min(max, val)); }");
  client.println("function percent(val, min, max) { return clamp(((val - min) / (max - min)) * 100, 0, 100); }");
  
  client.println("EventBus.on('sensors', data => {");
  client.println("  document.getElementById('light-value').textContent = Math.round(data.light_lux) + ' lux';");
  client.println("  document.getElementById('light-bar').style.width = percent(data.light_lux, config.lightMin, config.lightMax) + '%';");
  client.println("  document.getElementById('sound-value').textContent = data.sound_raw;");
  client.println("  document.getElementById('sound-bar').style.width = percent(data.sound_raw, config.soundMin, config.soundMax) + '%';");
  client.println("  document.getElementById('temp-value').innerHTML = data.temperature_c.toFixed(1) + ' &deg;C';");
  client.println("  document.getElementById('temp-bar').style.width = percent(data.temperature_c, config.tempMin, config.tempMax) + '%';");
  client.println("  document.getElementById('humidity-value').textContent = data.humidity.toFixed(1) + ' %';");
  client.println("  document.getElementById('humidity-bar').style.width = percent(data.humidity, config.humidityMin, config.humidityMax) + '%';");
  client.println("});");
  
  client.println("EventBus.on('status', msg => {");
  client.println("  const el = document.getElementById('status');");
  client.println("  el.textContent = msg.text;");
  client.println("  el.className = 'status ' + (msg.type || '');");
  client.println("});");
  
  client.println("async function fetchSensors() {");
  client.println("  try {");
  client.println("    const res = await fetch('/api/sensors');");
  client.println("    const data = await res.json();");
  client.println("    EventBus.emit('sensors', data);");
  client.println("    EventBus.emit('status', { text: 'Live updates active', type: 'connected' });");
  client.println("  } catch (e) {");
  client.println("    EventBus.emit('status', { text: 'Connection lost - retrying...', type: 'error' });");
  client.println("  }");
  client.println("}");
  
  client.println("fetchSensors();");
  client.println("setInterval(fetchSensors, 500);");  // Update every 500ms
  client.println("</script>");
  
  client.println("</body></html>");
}
