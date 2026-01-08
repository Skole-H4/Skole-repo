#!/usr/bin/env python3
"""
HuskyLens Face ID Reader - Diagnostic Tool

Simple script to read and display detected face IDs from HuskyLens.
Use this to verify face learning and see which IDs are being detected.

Usage:
    python3 huskylens_reader.py [--port /dev/serial0] [--baud 9600]
"""

import sys
import time
import argparse
from typing import Optional, Tuple, Set

try:
    import serial
except ImportError:
    print("Error: pyserial is required. Install with: pip install pyserial")
    sys.exit(1)


# =============================================================================
# HUSKYLENS PROTOCOL
# =============================================================================

HUSKY_ADDR = 0x11

# Commands
CMD_REQUEST_BLOCKS = 0x21

# Response types
RET_BLOCK = 0x2A
RET_BUSY = 0x3D


def calculate_checksum(packet: bytes) -> int:
    """Calculate HuskyLens protocol checksum."""
    return sum(packet) & 0xFF


def make_frame(cmd: int, data: bytes = b"") -> bytes:
    """
    Construct a HuskyLens protocol frame.
    Frame format: [0x55, 0xAA, ADDR, LENGTH, CMD, DATA..., CHECKSUM]
    """
    packet = bytes([0x55, 0xAA, HUSKY_ADDR, len(data), cmd]) + data
    return packet + bytes([calculate_checksum(packet)])


def read_frame(ser: serial.Serial) -> Optional[Tuple[int, int, bytes]]:
    """
    Read and parse a HuskyLens response frame.
    Returns: (address, command, data) or None if read failed
    """
    try:
        # Synchronize on frame header (0x55 0xAA)
        while True:
            byte1 = ser.read(1)
            if not byte1:
                return None
            if byte1 == b"\x55":
                byte2 = ser.read(1)
                if not byte2:
                    return None
                if byte2 == b"\xAA":
                    break

        # Read header: address, length, command
        header = ser.read(3)
        if len(header) != 3:
            return None
        
        addr, length, cmd = header[0], header[1], header[2]

        # Read payload data
        data = ser.read(length)
        if len(data) != length:
            return None

        # Read and validate checksum
        checksum_byte = ser.read(1)
        if len(checksum_byte) != 1:
            return None

        packet = b"\x55\xAA" + bytes([addr, length, cmd]) + data
        expected = calculate_checksum(packet)
        
        if expected != checksum_byte[0]:
            return None

        return (addr, cmd, data)
        
    except serial.SerialException:
        return None


def request_faces(ser: serial.Serial, timeout: float = 0.25) -> Set[int]:
    """
    Request face detection data from HuskyLens.
    Returns: Set of detected face IDs
    """
    try:
        ser.write(make_frame(CMD_REQUEST_BLOCKS))
        ser.flush()
    except serial.SerialException as e:
        print(f"Serial write error: {e}")
        return set()

    detected_ids: Set[int] = set()
    deadline = time.time() + timeout

    while time.time() < deadline:
        frame = read_frame(ser)
        
        if not frame:
            continue
            
        _, cmd, data = frame
        
        if cmd == RET_BUSY:
            break
            
        if cmd == RET_BLOCK and len(data) == 10:
            # Face ID is at bytes 8-9 (little-endian uint16)
            face_id = data[8] | (data[9] << 8)
            detected_ids.add(face_id)

    return detected_ids


# =============================================================================
# MAIN
# =============================================================================

def main():
    parser = argparse.ArgumentParser(
        description="HuskyLens Face ID Reader - See detected face IDs in real-time"
    )
    parser.add_argument("--port", default="/dev/serial0", help="Serial port")
    parser.add_argument("--baud", type=int, default=9600, help="Baud rate")
    args = parser.parse_args()

    print("=" * 50)
    print("  HuskyLens Face ID Reader")
    print("=" * 50)
    print(f"  Port: {args.port}")
    print(f"  Baud: {args.baud}")
    print("=" * 50)
    print()
    print("Face ID meanings:")
    print("  ID = 0    → Unknown face (not learned)")
    print("  ID = 1    → First learned face")
    print("  ID = 2    → Second learned face")
    print("  (no face) → No face in view")
    print()
    print("Press Ctrl+C to exit")
    print("-" * 50)

    try:
        with serial.Serial(args.port, args.baud, timeout=0.15) as ser:
            time.sleep(0.5)  # Let connection stabilize
            ser.reset_input_buffer()
            
            last_ids: Set[int] = set()
            
            while True:
                detected = request_faces(ser)
                
                # Only print on change (avoid spam)
                if detected != last_ids:
                    timestamp = time.strftime("%H:%M:%S")
                    
                    if not detected:
                        print(f"[{timestamp}] No face detected")
                    else:
                        for face_id in sorted(detected):
                            if face_id == 0:
                                status = "UNKNOWN (not learned)"
                            else:
                                status = f"LEARNED (ID: {face_id})"
                            print(f"[{timestamp}] Face detected → {status}")
                    
                    last_ids = detected
                
                time.sleep(0.15)

    except serial.SerialException as e:
        print(f"\nError: Could not open {args.port}: {e}")
        print("\nTroubleshooting:")
        print("  1. Check HuskyLens is connected to GPIO UART pins")
        print("  2. Verify UART is enabled: sudo raspi-config → Interface → Serial")
        print("  3. Check port exists: ls -la /dev/serial0")
        sys.exit(1)
    except KeyboardInterrupt:
        print("\n\nExiting...")
        sys.exit(0)


if __name__ == "__main__":
    main()
