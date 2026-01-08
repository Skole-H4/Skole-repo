#!/bin/bash
# =============================================================================
# sshGuard Installation Script
# =============================================================================
# 
# This script installs sshGuard as a systemd service on a Raspberry Pi.
# 
# Usage:
#   chmod +x install.sh
#   sudo ./install.sh
#
# After installation:
#   sudo systemctl status sshguard      # Check status
#   sudo journalctl -u sshguard -f      # View live logs
#   sudo systemctl stop sshguard        # Stop service
#   sudo systemctl start sshguard       # Start service
#   sudo systemctl disable sshguard     # Disable auto-start
#
# Security:
#   - Only users in the 'sshGuard' group can SSH in
#   - During face detection, only the detected user can log in
#   - Root cannot SSH directly (use 'sudo su' after logging in)
# =============================================================================

set -e  # Exit on error

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Configuration
INSTALL_DIR="/opt/sshGuard"
SERVICE_FILE="/etc/systemd/system/sshguard.service"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
SSHGUARD_GROUP="sshGuard"
ALLOWED_USER_FILE="/run/sshguard/allowed_user"

echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  sshGuard Installation${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""

# Check if running as root
if [ "$EUID" -ne 0 ]; then
    echo -e "${RED}Error: Please run as root (sudo ./install.sh)${NC}"
    exit 1
fi

# Check for Python 3
if ! command -v python3 &> /dev/null; then
    echo -e "${RED}Error: Python 3 is not installed${NC}"
    exit 1
fi
echo -e "${GREEN}✓${NC} Python 3 found: $(python3 --version)"

# Check for pyserial
if ! python3 -c "import serial" &> /dev/null; then
    echo -e "${YELLOW}Installing pyserial...${NC}"
    pip3 install pyserial
fi
echo -e "${GREEN}✓${NC} pyserial installed"

# Check for required files
if [ ! -f "$SCRIPT_DIR/sshguard.py" ]; then
    echo -e "${RED}Error: sshguard.py not found in $SCRIPT_DIR${NC}"
    exit 1
fi

if [ ! -f "$SCRIPT_DIR/sshguard.service" ]; then
    echo -e "${RED}Error: sshguard.service not found in $SCRIPT_DIR${NC}"
    exit 1
fi

# Create installation directory
echo -e "${YELLOW}Creating installation directory: $INSTALL_DIR${NC}"
mkdir -p "$INSTALL_DIR"

# Copy files
echo -e "${YELLOW}Copying files...${NC}"
cp "$SCRIPT_DIR/sshguard.py" "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/sshguard.py"
echo -e "${GREEN}✓${NC} Copied sshguard.py"

# Copy user creation script
if [ -f "$SCRIPT_DIR/create-user.sh" ]; then
    cp "$SCRIPT_DIR/create-user.sh" "$INSTALL_DIR/"
    chmod +x "$INSTALL_DIR/create-user.sh"
    echo -e "${GREEN}✓${NC} Copied create-user.sh"
fi

# =============================================================================
# SSH SECURITY CONFIGURATION
# =============================================================================
echo ""
echo -e "${YELLOW}Configuring SSH security...${NC}"

# Create sshGuard group if it doesn't exist
if ! getent group "$SSHGUARD_GROUP" > /dev/null 2>&1; then
    groupadd "$SSHGUARD_GROUP"
    echo -e "${GREEN}✓${NC} Created group '$SSHGUARD_GROUP'"
else
    echo -e "${GREEN}✓${NC} Group '$SSHGUARD_GROUP' already exists"
fi

# Create runtime directory for allowed user file
mkdir -p "$(dirname "$ALLOWED_USER_FILE")"
chmod 755 "$(dirname "$ALLOWED_USER_FILE")"
echo -e "${GREEN}✓${NC} Created runtime directory"

# Configure SSH to only allow sshGuard group
SSHD_CONFIG="/etc/ssh/sshd_config"
SSHD_CONFIG_DIR="/etc/ssh/sshd_config.d"
SSHGUARD_SSH_CONFIG="$SSHD_CONFIG_DIR/sshguard.conf"

# Use sshd_config.d if available (preferred), otherwise modify main config
if [ -d "$SSHD_CONFIG_DIR" ]; then
    # Create drop-in config file
    cat > "$SSHGUARD_SSH_CONFIG" << 'EOF'
# sshGuard SSH Security Configuration
# Only allow users in the sshGuard group to log in via SSH
# This prevents root and other users from direct SSH access
AllowGroups sshGuard

# Recommended security settings
PermitRootLogin no
PasswordAuthentication no
PubkeyAuthentication yes
AuthenticationMethods publickey
EOF
    chmod 644 "$SSHGUARD_SSH_CONFIG"
    echo -e "${GREEN}✓${NC} Created SSH config: $SSHGUARD_SSH_CONFIG"
else
    # Modify main sshd_config
    if ! grep -q "^AllowGroups.*sshGuard" "$SSHD_CONFIG" 2>/dev/null; then
        # Backup original config
        cp "$SSHD_CONFIG" "$SSHD_CONFIG.bak.$(date +%Y%m%d%H%M%S)"
        
        # Add AllowGroups directive
        echo "" >> "$SSHD_CONFIG"
        echo "# sshGuard: Only allow sshGuard group members" >> "$SSHD_CONFIG"
        echo "AllowGroups sshGuard" >> "$SSHD_CONFIG"
        echo -e "${GREEN}✓${NC} Added AllowGroups to $SSHD_CONFIG"
    else
        echo -e "${GREEN}✓${NC} AllowGroups already configured in $SSHD_CONFIG"
    fi
    
    # Disable root login if not already
    if grep -q "^PermitRootLogin yes" "$SSHD_CONFIG" 2>/dev/null; then
        sed -i 's/^PermitRootLogin yes/PermitRootLogin no/' "$SSHD_CONFIG"
        echo -e "${GREEN}✓${NC} Disabled root SSH login"
    fi
fi

# Configure PAM for dynamic user restriction
# Using 'account' phase instead of 'auth' so it works with SSH key authentication too
PAM_SSH="/etc/pam.d/sshd"
PAM_SSHGUARD_LINE="account required pam_listfile.so onerr=succeed item=user sense=allow file=$ALLOWED_USER_FILE"

if [ -f "$PAM_SSH" ]; then
    # Remove any old auth-based sshguard rules (from previous installs)
    if grep -qF "pam_listfile.so" "$PAM_SSH" 2>/dev/null && grep -qF "$ALLOWED_USER_FILE" "$PAM_SSH" 2>/dev/null; then
        sed -i "\|$ALLOWED_USER_FILE|d" "$PAM_SSH"
        # Also remove the comment line if present
        sed -i '/# sshGuard: Only allow the detected user/d' "$PAM_SSH"
        echo -e "${GREEN}✓${NC} Removed old sshGuard PAM rule"
    fi
    
    # Backup PAM config
    cp "$PAM_SSH" "$PAM_SSH.bak.$(date +%Y%m%d%H%M%S)"
    
    # Add sshGuard PAM rule in the account phase (after @include common-account)
    # Using onerr=succeed means if the file doesn't exist, authentication continues normally
    # This allows SSH to work when sshguard service is stopped
    if grep -q "^@include common-account" "$PAM_SSH" 2>/dev/null; then
        # Insert after @include common-account
        sed -i '/@include common-account/a # sshGuard: Only allow the detected user during access window\naccount required pam_listfile.so onerr=succeed item=user sense=allow file='"$ALLOWED_USER_FILE" "$PAM_SSH"
        echo -e "${GREEN}✓${NC} Configured PAM for dynamic user restriction (account phase)"
    else
        # Fallback: add after first account line or at the end
        if grep -q "^account" "$PAM_SSH" 2>/dev/null; then
            # Insert after first account line
            sed -i '0,/^account/{s/^account.*$/&\n# sshGuard: Only allow the detected user during access window\naccount required pam_listfile.so onerr=succeed item=user sense=allow file='"$ALLOWED_USER_FILE"'/}' "$PAM_SSH"
        else
            # Append at end
            echo "" >> "$PAM_SSH"
            echo "# sshGuard: Only allow the detected user during access window" >> "$PAM_SSH"
            echo "$PAM_SSHGUARD_LINE" >> "$PAM_SSH"
        fi
        echo -e "${GREEN}✓${NC} Configured PAM for dynamic user restriction"
    fi
    chmod 644 "$PAM_SSH"
else
    echo -e "${YELLOW}Warning: $PAM_SSH not found, skipping PAM configuration${NC}"
fi

# Restart SSH to apply configuration changes
if systemctl is-active --quiet ssh 2>/dev/null || systemctl is-active --quiet sshd 2>/dev/null; then
    echo -e "${YELLOW}Restarting SSH to apply security configuration...${NC}"
    systemctl restart ssh 2>/dev/null || systemctl restart sshd 2>/dev/null || true
    echo -e "${GREEN}✓${NC} SSH service restarted"
fi

# Install systemd service
echo -e "${YELLOW}Installing systemd service...${NC}"
cp "$SCRIPT_DIR/sshguard.service" "$SERVICE_FILE"
echo -e "${GREEN}✓${NC} Copied sshguard.service"

# Reload systemd
systemctl daemon-reload
echo -e "${GREEN}✓${NC} Systemd daemon reloaded"

# Enable service (start on boot)
systemctl enable sshguard
echo -e "${GREEN}✓${NC} Service enabled (will start on boot)"

# Start service
echo ""
echo -e "${YELLOW}Starting sshGuard service...${NC}"
systemctl start sshguard

# Check status
sleep 2
if systemctl is-active --quiet sshguard; then
    echo -e "${GREEN}✓${NC} sshGuard is running!"
else
    echo -e "${RED}✗${NC} sshGuard failed to start. Check logs:"
    echo -e "  ${YELLOW}sudo journalctl -u sshguard -n 20${NC}"
    exit 1
fi

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  Installation Complete!${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo "Useful commands:"
echo -e "  ${YELLOW}sudo systemctl status sshguard${NC}     - Check service status"
echo -e "  ${YELLOW}sudo journalctl -u sshguard -f${NC}     - View live logs"
echo -e "  ${YELLOW}sudo systemctl restart sshguard${NC}   - Restart service"
echo -e "  ${YELLOW}sudo systemctl stop sshguard${NC}      - Stop service"
echo ""
echo "Configuration files:"
echo "  Main script:    $INSTALL_DIR/sshguard.py"
echo "  Service file:   $SERVICE_FILE"
echo "  User mappings:  $INSTALL_DIR/users.conf"
echo "  SSH config:     $SSHGUARD_SSH_CONFIG"
echo ""
echo -e "${YELLOW}Security:${NC}"
echo "  • Only users in '$SSHGUARD_GROUP' group can SSH in"
echo "  • Root cannot SSH directly (use 'sudo su' after login)"
echo "  • During face detection, only the detected user can log in"
echo ""
echo -e "${GREEN}Tip:${NC} Add authorized users with:"
echo -e "  ${YELLOW}sudo $INSTALL_DIR/create-user.sh --username <name> --faceId <id>${NC}"
echo ""
echo -e "${YELLOW}Important:${NC} Create at least one user before SSH will work:"
echo -e "  ${YELLOW}sudo $INSTALL_DIR/create-user.sh --username yourname --faceId 1${NC}"
