# sshGuard Debian Package

This directory contains everything needed to build a `.deb` package for sshGuard.

## Building the Package

**On a Debian/Ubuntu/Raspberry Pi system:**

```bash
cd debian-package
chmod +x build-deb.sh
./build-deb.sh
```

This creates `sshguard-face_1.0.0_all.deb`.

## Installing

```bash
# Install the package
sudo dpkg -i sshguard-face_1.0.0_all.deb

# Install any missing dependencies
sudo apt-get install -f
```

## Post-Installation Steps

1. **Create users:**
   ```bash
   sudo /opt/sshGuard/create-user.sh martin
   ```

2. **Learn faces on HuskyLens:**
   - Point camera at face
   - Long-press the learning button until it saves

3. **Map faces to users:**
   ```bash
   # Find face IDs
   python3 /opt/sshGuard/huskylens_reader.py
   
   # Add mapping
   echo "1=martin" | sudo tee -a /opt/sshGuard/users.conf
   ```

4. **Start the service:**
   ```bash
   sudo systemctl start sshguard
   sudo systemctl status sshguard
   ```

## Removing

```bash
# Remove (keeps users.conf and sshGuard group)
sudo dpkg -r sshguard-face

# Purge (removes everything including group)
sudo dpkg -P sshguard-face
```

## Package Contents

| File | Installed To |
|------|--------------|
| `sshguard.py` | `/opt/sshGuard/sshguard.py` |
| `huskylens_reader.py` | `/opt/sshGuard/huskylens_reader.py` |
| `create-user.sh` | `/opt/sshGuard/create-user.sh` |
| `sshguard.service` | `/etc/systemd/system/sshguard.service` |

## Package Scripts

| Script | When | Purpose |
|--------|------|---------|
| `postinst` | After install | Creates group, SSH config, PAM rule, enables service |
| `prerm` | Before remove | Stops and disables service |
| `postrm` | After remove | Removes SSH/PAM config, optionally purges group |

## Dependencies

- **Required:** `python3`, `python3-serial`, `systemd`
- **Recommended:** `zenity` (GUI notifications), `putty-tools` (key conversion)
