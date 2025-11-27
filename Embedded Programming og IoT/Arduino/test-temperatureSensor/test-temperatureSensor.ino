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
  
  // Start up the library
  sensors.begin();
  
  Serial.println("DS18B20 Temperature Sensor Test");
}

void loop() {
  // Request temperature reading
  sensors.requestTemperatures();
  
  // Read temperature in Celsius
  float temperatureC = sensors.getTempCByIndex(0);
  
  // Print the temperature
  Serial.print("Temperature: ");
  Serial.print(temperatureC);
  Serial.println(" °C");
  
  // Also print in Fahrenheit (optional)
  float temperatureF = sensors.toFahrenheit(temperatureC);
  Serial.print("Temperature: ");
  Serial.print(temperatureF);
  Serial.println(" °F");
  
  // Wait a bit before next reading
  delay(1000);
}
