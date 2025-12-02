// I2C Scanner - Find the address of your LCD
// Upload this, open Serial Monitor at 9600 baud

#include <Wire.h>

void setup() {
  Wire.begin();
  Serial.begin(9600);
  delay(2000);
  Serial.println("\nI2C Scanner - Scanning...\n");
}

void loop() {
  byte error, address;
  int devicesFound = 0;

  for (address = 1; address < 127; address++) {
    Wire.beginTransmission(address);
    error = Wire.endTransmission();

    if (error == 0) {
      Serial.print("I2C device found at address 0x");
      if (address < 16) Serial.print("0");
      Serial.print(address, HEX);
      Serial.println(" !");
      devicesFound++;
    }
  }

  if (devicesFound == 0) {
    Serial.println("No I2C devices found!\n");
    Serial.println("Check wiring:");
    Serial.println("  SDA -> A4");
    Serial.println("  SCL -> A5");
    Serial.println("  VCC -> 5V");
    Serial.println("  GND -> GND");
  } else {
    Serial.print("\nFound ");
    Serial.print(devicesFound);
    Serial.println(" device(s)\n");
    Serial.println("Use this address in LCD_ADDRESS in combined_sensors.ino");
  }

  delay(5000);  // Wait 5 seconds before scanning again
}
