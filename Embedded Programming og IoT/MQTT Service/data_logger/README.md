# Arduino Sensor Data Logger

Logs MQTT sensor data to CSV for machine learning analysis.

## Setup

1. Install the required Python package:
   ```
   pip install paho-mqtt
   ```

2. Run the logger:
   ```
   python sensor_logger.py
   ```

3. Leave it running for 24+ hours to collect data.

4. Press `Ctrl+C` to stop and save.

## Output

CSV files are saved in the `data/` folder with format:
```
sensor_data_YYYYMMDD_HHMMSS.csv
```

### Columns

| Column | Type | Description |
|--------|------|-------------|
| timestamp | ISO datetime | When the reading was taken |
| light_lux | float | Light level (0-900 lux) |
| sound_raw | int | Sound amplitude (0-200 peak-to-peak) |
| temperature_c | float | Temperature in Celsius |
| humidity_percent | float | Relative humidity % |
| gas_raw | int | Flammable gas/smoke level (raw ADC 0-1023) |
| dust_ugm3 | float | Dust concentration in µg/m³ |
| motion | int | Motion detected (1=yes, 0=no) |

## Configuration

Edit `sensor_logger.py` to change:

- `MQTT_BROKER` - Broker address (default: localhost)
- `WRITE_INTERVAL_SECONDS` - How often to write rows (default: 1 second)

## Sample Data

After 24 hours at 1-second intervals, you'll have ~86,400 data points.
