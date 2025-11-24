#include "IndoorClimateLib.h"
#include <windows.h>
#include <chrono>
#include <thread>

namespace IndoorClimateLibNS {

void IndoorClimateLib::InitConsoleUtf8()
{
    // Switch console input/output code pages to UTF-8
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleCP(CP_UTF8);
}

static void ShowConsoleCursor(bool showFlag)
{
    HANDLE out = GetStdHandle(STD_OUTPUT_HANDLE);

    CONSOLE_CURSOR_INFO     cursorInfo;

    GetConsoleCursorInfo(out, &cursorInfo);
    cursorInfo.bVisible = showFlag; // set the cursor visibility
    SetConsoleCursorInfo(out, &cursorInfo);
}

void IndoorClimateLib::CalculateIndoorClimateIndex()
{
    indoorClimateIndex = (humidity + temperature + co2 + light + noise) / 5;
}

void IndoorClimateLib::GenerateFakeData()
{
    humidity = rand() % 101; // 0-100%
    temperature = rand() % 41; // 0-40°C
    co2 = rand() % 2001; // 0-2000 ppm
    light = rand() % 10001; // 0-10000 lux
    noise = rand() % 101; // 0-100 dB
}

void IndoorClimateLib::ResetCursor(short x, short y)
{
    HANDLE hStdout = GetStdHandle(STD_OUTPUT_HANDLE);
    COORD position = { x, y };
    SetConsoleCursorPosition(hStdout, position);
}


void IndoorClimateLib::SimulateLiveDataInterfaceLoop() {
    ShowConsoleCursor(false);
    while (true)
    {
        GenerateFakeData();
        UpdateSensorData(humidity, temperature, co2, light, noise);
		std::cout << title << "\n";
		std::cout << subtitle << "\n";
        PrintTemperature();
		PrintRightFrame(2);
		PrintHumidity();
		PrintRightFrame(3);
		PrintCO2();
		PrintRightFrame(4);
		PrintLight();
		PrintRightFrame(5);
		PrintNoise();
		PrintRightFrame(6);
        PrintIndoorClimateIndex();
		PrintRightFrame(7);
        std::cout << subtitle << "\n";
		int rating = EvaluateIndoorClimate(indoorClimateIndex);
		std::cout << "Indoor Climate Rating: " << IndoorClimateRating(rating) << "\n";
        std::this_thread::sleep_for(std::chrono::seconds(2));
        printf("\033c");
        ResetCursor(0, 0);
    }
}

int IndoorClimateLib::EvaluateIndoorClimate(int climateIndex) {
    if (climateIndex >= 0 && climateIndex <= 20)
        return 1; // Very Poor
    else if (climateIndex >= 21 && climateIndex <= 40)
        return 2; // Poor
    else if (climateIndex >= 41 && climateIndex <= 60)
        return 3; // Moderate
    else if (climateIndex >= 61 && climateIndex <= 80)
        return 4; // Good
    else if (climateIndex >= 81 && climateIndex <= 100)
        return 5; // Excellent
    else
        return 0; // Out of range
}

string IndoorClimateLib::IndoorClimateRating(int score) {
    switch (score)
    {
        case 1:
			return "Very Poor";
		case 2:
			return "Poor";
		case 3:
			return "Moderate";
		case 4:
			return "Good";
		case 5:
			return "Excellent";
    default:
		return "Unknown";
        break;
    }
}

void IndoorClimateLib::UpdateSensorData(int humidity, int temperature, int co2, int light, int noise)
{
    this->humidity = humidity;  // Humidistat in percentage
    this->temperature = temperature;  // Temperature in Celsius
    this->co2 = co2;  // CO2 level in ppm
    this->light = light;  // Light level in lux
    this->noise = noise;  // Noise level in dB
    CalculateIndoorClimateIndex();
}

void IndoorClimateLib::PrintRightFrame(int line)
{
    ResetCursor(31, line);
    std::cout << "|\n";
}

void IndoorClimateLib::PrintTemperature() const
{
    std::cout << "Temperature: " << temperature << "°C\n";
}

void IndoorClimateLib::PrintHumidity() const
{
    std::cout << "Humidity: " << humidity << "%\n";
}

void IndoorClimateLib::PrintCO2() const
{
    std::cout << "CO2 Level: " << co2 << " ppm\n";
}

void IndoorClimateLib::PrintLight() const
{
    std::cout << "Light Level: " << light << " lux\n";
}

void IndoorClimateLib::PrintNoise() const
{
    std::cout << "Noise Level: " << noise << " dB\n";
}

void IndoorClimateLib::PrintAllSensorData() const
{
    PrintTemperature();
    PrintHumidity();
    PrintCO2();
    PrintLight();
    PrintNoise();
}

void IndoorClimateLib::PrintIndoorClimateIndex() const
{
    std::cout << "Indoor Climate Index: " << indoorClimateIndex << "\n";
}

} // namespace IndoorClimateLibNS