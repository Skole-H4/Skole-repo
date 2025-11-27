# Constants
$Vin = 5.0        # Input voltage (5V)
$adcMax = 1023    # Arduino 10-bit ADC max value (0-1023)

# Range of R1 values: from 100Ω to 100MΩ (logarithmic stepping)
$R1Values = @(100, 200, 500, 1000, 2200, 4700, 10000, 22000, 47000, 100000, 220000, 470000, 1000000, 2200000, 4700000, 10000000, 22000000, 47000000, 100000000)

# Candidate R2 values (logarithmic sweep for reasonable range)
$R2Candidates = @(10, 22, 47, 100, 220, 470, 1000, 2200, 4700, 10000, 22000, 36010, 47000, 100000, 220000, 470000, 1000000, 2200000, 4700000, 10000000)

# Results collection
$results = @()

# Loop through each R2 candidate
foreach ($R2 in $R2Candidates) {
    Write-Host "Testing R2 = $($R2)Ω"
    
    # Dictionary to store unique ADC values for this R2
    $adcValues = @{}
    
    # Loop through each R1 value
    foreach ($R1 in $R1Values) {
        # Calculate the voltage at the ADC pin using the voltage divider formula
        # Configuration: Vin --[R1]-- ADC_PIN --[R2]-- GND
        # Vout (at ADC pin) = Vin × (R2 / (R1 + R2))
        $Vout = $Vin * ($R2 / ($R1 + $R2))
        
        # Calculate the corresponding ADC value (map 0-5V range to 0-1023)
        $adc = [math]::Floor(($Vout / $Vin) * $adcMax)
        
        # Store the unique ADC value (use a dictionary to automatically filter duplicates)
        $adcValues[$adc] = $true
        
        # Output the current test result for every R1-R2 combination
        Write-Host "  R1 = $($R1)Ω  -->  Vout = $($Vout) V  -->  ADC = $($adc)"
    }
    
    # Count how many unique ADC values were found for this R2
    $uniqueCount = $adcValues.Keys.Count
    
    # Store the results
    $results += [PSCustomObject]@{
        R2 = $R2
        UniqueADCSteps = $uniqueCount
    }
    
    Write-Host "  Unique ADC steps for R2 = $($R2)Ω: $($uniqueCount)"
    Write-Host ""
}

# Summary of results: Sort by the highest number of unique ADC steps
$bestResult = $results | Sort-Object UniqueADCSteps -Descending | Select-Object -First 1

# Output the best R2 value and the corresponding number of unique ADC steps
Write-Host "----------------------------------------"
Write-Host "Best R2: $($bestResult.R2)Ω"
Write-Host "Unique ADC steps: $($bestResult.UniqueADCSteps)"
Write-Host "----------------------------------------"
