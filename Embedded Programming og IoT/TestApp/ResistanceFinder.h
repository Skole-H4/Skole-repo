#pragma once
class ResistanceFinder
{
private:
	int firstResistor = 100;
	int secondResistorGuess = 0;
	double voltage = 5.0;
	double current = 0.0;
	int resolution = 1023;



public:
	ResistanceFinder();
	void SetVoltage(double voltage);
	void SetCurrent(double current);
	double CalculateResistance();
	int OptimalSecondResistor();
};

