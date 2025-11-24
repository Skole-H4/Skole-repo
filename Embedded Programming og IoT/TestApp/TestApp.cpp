// TestApp.cpp : This file contains the 'main' function. Program execution begins and ends there.
//

#include <iostream>
#include "IndoorClimateLib.h"
#include "ResistanceFinder.h"

int main()
{
    IndoorClimateLibNS::IndoorClimateLib climate;
    climate.InitConsoleUtf8();

    ResistanceFinder resistanceFinder;
    resistanceFinder.SetVoltage(5.0);
    resistanceFinder.SetCurrent(0.1);
    double resistance = resistanceFinder.CalculateResistance();
    int optimalSecondResistor = resistanceFinder.OptimalSecondResistor();

    std::cout << "First Resistor: " << resistance << " Ohms" << std::endl;
    std::cout << "Optimal Second Resistor: " << optimalSecondResistor << " Ohms" << std::endl;

    return 0;
}
    //climate.UpdateSensorData(45, 22, 700, 300, 35);
    //climate.PrintAllSensorData();

    // Avoid infinite loop unless intentionally testing:
    //climate.SimulateLiveDataInterfaceLoop();
    


