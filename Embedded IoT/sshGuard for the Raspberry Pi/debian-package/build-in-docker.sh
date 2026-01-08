#!/bin/bash
# build-in-docker.sh - Build script that runs inside Docker
set -e

cd /build

# Convert CRLF to LF for all text files in the package
find sshguard-face_1.0.0_all -type f -exec sed -i 's/\r$//' {} \;

# Fix directory permissions
chmod 755 sshguard-face_1.0.0_all/DEBIAN

# Fix file permissions
chmod 755 sshguard-face_1.0.0_all/DEBIAN/postinst
chmod 755 sshguard-face_1.0.0_all/DEBIAN/prerm
chmod 755 sshguard-face_1.0.0_all/DEBIAN/postrm
chmod 644 sshguard-face_1.0.0_all/DEBIAN/control
chmod 644 sshguard-face_1.0.0_all/DEBIAN/conffiles
chmod 755 sshguard-face_1.0.0_all/opt/sshGuard/*.py
chmod 755 sshguard-face_1.0.0_all/opt/sshGuard/*.sh
chmod 644 sshguard-face_1.0.0_all/etc/systemd/system/*.service

# Build the package
dpkg-deb --build sshguard-face_1.0.0_all

echo "Package built successfully!"
