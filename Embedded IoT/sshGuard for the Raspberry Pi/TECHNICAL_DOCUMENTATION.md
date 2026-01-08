# sshGuard - Technical Documentation

## Overview

**sshGuard** is a face-recognition-based SSH access control system for Raspberry Pi. It uses a HuskyLens camera module to detect authorized users and temporarily enables SSH access only when a recognized face is detected.

---

## System Architecture

```
┌─────────────────┐     UART      ┌──────────────────┐
│   HuskyLens     │──────────────▶│   Raspberry Pi   │
│  (Face Recog)   │  /dev/serial0 │                  │
└─────────────────┘               │  ┌────────────┐  │
                                  │  │ sshguard.py│  │
                                  │  └─────┬──────┘  │
                                  │        │         │
                                  │        ▼         │
                                  │  ┌────────────┐  │
                                  │  │ systemctl  │  │
                                  │  │ start/stop │  │
                                  │  │    ssh     │  │
                                  │  └────────────┘  │
                                  └──────────────────┘
```

---

## Core Functionality

### 1. Face Detection via HuskyLens

| Component | Description |
|-----------|-------------|
| **Protocol** | Custom UART protocol at 9600 baud |
| **Port** | `/dev/serial0` (Raspberry Pi GPIO UART) |
| **Frame Format** | `[0x55, 0xAA, ADDR, LENGTH, CMD, DATA..., CHECKSUM]` |
| **Face IDs** | ID=0 means unknown face, ID>0 means learned/authorized face |

**How it works:**
1. sshGuard sends `CMD_REQUEST_BLOCKS` (0x21) to HuskyLens
2. HuskyLens responds with `RET_BLOCK` (0x2A) containing face data
3. Face ID is extracted from bytes 8-9 (little-endian uint16)
4. ID > 0 indicates a learned (authorized) face

### 2. SSH Service Control

| Function | Purpose |
|----------|---------|
| `start_ssh_service()` | Starts SSH via `systemctl start ssh` |
| `stop_ssh_service()` | Stops SSH via `systemctl stop ssh` |
| `is_ssh_service_active()` | Checks if SSH is running |

**Strict Mode (`--strict`):**
- SSH is stopped when sshGuard starts
- SSH only runs during the access window
- SSH is stopped when the window closes
- **Warning:** Disconnects existing SSH sessions

**Non-Strict Mode (default):**
- Doesn't disrupt existing SSH if it's already running
- Only controls SSH start/stop when needed

### 3. Access Window

When an authorized face is detected consistently:

1. **Streak Threshold** (default: 3): Face must be detected N consecutive times
2. **SSH Opens** for `--open-seconds` duration (default: 15 seconds)
3. **Monitoring**: Tracks new SSH connections during window
4. **Cooldown** (default: 20 seconds): Prevents immediate re-trigger

### 4. User-to-Face Mapping

**Config File:** `/opt/sshGuard/users.conf`

```
# Format: face_id=username
1=martin
2=anders
3=admin
```

- Maps HuskyLens face IDs to Linux usernames
- File is hot-reloaded (changes apply without restart)
- Created by `create-user.sh` script

### 5. SSH Group Restriction

**Config File:** `/etc/ssh/sshd_config.d/sshguard.conf`

```
AllowGroups sshGuard
PermitRootLogin no
PasswordAuthentication no
PubkeyAuthentication yes
AuthenticationMethods publickey
```

**Effect:**
- Only users in the `sshGuard` Linux group can SSH in
- Root login is blocked entirely
- Only SSH key authentication allowed (all other methods disabled)
- `AuthenticationMethods publickey` explicitly enforces key-only access
- Local console login is **not affected** (still uses password)

### 6. Per-User SSH Restriction (PAM)

**Runtime File:** `/run/sshguard/allowed_user`

During a face detection window:
1. `set_allowed_ssh_user(username)` writes the username to the file
2. PAM checks this file during SSH authorization (account phase)
3. Only the detected user can log in (not all sshGuard members)
4. `clear_allowed_ssh_user()` removes the file when window closes

**PAM Config** (in `/etc/pam.d/sshd`):
```
# Must use 'account' phase (not 'auth') to work with SSH key authentication
account required pam_listfile.so onerr=succeed item=user sense=allow file=/run/sshguard/allowed_user
```

**Note:** Uses `onerr=succeed` so SSH works normally when sshGuard is stopped (file doesn't exist).

### 7. GUI Notifications (Zenity)

Displays pop-up notifications to logged-in desktop users:

| Event | Notification |
|-------|--------------|
| sshGuard starts | "sshGuard started. Waiting for authorized face…" |
| Face detected | "martin detected – Opening SSH for 15 seconds..." |
| Window closes | "SSH window closed. 1 login(s) recorded." |

**Technical Details:**
- Uses `loginctl` to enumerate active X11/Wayland sessions
- Reads environment from `/proc/{pid}/environ`
- Runs `zenity` as target user via `runuser`

### 8. Connection Monitoring

During the SSH window, tracks new connections:

```python
get_active_ssh_connections()  # Uses 'ss -tn sport = :22'
monitor_ssh_logins_during_window(duration)  # Polls for new connections
```

**Logged Information:**
- Remote IP address
- Remote port
- Connection time
- Number of new logins during window

---

## File Locations

| Path | Purpose |
|------|---------|
| `/opt/sshGuard/sshguard.py` | Main application |
| `/opt/sshGuard/users.conf` | Face ID → username mapping |
| `/opt/sshGuard/install.sh` | Installation script (manual install) |
| `/opt/sshGuard/uninstall.sh` | Uninstallation script (restores defaults) |
| `/opt/sshGuard/create-user.sh` | User creation helper |
| `/opt/sshGuard/huskylens_reader.py` | Diagnostic tool to view detected face IDs |
| `/etc/systemd/system/sshguard.service` | systemd service unit |
| `/etc/ssh/sshd_config.d/sshguard.conf` | SSH security config |
| `/run/sshguard/allowed_user` | Runtime: currently allowed user |
| `debian-package/` | Debian package build files |
| `debian-package/sshguard-face_1.0.0_all.deb` | Built Debian package |

---

## Command Line Options

```bash
sudo python3 sshguard.py [OPTIONS]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--port` | `/dev/serial0` | HuskyLens UART port |
| `--baud` | `9600` | UART baud rate |
| `--open-seconds`, `-s` | `15` | SSH window duration (seconds) |
| `--streak` | `3` | Consecutive detections required |
| `--cooldown` | `20` | Seconds before re-trigger allowed |
| `--strict` | `false` | Enforce SSH closed outside windows |

---

## Security Layers

```
Layer 1: SSH Service Control
├── SSH only runs during face detection windows (strict mode)
└── No SSH daemon = no remote access possible

Layer 2: AllowGroups sshGuard
├── Only members of 'sshGuard' Linux group can SSH
└── Non-members get "Permission denied"

Layer 3: SSH Key Only (PasswordAuthentication no)
├── Password login disabled for SSH (keys required)
└── Local console login unaffected (still uses password)

Layer 4: PAM User Restriction
├── Only the detected user can log in during window
└── Other sshGuard members blocked until their face is detected

Layer 5: PermitRootLogin no
└── Root can never SSH, even if in sshGuard group
```

---

## Diagnostic Tools

### HuskyLens Reader

Test HuskyLens communication and verify face learning:

```bash
python3 /opt/sshGuard/huskylens_reader.py
```

**Output:**
```
[14:32:15] No face detected
[14:32:17] Face detected → LEARNED (ID: 1)
[14:32:22] Face detected → UNKNOWN (not learned)
[14:32:25] Face detected → LEARNED (ID: 2)
```

Use this to:
- Verify HuskyLens is communicating
- Check which face ID is assigned to each person
- Debug face recognition issues

---

## Logging

Logs to systemd journal (viewable with `journalctl -u sshguard`):

| Event Type | Log Prefix | Example |
|------------|------------|---------|
| Face detected | `[AUTHORIZED]` | `Face detected: martin (IDs: [1])` |
| Unknown face | `[UNAUTHORIZED]` | `Unknown face detected (not learned)` |
| SSH login | `[SSH_LOGIN]` | `New SSH connection from 192.168.1.100` |
| Service control | (none) | `Starting SSH service...` |
| Trigger | `TRIGGER:` | `martin detected – Opening SSH...` |
| Access granted | (none) | `SSH access granted to user: martin` |

---

## User Management

### Creating a New User

```bash
sudo /opt/sshGuard/create-user.sh
```

The script:
1. Prompts for username and HuskyLens face ID
2. Creates Linux user (if doesn't exist)
3. Adds user to `sshGuard` group
4. Generates Ed25519 SSH keypair
5. Converts to PuTTY .ppk format (if puttygen available)
6. Adds mapping to `/opt/sshGuard/users.conf`

### Removing a User

```bash
# Remove from sshGuard group (blocks SSH access)
sudo gpasswd -d username sshGuard

# Remove face mapping
sudo sed -i '/=username$/d' /opt/sshGuard/users.conf

# Optionally delete user entirely
sudo userdel -r username
```

---

## Installation

### Option 1: Debian Package (Recommended)

```bash
# Copy .deb to Raspberry Pi
scp sshguard-face_1.0.0_all.deb pi@raspberrypi:~/

# Install on Pi
sudo dpkg -i sshguard-face_1.0.0_all.deb
sudo apt-get install -f  # Install any missing dependencies
```

**Package details:**
| Property | Value |
|----------|-------|
| Package name | `sshguard-face` |
| Dependencies | `python3`, `python3-serial`, `systemd` |
| Recommends | `zenity`, `putty-tools` |

**What the package does:**
1. Installs files to `/opt/sshGuard/`
2. Creates `sshGuard` Linux group
3. Configures SSH (key-only auth, AllowGroups)
4. Configures PAM for per-user restriction
5. Installs systemd service (enabled but not started)

**After install:**
```bash
# Create users
sudo /opt/sshGuard/create-user.sh martin

# Map face IDs (after learning on HuskyLens)
echo "1=martin" | sudo tee -a /opt/sshGuard/users.conf

# Start service
sudo systemctl start sshguard
```

### Option 2: Manual Install Script

```bash
# Clone or copy files to Raspberry Pi
cd /path/to/sshGuard
chmod +x install.sh
sudo ./install.sh
```

The install script:
1. Installs pyserial if needed
2. Copies files to `/opt/sshGuard/`
3. Creates `sshGuard` Linux group
4. Configures SSH (`/etc/ssh/sshd_config.d/sshguard.conf`)
5. Configures PAM (`/etc/pam.d/sshd`)
6. Installs and starts systemd service
7. Restarts SSH to apply changes

### Uninstallation

**Debian package:**
```bash
sudo dpkg -r sshguard-face    # Remove (keeps users.conf, group)
sudo dpkg -P sshguard-face    # Purge (removes everything)
```

**Manual install:**
```bash
sudo /opt/sshGuard/uninstall.sh
```

**Options:**
| Flag | Effect |
|------|--------|
| (none) | Standard uninstall, keeps `users.conf` |
| `--purge` | Remove all data including user mappings |
| `--keep-group` | Don't delete the sshGuard group |
| `--keep-users` | Don't remove users from sshGuard group |

**What gets restored:**
- SSH config removed (`AllowGroups` restriction lifted)
- PAM rule removed (all sshGuard members can log in)
- sshGuard group deleted (unless `--keep-group`)
- SSH restarted to apply changes

---

## Troubleshooting

### SSH Not Blocking Users

```bash
# Check SSH config is loaded
sudo sshd -T | grep allowgroups

# Check PAM rule is in account phase (not auth)
grep pam_listfile /etc/pam.d/sshd

# Verify config file exists and has correct permissions
ls -la /etc/ssh/sshd_config.d/sshguard.conf
# Should be: -rw-r--r-- root root

# Restart SSH
sudo systemctl restart ssh
```

### HuskyLens Not Responding

```bash
# Check serial port exists
ls -la /dev/serial0

# Test serial communication
python3 -c "import serial; s=serial.Serial('/dev/serial0', 9600, timeout=1); print('OK')"

# Use the diagnostic tool
python3 /opt/sshGuard/huskylens_reader.py

# Check UART is enabled in config
grep -E "enable_uart|dtoverlay=disable-bt" /boot/config.txt
```

### Service Won't Start

```bash
# Check service status
sudo systemctl status sshguard

# View logs
sudo journalctl -u sshguard -n 50

# Test manually
sudo python3 /opt/sshGuard/sshguard.py --strict
```

### Wrong User Can Log In (SSH Keys)

If users with SSH keys can log in when someone else's face is detected:

```bash
# PAM rule must be in 'account' phase, NOT 'auth'
# Auth phase is skipped for SSH key authentication!

# Check current rule:
grep pam_listfile /etc/pam.d/sshd

# Should show:
# account required pam_listfile.so onerr=succeed item=user sense=allow file=/run/sshguard/allowed_user

# If it shows 'auth' instead of 'account', fix it:
sudo sed -i 's/^auth.*pam_listfile.*allowed_user/account required pam_listfile.so onerr=succeed item=user sense=allow file=\/run\/sshguard\/allowed_user/' /etc/pam.d/sshd
```

### Failed to Set Allowed User (Read-only filesystem)

If logs show `[ERROR] Failed to set allowed SSH user: Read-only file system`:

```bash
# The systemd service needs RuntimeDirectory
# Check the service file has:
grep RuntimeDirectory /etc/systemd/system/sshguard.service

# Should show:
# RuntimeDirectory=sshguard

# If missing, update the service file and reload:
sudo systemctl daemon-reload
sudo systemctl restart sshguard
```

---

## systemd Service Details

The service file (`/etc/systemd/system/sshguard.service`) includes:

| Directive | Value | Purpose |
|-----------|-------|---------|
| `ExecStart` | `/usr/bin/python3 /opt/sshGuard/sshguard.py --strict` | Runs in strict mode by default |
| `Restart=always` | - | Auto-restart on failure |
| `RestartSec=5` | - | Wait 5 seconds before restart |
| `RuntimeDirectory=sshguard` | - | Creates `/run/sshguard/` automatically |
| `ProtectSystem=strict` | - | Read-only filesystem except allowed paths |
| `ReadWritePaths=/var/log` | - | Allow writing to logs |

---

## Dependencies

| Package | Purpose | Install |
|---------|---------|---------|
| Python 3 | Runtime | Pre-installed on Raspberry Pi OS |
| pyserial | UART communication | `pip install pyserial` |
| zenity | GUI notifications | `sudo apt install zenity` (optional) |
| systemd | Service management | Pre-installed |
| puttygen | SSH key conversion | `sudo apt install putty-tools` (optional) |

---

## Security Considerations

1. **sshGuard runs as root** - Required for `systemctl` and `runuser`
2. **Physical access = bypass** - Someone with physical access can disable sshGuard
3. **Face recognition accuracy** - HuskyLens may have false positives/negatives
4. **Network trust** - SSH keys should be protected; use passphrase
5. **Backup access** - Keep physical console access available in case of lockout
6. **PAM phase matters** - Must use `account` phase for SSH key compatibility
