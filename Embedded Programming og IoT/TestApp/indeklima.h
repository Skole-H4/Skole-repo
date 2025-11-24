#pragma once
#include <iostream>
#include <cstdlib>
using namespace std;

namespace IndoorClimateLibNS {

	class IndoorClimateLib
	{

//	public:
//		IndoorClimateLib(int humidity, int temperature, int co2, int light, int noise);

	private:
		int humidity = 0; // Humidistat in percentage
		int temperature = 0; // Temperature in Celsius
		int co2 = 0; // CO2 level in ppm
		int light = 0; // Light level in lux
		int noise = 0; // Noise level in dB
		int indoorClimateIndex = 0; // Calculated Indoor Climate Index
	
		void CalculateIndoorClimateIndex()
		{
			indoorClimateIndex = (humidity + temperature + co2 + light + noise) / 5;
		}

		void GenerateFakeData()
		{
			humidity = rand() % 101; // 0-100%
			temperature = rand() % 41; // 0-40°C
			co2 = rand() % 2001; // 0-2000 ppm
			light = rand() % 10001; // 0-10000 lux
			noise = rand() % 101; // 0-100 dB
		}

		void LoopFakeData()
		{
			while (true)
			{
				GenerateFakeData();
				UpdateSensorData(humidity, temperature, co2, light, noise);
				PrintAllSensorData();
				PrintIndoorClimateIndex();
			}
		}

	public:
		void StartRandomLoop()
		{
			LoopFakeData();
		}

		void UpdateSensorData(int humidity, int temperature, int co2, int light, int noise)
		{
			this->humidity = humidity;
			this->temperature = temperature;
			this->co2 = co2;
			this->light = light;
			this->noise = noise;
			CalculateIndoorClimateIndex();
			PrintIndoorClimateIndex();
		}

		void PrintTemperature() const
		{
			cout << "Temperature: " << temperature << "°C" << endl;
		}

		void PrintHumidity() const
		{
			cout << "Humidity: " << humidity << "%" << endl;
		}

		void PrintCO2() const
		{
			cout << "CO2 Level: " << co2 << " ppm" << endl;
		}

		void PrintLight() const
		{
			cout << "Light Level: " << light << " lux" << endl;
		}

		void PrintNoise() const
		{
			cout << "Noise Level: " << noise << " dB" << endl;
		}

		void PrintAllSensorData() const
		{
			PrintTemperature();
			PrintHumidity();
			PrintCO2();
			PrintLight();
			PrintNoise();
		}

		void PrintIndoorClimateIndex() const
		{
			cout << "Indoor Climate Index: " << indoorClimateIndex << endl;
		}

	};

}