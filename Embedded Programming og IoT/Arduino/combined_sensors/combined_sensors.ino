// Combined Sensor Dashboard - Arduino UNO R4 WiFi
// Displays Light, Sound, Temperature and Humidity on LED Matrix columns
// 
// MODES (press button to cycle through):
//   - MQTT Mode: Publishes sensor data to local MQTT broker (Home Assistant)
//   - Web Server Mode: Hosts a web dashboard on the Arduino
//   - Cloud Mode: Sends sensor data to mqtt-api.asgaard.online via HTTPS
// 
// Sensors:
//   - LDR Light Sensor LM393: A0 -> Column 0
//   - KY-038 Sound Sensor: A1 -> Column 2
//   - MQ2 Flammable Gas/Smoke Sensor (FC-22): A2 -> Detects flammable gases & smoke
//   - DS18B20 Temperature: Digital Pin 1 -> Column 4
//   - DHT11 Humidity: Digital Pin 2 -> Column 6
//   - HC-SR501 PIR Motion: Digital Pin 4 -> Motion detection
//   - Grove Dust Sensor (PPD42NS): Digital Pin 5 -> Air quality/dust
//   - Sound Module Digital Out (Clap): Digital Pin 6 -> Clap detection
//   - Gravity CO2 Sensor V2.0 (DFRobot): A3 -> CO2 concentration (400-5000 ppm)
//   - LCM1602C LCD (I2C): A4 (SDA), A5 (SCL) -> Display readouts

#include "Arduino_LED_Matrix.h"
#include <OneWire.h>
#include <DallasTemperature.h>
#include <WiFiS3.h>
#include <DHT.h>
#include <PubSubClient.h>
#include <LiquidCrystal_I2C.h>
#include <Wire.h>
#include <ArduinoHttpClient.h>

// ============== WIFI CONFIGURATION ==============
// Client mode - connect to existing WiFi network
const char* WIFI_SSID = "prog";
const char* WIFI_PASSWORD = "Alvorlig5And";

// ============== MQTT CONFIGURATION ==============
const char* MQTT_SERVER = "10.108.137.82";  // Your PC's WiFi IP address
const int MQTT_PORT = 1883;
const char* MQTT_USER = "arduino";
const char* MQTT_PASSWORD = "sensor1234";
const char* MQTT_CLIENT_ID = "ArduinoSensors";

// ============== CLOUD API CONFIGURATION ==============
// Direct HTTPS to mqtt-api.asgaard.online via Cloudflare
const bool CLOUD_DEBUG = false;  // Set to true for verbose HTTP debugging
const char* CLOUD_API_HOST = "mqtt-api.asgaard.online";
const int CLOUD_API_PORT = 443;
const char* CLOUD_API_PATH = "/publish";
const char* CF_CLIENT_ID = "7b6731f1db1f5c39eabc0af704ca3d4d.access";
const char* CF_CLIENT_SECRET = "21c58b5f4bd128f140b2c16ec92e4656a0cdad36cce5589b51275640433712dd";
const char* CLOUD_TOPIC = "school/classroom/environment";
const char* CLOUD_DEVICE_ID = "arduino-classroom-01";

// GTS Root R1 - Google Trust Services (used by Cloudflare for this domain)
// Issuer: CN=WE1, O=Google Trust Services, C=US
// Valid until: 2036
const char* CLOUDFLARE_ROOT_CA = 
"-----BEGIN CERTIFICATE-----\n"
"MIIFVzCCAz+gAwIBAgINAgPlk28xsBNJiGuiFzANBgkqhkiG9w0BAQwFADBHMQsw\n"
"CQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEU\n"
"MBIGA1UEAxMLR1RTIFJvb3QgUjEwHhcNMTYwNjIyMDAwMDAwWhcNMzYwNjIyMDAw\n"
"MDAwWjBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZp\n"
"Y2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjEwggIiMA0GCSqGSIb3DQEBAQUA\n"
"A4ICDwAwggIKAoICAQC2EQKLHuOhd5s73L+UPreVp0A8of2C+X0yBoJx9vaMf/vo\n"
"27xqLpeXo4xL+Sv2sfnOhB2x+cWX3u+58qPpvBKJXqeqUqv4IyfLpLGcY9vXmX7w\n"
"Cl7raKb0xlpHDU0QM+NOsROjyBhsS+z8CZDfnWQpJSMHobTSPS5g4M/SCYe7zUjw\n"
"TcLCeoiKu7rPWRnWr4+wB7CeMfGCwcDfLqZtbBkOtdh+JhpFAz2weaSUKK0Pfge/\n"
"BmQNNQz0H6gR+bIjlHN6WRweDbzqZ4dPl/WFHrGkqvvfGshw6MOWBN4buqpfpT+j\n"
"qHXyjj/pFoHN0dCWvlRD/2+OlPFOT+7ysqSZPkxKCRxxS4s0iJj2NR9hKXqRPZPu\n"
"hYP1YLjv2qL3zG3QT82adQ/yCL7l54h4zdE3HyG7EuK10Gw/awfSLbqOn9AwQVwT\n"
"6uc7FPYWPPaJgaFgje+E/ze/8P1ZOAJ7itLRQ5j7UErAiotc9c7DCM5G6qJLMcNY\n"
"TlCJr1Qr0aLPg7PZkGthRM4mPoRJjJpfLLB8L4Nu1BfJePNhGGnJ8TZwOYkbIEIH\n"
"y22dq53Gyd/nEn7dqomGJXPy+axifyk0E+XVwDBmsud6DqEfLVPyKzDeVT0TDMTR\n"
"xkHeavPij8Z9n60HG7j2FmK8Rk0TPGI7frvJwQIDAQABo0IwQDAOBgNVHQ8BAf8E\n"
"BAMCAYYwDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4EFgQU5K8rJnEaK0gnhS9SZizv\n"
"8IkTcT4wDQYJKoZIhvcNAQEMBQADggIBAJ+qQibbC5u2/EY9k3BqJ5nL+GM+Gnws\n"
"hk7+VwYPEgBwxOlbkZNPsW7g+oPwrHoGQtDL4sPzqUYPjmPPP6E4Dc5qQ4cE8c8V\n"
"9J9fOC1U5h9XjFy0RDLPcYlq9VPC8ETFd2bwv7rnG8t/sfhAL/aEG8s6UjL6sfpT\n"
"3J+Fey5rEulB/FJMDl6qWA1xGsNNXLedBNOoT7nv3DY5mF+gOYEt0mY8qVjlWxKf\n"
"nWmvj3I+hZj2qO9RMTFWLX1pp6XuB4wZN4gPELYi+McXGZ0LmtLsFZL/tyYvG9U8\n"
"I0MmReJm0kJR5E8N+JRFihKOMx1eBNT4K7gj0ZVmqLOR1CdtnB3S+HbCtSW+hmBz\n"
"VoJ6OQt00QP97P2dkX+8MxN4lu7/QFJyvO+Y7MceBa3HQO4jNoXDL/BVJByn0yDM\n"
"kDFwjPIB9P0jKoH/hf+ljGbp44tlZV6sE2EGsTmBt1xJpZJxdJHPTTlJopjdObl8\n"
"QCce5WBMHMxNpljnNa3eo1gg+T8xp9M0R+m69M0rtsNMGHVL0XuxGJ6fNmMOQvi4\n"
"Nlj4dozDB0j7MIi+jTY/wBIQfzAPtqia+d/81L5VJDMj4id8J37P0rPAHeXwzAP7\n"
"FBpXBFJJUFDLqRrXfBD9ySzn/X0Rhj+6Lfl4k0QBy0fPhkAlwaVkfPrxeN0Ei9EV\n"
"LwMpk0SMRRQz\n"
"-----END CERTIFICATE-----\n";

// MQTT Topics
const char* TOPIC_LIGHT = "sensors/light";
const char* TOPIC_SOUND = "sensors/sound";
const char* TOPIC_TEMPERATURE = "sensors/temperature";
const char* TOPIC_HUMIDITY = "sensors/humidity";
const char* TOPIC_MOTION = "sensors/motion";
const char* TOPIC_CLAP = "sensors/clap";
const char* TOPIC_CO2 = "sensors/co2";
const char* TOPIC_GAS = "sensors/gas";
const char* TOPIC_GAS_DETECTED = "sensors/gas_detected";
const char* TOPIC_DUST = "sensors/dust";
const char* TOPIC_ALL = "sensors/all";  // JSON with all values

// ============== PIN CONFIGURATION ==============
const int LDR_PIN = A0;           // Light sensor analog pin
const int SOUND_PIN = A1;         // Sound sensor analog pin
const int MQ2_PIN = A2;           // MQ2 gas sensor analog pin (FC-22 board)
#define ONE_WIRE_BUS 1            // Temperature sensor digital pin
#define DHT_PIN 2                 // DHT11 humidity sensor digital pin
#define DHT_TYPE DHT11            // DHT sensor type
const int MODE_BUTTON_PIN = 3;    // Push button for mode selection (wired to D3 and GND)
const int PIR_PIN = 4;            // HC-SR501 PIR motion sensor (wired to D4)
const int DUST_PIN = 5;           // Grove Dust Sensor PPD42NS (wired to D5)
const int CLAP_PIN = 6;           // Sound module digital output (clap detection)
const int CO2_PIN = A3;           // Gravity CO2 Sensor - testing analog read

// ============== LCD CONFIGURATION ==============
// I2C LCD with WWZMDiB adapter - common addresses are 0x27 or 0x3F
const uint8_t LCD_ADDRESS = 0x27;  // Found via I2C scanner
const uint8_t LCD_COLS = 16;
const uint8_t LCD_ROWS = 2;
LiquidCrystal_I2C lcd(LCD_ADDRESS, LCD_COLS, LCD_ROWS);

// LCD display page (cycles through different views)
int lcdPage = 0;
const int LCD_PAGES = 5;  // Number of different display pages
unsigned long lastLcdUpdate = 0;
const unsigned long LCD_UPDATE_INTERVAL = 2000;  // Change page every 2 seconds

// ============== MODE CONFIGURATION ==============
enum OperatingMode { MODE_MQTT, MODE_WEBSERVER, MODE_CLOUD };
OperatingMode currentMode = MODE_MQTT;  // Start in MQTT mode

// Cloud mode timing
unsigned long lastCloudPublish = 0;
const unsigned long CLOUD_PUBLISH_INTERVAL = 5000;  // Publish to cloud every 5 seconds (rate limiting)

// Button debouncing
unsigned long lastButtonPress = 0;
const unsigned long DEBOUNCE_DELAY = 300;  // 300ms debounce

// Web server (only active in web server mode)
WiFiServer server(80);

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
// Calibration based on real measurements:
//   - Classroom (918 lux) = 0.93V
//   - Flashlight (10311 lux) = 0.25V
// LDR response is logarithmic, so we use exponential formula:
//   lux = A * e^(-B * voltage)
// Solving with two data points:
//   918 = A * e^(-B * 0.93)
//   10311 = A * e^(-B * 0.25)
// Gives: B = 3.57, A = 25000
const float LIGHT_EXP_A = 25000.0;       // Exponential coefficient A
const float LIGHT_EXP_B = 3.57;          // Exponential coefficient B
const float MIN_LUX = 0.0;               // Minimum lux (dark)
const float MAX_LUX_DISPLAY = 15000.0;   // Maximum lux for display/clamping

// ============== SOUND SENSOR CALIBRATION ==============
// KY-038 outputs ~512 at silence (mid-point), spikes up/down with sound
// We measure peak-to-peak amplitude over multiple samples
const int SOUND_SAMPLES = 50;     // Number of samples to take for peak detection
const int SOUND_BASELINE = 512;   // Expected baseline (mid-point)
const int MIN_SOUND_PP = 0;       // Minimum peak-to-peak (silence)
const int MAX_SOUND_PP = 200;     // Maximum peak-to-peak (loud sound)

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

// ============== MQTT CLIENT SETUP ==============
WiFiClient wifiClient;
PubSubClient mqttClient(wifiClient);

// ============== TIMING ==============
unsigned long lastTempRead = 0;
const unsigned long TEMP_INTERVAL = 1000;  // Read temperature every 1 second
unsigned long lastMqttPublish = 0;
const unsigned long MQTT_PUBLISH_INTERVAL = 500;  // Publish to MQTT every 500ms for responsive updates
float lastTemperature = 0;
float lastHumidity = 0;

// Global sensor values for web server
float currentLightLux = 0;
float currentLightVoltage = 0;  // For debugging
int currentSoundValue = 0;
bool motionDetected = false;
bool clapDetected = false;
int currentGasValue = 0;
int currentCO2ppm = 0;        // CO2 concentration in ppm
float dustConcentration = 0;  // ug/m3

// Dust sensor timing (PPD42NS needs 30-second sampling)
unsigned long dustSampleStart = 0;
unsigned long dustLowPulseTotal = 0;
const unsigned long DUST_SAMPLE_TIME = 30000;  // 30 seconds

void setup() {
  Serial.begin(9600);
  delay(2000);  // Wait for Serial to be ready
  
  // Setup mode button with internal pull-up
  pinMode(MODE_BUTTON_PIN, INPUT_PULLUP);
  
  // Setup PIR motion sensor
  pinMode(PIR_PIN, INPUT);
  pinMode(CLAP_PIN, INPUT);
  pinMode(CO2_PIN, INPUT);  // CO2 sensor PWM input
  
  // Setup dust sensor
  pinMode(DUST_PIN, INPUT);
  dustSampleStart = millis();
  
  // Check button state at startup to select initial mode
  if (digitalRead(MODE_BUTTON_PIN) == LOW) {
    currentMode = MODE_WEBSERVER;  // Button held = start in web server mode
  } else {
    currentMode = MODE_MQTT;  // Default = MQTT mode
  }
  
  Serial.println();
  Serial.println("=== Arduino UNO R4 WiFi Starting ===");
  Serial.println("Press button to switch between MQTT and Web Server modes");
  Serial.println();
  
  // Initialize LED Matrix
  matrix.begin();
  
  // Initialize I2C LCD
  Wire.begin();
  lcd.init();
  lcd.backlight();
  lcd.clear();
  lcd.setCursor(0, 0);
  lcd.print("Arduino Sensors");
  lcd.setCursor(0, 1);
  lcd.print("Starting...");
  
  // ============== CONNECT TO WIFI ==============
  Serial.print("Connecting to WiFi: ");
  Serial.println(WIFI_SSID);
  
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  
  int attempts = 0;
  while (WiFi.status() != WL_CONNECTED && attempts < 30) {
    delay(500);
    Serial.print(".");
    attempts++;
  }
  Serial.println();
  
  if (WiFi.status() == WL_CONNECTED) {
    Serial.println("WiFi connected!");
    Serial.print("IP Address: ");
    Serial.println(WiFi.localIP());
    Serial.println();
    
    // Setup MQTT client
    mqttClient.setServer(MQTT_SERVER, MQTT_PORT);
    mqttClient.setCallback(mqttCallback);
    
    // Start web server (always ready, but only serves in web server mode)
    server.begin();
    
    Serial.println("MQTT Server: " + String(MQTT_SERVER) + ":" + String(MQTT_PORT));
    Serial.println("Web Server: http://" + WiFi.localIP().toString());
    Serial.println();
  } else {
    Serial.println("WiFi connection failed!");
    Serial.println("Continuing without network...");
  }
  
  // Print initial mode
  printCurrentMode();
  
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
  // ============== CHECK MODE BUTTON ==============
  checkModeButton();
  
  // ============== HANDLE CURRENT MODE ==============
  if (WiFi.status() == WL_CONNECTED) {
    if (currentMode == MODE_MQTT) {
      // MQTT Mode: connect and publish to broker
      if (!mqttClient.connected()) {
        reconnectMqtt();
      }
      mqttClient.loop();
    } else if (currentMode == MODE_WEBSERVER) {
      // Web Server Mode: handle incoming HTTP requests
      handleWebClient();
    }
    // Cloud mode publishing is handled separately below
  }
  
  // ============== READ LIGHT SENSOR ==============
  int lightRaw = analogRead(LDR_PIN);
  float lightVoltage = (lightRaw / 1023.0) * 5.0;
  currentLightVoltage = lightVoltage;  // Store for debugging
  // LM393: INVERTED - lower voltage = more light
  // Exponential formula: lux = A * e^(-B * voltage)
  // Calibrated: 0.93V = 918 lux, 0.25V = 10311 lux
  float lightLux = LIGHT_EXP_A * exp(-LIGHT_EXP_B * lightVoltage);
  lightLux = constrain(lightLux, MIN_LUX, MAX_LUX_DISPLAY);  // Clamp to display range
  currentLightLux = lightLux;  // Store for web server
  int lightLeds = map(lightLux, MIN_LUX, MAX_LUX_DISPLAY, 0, MATRIX_HEIGHT);
  lightLeds = constrain(lightLeds, 0, MATRIX_HEIGHT);
  
  // ============== READ SOUND SENSOR ==============
  // Sample multiple times and calculate peak-to-peak amplitude
  int soundMin = 1023;
  int soundMax = 0;
  for (int i = 0; i < SOUND_SAMPLES; i++) {
    int sample = analogRead(SOUND_PIN);
    if (sample > soundMax) soundMax = sample;
    if (sample < soundMin) soundMin = sample;
  }
  int soundPeakToPeak = soundMax - soundMin;  // Amplitude of sound wave
  currentSoundValue = soundPeakToPeak;  // Store for web server/MQTT
  int soundLeds = map(soundPeakToPeak, MIN_SOUND_PP, MAX_SOUND_PP, 0, MATRIX_HEIGHT);
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
  
  // ============== READ PIR MOTION SENSOR ==============
  motionDetected = digitalRead(PIR_PIN) == HIGH;
  
  // ============== READ CLAP SENSOR (Sound module digital out) ==============
  // KY-038: Digital output goes HIGH when sound exceeds threshold set by potentiometer
  clapDetected = digitalRead(CLAP_PIN) == HIGH;
  
  // ============== READ CO2 SENSOR (Gravity CO2 V2.0 - PWM via Analog) ==============
  // The PWM signal averages out when read as analog
  // This sensor: LOW time corresponds to CO2 (inverted from typical)
  // low_time_ms = (1 - voltage/5.0) * 1004
  // ppm = (low_time_ms - 2) * 5
  // CALIBRATION: Offset applied based on known reference (well-ventilated room = 500 ppm)
  const int CO2_CALIBRATION_OFFSET = -1178;  // Adjust this value to calibrate
  static unsigned long lastCO2Debug = 0;
  
  int co2Raw = analogRead(CO2_PIN);
  float co2Voltage = co2Raw * (5.0 / 1023.0);
  
  // Calculate PPM from LOW time (inverted duty cycle)
  float dutyCycle = co2Voltage / 5.0;  // HIGH portion
  float lowTimeMs = (1.0 - dutyCycle) * 1004.0;  // LOW portion = CO2 indicator
  
  int rawPpm = 0;
  if (lowTimeMs < 2.0) {
    rawPpm = 0;  // Preheating
  } else if (lowTimeMs > 1002.0) {
    rawPpm = 5000;  // Max range
  } else {
    rawPpm = (int)((lowTimeMs - 2.0) * 5.0);
  }
  
  // Apply calibration offset and clamp to valid range
  currentCO2ppm = rawPpm + CO2_CALIBRATION_OFFSET;
  if (currentCO2ppm < 400) currentCO2ppm = 400;  // Outdoor minimum
  if (currentCO2ppm > 5000) currentCO2ppm = 5000;  // Sensor max
  
  // Debug every 2 seconds
  if (millis() - lastCO2Debug >= 2000) {
    Serial.print("CO2 - Voltage: ");
    Serial.print(co2Voltage, 2);
    Serial.print("V | Raw: ");
    Serial.print(rawPpm);
    Serial.print(" | Calibrated: ");
    Serial.print(currentCO2ppm);
    Serial.println(" ppm");
    lastCO2Debug = millis();
  }
  
  // ============== READ MQ2 GAS SENSOR ==============
  currentGasValue = analogRead(MQ2_PIN);
  
  // ============== READ DUST SENSOR ==============
  // PPD42NS outputs LOW pulse when dust is detected
  // Accumulate low pulse time over sample period
  unsigned long lowPulseDuration = pulseIn(DUST_PIN, LOW, 10000);  // 10ms timeout
  dustLowPulseTotal += lowPulseDuration;
  
  // Calculate concentration every DUST_SAMPLE_TIME
  if (millis() - dustSampleStart >= DUST_SAMPLE_TIME) {
    // Calculate ratio of low pulse time
    float ratio = (dustLowPulseTotal / 1000.0) / (DUST_SAMPLE_TIME / 1000.0) * 100.0;  // percentage
    // Convert to concentration using sensor characteristic curve
    // Formula from Grove documentation: concentration = 1.1 * ratio^3 - 3.8 * ratio^2 + 520 * ratio + 0.62
    dustConcentration = 1.1 * pow(ratio, 3) - 3.8 * pow(ratio, 2) + 520.0 * ratio + 0.62;
    if (dustConcentration < 0) dustConcentration = 0;
    
    // Reset for next sample
    dustLowPulseTotal = 0;
    dustSampleStart = millis();
  }
  
  // ============== UPDATE FRAME BUFFER ==============
  updateColumn(LIGHT_COLUMN, lightLeds);
  updateColumn(SOUND_COLUMN, soundLeds);
  updateColumn(TEMP_COLUMN, tempLeds);
  updateColumn(HUMIDITY_COLUMN, humidityLeds);
  
  // ============== RENDER TO MATRIX ==============
  matrix.renderBitmap(frame, 8, 12);
  
  // ============== PUBLISH TO MQTT (only in MQTT mode) ==============
  if (currentMode == MODE_MQTT && mqttClient.connected()) {
    if (millis() - lastMqttPublish >= MQTT_PUBLISH_INTERVAL) {
      publishSensorData();
      lastMqttPublish = millis();
    }
  }
  
  // ============== PUBLISH TO CLOUD (only in Cloud mode) ==============
  if (currentMode == MODE_CLOUD && WiFi.status() == WL_CONNECTED) {
    if (millis() - lastCloudPublish >= CLOUD_PUBLISH_INTERVAL) {
      publishToCloud();
      lastCloudPublish = millis();
    }
  }
  
  // ============== UPDATE LCD DISPLAY ==============
  updateLcdDisplay();
  
  // ============== DEBUG OUTPUT ==============
  static unsigned long lastPrint = 0;
  if (millis() - lastPrint >= 1000) {  // Print every second
    String modeStr;
    if (currentMode == MODE_MQTT) modeStr = "[MQTT]";
    else if (currentMode == MODE_WEBSERVER) modeStr = "[WEB]";
    else modeStr = "[CLOUD]";
    Serial.print(modeStr);
    Serial.print(" IP: ");
    Serial.print(WiFi.localIP());
    Serial.print(" | Light: ");
    Serial.print(currentLightLux, 0);
    Serial.print("lux (");
    Serial.print(currentLightVoltage, 2);
    Serial.print("V)");
    Serial.print(" | Sound: ");
    Serial.print(soundPeakToPeak);
    Serial.print(" | Temp: ");
    Serial.print(lastTemperature, 1);
    Serial.print("C | Hum: ");
    Serial.print(lastHumidity, 1);
    Serial.print("% | Flam.Gas: ");
    Serial.print(currentGasValue);
    Serial.print(currentGasValue > 200 ? " DETECTED!" : " OK");
    Serial.print(" | Dust: ");
    Serial.print(dustConcentration, 0);
    Serial.print("ug/m3");
    Serial.print(" | Motion: ");
    Serial.print(motionDetected ? "DETECTED" : "none");
    Serial.print(" | Clap: ");
    Serial.print(clapDetected ? "DETECTED" : "none");
    Serial.print(" | CO2: ");
    Serial.print(currentCO2ppm);
    Serial.println("ppm");
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

// ============== MODE SWITCHING ==============

// Check if mode button was pressed
void checkModeButton() {
  static bool lastButtonState = HIGH;  // Previous button state (HIGH = not pressed due to pull-up)
  bool currentButtonState = digitalRead(MODE_BUTTON_PIN);
  
  // Detect button press (transition from HIGH to LOW)
  if (currentButtonState == LOW && lastButtonState == HIGH) {
    // Debounce check
    if (millis() - lastButtonPress > DEBOUNCE_DELAY) {
      // Cycle through modes: MQTT -> WebServer -> Cloud -> MQTT
      if (currentMode == MODE_MQTT) {
        currentMode = MODE_WEBSERVER;
      } else if (currentMode == MODE_WEBSERVER) {
        currentMode = MODE_CLOUD;
      } else {
        currentMode = MODE_MQTT;
      }
      
      showModeOnLcd();  // Instant LCD feedback - first thing after mode change
      
      Serial.println();
      Serial.println(">>> BUTTON PRESSED! <<<");
      
      // Disconnect MQTT when switching away from MQTT mode
      if (currentMode != MODE_MQTT && mqttClient.connected()) {
        mqttClient.disconnect();
      }
      
      printCurrentMode();
      lastButtonPress = millis();
    }
  }
  
  lastButtonState = currentButtonState;
}

// Print current mode to Serial
void printCurrentMode() {
  Serial.println();
  Serial.println("========================================");
  if (currentMode == MODE_MQTT) {
    Serial.println("MODE: MQTT (Home Assistant)");
    Serial.print("Publishing to: ");
    Serial.println(MQTT_SERVER);
  } else if (currentMode == MODE_WEBSERVER) {
    Serial.println("MODE: Web Server");
    Serial.print("Dashboard: http://");
    Serial.println(WiFi.localIP());
  } else {
    Serial.println("MODE: Cloud (asgaard.online)");
    Serial.print("Publishing to: ");
    Serial.println(CLOUD_API_HOST);
  }
  Serial.println("Press button to switch modes");
  Serial.println("========================================");
  Serial.println();
}

// Show current mode on LCD display
void showModeOnLcd() {
  lcd.clear();
  lcd.setCursor(0, 0);
  lcd.print("Mode Changed:");
  lcd.setCursor(0, 1);
  
  if (currentMode == MODE_MQTT) {
    lcd.print("MQTT (Local)");
  } else if (currentMode == MODE_WEBSERVER) {
    lcd.print("WebServer (Local)");
  } else {
    lcd.print("MQTT (Remote)");
  }
  
  // Reset LCD update timer so normal updates don't immediately overwrite
  lastLcdUpdate = millis();
  
  // Keep showing for a moment, then normal LCD updates resume
  delay(1500);
  
  // Reset again after delay so we get a full 2s before next page
  lastLcdUpdate = millis();
}

// ============== MQTT FUNCTIONS ==============

// Callback for incoming MQTT messages (if subscribed to any topics)
void mqttCallback(char* topic, byte* payload, unsigned int length) {
  Serial.print("MQTT Message [");
  Serial.print(topic);
  Serial.print("]: ");
  for (unsigned int i = 0; i < length; i++) {
    Serial.print((char)payload[i]);
  }
  Serial.println();
}

// Reconnect to MQTT broker (Local Home Assistant)
void reconnectMqtt() {
  static unsigned long lastAttempt = 0;
  const unsigned long RETRY_INTERVAL = 5000;  // Try every 5 seconds
  
  if (millis() - lastAttempt < RETRY_INTERVAL) {
    return;  // Don't spam connection attempts
  }
  lastAttempt = millis();
  
  Serial.print("LOCAL MQTT: Connecting to ");
  Serial.print(MQTT_SERVER);
  Serial.print("...");
  
  if (mqttClient.connect(MQTT_CLIENT_ID, MQTT_USER, MQTT_PASSWORD)) {
    Serial.println(" connected!");
    
    // Send Home Assistant MQTT Discovery messages
    publishHomeAssistantDiscovery();
    
    // Subscribe to command topics if needed
    // mqttClient.subscribe("commands/#");
    
  } else {
    Serial.print(" failed, rc=");
    Serial.print(mqttClient.state());
    Serial.println(" - retrying in 5s");
  }
}

// Publish all sensor data to MQTT
void publishSensorData() {
  char payload[16];
  
  // Publish individual sensor values
  dtostrf(currentLightLux, 1, 1, payload);
  mqttClient.publish(TOPIC_LIGHT, payload);
  
  itoa(currentSoundValue, payload, 10);
  mqttClient.publish(TOPIC_SOUND, payload);
  
  dtostrf(lastTemperature, 1, 1, payload);
  mqttClient.publish(TOPIC_TEMPERATURE, payload);
  
  dtostrf(lastHumidity, 1, 1, payload);
  mqttClient.publish(TOPIC_HUMIDITY, payload);
  
  // Publish motion status
  mqttClient.publish(TOPIC_MOTION, motionDetected ? "ON" : "OFF");

  // Publish clap detection
  mqttClient.publish(TOPIC_CLAP, clapDetected ? "ON" : "OFF");

  // Publish CO2 concentration
  itoa(currentCO2ppm, payload, 10);
  mqttClient.publish(TOPIC_CO2, payload);

  // Publish gas sensor value (raw)
  itoa(currentGasValue, payload, 10);
  mqttClient.publish(TOPIC_GAS, payload);
  
  // Publish gas detected as binary (matches hosted HA)
  bool gasDetected = currentGasValue > 200;
  mqttClient.publish(TOPIC_GAS_DETECTED, gasDetected ? "ON" : "OFF");
  
  // Publish dust concentration
  dtostrf(dustConcentration, 1, 1, payload);
  mqttClient.publish(TOPIC_DUST, payload);
  
  // Publish combined JSON
  String json = "{";
  json += "\"light\":" + String(currentLightLux, 1) + ",";
  json += "\"sound\":" + String(currentSoundValue) + ",";
  json += "\"temperature\":" + String(lastTemperature, 1) + ",";
  json += "\"humidity\":" + String(lastHumidity, 1) + ",";
  json += "\"gas\":" + String(currentGasValue) + ",";
  json += "\"dust\":" + String(dustConcentration, 1) + ",";
  json += "\"motion\":\"" + String(motionDetected ? "ON" : "OFF") + "\",";
  json += "\"clap\":\"" + String(clapDetected ? "ON" : "OFF") + "\",";
  json += "\"co2\":" + String(currentCO2ppm);
  json += "}";
  mqttClient.publish(TOPIC_ALL, json.c_str());
  
  Serial.println("MQTT: Published sensor data");
}

// Publish Home Assistant MQTT Discovery configuration
void publishHomeAssistantDiscovery() {
  Serial.println("Publishing Home Assistant discovery config...");
  
  // Common device info - matches hosted HA naming
  const char* deviceInfo = "\"device\":{\"identifiers\":[\"classroom_sensor\"],\"name\":\"Classroom Sensor\",\"model\":\"UNO R4 WiFi\",\"manufacturer\":\"Arduino\"}";
  
  // Light sensor discovery
  String lightConfig = "{";
  lightConfig += "\"name\":\"Classroom Light Level\",";
  lightConfig += "\"state_topic\":\"sensors/light\",";
  lightConfig += "\"unit_of_measurement\":\"lux\",";
  lightConfig += "\"device_class\":\"illuminance\",";
  lightConfig += "\"unique_id\":\"classroom_light_level\",";
  lightConfig += deviceInfo;
  lightConfig += "}";
  mqttClient.publish("homeassistant/sensor/classroom_light_level/config", lightConfig.c_str(), true);
  
  // Sound/Noise sensor discovery
  String soundConfig = "{";
  soundConfig += "\"name\":\"Classroom Noise Level\",";
  soundConfig += "\"state_topic\":\"sensors/sound\",";
  soundConfig += "\"unit_of_measurement\":\"level\",";
  soundConfig += "\"icon\":\"mdi:volume-high\",";
  soundConfig += "\"unique_id\":\"classroom_noise_level\",";
  soundConfig += deviceInfo;
  soundConfig += "}";
  mqttClient.publish("homeassistant/sensor/classroom_noise_level/config", soundConfig.c_str(), true);
  
  // Temperature sensor discovery
  String tempConfig = "{";
  tempConfig += "\"name\":\"Classroom Temperature\",";
  tempConfig += "\"state_topic\":\"sensors/temperature\",";
  tempConfig += "\"unit_of_measurement\":\"°C\",";
  tempConfig += "\"device_class\":\"temperature\",";
  tempConfig += "\"unique_id\":\"classroom_temperature\",";
  tempConfig += deviceInfo;
  tempConfig += "}";
  mqttClient.publish("homeassistant/sensor/classroom_temperature/config", tempConfig.c_str(), true);
  
  // Humidity sensor discovery
  String humidityConfig = "{";
  humidityConfig += "\"name\":\"Classroom Humidity\",";
  humidityConfig += "\"state_topic\":\"sensors/humidity\",";
  humidityConfig += "\"unit_of_measurement\":\"%\",";
  humidityConfig += "\"device_class\":\"humidity\",";
  humidityConfig += "\"unique_id\":\"classroom_humidity\",";
  humidityConfig += deviceInfo;
  humidityConfig += "}";
  mqttClient.publish("homeassistant/sensor/classroom_humidity/config", humidityConfig.c_str(), true);
  
  // CO2 sensor discovery
  String co2Config = "{";
  co2Config += "\"name\":\"Classroom CO2\",";
  co2Config += "\"state_topic\":\"sensors/co2\",";
  co2Config += "\"unit_of_measurement\":\"ppm\",";
  co2Config += "\"device_class\":\"carbon_dioxide\",";
  co2Config += "\"unique_id\":\"classroom_co2\",";
  co2Config += deviceInfo;
  co2Config += "}";
  mqttClient.publish("homeassistant/sensor/classroom_co2/config", co2Config.c_str(), true);
  
  // Dust sensor discovery
  String dustConfig = "{";
  dustConfig += "\"name\":\"Classroom Dust Level\",";
  dustConfig += "\"state_topic\":\"sensors/dust\",";
  dustConfig += "\"unit_of_measurement\":\"ug/m3\",";
  dustConfig += "\"icon\":\"mdi:blur\",";
  dustConfig += "\"unique_id\":\"classroom_dust_level\",";
  dustConfig += deviceInfo;
  dustConfig += "}";
  mqttClient.publish("homeassistant/sensor/classroom_dust_level/config", dustConfig.c_str(), true);
  
  // Motion sensor discovery (binary sensor)
  String motionConfig = "{";
  motionConfig += "\"name\":\"Classroom Motion\",";
  motionConfig += "\"state_topic\":\"sensors/motion\",";
  motionConfig += "\"device_class\":\"motion\",";
  motionConfig += "\"payload_on\":\"ON\",";
  motionConfig += "\"payload_off\":\"OFF\",";
  motionConfig += "\"unique_id\":\"classroom_motion\",";
  motionConfig += deviceInfo;
  motionConfig += "}";
  mqttClient.publish("homeassistant/binary_sensor/classroom_motion/config", motionConfig.c_str(), true);
  
  // Clap sensor discovery (binary sensor)
  String clapConfig = "{";
  clapConfig += "\"name\":\"Classroom Clap\",";
  clapConfig += "\"state_topic\":\"sensors/clap\",";
  clapConfig += "\"device_class\":\"sound\",";
  clapConfig += "\"payload_on\":\"ON\",";
  clapConfig += "\"payload_off\":\"OFF\",";
  clapConfig += "\"unique_id\":\"classroom_clap\",";
  clapConfig += deviceInfo;
  clapConfig += "}";
  mqttClient.publish("homeassistant/binary_sensor/classroom_clap/config", clapConfig.c_str(), true);
  
  // Gas sensor discovery (binary sensor for detected state)
  String gasConfig = "{";
  gasConfig += "\"name\":\"Classroom Gas Detected\",";
  gasConfig += "\"state_topic\":\"sensors/gas_detected\",";
  gasConfig += "\"device_class\":\"gas\",";
  gasConfig += "\"payload_on\":\"ON\",";
  gasConfig += "\"payload_off\":\"OFF\",";
  gasConfig += "\"icon\":\"mdi:fire-alert\",";
  gasConfig += "\"unique_id\":\"classroom_gas_detected\",";
  gasConfig += deviceInfo;
  gasConfig += "}";
  mqttClient.publish("homeassistant/binary_sensor/classroom_gas_detected/config", gasConfig.c_str(), true);
  
  Serial.println("Home Assistant discovery config published!");
}

// ============== CLOUD API FUNCTIONS ==============

// Publish sensor data to cloud MQTT bridge via HTTPS
void publishToCloud() {
  Serial.println("===========================================");
  Serial.println("CLOUD: Starting HTTPS publish to asgaard.online");
  Serial.println("===========================================");
  
  // Step 1: Check WiFi status
  Serial.print("CLOUD [1/5] WiFi Status: ");
  if (WiFi.status() == WL_CONNECTED) {
    Serial.print("Connected, IP: ");
    Serial.println(WiFi.localIP());
  } else {
    Serial.print("DISCONNECTED! Status code: ");
    Serial.println(WiFi.status());
    return;
  }
  
  // Step 2: DNS resolution test
  Serial.print("CLOUD [2/5] DNS Lookup: ");
  Serial.print(CLOUD_API_HOST);
  Serial.print(" -> ");
  IPAddress resolvedIP;
  int dnsResult = WiFi.hostByName(CLOUD_API_HOST, resolvedIP);
  if (dnsResult == 1) {
    Serial.println(resolvedIP);
  } else {
    Serial.print("FAILED! Error code: ");
    Serial.println(dnsResult);
    return;
  }
  
  // Step 3: Test raw TCP connection first (without SSL)
  Serial.print("CLOUD [3/5] Raw TCP test to ");
  Serial.print(resolvedIP);
  Serial.print(":443 -> ");
  WiFiClient rawClient;
  rawClient.setTimeout(5000);
  unsigned long tcpStart = millis();
  if (rawClient.connect(resolvedIP, 443)) {
    Serial.print("OK (");
    Serial.print(millis() - tcpStart);
    Serial.println("ms)");
    rawClient.stop();
  } else {
    Serial.print("FAILED after ");
    Serial.print(millis() - tcpStart);
    Serial.println("ms - TCP blocked?");
    return;
  }
  
  // Step 4: SSL/TLS connection using default CA bundle (works with TLS 1.2)
  if (CLOUD_DEBUG) Serial.println("CLOUD: SSL Handshake...");
  WiFiSSLClient sslClient;
  sslClient.setConnectionTimeout(15000);
  
  unsigned long startConnect = millis();
  int connectResult = sslClient.connect(CLOUD_API_HOST, CLOUD_API_PORT);
  unsigned long connectTime = millis() - startConnect;
  
  if (!connectResult) {
    Serial.print("CLOUD: SSL failed after ");
    Serial.print(connectTime);
    Serial.println("ms");
    return;
  }
  
  if (CLOUD_DEBUG) {
    Serial.print("CLOUD: SSL connected in ");
    Serial.print(connectTime);
    Serial.println("ms");
  }
  
  // Build the sensor data JSON
  int noiseLevel = map(currentSoundValue, 0, 200, 1, 10);
  noiseLevel = constrain(noiseLevel, 1, 10);
  float dustMgM3 = dustConcentration / 1000.0;
  bool gasDetected = currentGasValue > 200;
  
  // Build inner message JSON (sensor data)
  String sensorJson = "{";
  sensorJson += "\"temperature\":{\"value\":" + String(lastTemperature, 1) + ",\"unit\":\"C\"},";
  sensorJson += "\"humidity\":{\"value\":" + String(lastHumidity, 0) + ",\"unit\":\"%\"},";
  sensorJson += "\"light\":{\"value\":" + String((int)currentLightLux) + ",\"unit\":\"lux\"},";
  sensorJson += "\"noise\":{\"value\":" + String(noiseLevel) + ",\"unit\":\"level\"},";
  sensorJson += "\"dust\":{\"value\":" + String(dustMgM3, 2) + ",\"unit\":\"mg/m3\"},";
  sensorJson += "\"gas\":{\"detected\":" + String(gasDetected ? "true" : "false") + "},";
  sensorJson += "\"motion\":{\"detected\":" + String(motionDetected ? "true" : "false") + "},";
  sensorJson += "\"clap\":{\"detected\":" + String(clapDetected ? "true" : "false") + "},";
  sensorJson += "\"co2\":{\"value\":" + String(currentCO2ppm) + ",\"unit\":\"ppm\"},";
  sensorJson += "\"device_id\":\"" + String(CLOUD_DEVICE_ID) + "\"";
  sensorJson += "}";
  
  // Build outer payload JSON
  String payload = "{";
  payload += "\"topic\":\"" + String(CLOUD_TOPIC) + "\",";
  payload += "\"message\":" + sensorJson;
  payload += "}";
  
  // Send HTTP POST request with Cloudflare Access headers
  if (CLOUD_DEBUG) Serial.println("CLOUD: Sending request...");
  
  sslClient.print("POST ");
  sslClient.print(CLOUD_API_PATH);
  sslClient.println(" HTTP/1.1");
  sslClient.print("Host: ");
  sslClient.println(CLOUD_API_HOST);
  sslClient.print("CF-Access-Client-Id: ");
  sslClient.println(CF_CLIENT_ID);
  sslClient.print("CF-Access-Client-Secret: ");
  sslClient.println(CF_CLIENT_SECRET);
  sslClient.println("Content-Type: application/json");
  sslClient.print("Content-Length: ");
  sslClient.println(payload.length());
  sslClient.println("Connection: close");
  sslClient.println();
  sslClient.print(payload);
  sslClient.flush();
  
  // Wait for response with timeout
  unsigned long responseStart = millis();
  while (!sslClient.available() && sslClient.connected() && millis() - responseStart < 10000) {
    delay(50);
  }
  
  if (sslClient.available()) {
    String statusLine = sslClient.readStringUntil('\n');
    if (CLOUD_DEBUG) {
      Serial.print("CLOUD: ");
      Serial.println(statusLine);
      while (sslClient.available()) {
        String line = sslClient.readStringUntil('\n');
        Serial.print("  ");
        Serial.println(line);
        if (line.length() < 2) break;
      }
    }
    if (statusLine.indexOf("200") > 0 || statusLine.indexOf("201") > 0) {
      Serial.println("CLOUD: OK");
    } else {
      Serial.print("CLOUD: Error - ");
      Serial.println(statusLine);
    }
  } else if (!sslClient.connected()) {
    Serial.println("CLOUD: Connection closed");
  } else {
    Serial.println("CLOUD: Timeout");
  }
  
  sslClient.stop();
}

// ============== WEB SERVER FUNCTIONS ==============

// Handle incoming web requests
void handleWebClient() {
  WiFiClient client = server.available();
  
  if (client) {
    String currentLine = "";
    String requestPath = "";
    unsigned long timeout = millis() + 3000;  // 3 second timeout
    
    while (client.connected() && millis() < timeout) {
      if (client.available()) {
        char c = client.read();
        
        if (c == '\n') {
          if (currentLine.length() == 0) {
            // Empty line = end of headers, send response
            if (requestPath.indexOf("/api/sensors") >= 0) {
              sendJsonResponse(client);
            } else {
              sendHtmlPage(client);
            }
            break;
          } else {
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
    
    delay(1);
    client.stop();
  }
}

// Send JSON response with sensor data
void sendJsonResponse(WiFiClient& client) {
  String json = "{";
  json += "\"light_lux\":" + String(currentLightLux, 1) + ",";
  json += "\"sound_raw\":" + String(currentSoundValue) + ",";
  json += "\"temperature_c\":" + String(lastTemperature, 1) + ",";
  json += "\"humidity\":" + String(lastHumidity, 1) + ",";
  json += "\"gas\":" + String(currentGasValue) + ",";
  json += "\"dust\":" + String(dustConcentration, 1) + ",";
  json += "\"co2\":" + String(currentCO2ppm) + ",";
  json += "\"motion\":" + String(motionDetected ? "true" : "false") + ",";
  json += "\"mode\":\"webserver\"";
  json += "}";
  
  client.println("HTTP/1.1 200 OK");
  client.println("Content-Type: application/json");
  client.println("Access-Control-Allow-Origin: *");
  client.println("Connection: close");
  client.println();
  client.println(json);
}

// Send HTML dashboard page
void sendHtmlPage(WiFiClient& client) {
  client.println("HTTP/1.1 200 OK");
  client.println("Content-Type: text/html; charset=UTF-8");
  client.println("Connection: close");
  client.println();
  
  client.println("<!DOCTYPE html><html><head>");
  client.println("<title>Arduino Sensor Dashboard</title>");
  client.println("<meta charset='UTF-8'>");
  client.println("<meta name='viewport' content='width=device-width, initial-scale=1'>");
  client.println("<link rel='stylesheet' href='https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.1/css/all.min.css'>");
  client.println("<style>");
  client.println("*{box-sizing:border-box;margin:0;padding:0}");
  client.println("body{font-family:system-ui,-apple-system,sans-serif;background:#0f172a;color:#e2e8f0;min-height:100vh;padding:20px}");
  client.println("h1{text-align:center;color:#38bdf8;margin-bottom:20px;font-size:1.5em}");
  client.println(".grid{display:grid;grid-template-columns:repeat(2,1fr);gap:15px;max-width:500px;margin:0 auto}");
  client.println(".card{background:#1e293b;border-radius:12px;padding:20px;text-align:center}");
  client.println(".card.wide{grid-column:span 2}");
  client.println(".icon{font-size:2em;margin-bottom:8px}");
  client.println(".value{font-size:2em;font-weight:bold;margin:8px 0}");
  client.println(".label{color:#94a3b8;font-size:0.9em}");
  client.println(".light .icon,.light .value{color:#fbbf24}");
  client.println(".sound .icon,.sound .value{color:#f87171}");
  client.println(".temp .icon,.temp .value{color:#34d399}");
  client.println(".humidity .icon,.humidity .value{color:#a78bfa}");
  client.println(".gas .icon,.gas .value{color:#64748b}");
  client.println(".gas.warning .icon,.gas.warning .value{color:#f59e0b}");
  client.println(".gas.danger .icon,.gas.danger .value{color:#ef4444}");
  client.println(".dust .icon,.dust .value{color:#06b6d4}");
  client.println(".dust.warning .icon,.dust.warning .value{color:#f59e0b}");
  client.println(".dust.danger .icon,.dust.danger .value{color:#ef4444}");
  client.println(".motion .icon,.motion .value{color:#fb923c}");
  client.println(".motion.detected .icon,.motion.detected .value{color:#22c55e}");
  client.println(".co2 .icon,.co2 .value{color:#10b981}");
  client.println(".co2.warning .icon,.co2.warning .value{color:#f59e0b}");
  client.println(".co2.danger .icon,.co2.danger .value{color:#ef4444}");
  client.println(".status{text-align:center;margin-top:20px;color:#64748b;font-size:0.8em}");
  client.println("</style></head><body>");
  
  client.println("<h1><i class='fa-solid fa-microchip'></i> Arduino Sensors</h1>");
  client.println("<div class='grid'>");
  
  client.println("<div class='card light'><div class='icon'><i class='fa-solid fa-sun'></i></div>");
  client.println("<div class='value' id='light'>--</div>");
  client.println("<div class='label'>Light (lux)</div></div>");
  
  client.println("<div class='card sound'><div class='icon'><i class='fa-solid fa-volume-high'></i></div>");
  client.println("<div class='value' id='sound'>--</div>");
  client.println("<div class='label'>Sound</div></div>");
  
  client.println("<div class='card temp'><div class='icon'><i class='fa-solid fa-temperature-half'></i></div>");
  client.println("<div class='value' id='temp'>--</div>");
  client.println("<div class='label'>Temperature</div></div>");
  
  client.println("<div class='card humidity'><div class='icon'><i class='fa-solid fa-droplet'></i></div>");
  client.println("<div class='value' id='humidity'>--</div>");
  client.println("<div class='label'>Humidity</div></div>");
  
  client.println("<div class='card gas' id='gas-card'><div class='icon'><i class='fa-solid fa-fire'></i></div>");
  client.println("<div class='value' id='gas'>--</div>");
  client.println("<div class='label'>Flammable Gas/Smoke</div></div>");
  
  client.println("<div class='card dust' id='dust-card'><div class='icon'><i class='fa-solid fa-smog'></i></div>");
  client.println("<div class='value' id='dust'>--</div>");
  client.println("<div class='label'>Air Quality (Dust)</div></div>");
  
  client.println("<div class='card co2' id='co2-card'><div class='icon'><i class='fa-solid fa-cloud'></i></div>");
  client.println("<div class='value' id='co2'>--</div>");
  client.println("<div class='label'>CO2 Level</div></div>");
  
  client.println("<div class='card motion' id='motion-card'><div class='icon'><i class='fa-solid fa-person-walking' id='motion-icon'></i></div>");
  client.println("<div class='value' id='motion'>--</div>");
  client.println("<div class='label'>Motion</div></div>");
  
  client.println("</div>");
  client.println("<div class='status' id='status'><i class='fa-solid fa-circle-notch fa-spin'></i> Connecting...</div>");
  
  client.println("<script>");
  client.println("async function update(){try{");
  client.println("const r=await fetch('/api/sensors');const d=await r.json();");
  client.println("document.getElementById('light').textContent=d.light_lux.toFixed(0);");
  client.println("document.getElementById('sound').textContent=d.sound_raw;");
  client.println("document.getElementById('temp').textContent=d.temperature_c.toFixed(1)+'\\u00B0C';");
  client.println("document.getElementById('humidity').textContent=d.humidity.toFixed(0)+'%';");
client.println("document.getElementById('gas').textContent=d.gas>200?'DETECTED ('+d.gas+')':'Not Detected';");
  client.println("document.getElementById('gas-card').className=d.gas>400?'card gas danger':d.gas>200?'card gas warning':'card gas';");
  client.println("document.getElementById('dust').textContent=d.dust.toFixed(0)+' ug/m3';");
  client.println("document.getElementById('dust-card').className=d.dust>150?'card dust danger':d.dust>75?'card dust warning':'card dust';");
  client.println("document.getElementById('co2').textContent=d.co2+' ppm';");
  client.println("document.getElementById('co2-card').className=d.co2>1500?'card co2 danger':d.co2>1000?'card co2 warning':'card co2';");
  client.println("document.getElementById('motion').textContent=d.motion?'DETECTED':'No Movement';");
  client.println("document.getElementById('motion-card').className=d.motion?'card motion wide detected':'card motion wide';");
  client.println("document.getElementById('motion-icon').className=d.motion?'fa-solid fa-person-running':'fa-solid fa-person-walking';");
  client.println("document.getElementById('status').innerHTML='<i class=\"fa-solid fa-circle-check\"></i> Live \\u2022 '+new Date().toLocaleTimeString();");
  client.println("}catch(e){document.getElementById('status').innerHTML='<i class=\"fa-solid fa-circle-xmark\"></i> Reconnecting...';}}");
  client.println("update();setInterval(update,500);");
  client.println("</script></body></html>");
}

// ============== LCD DISPLAY FUNCTIONS ==============

// Update the LCD display - cycles through pages
void updateLcdDisplay() {
  if (millis() - lastLcdUpdate < LCD_UPDATE_INTERVAL) {
    return;  // Not time to update yet
  }
  lastLcdUpdate = millis();
  
  lcd.clear();
  
  switch (lcdPage) {
    case 0:  // Temperature & Humidity
      lcd.setCursor(0, 0);
      lcd.print("Temp: ");
      lcd.print(lastTemperature, 1);
      lcd.print((char)223);  // Degree symbol
      lcd.print("C");
      
      lcd.setCursor(0, 1);
      lcd.print("Humidity: ");
      lcd.print(lastHumidity, 0);
      lcd.print("%");
      break;
      
    case 1:  // Light & Sound
      lcd.setCursor(0, 0);
      lcd.print("Light: ");
      lcd.print((int)currentLightLux);
      lcd.print(" lux");
      
      lcd.setCursor(0, 1);
      lcd.print("Sound: ");
      lcd.print(currentSoundValue);
      lcd.print(" raw");
      break;
      
    case 2:  // Gas & Motion (alert page)
      lcd.setCursor(0, 0);
      lcd.print("Gas:");
      if (currentGasValue > 200) {
        lcd.print(" DETECTED!");
      } else {
        lcd.print(" OK (");
        lcd.print(currentGasValue);
        lcd.print(")");
      }
      
      lcd.setCursor(0, 1);
      lcd.print("Motion: ");
      lcd.print(motionDetected ? "DETECTED!" : "None");
      break;
      
    case 3:  // CO2 Level
      lcd.setCursor(0, 0);
      lcd.print("CO2 Level:");
      lcd.setCursor(0, 1);
      lcd.print(currentCO2ppm);
      lcd.print(" ppm ");
      if (currentCO2ppm > 1500) {
        lcd.print("BAD!");
      } else if (currentCO2ppm > 1000) {
        lcd.print("VENT");
      } else {
        lcd.print("GOOD");
      }
      break;
      
    case 4:  // Dust/Air Quality
      lcd.setCursor(0, 0);
      lcd.print("Air Quality:");
      lcd.setCursor(0, 1);
      lcd.print(dustConcentration, 0);
      lcd.print(" ug/m3 ");
      if (dustConcentration > 150) {
        lcd.print("BAD!");
      } else if (dustConcentration > 75) {
        lcd.print("MOD");
      } else {
        lcd.print("GOOD");
      }
      break;
  }
  
  // Move to next page
  lcdPage = (lcdPage + 1) % LCD_PAGES;
}
