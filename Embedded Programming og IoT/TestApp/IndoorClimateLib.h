#pragma once
#include <iostream>
#include <cstdlib>
#include <windows.h>
#include <string>
using namespace std;

namespace IndoorClimateLibNS {

class IndoorClimateLib
{
public:
    static void InitConsoleUtf8(); // Initialize console for UTF-8 output
    void StartRandomLoop();
    void UpdateSensorData(int humidity, int temperature, int co2, int light, int noise);
    void PrintTemperature() const;
    void PrintHumidity() const;
    void PrintCO2() const;
    void PrintLight() const;
    void PrintNoise() const;
    void PrintAllSensorData() const;
    void PrintIndoorClimateIndex() const;
    void SimulateLiveDataInterfaceLoop();
    void PrintRightFrame(int line); // Added declaration

private:
    int humidity = 0; // Humidistat in percentage
    int temperature = 0; // Temperature in Celsius
    int co2 = 0; // CO2 level in ppm
    int light = 0; // Light level in lux
    int noise = 0; // Noise level in dB
    int indoorClimateIndex = 0; // Calculated Indoor Climate Index
    string title    = "Indoor Climate Monitoring System";
    string subtitle = "================================";
    void CalculateIndoorClimateIndex();
    void GenerateFakeData();
    void ResetCursor(short x, short y);
    int EvaluateIndoorClimate(int climateIndex);
    string IndoorClimateRating(int score);

};

} // namespace IndoorClimateLibNS