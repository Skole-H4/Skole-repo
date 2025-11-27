#include "Arduino_LED_Matrix.h"

// Voltage range
const float LOWER_VOLTAGE = 3.5;  // Lower threshold (3.5V)
const float UPPER_VOLTAGE = 4.8;  // Upper threshold (4.8V)

// LED Matrix configuration (12x8 matrix on R4 WiFi)
const int NUM_ROWS = 8;
const int NUM_COLS = 12;
const int DISPLAY_COLUMN = 0;  // Which column to use for voltage display (0-11)

// Analog input pin
const int VOLTAGE_PIN = A0;

// Create LED matrix object
ArduinoLEDMatrix matrix;

void setup() {
  Serial.begin(9600);
  
  // Initialize the LED matrix
  matrix.begin();
}

void loop() {
  // Read the voltage from analog pin
  int raw = analogRead(VOLTAGE_PIN);
  float voltage = (raw / 1023.0) * 5.0;  // Convert ADC to voltage (10-bit ADC: 0-1023)
  
  // Calculate how many LEDs to light up based on voltage
  int ledsToLight = 0;
  
  if (voltage >= UPPER_VOLTAGE) {
    // At or above upper threshold: light all LEDs
    ledsToLight = NUM_ROWS;
  } else if (voltage <= LOWER_VOLTAGE) {
    // At or below lower threshold: no LEDs
    ledsToLight = 0;
  } else {
    // Between thresholds: map voltage to number of LEDs
    float voltageRange = UPPER_VOLTAGE - LOWER_VOLTAGE;
    float normalizedVoltage = (voltage - LOWER_VOLTAGE) / voltageRange;
    ledsToLight = (int)(normalizedVoltage * NUM_ROWS);
  }
  
  // Create frame buffer for the LED matrix (12x8 = 96 bits = 3 uint32_t)
  uint32_t frame[3] = {0, 0, 0};
  
  // Light up the appropriate LEDs in the display column
  for (int row = 0; row < NUM_ROWS; row++) {
    if (row < ledsToLight) {
      // Calculate bit position for this LED
      int bitPosition = row * NUM_COLS + DISPLAY_COLUMN;
      int wordIndex = bitPosition / 32;
      int bitIndex = bitPosition % 32;
      
      // Set the bit to turn on the LED
      frame[wordIndex] |= (1UL << (31 - bitIndex));
    }
  }
  
  // Display the frame on the matrix
  matrix.loadFrame(frame);
  
  // Debug output to Serial Monitor
  static unsigned long lastPrint = 0;
  if (millis() - lastPrint > 200) {
    Serial.print("Raw ADC: ");
    Serial.print(raw);
    Serial.print(" | Voltage: ");
    Serial.print(voltage, 3);
    Serial.print(" V | LEDs lit: ");
    Serial.print(ledsToLight);
    Serial.print("/");
    Serial.println(NUM_ROWS);
    lastPrint = millis();
  }
  
  delay(100);  // Update rate
}
