#include <OneWire.h>
#include <DallasTemperature.h>

// Data wire is connected to digital pin 1
#define ONE_WIRE_BUS 1

// Setup a oneWire instance to communicate with any OneWire devices
OneWire oneWire(ONE_WIRE_BUS);

// Pass our oneWire reference to Dallas Temperature sensor
DallasTemperature sensors(&oneWire);

void setup() {
  // Start serial communication
  Serial.begin(9600);
  
  // Wait for serial port to connect (important for some boards)
  delay(2000);
  
  Serial.println("=== DS18B20 Temperature Sensor Test ===");
  
  // Start up the library
  sensors.begin();
  
  // Check if sensor is connected
  int deviceCount = sensors.getDeviceCount();
  Serial.print("Found ");
  Serial.print(deviceCount);
  Serial.println(" sensor(s)");
  
  if (deviceCount == 0) {
    Serial.println("ERROR: No DS18B20 sensors found!");
    Serial.println("Check wiring:");
    Serial.println("- VCC to 5V or 3.3V");
    Serial.println("- GND to GND");
    Serial.println("- Data to pin 1");
    Serial.println("- 4.7k resistor between VCC and Data");
  }
  
  Serial.println("Starting readings...\n");
}

void loop() {
  Serial.println("--- Reading ---");
  
  // Request temperature reading
  sensors.requestTemperatures();
  
  // Read temperature in Celsius
  float temperatureC = sensors.getTempCByIndex(0);
  
  // Check if reading is valid
  if (temperatureC == DEVICE_DISCONNECTED_C) {
    Serial.println("ERROR: Sensor disconnected or not responding!");
    Serial.println("Check connections and pull-up resistor (4.7k)");
  } else {
    // Print the temperature
    Serial.print("Temperature: ");
    Serial.print(temperatureC);
    Serial.println(" °C");
    
    // Also print in Fahrenheit
    float temperatureF = sensors.toFahrenheit(temperatureC);
    Serial.print("Temperature: ");
    Serial.print(temperatureF);
    Serial.println(" °F");
  }
  
  Serial.println();
  
  // Wait a bit before next reading
  delay(2000);
}
