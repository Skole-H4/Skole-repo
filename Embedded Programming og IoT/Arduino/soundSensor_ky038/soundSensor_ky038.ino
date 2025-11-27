// Arduino UNO R4 WiFi with KY-038 sound sensor and LED Matrix
#include "Arduino_LED_Matrix.h"

ArduinoLEDMatrix matrix;

const int SOUND_ANALOG_PIN = A1;  // Analog output from KY-038
const int MATRIX_HEIGHT = 8;      // LED Matrix has 8 rows
const int MATRIX_WIDTH = 12;      // LED Matrix has 12 columns
const int DISPLAY_COLUMN = 2;     // Column 2 for noise visualization

// Calibration values - adjust based on your environment
const int MIN_SOUND = 79;         // Minimum expected analog reading
const int MAX_SOUND = 90;         // Maximum analog reading

// LED Matrix frame buffer (8 rows x 12 columns)
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

void setup() {
  Serial.begin(9600);
  matrix.begin();
  
  Serial.println("Sound Sensor LED Matrix Visualizer");
  Serial.println("Column 2 shows noise level");
}

void loop() {
  // Read analog sound sensor value directly (no smoothing for max sensitivity)
  int soundValue = analogRead(SOUND_ANALOG_PIN);
  
  // Map sound level to number of LEDs (0-8)
  int ledCount = map(soundValue, MIN_SOUND, MAX_SOUND, 0, MATRIX_HEIGHT);
  ledCount = constrain(ledCount, 0, MATRIX_HEIGHT);
  
  // Clear column 2
  for (int row = 0; row < MATRIX_HEIGHT; row++) {
    frame[row][DISPLAY_COLUMN] = 0;
  }
  
  // Light up LEDs in column 2 from bottom to top based on noise level
  for (int i = 0; i < ledCount; i++) {
    int row = MATRIX_HEIGHT - 1 - i;  // Start from bottom (row 7) going up
    frame[row][DISPLAY_COLUMN] = 1;
  }
  
  // Display on matrix
  matrix.renderBitmap(frame, 8, 12);
  
  // Debug output
  Serial.print("Sound: ");
  Serial.print(soundValue);
  Serial.print(" | LEDs lit: ");
  Serial.println(ledCount);
  
  delay(10); // Minimal delay for maximum responsiveness
}