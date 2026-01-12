# sshGuard - HuskyLens Face Recognition SSH Access Control

> **Biometric-gated SSH access** for Raspberry Pi using HuskyLens face recognition.  
> SSH is only available when an authorized face is detected by the camera.

---

## Table of Contents

1. [Overview](#overview)
2. [Features](#features)
3. [System Architecture](#system-architecture)
   - [Component Diagram](#component-diagram)
   - [Authentication Flow](#authentication-flow)
   - [Strict Mode Flow](#strict-mode-flow)
4. [Hardware Requirements](#hardware-requirements)
5. [Software Requirements](#software-requirements)
6. [Installation](#installation)
   - [Quick Install](#quick-install)
   - [Manual Installation](#manual-installation)
   - [Debian Package Installation](#debian-package-installation)
7. [Configuration](#configuration)
   - [Command Line Options](#command-line-options)
   - [User Configuration File](#user-configuration-file)
   - [systemd Service Configuration](#systemd-service-configuration)
8. [User Management](#user-management)
   - [Creating Users](#creating-users)
   - [Face ID Mapping](#face-id-mapping)
   - [SSH Key Authentication](#ssh-key-authentication)
9. [Security Model](#security-model)
   - [PAM Integration](#pam-integration)
   - [SSH Hardening](#ssh-hardening)
   - [Access Windows](#access-windows)
10. [Debugging and Troubleshooting](#debugging-and-troubleshooting)
    - [Service Status Commands](#service-status-commands)
    - [Log Analysis](#log-analysis)
    - [HuskyLens Connection Issues](#huskylens-connection-issues)
    - [SSH Access Issues](#ssh-access-issues)
    - [PAM and Authentication Issues](#pam-and-authentication-issues)
    - [Serial Port Troubleshooting](#serial-port-troubleshooting)
    - [Common Error Messages](#common-error-messages)
11. [Uninstallation](#uninstallation)
12. [File Locations](#file-locations)
13. [License](#license)

---

## Overview

**sshGuard** is a security solution that uses a HuskyLens AI camera module to control SSH access on a Raspberry Pi. The system keeps SSH disabled by default and only enables it temporarily when an authorized face is detected. This provides physical presence verification before allowing remote access.

**How it works:**
1. HuskyLens continuously scans for faces
2. When a learned (authorized) face is detected for a configurable number of consecutive frames, SSH is enabled
3. SSH remains open for a configurable time window (default: 15 seconds)
4. Only the detected user can log in during that window (PAM enforcement)
5. After the window closes, SSH is stopped (in strict mode)

---

## Features

- ✅ **Face-based SSH gating** – SSH only available when authorized face detected
- ✅ **Per-user authorization** – Only the detected user can log in during the access window
- ✅ **SSH key authentication** – Password auth disabled, keys generated per user
- ✅ **AllowGroups restriction** – Only `sshGuard` group members can SSH
- ✅ **Strict mode** – SSH is completely stopped outside access windows
- ✅ **Desktop notifications** – Zenity dialogs notify GUI users of access events
- ✅ **Configurable timing** – Streak threshold, window duration, and cooldown
- ✅ **systemd integration** – Runs as a service with auto-restart
- ✅ **Debian packaging** – Easy installation via `.deb` package

---

## System Architecture

### Component Diagram

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              Raspberry Pi                                    │
│                                                                              │
│  ┌──────────────┐     UART      ┌──────────────────────────────────────┐    │
│  │  HuskyLens   │──────────────▶│          sshguard.py                 │    │
│  │  AI Camera   │  /dev/serial0 │  (Python daemon)                     │    │
│  │              │               │                                      │    │
│  │  • Face      │               │  • Reads face IDs from HuskyLens     │    │
│  │    Detection │               │  • Manages SSH service via systemctl │    │
│  │  • Face      │               │  • Writes allowed user to PAM file   │    │
│  │    Learning  │               │  • Sends Zenity notifications        │    │
│  └──────────────┘               └──────────────┬───────────────────────┘    │
│                                                 │                            │
│                    ┌────────────────────────────┼────────────────────────┐   │
│                    │                            │                        │   │
│                    ▼                            ▼                        ▼   │
│  ┌─────────────────────┐   ┌─────────────────────────┐   ┌──────────────────┐│
│  │   systemd           │   │   PAM (pam_listfile)    │   │   Zenity         ││
│  │                     │   │                         │   │   Dialogs        ││
│  │  • ssh.service      │   │  /run/sshguard/         │   │                  ││
│  │    start/stop       │   │    allowed_user         │   │  • Access        ││
│  │                     │   │                         │   │    granted       ││
│  │  • sshguard.service │   │  Only user in file      │   │  • Window        ││
│  │    (this daemon)    │   │  can authenticate       │   │    closed        ││
│  └─────────────────────┘   └─────────────────────────┘   └──────────────────┘│
│                                                                              │
└─────────────────────────────────────────────────────────────────────────────┘
                                       │
                                       │ SSH (port 22)
                                       │ only when window open
                                       ▼
                              ┌──────────────────┐
                              │   Remote User    │
                              │   (SSH Client)   │
                              └──────────────────┘
```

### Authentication Flow

```
┌─────────────┐                                                              
│   Start     │                                                              
└──────┬──────┘                                                              
       │                                                                     
       ▼                                                                     
┌──────────────────┐                                                         
│  HuskyLens       │                                                         
│  detects face    │                                                         
└────────┬─────────┘                                                         
         │                                                                   
         ▼                                                                   
┌──────────────────────┐     No      ┌─────────────────┐                     
│  Face ID > 0?        │────────────▶│  Reset streak   │                     
│  (Learned face)      │             │  counter        │                     
└────────┬─────────────┘             └────────┬────────┘                     
         │ Yes                                │                              
         ▼                                    │                              
┌──────────────────────┐                      │                              
│  Increment streak    │                      │                              
│  counter             │                      │                              
└────────┬─────────────┘                      │                              
         │                                    │                              
         ▼                                    │                              
┌──────────────────────┐     No               │                              
│  Streak >= threshold │──────────────────────┤                              
│  (default: 3)?       │                      │                              
└────────┬─────────────┘                      │                              
         │ Yes                                │                              
         ▼                                    │                              
┌──────────────────────┐     No               │                              
│  Cooldown passed?    │──────────────────────┤                              
└────────┬─────────────┘                      │                              
         │ Yes                                │                              
         ▼                                    │                              
┌──────────────────────┐                      │                              
│  Look up username    │                      │                              
│  from face ID        │                      │                              
│  (users.conf)        │                      │                              
└────────┬─────────────┘                      │                              
         │                                    │                              
         ▼                                    │                              
┌──────────────────────┐                      │                              
│  Write username to   │                      │                              
│  /run/sshguard/      │                      │                              
│  allowed_user        │                      │                              
└────────┬─────────────┘                      │                              
         │                                    │                              
         ▼                                    │                              
┌──────────────────────┐                      │                              
│  Start SSH service   │                      │                              
│  (systemctl start    │                      │                              
│   ssh)               │                      │                              
└────────┬─────────────┘                      │                              
         │                                    │                              
         ▼                                    │                              
┌──────────────────────┐                      │                              
│  Show Zenity         │                      │                              
│  notification        │                      │                              
└────────┬─────────────┘                      │                              
         │                                    │                              
         ▼                                    │                              
┌──────────────────────┐                      │                              
│  Wait for window     │                      │                              
│  duration            │                      │                              
│  (default: 15s)      │                      │                              
└────────┬─────────────┘                      │                              
         │                                    │                              
         ▼                                    │                              
┌──────────────────────┐                      │                              
│  Stop SSH service    │                      │                              
│  (strict mode)       │                      │                              
└────────┬─────────────┘                      │                              
         │                                    │                              
         ▼                                    │                              
┌──────────────────────┐                      │                              
│  Clear allowed_user  │                      │                              
│  file                │                      │                              
└────────┬─────────────┘                      │                              
         │                                    │                              
         ▼                                    │                              
┌──────────────────────┐                      │                              
│  Start cooldown      │◀─────────────────────┘                              
│  timer               │                                                     
└────────┬─────────────┘                                                     
         │                                                                   
         ▼                                                                   
┌──────────────────────┐                                                     
│  Continue detection  │                                                     
│  loop                │                                                     
└──────────────────────┘                                                     
```

### Strict Mode Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            STRICT MODE (--strict)                           │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  SSH STATE:   ████████░░░░░░░░░░░░████████░░░░░░░░░░░░░░░░░░░████████░░░░░  │
│               STOPPED    OPEN     STOPPED         STOPPED     OPEN   STOP  │
│                          15s                                  15s          │
│                           │                                    │           │
│  TIMELINE:   ─────────────┼────────────────────────────────────┼─────────▶ │
│               Face       Window    Cooldown                   Face         │
│               detected   closes    (3s)                       detected     │
│                                                                             │
│  Note: In strict mode, SSH is STOPPED on sshGuard startup and after each   │
│        access window. This WILL disconnect existing SSH sessions.          │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│                          NON-STRICT MODE (default)                          │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  SSH STATE:   Depends on whether SSH was already running when sshGuard     │
│               detected a face. If SSH was running, it stays running.       │
│               If SSH was stopped, it's started temporarily and stopped     │
│               after the window.                                             │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Hardware Requirements

| Component | Description |
|-----------|-------------|
| **Raspberry Pi** | Any model with UART GPIO pins (tested on Pi 4, Pi 5) |
| **HuskyLens** | DFRobot HuskyLens AI Camera (Gravity version) |
| **Wiring** | 4 wires: VCC (5V), GND, TX, RX |

### HuskyLens Wiring Diagram

```
HuskyLens          Raspberry Pi GPIO
───────────        ──────────────────
   VCC  ──────────▶  Pin 2 (5V)
   GND  ──────────▶  Pin 6 (GND)
   TX   ──────────▶  Pin 10 (GPIO15 / RXD)
   RX   ──────────▶  Pin 8 (GPIO14 / TXD)
```

> **Important:** HuskyLens TX connects to Pi RX, and HuskyLens RX connects to Pi TX (crossed).

---

## Software Requirements

- **Raspberry Pi OS** (Bullseye or later recommended)
- **Python 3.7+**
- **pyserial** (`pip install pyserial`)
- **systemd** (standard on Raspberry Pi OS)
- **zenity** (optional, for desktop notifications)
- **putty-tools** (optional, for `.ppk` key generation)

---

## Installation

### Quick Install

```bash
# Clone or copy files to the Pi
cd /path/to/sshGuard

# Make install script executable
chmod +x install.sh

# Run installer as root
sudo ./install.sh
```

### Manual Installation

```bash
# 1. Install dependencies
sudo apt update
sudo apt install python3 python3-pip zenity putty-tools
pip3 install pyserial

# 2. Create installation directory
sudo mkdir -p /opt/sshGuard
sudo cp sshguard.py /opt/sshGuard/
sudo cp create-user.sh /opt/sshGuard/
sudo chmod +x /opt/sshGuard/*.py /opt/sshGuard/*.sh

# 3. Install systemd service
sudo cp sshguard.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable sshguard
sudo systemctl start sshguard
```

### Debian Package Installation

```bash
# Build the package (on a Debian/Raspbian system)
cd debian-package
./build-deb.sh

# Install the package
sudo dpkg -i sshguard-face_1.0.0_all.deb
sudo apt-get install -f  # Install dependencies

# The package automatically:
# - Installs files to /opt/sshGuard
# - Creates the sshGuard group
# - Configures SSH and PAM
# - Enables and starts the service
```

---

## Configuration

### Command Line Options

| Option | Default | Description |
|--------|---------|-------------|
| `--port` | `/dev/serial0` | Serial port for HuskyLens connection |
| `--baud` | `9600` | Baud rate for serial communication |
| `--open-seconds`, `-s` | `15` | Duration to keep SSH open (seconds) |
| `--streak` | `3` | Consecutive detections required to trigger |
| `--cooldown` | `3` | Seconds before re-triggering is allowed |
| `--strict` | `false` | Enforce SSH closed outside access windows |

**Example:**

```bash
# Open SSH for 30 seconds, require 5 consecutive detections
sudo python3 /opt/sshGuard/sshguard.py --open-seconds 30 --streak 5 --strict
```

### User Configuration File

**Location:** `/opt/sshGuard/users.conf`

**Format:**
```
# Face ID to username mapping
# Format: face_id=username

1=martin
2=admin
3=developer
```

- Lines starting with `#` are comments
- Each line maps a HuskyLens face ID to a Linux username
- Face IDs are assigned by HuskyLens when you teach it a face

### systemd Service Configuration

**Location:** `/etc/systemd/system/sshguard.service`

To modify service options:

```bash
# Edit the service file
sudo systemctl edit sshguard --full

# Or create an override
sudo systemctl edit sshguard
```

**Example override to change open-seconds:**

```ini
[Service]
ExecStart=
ExecStart=/usr/bin/python3 /opt/sshGuard/sshguard.py --strict --open-seconds 30
```

---

## User Management

### Creating Users

Use the provided script to create users with face ID mappings:

```bash
# Create user with SSH key only (recommended)
sudo /opt/sshGuard/create-user.sh --username martin --faceId 1

# Create user with both SSH key and password
sudo /opt/sshGuard/create-user.sh --username martin --faceId 1 --password secret123
```

The script:
1. Creates the Linux user (or updates existing)
2. Adds user to the `sshGuard` group
3. Generates an SSH keypair (ed25519)
4. Optionally converts to PuTTY `.ppk` format
5. Adds the public key to `~/.ssh/authorized_keys`
6. Updates `/opt/sshGuard/users.conf` with face ID mapping

### Face ID Mapping

Face IDs are assigned by HuskyLens during face learning:

1. Set HuskyLens to **Face Recognition** mode
2. Point camera at the person's face
3. Press the learning button to teach the face
4. HuskyLens assigns an ID (1, 2, 3, ...)
5. Map this ID to a username in `users.conf`

### SSH Key Authentication

After running `create-user.sh`:

- **Private key (OpenSSH):** `/opt/sshGuard/ssh-keys/{username}_sshguard`
- **Private key (PuTTY):** `/opt/sshGuard/ssh-keys/{username}_sshguard.ppk`
- **Public key:** `/opt/sshGuard/ssh-keys/{username}_sshguard.pub`

Transfer the private key to your SSH client machine:

```bash
# Copy key from Pi to your machine
scp pi@raspberry:/opt/sshGuard/ssh-keys/martin_sshguard ~/.ssh/

# Set permissions
chmod 600 ~/.ssh/martin_sshguard

# Use the key
ssh -i ~/.ssh/martin_sshguard martin@raspberry
```

---

## Security Model

### PAM Integration

sshGuard uses PAM (`pam_listfile`) to restrict SSH logins to the detected user:

**PAM Rule (added to `/etc/pam.d/sshd`):**
```
account required pam_listfile.so onerr=succeed item=user sense=allow file=/run/sshguard/allowed_user
```

- `onerr=succeed`: If the file doesn't exist, allow login (for when sshguard is stopped)
- `sense=allow`: Only users listed in the file can log in
- The file contains exactly one username (the detected user)

### SSH Hardening

The installer configures SSH with these security settings:

**`/etc/ssh/sshd_config.d/sshguard.conf`:**
```
AllowGroups sshGuard
PermitRootLogin no
PasswordAuthentication no
PubkeyAuthentication yes
AuthenticationMethods publickey
```

- Only `sshGuard` group members can SSH
- Root cannot SSH directly
- Only public key authentication allowed

### Access Windows

```
┌─────────────────────────────────────────────────────────────────┐
│                    Access Window Timeline                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│  Detection    Write allowed_user    Start SSH    Window closes  │
│      │              │                   │              │        │
│      ▼              ▼                   ▼              ▼        │
│  ────┼──────────────┼───────────────────┼──────────────┼──────▶ │
│      │              │                   │              │        │
│      │              │                   │◀────────────▶│        │
│      │              │                   │  15 seconds  │        │
│      │              │                   │  (default)   │        │
│      │              │                   │              │        │
│      │              │  SSH available    │              │        │
│      │              │  Only "martin"    │              │        │
│      │              │  can log in       │              │        │
│                                                                  │
│  After window: SSH stopped, allowed_user file deleted           │
│  Cooldown: 3 seconds before next trigger                        │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## Debugging and Troubleshooting

### Service Status Commands

```bash
# Check if sshguard service is running
sudo systemctl status sshguard

# Check if SSH service is running
sudo systemctl status ssh

# List all sshguard-related processes
ps aux | grep sshguard

# Check if sshguard is listening on serial port
lsof /dev/serial0
```

### Log Analysis

```bash
# View live sshguard logs
sudo journalctl -u sshguard -f

# View last 100 log lines
sudo journalctl -u sshguard -n 100

# View logs since boot
sudo journalctl -u sshguard -b

# View logs from the last hour
sudo journalctl -u sshguard --since "1 hour ago"

# Search for specific events
sudo journalctl -u sshguard | grep "AUTHORIZED"
sudo journalctl -u sshguard | grep "TRIGGER"
sudo journalctl -u sshguard | grep "ERROR"

# Export logs to file
sudo journalctl -u sshguard > /tmp/sshguard-logs.txt
```

### HuskyLens Connection Issues

```bash
# Check if serial port exists
ls -la /dev/serial0

# Check serial port permissions
stat /dev/serial0

# Test serial connection manually
python3 -c "import serial; s = serial.Serial('/dev/serial0', 9600, timeout=1); print('Connected')"

# Check if serial is enabled in Raspberry Pi config
sudo raspi-config nonint get_serial_hw
# Should return 0 (enabled)

# Enable serial if disabled
sudo raspi-config nonint do_serial_hw 0
sudo reboot

# Monitor serial port traffic (requires minicom)
sudo apt install minicom
sudo minicom -D /dev/serial0 -b 9600

# Check kernel messages for serial issues
dmesg | grep -i serial
dmesg | grep -i tty
```

### SSH Access Issues

```bash
# Check SSH service status
sudo systemctl status ssh

# Check SSH configuration syntax
sudo sshd -t

# View SSH authentication logs
sudo journalctl -u ssh | tail -50

# Check if user is in sshGuard group
groups <username>
id <username>

# Check AllowGroups setting
grep -i allowgroups /etc/ssh/sshd_config /etc/ssh/sshd_config.d/*

# Check which users are in sshGuard group
getent group sshGuard

# Test SSH connection with verbose output
ssh -vvv user@raspberry

# Check active SSH connections
ss -tn sport = :22

# Check if port 22 is listening
sudo netstat -tlnp | grep :22
ss -tln | grep :22
```

### PAM and Authentication Issues

```bash
# Check PAM configuration for sshd
cat /etc/pam.d/sshd

# Check if pam_listfile rule is present
grep -i sshguard /etc/pam.d/sshd
grep allowed_user /etc/pam.d/sshd

# Check current allowed user file
cat /run/sshguard/allowed_user

# Check if runtime directory exists
ls -la /run/sshguard/

# Check PAM debug logs (enable in /etc/pam.d/sshd first)
# Add "debug" to pam_listfile line, then:
sudo journalctl | grep -i pam

# Verify user exists and has valid shell
getent passwd <username>
grep <username> /etc/shells

# Check authorized_keys file
cat /home/<username>/.ssh/authorized_keys
ls -la /home/<username>/.ssh/

# Test PAM authentication manually
pamtester sshd <username> authenticate
```

### Serial Port Troubleshooting

```bash
# Check UART configuration
cat /boot/config.txt | grep -i uart
cat /boot/config.txt | grep -i serial

# Ensure these lines are present in /boot/config.txt:
# enable_uart=1
# dtoverlay=disable-bt  (for Pi 3/4/5, to free up primary UART)

# Check for conflicting processes
sudo fuser /dev/serial0

# Kill any conflicting process
sudo fuser -k /dev/serial0

# Check if Bluetooth is using the UART (Pi 3/4/5)
hciconfig

# Disable Bluetooth to free UART (if needed)
sudo systemctl disable bluetooth
sudo systemctl stop bluetooth

# Re-check serial permissions after changes
sudo chmod 666 /dev/serial0  # Temporary fix for testing
```

### Common Error Messages

| Error Message | Cause | Solution |
|---------------|-------|----------|
| `Serial connection failed` | HuskyLens not connected or wrong port | Check wiring, verify `/dev/serial0` exists |
| `pyserial is required` | Missing Python library | `pip3 install pyserial` |
| `This script must run as root` | Not running with sudo | `sudo python3 sshguard.py` |
| `Users config not found` | No users configured | Run `create-user.sh` to add users |
| `Permission denied: /run/sshguard/` | Runtime directory issue | Check systemd RuntimeDirectory setting |
| `Failed to start SSH` | SSH service issue | `sudo systemctl status ssh` |
| `UNAUTHORIZED: Unknown face` | Face not learned in HuskyLens | Teach face to HuskyLens |

### Debug Mode

Run sshguard manually with debug output:

```bash
# Stop the service first
sudo systemctl stop sshguard

# Run manually with debug logging
sudo python3 /opt/sshGuard/sshguard.py --strict 2>&1 | tee /tmp/sshguard-debug.log

# In another terminal, watch the debug output
tail -f /tmp/sshguard-debug.log
```

### Restart Everything

If nothing else works, try a clean restart:

```bash
# Stop all services
sudo systemctl stop sshguard
sudo systemctl stop ssh

# Clear runtime files
sudo rm -rf /run/sshguard/

# Reload systemd
sudo systemctl daemon-reload

# Restart services
sudo systemctl start sshguard
sudo systemctl start ssh

# Check status
sudo systemctl status sshguard ssh
```

---

## Uninstallation

```bash
# Run uninstall script
sudo ./uninstall.sh

# Options:
sudo ./uninstall.sh --keep-users   # Don't remove users from group
sudo ./uninstall.sh --keep-group   # Don't remove the sshGuard group
sudo ./uninstall.sh --purge        # Also remove users.conf and keys

# For Debian package:
sudo dpkg -r sshguard-face    # Remove package
sudo dpkg -P sshguard-face    # Purge (remove config too)
```

The uninstall script:
1. Stops and disables the sshguard service
2. Removes SSH configuration (`sshguard.conf`)
3. Removes PAM rules
4. Cleans up runtime files
5. Optionally removes the `sshGuard` group and user memberships

---

## File Locations

| File | Purpose |
|------|---------|
| `/opt/sshGuard/sshguard.py` | Main daemon script |
| `/opt/sshGuard/create-user.sh` | User creation script |
| `/opt/sshGuard/users.conf` | Face ID to username mappings |
| `/opt/sshGuard/ssh-keys/` | Generated SSH keys |
| `/etc/systemd/system/sshguard.service` | systemd service file |
| `/etc/ssh/sshd_config.d/sshguard.conf` | SSH security configuration |
| `/etc/pam.d/sshd` | PAM configuration (modified) |
| `/run/sshguard/allowed_user` | Runtime file for current allowed user |

---

## License

This project is provided for educational purposes. See the repository for license details.

---

**Questions or issues?** Check the [Debugging and Troubleshooting](#debugging-and-troubleshooting) section or open an issue in the repository.
