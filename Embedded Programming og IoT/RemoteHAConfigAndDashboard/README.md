# Remote Home Assistant Configuration Guide
============================================

## Files in this folder:

1. **configuration.yaml** - MQTT sensors + template sensor for CO2 recommendation
2. **ui-lovelace.yaml** - Dashboard with gauges, history graphs, and recommendations

## How to apply to Remote HA:

### Option A: If using YAML mode for Lovelace
1. Copy the contents of `configuration.yaml` into your remote HA's `configuration.yaml`
   - Merge the `mqtt:` section with your existing MQTT config
   - Add the `template:` section
   - Add `recorder:` and `history:` if not already present

2. Copy `ui-lovelace.yaml` to your remote HA config folder

3. Add to your main `configuration.yaml`:
   ```yaml
   lovelace:
     mode: yaml
   ```

4. Restart Home Assistant

### Option B: If using UI mode for Lovelace
1. Apply the `configuration.yaml` changes as in Option A

2. Restart Home Assistant

3. Go to Settings > Dashboards > Add Dashboard

4. Use the Raw Configuration Editor and paste the content from `ui-lovelace.yaml`

## Sensors included:

| Sensor | Entity ID | Type |
|--------|-----------|------|
| Temperature | sensor.classroom_sensor_classroom_temperature | Gauge |
| Humidity | sensor.classroom_sensor_classroom_humidity | Gauge |
| CO2 | sensor.classroom_sensor_classroom_co2 | Gauge + Recommendation |
| Light | sensor.classroom_sensor_classroom_light_level | Gauge |
| Sound | sensor.classroom_sensor_classroom_noise_level | Gauge |
| Dust | sensor.classroom_sensor_classroom_dust_level | Gauge |
| Motion | binary_sensor.classroom_sensor_classroom_motion | Tile |
| Clap | binary_sensor.classroom_sensor_classroom_clap | Tile |
| Gas/Smoke | binary_sensor.classroom_sensor_classroom_gas_detected | Tile |
| CO2 Quality | sensor.co2_air_quality | Text (Excellent/Good/Moderate/Poor/Bad) |

## CO2 Thresholds:

| Range | Color | Label |
|-------|-------|-------|
| < 600 ppm | Green | Excellent |
| 600-999 ppm | Green | Good |
| 1000-1499 ppm | Yellow | Moderate |
| 1500-1999 ppm | Yellow | Poor |
| 2000+ ppm | Red | Bad |
