#include "ResistanceFinder.h"
#include <cmath>


ResistanceFinder::ResistanceFinder() : firstResistor(0), secondResistorGuess(0), voltage(0.0), current(0.0)
{
}



void ResistanceFinder::SetVoltage(double voltage)
{
	this->voltage = voltage;
}

void ResistanceFinder::SetCurrent(double current)
{
	this->current = current;
}

double ResistanceFinder::CalculateResistance()
{
	// If measuring current via shunt: R = V / I (ensure 'voltage' is the max expected at shunt)
	firstResistor = static_cast<int>(voltage / current);
	return firstResistor;
}

int ResistanceFinder::OptimalSecondResistor()
{
	// Original formula caused division by zero: (voltage - firstResistor * current) == 0
	// For a voltage divider scaling a source 'voltage' down to ADC reference (assume 5V):
	const double vRef = 5.0;
	if (voltage <= vRef)
	{
		secondResistorGuess = 0; // No divider needed
		return secondResistorGuess;
	}
	// R2 = (vRef * R1) / (Vsource - vRef)
	double r2 = (vRef * firstResistor) / (voltage - vRef);
	secondResistorGuess = static_cast<int>(std::round(r2));
	return secondResistorGuess;
}