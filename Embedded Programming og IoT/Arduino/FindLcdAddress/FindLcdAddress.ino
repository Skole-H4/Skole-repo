// I2C Address Scanner for LCD Display
// Upload this sketch to find the I2C address of your LCD
// Open Serial Monitor at 9600 baud to see results
// Compatible with Arduino UNO R4 WiFi (Renesas)

#include <Wire.h>

void setup() {
  Serial.begin(9600);
  delay(2000);  // Give R4 time to initialize
  while (!Serial);  // Wait for Serial Monitor
  
  // Initialize I2C - R4 uses Wire library same as classic
  Wire.begin();
  delay(100);  // Let I2C stabilize
  
  Serial.println("=================================");
  Serial.println("I2C Address Scanner for LCD");
  Serial.println("Arduino UNO R4 WiFi Compatible");
  Serial.println("=================================");
  Serial.println();
  Serial.println("I2C Pins: SDA=A4, SCL=A5");
  Serial.println("Scanning all addresses 0x01-0x7F...");
  Serial.println();
}

void loop() {
  byte error, address;
  int devicesFound = 0;
  
  for (address = 1; address < 127; address++) {
    Wire.beginTransmission(address);
    error = Wire.endTransmission();
    
    if (error == 0) {
      Serial.print("Device found at address 0x");
      if (address < 16) Serial.print("0");
      Serial.print(address, HEX);
      
      // Common LCD addresses
      if (address == 0x27) {
        Serial.print("  <-- Common LCD address (PCF8574)");
      } else if (address == 0x3F) {
        Serial.print("  <-- Common LCD address (PCF8574A)");
      } else if (address == 0x20) {
        Serial.print("  <-- Possible LCD (PCF8574)");
      }
      
      Serial.println();
      devicesFound++;
    }
  }
  
  Serial.println();
  if (devicesFound == 0) {
    Serial.println("No I2C devices found!");
    Serial.println("Check wiring: SDA->A4, SCL->A5");
  } else {
    Serial.print("Found ");
    Serial.print(devicesFound);
    Serial.println(" device(s)");
    Serial.println();
    Serial.println("Use the address shown above in your code:");
    Serial.println("  LiquidCrystal_I2C lcd(0xXX, 16, 2);");
  }
  
  Serial.println();
  Serial.println("Scan complete. Waiting 5 seconds...");
  Serial.println("=================================");
  delay(5000);
}
