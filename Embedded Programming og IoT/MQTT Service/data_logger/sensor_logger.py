#!/usr/bin/env python3
"""
Arduino Sensor Data Logger
Subscribes to MQTT topics and logs all sensor data to CSV for ML analysis.
Run this script to collect 24+ hours of sensor data.
"""

import paho.mqtt.client as mqtt
import csv
import os
from datetime import datetime
from pathlib import Path

# MQTT Configuration
MQTT_BROKER = "localhost"  # Change to "10.108.137.124" if running outside Docker network
MQTT_PORT = 1883
MQTT_USER = "arduino"
MQTT_PASSWORD = "sensor1234"

# Output file
OUTPUT_DIR = Path(__file__).parent / "data"
OUTPUT_FILE = OUTPUT_DIR / f"sensor_data_{datetime.now().strftime('%Y%m%d_%H%M%S')}.csv"

# CSV columns
COLUMNS = [
    "timestamp",
    "light_lux",
    "sound_raw",
    "temperature_c",
    "humidity_percent",
    "gas_raw",
    "dust_ugm3",
    "motion"
]

# Current sensor values (updated as messages arrive)
sensor_data = {
    "light_lux": None,
    "sound_raw": None,
    "temperature_c": None,
    "humidity_percent": None,
    "gas_raw": None,
    "dust_ugm3": None,
    "motion": None
}

# Track when we last wrote a row
last_write_time = None
WRITE_INTERVAL_SECONDS = 1  # Write one row per second


def on_connect(client, userdata, flags, rc, properties=None):
    """Called when connected to MQTT broker."""
    if rc == 0:
        print(f"✓ Connected to MQTT broker at {MQTT_BROKER}:{MQTT_PORT}")
        # Subscribe to all sensor topics
        client.subscribe("sensors/#")
        print("✓ Subscribed to sensors/#")
        print(f"✓ Logging to: {OUTPUT_FILE}")
        print("\n" + "=" * 60)
        print("Data logging started. Press Ctrl+C to stop.")
        print("=" * 60 + "\n")
    else:
        print(f"✗ Connection failed with code {rc}")


def on_message(client, userdata, msg):
    """Called when a message is received."""
    global last_write_time
    
    topic = msg.topic
    payload = msg.payload.decode("utf-8")
    
    # Update sensor values based on topic
    if topic == "sensors/light":
        sensor_data["light_lux"] = float(payload)
    elif topic == "sensors/sound":
        sensor_data["sound_raw"] = int(payload)
    elif topic == "sensors/temperature":
        sensor_data["temperature_c"] = float(payload)
    elif topic == "sensors/humidity":
        sensor_data["humidity_percent"] = float(payload)
    elif topic == "sensors/gas":
        sensor_data["gas_raw"] = int(payload)
    elif topic == "sensors/dust":
        sensor_data["dust_ugm3"] = float(payload)
    elif topic == "sensors/motion":
        sensor_data["motion"] = 1 if payload == "ON" else 0
    
    # Write row at regular intervals (not on every message)
    now = datetime.now()
    if last_write_time is None or (now - last_write_time).total_seconds() >= WRITE_INTERVAL_SECONDS:
        write_row(now)
        last_write_time = now


def write_row(timestamp):
    """Write current sensor values to CSV."""
    # Only write if we have at least some data
    if all(v is None for v in sensor_data.values()):
        return
    
    row = {
        "timestamp": timestamp.isoformat(),
        **sensor_data
    }
    
    with open(OUTPUT_FILE, "a", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=COLUMNS)
        writer.writerow(row)
    
    # Print status every 10 seconds
    if timestamp.second % 10 == 0:
        print(f"[{timestamp.strftime('%H:%M:%S')}] "
              f"T:{sensor_data['temperature_c']:.1f}°C "
              f"H:{sensor_data['humidity_percent']:.0f}% "
              f"L:{sensor_data['light_lux']:.0f}lux "
              f"S:{sensor_data['sound_raw']} "
              f"G:{sensor_data['gas_raw']} "
              f"D:{sensor_data['dust_ugm3']:.1f}ug/m3 "
              f"M:{'YES' if sensor_data['motion'] else 'no'}")


def setup_csv():
    """Create output directory and CSV file with headers."""
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    
    with open(OUTPUT_FILE, "w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=COLUMNS)
        writer.writeheader()
    
    print(f"✓ Created CSV file: {OUTPUT_FILE}")


def main():
    print("\n" + "=" * 60)
    print("  Arduino Sensor Data Logger for Machine Learning")
    print("=" * 60 + "\n")
    
    # Setup CSV file
    setup_csv()
    
    # Setup MQTT client
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2)
    client.username_pw_set(MQTT_USER, MQTT_PASSWORD)
    client.on_connect = on_connect
    client.on_message = on_message
    
    try:
        print(f"Connecting to MQTT broker at {MQTT_BROKER}:{MQTT_PORT}...")
        client.connect(MQTT_BROKER, MQTT_PORT, 60)
        client.loop_forever()
    except KeyboardInterrupt:
        print("\n\n" + "=" * 60)
        print("Logging stopped by user.")
        print(f"Data saved to: {OUTPUT_FILE}")
        
        # Count rows
        with open(OUTPUT_FILE, "r", encoding="utf-8") as f:
            row_count = sum(1 for _ in f) - 1  # Subtract header
        print(f"Total data points: {row_count:,}")
        print("=" * 60 + "\n")
    except Exception as e:
        print(f"Error: {e}")
    finally:
        client.disconnect()


if __name__ == "__main__":
    main()
