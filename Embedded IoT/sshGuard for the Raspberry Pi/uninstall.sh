#!/bin/bash
# =============================================================================
# sshGuard Uninstallation Script
# =============================================================================
# 
# This script removes sshGuard and restores SSH/PAM to default configuration.
# 
# Usage:
#   sudo ./uninstall.sh [--keep-users] [--keep-group]
#
# Options:
#   --keep-users   Don't remove users from sshGuard group
#   --keep-group   Don't remove the sshGuard group
#   --purge        Also remove user data (users.conf, SSH keys)
#
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
SSHGUARD_GROUP="sshGuard"
SSHGUARD_SSH_CONFIG="/etc/ssh/sshd_config.d/sshguard.conf"
PAM_SSH="/etc/pam.d/sshd"
ALLOWED_USER_FILE="/run/sshguard/allowed_user"

# Parse arguments
KEEP_USERS=false
KEEP_GROUP=false
PURGE=false

for arg in "$@"; do
    case $arg in
        --keep-users)
            KEEP_USERS=true
            ;;
        --keep-group)
            KEEP_GROUP=true
            ;;
        --purge)
            PURGE=true
            ;;
        --help|-h)
            echo "Usage: sudo ./uninstall.sh [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --keep-users   Don't remove users from sshGuard group"
            echo "  --keep-group   Don't remove the sshGuard group"
            echo "  --purge        Also remove user data (users.conf)"
            echo "  --help, -h     Show this help message"
            exit 0
            ;;
        *)
            echo -e "${RED}Unknown option: $arg${NC}"
            echo "Use --help for usage information"
            exit 1
            ;;
    esac
done

echo -e "${RED}========================================${NC}"
echo -e "${RED}  sshGuard Uninstallation${NC}"
echo -e "${RED}========================================${NC}"
echo ""

# Check if running as root
if [ "$EUID" -ne 0 ]; then
    echo -e "${RED}Error: Please run as root (sudo ./uninstall.sh)${NC}"
    exit 1
fi

# Confirmation
echo -e "${YELLOW}This will remove sshGuard and restore SSH/PAM defaults.${NC}"
echo ""
echo "The following will be removed:"
echo "  • sshguard systemd service"
echo "  • SSH config: $SSHGUARD_SSH_CONFIG"
echo "  • PAM sshGuard rule"
if [ "$PURGE" = true ]; then
    echo "  • Installation directory: $INSTALL_DIR (including users.conf)"
else
    echo "  • Installation directory: $INSTALL_DIR (users.conf preserved)"
fi
if [ "$KEEP_GROUP" = false ]; then
    echo "  • Group: $SSHGUARD_GROUP"
fi
echo ""
read -p "Continue? [y/N] " -n 1 -r
echo ""
if [[ ! $REPLY =~ ^[Yy]$ ]]; then
    echo "Aborted."
    exit 0
fi

echo ""

# =============================================================================
# STOP AND REMOVE SERVICE
# =============================================================================

echo -e "${YELLOW}Stopping sshGuard service...${NC}"
if systemctl is-active --quiet sshguard 2>/dev/null; then
    systemctl stop sshguard
    echo -e "${GREEN}✓${NC} Service stopped"
else
    echo -e "${GREEN}✓${NC} Service was not running"
fi

if systemctl is-enabled --quiet sshguard 2>/dev/null; then
    systemctl disable sshguard
    echo -e "${GREEN}✓${NC} Service disabled"
fi

if [ -f "$SERVICE_FILE" ]; then
    rm -f "$SERVICE_FILE"
    systemctl daemon-reload
    echo -e "${GREEN}✓${NC} Removed service file"
else
    echo -e "${GREEN}✓${NC} Service file already removed"
fi

# =============================================================================
# RESTORE SSH CONFIGURATION
# =============================================================================

echo ""
echo -e "${YELLOW}Restoring SSH configuration...${NC}"

# Remove drop-in config
if [ -f "$SSHGUARD_SSH_CONFIG" ]; then
    rm -f "$SSHGUARD_SSH_CONFIG"
    echo -e "${GREEN}✓${NC} Removed $SSHGUARD_SSH_CONFIG"
    echo "    (AllowGroups, PermitRootLogin, PasswordAuthentication restored to defaults)"
else
    echo -e "${GREEN}✓${NC} SSH config already removed"
fi

# Check main sshd_config for any sshGuard additions
SSHD_CONFIG="/etc/ssh/sshd_config"
if grep -q "# sshGuard" "$SSHD_CONFIG" 2>/dev/null; then
    # Remove sshGuard lines from main config
    sed -i '/# sshGuard/d' "$SSHD_CONFIG"
    sed -i '/^AllowGroups sshGuard$/d' "$SSHD_CONFIG"
    echo -e "${GREEN}✓${NC} Cleaned sshGuard entries from $SSHD_CONFIG"
fi

# =============================================================================
# RESTORE PAM CONFIGURATION
# =============================================================================

echo ""
echo -e "${YELLOW}Restoring PAM configuration...${NC}"

if [ -f "$PAM_SSH" ]; then
    # Remove sshGuard PAM rules (both old 'auth' and new 'account' versions)
    if grep -qF "sshguard" "$PAM_SSH" 2>/dev/null || grep -qF "$ALLOWED_USER_FILE" "$PAM_SSH" 2>/dev/null; then
        # Remove the comment line
        sed -i '/# sshGuard/d' "$PAM_SSH"
        # Remove the pam_listfile rule for allowed_user
        sed -i "\|$ALLOWED_USER_FILE|d" "$PAM_SSH"
        # Remove any empty lines we might have left
        sed -i '/^$/N;/^\n$/d' "$PAM_SSH"
        echo -e "${GREEN}✓${NC} Removed sshGuard PAM rule"
    else
        echo -e "${GREEN}✓${NC} PAM already clean"
    fi
else
    echo -e "${YELLOW}Warning: $PAM_SSH not found${NC}"
fi

# =============================================================================
# REMOVE RUNTIME DIRECTORY
# =============================================================================

echo ""
echo -e "${YELLOW}Cleaning up runtime files...${NC}"

if [ -d "/run/sshguard" ]; then
    rm -rf "/run/sshguard"
    echo -e "${GREEN}✓${NC} Removed /run/sshguard"
else
    echo -e "${GREEN}✓${NC} Runtime directory already clean"
fi

# =============================================================================
# HANDLE GROUP AND USERS
# =============================================================================

echo ""
echo -e "${YELLOW}Handling group and users...${NC}"

if getent group "$SSHGUARD_GROUP" > /dev/null 2>&1; then
    if [ "$KEEP_USERS" = false ]; then
        # Get list of users in the group
        USERS_IN_GROUP=$(getent group "$SSHGUARD_GROUP" | cut -d: -f4 | tr ',' ' ')
        if [ -n "$USERS_IN_GROUP" ]; then
            echo "Removing users from $SSHGUARD_GROUP group: $USERS_IN_GROUP"
            for user in $USERS_IN_GROUP; do
                gpasswd -d "$user" "$SSHGUARD_GROUP" 2>/dev/null || true
                echo -e "${GREEN}✓${NC} Removed $user from $SSHGUARD_GROUP"
            done
        fi
    else
        echo -e "${YELLOW}!${NC} Keeping users in $SSHGUARD_GROUP group (--keep-users)"
    fi
    
    if [ "$KEEP_GROUP" = false ]; then
        groupdel "$SSHGUARD_GROUP" 2>/dev/null || true
        echo -e "${GREEN}✓${NC} Removed group: $SSHGUARD_GROUP"
    else
        echo -e "${YELLOW}!${NC} Keeping group: $SSHGUARD_GROUP (--keep-group)"
    fi
else
    echo -e "${GREEN}✓${NC} Group $SSHGUARD_GROUP doesn't exist"
fi

# =============================================================================
# REMOVE INSTALLATION DIRECTORY
# =============================================================================

echo ""
echo -e "${YELLOW}Removing installation files...${NC}"

if [ -d "$INSTALL_DIR" ]; then
    if [ "$PURGE" = true ]; then
        rm -rf "$INSTALL_DIR"
        echo -e "${GREEN}✓${NC} Removed $INSTALL_DIR (including all data)"
    else
        # Keep users.conf if it exists
        if [ -f "$INSTALL_DIR/users.conf" ]; then
            # Remove everything except users.conf
            find "$INSTALL_DIR" -type f ! -name "users.conf" -delete
            find "$INSTALL_DIR" -type d -empty -delete 2>/dev/null || true
            echo -e "${GREEN}✓${NC} Removed scripts (kept users.conf)"
            echo -e "${YELLOW}Note:${NC} User mappings preserved at $INSTALL_DIR/users.conf"
        else
            rm -rf "$INSTALL_DIR"
            echo -e "${GREEN}✓${NC} Removed $INSTALL_DIR"
        fi
    fi
else
    echo -e "${GREEN}✓${NC} Installation directory already removed"
fi

# =============================================================================
# RESTART SSH
# =============================================================================

echo ""
echo -e "${YELLOW}Restarting SSH service...${NC}"

if systemctl is-active --quiet ssh 2>/dev/null; then
    systemctl restart ssh
    echo -e "${GREEN}✓${NC} SSH service restarted"
elif systemctl is-active --quiet sshd 2>/dev/null; then
    systemctl restart sshd
    echo -e "${GREEN}✓${NC} SSHD service restarted"
else
    echo -e "${YELLOW}!${NC} SSH service not running (start manually if needed)"
fi

# =============================================================================
# DONE
# =============================================================================

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  Uninstallation Complete!${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo "SSH has been restored to default settings:"
echo "  • AllowGroups restriction removed"
echo "  • PAM user restriction removed"
echo "  • All users can SSH again (if they have accounts)"
echo ""

if [ "$KEEP_GROUP" = true ] || [ "$KEEP_USERS" = true ]; then
    echo -e "${YELLOW}Note:${NC} Some items were preserved:"
    [ "$KEEP_GROUP" = true ] && echo "  • Group '$SSHGUARD_GROUP' still exists"
    [ "$KEEP_USERS" = true ] && echo "  • Users are still in '$SSHGUARD_GROUP' group"
    echo ""
fi

if [ "$PURGE" = false ] && [ -f "$INSTALL_DIR/users.conf" ]; then
    echo -e "${YELLOW}Note:${NC} User mappings preserved at $INSTALL_DIR/users.conf"
    echo "      Use --purge to remove all data on next uninstall"
    echo ""
fi

echo -e "${GREEN}SSH is now open to all users with valid credentials.${NC}"
