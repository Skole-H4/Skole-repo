#!/bin/bash
# =============================================================================
# sshGuard User Creation Script
# =============================================================================
#
# Creates a new user for sshGuard with face ID mapping and SSH key authentication.
#
# Usage:
#   sudo ./create-user.sh --username <name> --faceId <id> [--password <pass>]
#
# Examples:
#   # Create user with SSH key only (recommended)
#   sudo ./create-user.sh --username martin --faceId 1
#
#   # Create user with both SSH key and password
#   sudo ./create-user.sh --username martin --faceId 1 --password secret123
#
# =============================================================================

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Configuration
SSHGUARD_GROUP="sshGuard"
USERS_CONFIG="/opt/sshGuard/users.conf"
SSH_KEYS_DIR="/opt/sshGuard/ssh-keys"

# Parse command line arguments
USERNAME=""
PASSWORD=""
FACE_ID=""

print_usage() {
    echo "Usage: sudo $0 --username <name> --faceId <id> [--password <pass>]"
    echo ""
    echo "Options:"
    echo "  --username    Linux username to create (required)"
    echo "  --faceId      HuskyLens face ID, positive integer (required)"
    echo "  --password    Password for the user (optional, SSH key is always created)"
    echo ""
    echo "Examples:"
    echo "  sudo $0 --username martin --faceId 1"
    echo "  sudo $0 --username martin --faceId 1 --password secret123"
}

while [[ $# -gt 0 ]]; do
    case $1 in
        --username)
            USERNAME="$2"
            shift 2
            ;;
        --password)
            PASSWORD="$2"
            shift 2
            ;;
        --faceId)
            FACE_ID="$2"
            shift 2
            ;;
        -h|--help)
            print_usage
            exit 0
            ;;
        *)
            echo -e "${RED}Error: Unknown option $1${NC}"
            print_usage
            exit 1
            ;;
    esac
done

# Validate required parameters
if [ -z "$USERNAME" ]; then
    echo -e "${RED}Error: --username is required${NC}"
    print_usage
    exit 1
fi

if [ -z "$FACE_ID" ]; then
    echo -e "${RED}Error: --faceId is required${NC}"
    print_usage
    exit 1
fi

# Validate face ID is a positive integer
if ! [[ "$FACE_ID" =~ ^[1-9][0-9]*$ ]]; then
    echo -e "${RED}Error: --faceId must be a positive integer (1, 2, 3, ...)${NC}"
    exit 1
fi

# Check if running as root
if [ "$EUID" -ne 0 ]; then
    echo -e "${RED}Error: Please run as root (sudo $0 ...)${NC}"
    exit 1
fi

echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  sshGuard User Creation${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo "Username: $USERNAME"
echo "Face ID:  $FACE_ID"
echo ""

# Create sshGuard group if it doesn't exist
if ! getent group "$SSHGUARD_GROUP" > /dev/null 2>&1; then
    echo -e "${YELLOW}Creating group '$SSHGUARD_GROUP'...${NC}"
    groupadd "$SSHGUARD_GROUP"
    echo -e "${GREEN}✓${NC} Group '$SSHGUARD_GROUP' created"
else
    echo -e "${GREEN}✓${NC} Group '$SSHGUARD_GROUP' already exists"
fi

# Check if user already exists
if id "$USERNAME" > /dev/null 2>&1; then
    echo -e "${YELLOW}User '$USERNAME' already exists. Adding to group and updating face mapping...${NC}"
    
    # Add to sshGuard group
    usermod -aG "$SSHGUARD_GROUP" "$USERNAME"
    echo -e "${GREEN}✓${NC} User '$USERNAME' added to group '$SSHGUARD_GROUP'"
else
    # Create user with home directory and bash shell
    echo -e "${YELLOW}Creating user '$USERNAME'...${NC}"
    useradd -m -s /bin/bash -G "$SSHGUARD_GROUP" "$USERNAME"
    echo -e "${GREEN}✓${NC} User '$USERNAME' created"
fi

# Set password if provided
if [ -n "$PASSWORD" ]; then
    echo -e "${YELLOW}Setting password...${NC}"
    echo "$USERNAME:$PASSWORD" | chpasswd
    echo -e "${GREEN}✓${NC} Password set"
else
    # Lock password login (SSH key only)
    passwd -l "$USERNAME" > /dev/null 2>&1
    echo -e "${GREEN}✓${NC} Password login disabled (SSH key only)"
fi

# Ensure user can log in via SSH (not in DenyUsers, shell is valid)
# Check if user's shell is in /etc/shells
USER_SHELL=$(getent passwd "$USERNAME" | cut -d: -f7)
if ! grep -q "^$USER_SHELL$" /etc/shells 2>/dev/null; then
    echo -e "${YELLOW}Warning: User's shell '$USER_SHELL' may not allow SSH login${NC}"
fi

# =============================================================================
# SSH KEY GENERATION
# =============================================================================

# Create SSH keys directory with restricted permissions
mkdir -p "$SSH_KEYS_DIR"
chmod 700 "$SSH_KEYS_DIR"

# Get user's home directory
USER_HOME=$(getent passwd "$USERNAME" | cut -d: -f6)
USER_SSH_DIR="$USER_HOME/.ssh"

# Create user's .ssh directory
mkdir -p "$USER_SSH_DIR"
chmod 700 "$USER_SSH_DIR"
chown "$USERNAME:$USERNAME" "$USER_SSH_DIR"

# Generate SSH keypair
KEY_FILE="$SSH_KEYS_DIR/${USERNAME}_sshguard"
PPK_FILE="${KEY_FILE}.ppk"
if [ -f "$KEY_FILE" ]; then
    echo -e "${YELLOW}SSH key already exists for '$USERNAME'. Keeping existing key.${NC}"
    echo -e "${GREEN}✓${NC} Existing SSH key: $KEY_FILE"
else
    echo -e "${YELLOW}Generating SSH keypair...${NC}"
    ssh-keygen -t ed25519 -C "sshguard-${USERNAME}@$(hostname)" -f "$KEY_FILE" -N "" -q
    chmod 600 "$KEY_FILE"
    chmod 644 "${KEY_FILE}.pub"
    echo -e "${GREEN}✓${NC} SSH keypair generated (OpenSSH format)"
fi

# Generate PuTTY .ppk key if puttygen is available
if command -v puttygen &> /dev/null; then
    if [ -f "$PPK_FILE" ]; then
        echo -e "${GREEN}✓${NC} PuTTY key already exists: $PPK_FILE"
    else
        echo -e "${YELLOW}Converting to PuTTY format...${NC}"
        puttygen "$KEY_FILE" -o "$PPK_FILE" -O private
        chmod 600 "$PPK_FILE"
        echo -e "${GREEN}✓${NC} PuTTY key generated: $PPK_FILE"
    fi
else
    echo -e "${YELLOW}Note: Install putty-tools for PuTTY .ppk format:${NC}"
    echo -e "      sudo apt install putty-tools"
fi

# Add public key to user's authorized_keys
AUTHORIZED_KEYS="$USER_SSH_DIR/authorized_keys"
PUBLIC_KEY=$(cat "${KEY_FILE}.pub")

# Check if key already in authorized_keys
if [ -f "$AUTHORIZED_KEYS" ] && grep -qF "$PUBLIC_KEY" "$AUTHORIZED_KEYS" 2>/dev/null; then
    echo -e "${GREEN}✓${NC} Public key already in authorized_keys"
else
    echo "$PUBLIC_KEY" >> "$AUTHORIZED_KEYS"
    echo -e "${GREEN}✓${NC} Public key added to authorized_keys"
fi

# Set proper permissions on authorized_keys
chmod 600 "$AUTHORIZED_KEYS"
chown "$USERNAME:$USERNAME" "$AUTHORIZED_KEYS"

# Create users.conf directory if needed
mkdir -p "$(dirname "$USERS_CONFIG")"

# Create users.conf if it doesn't exist
if [ ! -f "$USERS_CONFIG" ]; then
    echo -e "${YELLOW}Creating face mapping config: $USERS_CONFIG${NC}"
    cat > "$USERS_CONFIG" << 'EOF'
# sshGuard Face ID Mappings
# =========================
# Maps HuskyLens face IDs to Linux usernames.
# Format: face_id=username
#
# The face_id corresponds to the ID shown on HuskyLens when a face is learned.
# Learn faces on HuskyLens first, note the ID, then add the mapping here.
#
# Example:
#   1=martin
#   2=john
#   3=admin
#
EOF
    echo -e "${GREEN}✓${NC} Created $USERS_CONFIG"
fi

# Check if face ID is already mapped
if grep -q "^${FACE_ID}=" "$USERS_CONFIG" 2>/dev/null; then
    EXISTING_USER=$(grep "^${FACE_ID}=" "$USERS_CONFIG" | cut -d= -f2)
    if [ "$EXISTING_USER" != "$USERNAME" ]; then
        echo -e "${YELLOW}Warning: Face ID $FACE_ID was mapped to '$EXISTING_USER'. Updating to '$USERNAME'...${NC}"
        # Remove old mapping
        sed -i "/^${FACE_ID}=/d" "$USERS_CONFIG"
    else
        echo -e "${GREEN}✓${NC} Face ID $FACE_ID already mapped to '$USERNAME'"
    fi
fi

# Add face mapping if not already present
if ! grep -q "^${FACE_ID}=${USERNAME}$" "$USERS_CONFIG" 2>/dev/null; then
    echo "${FACE_ID}=${USERNAME}" >> "$USERS_CONFIG"
    echo -e "${GREEN}✓${NC} Face ID $FACE_ID mapped to '$USERNAME'"
fi

# Show current mappings
echo ""
echo -e "${YELLOW}Current face mappings:${NC}"
grep -v "^#" "$USERS_CONFIG" | grep -v "^$" | while read -r line; do
    echo "  $line"
done

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  User '$USERNAME' ready for sshGuard${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo -e "${YELLOW}SSH Keys:${NC}"
echo "  OpenSSH:  $KEY_FILE"
if [ -f "$PPK_FILE" ]; then
    echo "  PuTTY:    $PPK_FILE"
fi
echo ""
echo -e "${YELLOW}For Linux/Mac/Windows Terminal:${NC}"
echo "  1. Copy the private key to your computer:"
echo "     scp root@$(hostname -I | awk '{print $1}'):$KEY_FILE ~/.ssh/${USERNAME}_sshguard"
echo ""
echo "  2. Set permissions on client:"
echo "     chmod 600 ~/.ssh/${USERNAME}_sshguard"
echo ""
echo "  3. Connect with:"
echo "     ssh -i ~/.ssh/${USERNAME}_sshguard $USERNAME@$(hostname -I | awk '{print $1}')"
echo ""
if [ -f "$PPK_FILE" ]; then
    echo -e "${YELLOW}For PuTTY (Windows):${NC}"
    echo "  1. Copy the .ppk key to your computer:"
    echo "     scp root@$(hostname -I | awk '{print $1}'):$PPK_FILE ~/Desktop/${USERNAME}_sshguard.ppk"
    echo ""
    echo "  2. In PuTTY: Connection > SSH > Auth > Credentials"
    echo "     Browse to: ${USERNAME}_sshguard.ppk"
    echo ""
fi
echo -e "${YELLOW}Or add to ~/.ssh/config:${NC}"
echo "     Host pi-sshguard"
echo "         HostName $(hostname -I | awk '{print $1}')"
echo "         User $USERNAME"
echo "         IdentityFile ~/.ssh/${USERNAME}_sshguard"
echo ""
echo "Next steps:"
echo "  1. Ensure face ID $FACE_ID is learned on HuskyLens"
echo "  2. Restart sshGuard: sudo systemctl restart sshguard"
echo "  3. Copy the private key to your client machine"
echo ""
echo -e "${YELLOW}Tip:${NC} View all sshGuard users with:"
echo "  getent group $SSHGUARD_GROUP"
