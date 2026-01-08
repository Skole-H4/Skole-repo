#!/bin/bash
# build-deb.sh - Build the Debian package
# Run this on a Debian/Ubuntu/Raspberry Pi system

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PACKAGE_DIR="$SCRIPT_DIR/sshguard-face_1.0.0_all"
SOURCE_DIR="$(dirname "$SCRIPT_DIR")"

echo "Building sshGuard Debian package..."
echo "Source: $SOURCE_DIR"
echo "Package: $PACKAGE_DIR"
echo ""

# Copy application files
echo "Copying application files..."
cp "$SOURCE_DIR/sshguard.py" "$PACKAGE_DIR/opt/sshGuard/"
cp "$SOURCE_DIR/huskylens_reader.py" "$PACKAGE_DIR/opt/sshGuard/"
cp "$SOURCE_DIR/create-user.sh" "$PACKAGE_DIR/opt/sshGuard/"

# Copy systemd service
echo "Copying systemd service..."
cp "$SOURCE_DIR/sshguard.service" "$PACKAGE_DIR/etc/systemd/system/"

# Set permissions for DEBIAN scripts
echo "Setting permissions..."
chmod 755 "$PACKAGE_DIR/DEBIAN/postinst"
chmod 755 "$PACKAGE_DIR/DEBIAN/prerm"
chmod 755 "$PACKAGE_DIR/DEBIAN/postrm"
chmod 644 "$PACKAGE_DIR/DEBIAN/control"
chmod 644 "$PACKAGE_DIR/DEBIAN/conffiles"

# Set permissions for application files
chmod 755 "$PACKAGE_DIR/opt/sshGuard/sshguard.py"
chmod 755 "$PACKAGE_DIR/opt/sshGuard/huskylens_reader.py"
chmod 755 "$PACKAGE_DIR/opt/sshGuard/create-user.sh"
chmod 644 "$PACKAGE_DIR/etc/systemd/system/sshguard.service"

# Build the package
echo "Building .deb package..."
cd "$SCRIPT_DIR"
dpkg-deb --build sshguard-face_1.0.0_all

echo ""
echo "=========================================="
echo "Package built: sshguard-face_1.0.0_all.deb"
echo "=========================================="
echo ""
echo "Install with: sudo dpkg -i sshguard-face_1.0.0_all.deb"
echo "              sudo apt-get install -f  # Install dependencies"
echo ""
echo "Remove with:  sudo dpkg -r sshguard-face"
echo "Purge with:   sudo dpkg -P sshguard-face"
