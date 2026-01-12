#!/usr/bin/env python3
"""
sshGuard - HuskyLens Face Recognition SSH Access Control

This script uses a HuskyLens camera module to detect authorized faces
and temporarily opens SSH access when a recognized face is detected.
It provides visual feedback via Zenity dialogs to GUI users.

Requirements:
    - HuskyLens connected via UART (default: /dev/serial0)
    - pyserial library (pip install pyserial)
    - zenity (optional, for GUI notifications)
    - systemd with ssh.service
    - Root privileges (for systemctl and runuser)

Usage:
    sudo python3 sshguard.py --open-seconds 15 --strict
"""

import os
import sys
import time
import argparse
import subprocess
import logging
from typing import Optional, Dict, List, Set, Tuple
from dataclasses import dataclass
from shutil import which

try:
    import serial
except ImportError:
    print("Error: pyserial is required. Install with: pip install pyserial")
    sys.exit(1)


# =============================================================================
# CONFIGURATION
# =============================================================================

APP_NAME = "sshGuard"

# HuskyLens UART Configuration
DEFAULT_PORT = "/dev/serial0"
DEFAULT_BAUD = 9600
HUSKY_ADDR = 0x11

# HuskyLens Protocol Commands
CMD_REQUEST_BLOCKS = 0x21
RET_BLOCK = 0x2A
RET_BUSY = 0x3D

# Face ID to username mapping config file
USERS_CONFIG_FILE = "/opt/sshGuard/users.conf"

# Runtime file for currently allowed SSH user (checked by PAM)
ALLOWED_USER_FILE = "/run/sshguard/allowed_user"

# Cache for face ID mappings (loaded from config file)
_face_id_cache: Dict[int, str] = {}
_face_id_cache_mtime: float = 0.0


def load_face_id_mappings() -> Dict[int, str]:
    """
    Load face ID to username mappings from config file.
    
    Config file format (one mapping per line):
        face_id=username
        # Comments start with #
    
    Returns:
        Dictionary mapping face IDs to usernames
    """
    global _face_id_cache, _face_id_cache_mtime
    
    # Check if config file exists
    if not os.path.isfile(USERS_CONFIG_FILE):
        if not _face_id_cache:
            logger.warning(f"Users config not found: {USERS_CONFIG_FILE}")
            logger.warning("Run create-user.sh to add authorized users")
        return _face_id_cache
    
    # Check if file was modified (reload if changed)
    try:
        current_mtime = os.path.getmtime(USERS_CONFIG_FILE)
        if current_mtime == _face_id_cache_mtime and _face_id_cache:
            return _face_id_cache  # Use cached version
    except OSError:
        return _face_id_cache
    
    # Parse config file
    mappings: Dict[int, str] = {}
    try:
        with open(USERS_CONFIG_FILE, "r", encoding="utf-8") as f:
            for line_num, line in enumerate(f, 1):
                line = line.strip()
                
                # Skip empty lines and comments
                if not line or line.startswith("#"):
                    continue
                
                # Parse face_id=username
                if "=" not in line:
                    logger.warning(f"{USERS_CONFIG_FILE}:{line_num}: Invalid format (expected face_id=username)")
                    continue
                
                face_id_str, username = line.split("=", 1)
                face_id_str = face_id_str.strip()
                username = username.strip()
                
                try:
                    face_id = int(face_id_str)
                    if face_id <= 0:
                        raise ValueError("Face ID must be positive")
                    mappings[face_id] = username
                except ValueError as e:
                    logger.warning(f"{USERS_CONFIG_FILE}:{line_num}: Invalid face ID '{face_id_str}': {e}")
                    continue
        
        _face_id_cache = mappings
        _face_id_cache_mtime = current_mtime
        
        if mappings:
            logger.info(f"Loaded {len(mappings)} face mapping(s) from {USERS_CONFIG_FILE}")
        else:
            logger.warning(f"No face mappings found in {USERS_CONFIG_FILE}")
            
    except IOError as e:
        logger.error(f"Failed to read {USERS_CONFIG_FILE}: {e}")
    
    return _face_id_cache

# Configure logging (compatible with systemd journal)
logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S"
)
logger = logging.getLogger(__name__)

# Detection event types for logging
EVENT_AUTHORIZED = "AUTHORIZED"
EVENT_UNAUTHORIZED = "UNAUTHORIZED"
EVENT_NO_FACE = "NO_FACE"
EVENT_SSH_LOGIN = "SSH_LOGIN"


# =============================================================================
# DATA CLASSES
# =============================================================================

@dataclass
class GuiSession:
    """Represents an active GUI session for a user."""
    user: str
    uid: int
    leader: int
    env: Dict[str, str]


@dataclass
class Config:
    """Application configuration from command line arguments."""
    port: str
    baud: int
    open_seconds: int
    streak_threshold: int
    cooldown_seconds: int
    strict_mode: bool


# =============================================================================
# UTILITY FUNCTIONS
# =============================================================================

def run_command(cmd: List[str]) -> subprocess.CompletedProcess:
    """
    Execute a shell command and return the result.
    
    Args:
        cmd: Command and arguments as a list
        
    Returns:
        CompletedProcess with stdout, stderr, and returncode
    """
    try:
        return subprocess.run(cmd, text=True, capture_output=True, timeout=30)
    except subprocess.TimeoutExpired:
        logger.warning(f"Command timed out: {' '.join(cmd)}")
        return subprocess.CompletedProcess(cmd, returncode=-1, stdout="", stderr="timeout")
    except Exception as e:
        logger.error(f"Command failed: {' '.join(cmd)} - {e}")
        return subprocess.CompletedProcess(cmd, returncode=-1, stdout="", stderr=str(e))


def is_tool_available(name: str) -> bool:
    """Check if a command-line tool is available in PATH."""
    return which(name) is not None


# =============================================================================
# HUSKYLENS PROTOCOL FUNCTIONS
# =============================================================================

def calculate_checksum(packet: bytes) -> int:
    """
    Calculate HuskyLens protocol checksum.
    
    The checksum is the sum of all bytes in the packet, masked to 8 bits.
    
    Args:
        packet: Packet bytes without the checksum
        
    Returns:
        Single byte checksum value
    """
    return sum(packet) & 0xFF


def make_frame(cmd: int, data: bytes = b"") -> bytes:
    """
    Construct a HuskyLens protocol frame.
    
    Frame format: [0x55, 0xAA, ADDR, LENGTH, CMD, DATA..., CHECKSUM]
    
    Args:
        cmd: Command byte
        data: Optional payload data
        
    Returns:
        Complete frame with header and checksum
    """
    packet = bytes([0x55, 0xAA, HUSKY_ADDR, len(data), cmd]) + data
    return packet + bytes([calculate_checksum(packet)])


def read_frame(ser: serial.Serial) -> Optional[Tuple[int, int, bytes]]:
    """
    Read and parse a HuskyLens response frame.
    
    Synchronizes on the 0x55 0xAA header, then reads the complete frame
    and validates the checksum.
    
    Args:
        ser: Open serial port
        
    Returns:
        Tuple of (address, command, data) or None if read failed/invalid
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
                    break  # Found sync header

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

        packet_without_checksum = b"\x55\xAA" + bytes([addr, length, cmd]) + data
        expected_checksum = calculate_checksum(packet_without_checksum)
        
        if expected_checksum != checksum_byte[0]:
            logger.debug(f"Checksum mismatch: expected {expected_checksum}, got {checksum_byte[0]}")
            return None

        return (addr, cmd, data)
        
    except serial.SerialException as e:
        logger.error(f"Serial read error: {e}")
        return None


def parse_uint16_le(low_byte: int, high_byte: int) -> int:
    """Parse a little-endian 16-bit unsigned integer from two bytes."""
    return low_byte | (high_byte << 8)


# =============================================================================
# SYSTEM INTERACTION FUNCTIONS
# =============================================================================

def read_process_environment(pid: int) -> Dict[str, str]:
    """
    Read environment variables from a process via /proc filesystem.
    
    Args:
        pid: Process ID
        
    Returns:
        Dictionary of environment variable names to values
    """
    env = {}
    try:
        with open(f"/proc/{pid}/environ", "rb") as f:
            raw = f.read()
        
        for item in raw.split(b"\x00"):
            if b"=" in item:
                key, value = item.split(b"=", 1)
                env[key.decode(errors="ignore")] = value.decode(errors="ignore")
                
    except PermissionError:
        logger.debug(f"Permission denied reading environ for PID {pid}")
    except FileNotFoundError:
        logger.debug(f"Process {pid} no longer exists")
    except Exception as e:
        logger.debug(f"Error reading environ for PID {pid}: {e}")
    
    return env


def get_active_gui_sessions() -> List[GuiSession]:
    """
    Enumerate active GUI sessions using loginctl.
    
    Finds all active X11 or Wayland sessions and retrieves their
    environment variables for launching GUI applications.
    
    Returns:
        List of GuiSession objects for active GUI users
    """
    if not is_tool_available("loginctl"):
        logger.debug("loginctl not available")
        return []

    # Get list of sessions
    result = run_command(["loginctl", "list-sessions", "--no-legend"])
    if result.returncode != 0:
        return []

    sessions = []
    seen_users = set()  # Track users to avoid duplicates

    for line in result.stdout.strip().splitlines():
        parts = line.split()
        if not parts:
            continue
        
        session_id = parts[0]
        
        # Get session details
        session_result = run_command([
            "loginctl", "show-session", session_id,
            "-p", "Name", "-p", "User", "-p", "Type", "-p", "Active", "-p", "Leader"
        ])
        
        if session_result.returncode != 0:
            continue

        # Parse session properties
        props = {}
        for prop_line in session_result.stdout.splitlines():
            if "=" in prop_line:
                key, value = prop_line.split("=", 1)
                props[key] = value

        # Filter: only active GUI sessions
        if props.get("Active") != "yes":
            continue
        if props.get("Type") not in ("x11", "wayland"):
            continue

        user = props.get("Name", "")
        if not user or user in seen_users:
            continue
        
        try:
            uid = int(props.get("User", ""))
            leader = int(props.get("Leader", "0"))
        except ValueError:
            continue

        if leader <= 0:
            continue

        # Get environment from session leader process
        env = read_process_environment(leader)
        
        # Set sensible defaults for display variables
        env.setdefault("DISPLAY", ":0")
        env.setdefault("XDG_RUNTIME_DIR", f"/run/user/{uid}")

        sessions.append(GuiSession(user=user, uid=uid, leader=leader, env=env))
        seen_users.add(user)

    return sessions


# =============================================================================
# NOTIFICATION FUNCTIONS
# =============================================================================

def broadcast_zenity_notification(title: str, text: str, timeout_seconds: int) -> None:
    """
    Display a Zenity notification to all active GUI users.
    
    Launches non-blocking Zenity info dialogs that auto-close after
    the specified timeout.
    
    Args:
        title: Dialog window title
        text: Message text to display
        timeout_seconds: Seconds until dialog auto-closes
    """
    if not is_tool_available("zenity"):
        logger.warning("zenity not installed; skipping GUI notifications")
        return

    sessions = get_active_gui_sessions()
    
    if not sessions:
        logger.debug("No active GUI sessions found for notification")
        return
    
    logger.info(f"Sending notification to users: {[s.user for s in sessions]}")

    for session in sessions:
        # Build environment variables for the command
        env_vars = [f"DISPLAY={session.env.get('DISPLAY', ':0')}"]
        
        if xdg := session.env.get("XDG_RUNTIME_DIR"):
            env_vars.append(f"XDG_RUNTIME_DIR={xdg}")
        if xauth := session.env.get("XAUTHORITY"):
            env_vars.append(f"XAUTHORITY={xauth}")
        if wayland := session.env.get("WAYLAND_DISPLAY"):
            env_vars.append(f"WAYLAND_DISPLAY={wayland}")

        # Construct command to run zenity as the target user
        cmd = [
            "runuser", "-u", session.user, "--", "env"
        ] + env_vars + [
            "zenity", "--info",
            "--title", title,
            "--text", text,
            f"--timeout={timeout_seconds}",
        ]

        try:
            # Launch non-blocking (don't wait for dialog to close)
            subprocess.Popen(
                cmd, 
                stdout=subprocess.DEVNULL, 
                stderr=subprocess.DEVNULL
            )
        except Exception as e:
            logger.debug(f"Failed to show notification to {session.user}: {e}")


# =============================================================================
# SSH SERVICE CONTROL
# =============================================================================

def is_ssh_service_active() -> bool:
    """Check if the SSH service is currently running."""
    if not is_tool_available("systemctl"):
        logger.warning("systemctl not available; cannot check SSH status")
        return False
    
    result = run_command(["systemctl", "is-active", "--quiet", "ssh"])
    return result.returncode == 0


def start_ssh_service() -> bool:
    """
    Start the SSH service.
    
    Returns:
        True if service started successfully
    """
    logger.info("Starting SSH service...")
    result = run_command(["systemctl", "start", "ssh"])
    
    if result.returncode == 0:
        logger.info("SSH service started")
        return True
    else:
        logger.error(f"Failed to start SSH: {result.stderr}")
        return False


def stop_ssh_service() -> bool:
    """
    Stop the SSH service.
    
    Returns:
        True if service stopped successfully
    """
    logger.info("Stopping SSH service...")
    result = run_command(["systemctl", "stop", "ssh"])
    
    if result.returncode == 0:
        logger.info("SSH service stopped")
        return True
    else:
        logger.error(f"Failed to stop SSH: {result.stderr}")
        return False


def set_allowed_ssh_user(username: str) -> bool:
    """
    Set the currently allowed SSH user.
    
    Writes the username to a file that PAM checks during SSH authentication.
    Only this user will be allowed to log in via SSH.
    
    Args:
        username: The username to allow SSH access
        
    Returns:
        True if file was written successfully
    """
    try:
        # Create runtime directory if it doesn't exist
        runtime_dir = os.path.dirname(ALLOWED_USER_FILE)
        os.makedirs(runtime_dir, mode=0o755, exist_ok=True)
        
        # Write allowed username
        with open(ALLOWED_USER_FILE, "w") as f:
            f.write(f"{username}\n")
        
        os.chmod(ALLOWED_USER_FILE, 0o644)
        logger.info(f"SSH access granted to user: {username}")
        return True
        
    except IOError as e:
        logger.error(f"Failed to set allowed SSH user: {e}")
        return False


def clear_allowed_ssh_user() -> bool:
    """
    Clear the allowed SSH user file.
    
    This effectively blocks all SSH logins (when combined with PAM config).
    
    Returns:
        True if file was cleared/removed successfully
    """
    try:
        if os.path.exists(ALLOWED_USER_FILE):
            os.remove(ALLOWED_USER_FILE)
        logger.debug("Cleared allowed SSH user")
        return True
        
    except IOError as e:
        logger.error(f"Failed to clear allowed SSH user: {e}")
        return False


def get_active_ssh_connections() -> List[Dict[str, str]]:
    """
    Get list of active SSH connections using 'ss' command.
    
    Returns:
        List of dicts with 'remote_ip', 'remote_port', and 'connection_id' keys.
        connection_id is a unique identifier for the specific connection.
    """
    connections = []
    
    if not is_tool_available("ss"):
        logger.debug("ss command not available")
        return connections
    
    # Get established SSH connections (port 22)
    # Using -t for TCP, -n for numeric, filtering on local sport 22
    result = run_command(["ss", "-tn", "sport", "=", ":22"])
    
    if result.returncode != 0:
        logger.debug(f"ss command failed: {result.stderr}")
        return connections
    
    for line in result.stdout.strip().splitlines():
        # Skip header line
        if line.startswith("State") or line.startswith("Recv-Q"):
            continue
            
        parts = line.split()
        if len(parts) >= 5:
            # Format: State Recv-Q Send-Q Local:Port Peer:Port
            # Or without state: Recv-Q Send-Q Local:Port Peer:Port
            local_addr = parts[-2]  # Second to last
            peer_addr = parts[-1]   # Last column
            
            if ":" in peer_addr and ":22" in local_addr:
                # Handle IPv6 format [::1]:port or IPv4 format 192.168.1.1:port
                if peer_addr.startswith("["):
                    # IPv6: [::1]:54321
                    bracket_end = peer_addr.rfind("]")
                    remote_ip = peer_addr[1:bracket_end]
                    remote_port = peer_addr[bracket_end+2:]
                else:
                    # IPv4: 192.168.1.1:54321
                    remote_ip, remote_port = peer_addr.rsplit(":", 1)
                
                # Unique connection ID based on remote endpoint
                connection_id = f"{remote_ip}:{remote_port}"
                connections.append({
                    "remote_ip": remote_ip,
                    "remote_port": remote_port,
                    "connection_id": connection_id
                })
    
    logger.debug(f"Found {len(connections)} SSH connections: {[c['connection_id'] for c in connections]}")
    return connections


def monitor_ssh_logins_during_window(duration_seconds: int, check_interval: float = 1.0) -> List[Dict[str, str]]:
    """
    Monitor for new SSH connections during the access window.
    
    Tracks connections by their unique connection_id (remote_ip:remote_port)
    so multiple connections from the same IP are detected separately.
    
    Args:
        duration_seconds: Total duration to monitor
        check_interval: How often to check for new connections
        
    Returns:
        List of new connections detected during the window
    """
    # Get baseline connections BEFORE starting (these existed before the window)
    initial_connections = get_active_ssh_connections()
    initial_conn_ids = {c["connection_id"] for c in initial_connections}
    
    logger.info(f"SSH window opened. Monitoring for {duration_seconds}s. Initial connections: {len(initial_connections)}")
    if initial_connections:
        logger.debug(f"Pre-existing connections: {list(initial_conn_ids)}")
    
    new_logins = []
    seen_new_conn_ids: Set[str] = set()
    
    elapsed = 0.0
    while elapsed < duration_seconds:
        time.sleep(check_interval)
        elapsed += check_interval
        
        current_connections = get_active_ssh_connections()
        current_conn_ids = {c["connection_id"] for c in current_connections}
        
        # Find genuinely new connections (not in initial set, not already logged)
        new_conn_ids = current_conn_ids - initial_conn_ids - seen_new_conn_ids
        
        for conn in current_connections:
            if conn["connection_id"] in new_conn_ids:
                ip = conn["remote_ip"]
                logger.info(f"[{EVENT_SSH_LOGIN}] New SSH connection from {ip} (port {conn['remote_port']})")
                new_logins.append({
                    "remote_ip": ip,
                    "remote_port": conn["remote_port"],
                    "time": time.strftime("%H:%M:%S")
                })
                seen_new_conn_ids.add(conn["connection_id"])
    
    logger.info(f"SSH monitoring complete. Detected {len(new_logins)} new login(s)")
    return new_logins


def open_ssh_access_window(
    duration_seconds: int, 
    strict_mode: bool,
    allowed_username: Optional[str] = None
) -> List[Dict[str, str]]:
    """
    Temporarily enable SSH access for a specified duration.
    
    Args:
        duration_seconds: How long to keep SSH enabled
        strict_mode: If True, ensures SSH is stopped before and after the window.
                    WARNING: This will disconnect existing SSH sessions.
        allowed_username: If provided, only this user can log in via SSH.
                         Requires PAM configuration (see install.sh).
                    
    Returns:
        List of new SSH connections made during the window
    """
    ssh_was_active = is_ssh_service_active()
    new_logins = []

    # Set allowed user before starting SSH (if specified)
    if allowed_username:
        set_allowed_ssh_user(allowed_username)

    try:
        if strict_mode:
            # Strict mode: Ensure SSH is closed outside the access window
            if ssh_was_active:
                logger.warning("Strict mode: stopping existing SSH (will disconnect active sessions)")
                stop_ssh_service()
                ssh_was_active = False

            # Open the window and monitor for logins
            start_ssh_service()
            try:
                new_logins = monitor_ssh_logins_during_window(duration_seconds)
            finally:
                stop_ssh_service()
        else:
            # Non-strict mode: Don't disrupt existing SSH usage
            if ssh_was_active:
                # SSH already running - just monitor
                logger.info("SSH already active; maintaining access for window duration")
                new_logins = monitor_ssh_logins_during_window(duration_seconds)
            else:
                # Start SSH temporarily and monitor
                start_ssh_service()
                try:
                    new_logins = monitor_ssh_logins_during_window(duration_seconds)
                finally:
                    stop_ssh_service()
    finally:
        # Always clear allowed user when window closes
        if allowed_username:
            clear_allowed_ssh_user()
    
    logger.info(f"Access window closed. Returning {len(new_logins)} login(s) to caller")
    return new_logins


# =============================================================================
# FACE DETECTION LOOP
# =============================================================================

def request_detected_faces(ser: serial.Serial, timeout: float = 0.25) -> Set[int]:
    """
    Request and collect face detection data from HuskyLens.
    
    Sends a block request command and collects all face IDs seen
    within the timeout period.
    
    Args:
        ser: Open serial connection to HuskyLens
        timeout: How long to wait for responses (seconds)
        
    Returns:
        Set of detected face IDs
    """
    # Send request for detected blocks (faces)
    try:
        ser.write(make_frame(CMD_REQUEST_BLOCKS))
        ser.flush()
    except serial.SerialException as e:
        logger.error(f"Failed to send request: {e}")
        return set()

    # Collect responses until timeout
    detected_ids: Set[int] = set()
    deadline = time.time() + timeout

    while time.time() < deadline:
        frame = read_frame(ser)
        
        if not frame:
            continue
            
        _, cmd, data = frame
        
        if cmd == RET_BUSY:
            # HuskyLens is busy, stop waiting
            break
            
        if cmd == RET_BLOCK and len(data) == 10:
            # Block data format: 10 bytes, face ID at bytes 8-9 (little-endian)
            face_id = parse_uint16_le(data[8], data[9])
            detected_ids.add(face_id)

    return detected_ids


def get_authorized_face_ids(detected_ids: Set[int]) -> List[int]:
    """
    Filter detected faces to only include learned/authorized ones.
    
    Face ID 0 means unlearned face, positive IDs are learned faces.
    
    Args:
        detected_ids: Set of all detected face IDs
        
    Returns:
        Sorted list of authorized (learned) face IDs
    """
    return sorted(face_id for face_id in detected_ids if face_id > 0)


def get_user_name(face_id: int) -> str:
    """Get the display name for a face ID, or a default string."""
    mappings = load_face_id_mappings()
    return mappings.get(face_id, f"Authorized user (ID {face_id})")


# =============================================================================
# MAIN APPLICATION
# =============================================================================

def parse_arguments() -> Config:
    """Parse command line arguments and return configuration."""
    parser = argparse.ArgumentParser(
        description="HuskyLens face recognition -> temporary SSH access with Zenity notifications.",
        formatter_class=argparse.ArgumentDefaultsHelpFormatter
    )
    
    parser.add_argument(
        "--port", 
        default=DEFAULT_PORT, 
        help="Serial port for HuskyLens"
    )
    parser.add_argument(
        "--baud", 
        type=int, 
        default=DEFAULT_BAUD, 
        help="Baud rate for serial connection"
    )
    parser.add_argument(
        "--open-seconds", "-s", 
        type=int, 
        default=15, 
        help="Duration to keep SSH open (seconds)"
    )
    parser.add_argument(
        "--streak", 
        type=int, 
        default=3, 
        help="Consecutive detections required to trigger"
    )
    parser.add_argument(
        "--cooldown", 
        type=int, 
        default=3, 
        help="Seconds before re-triggering is allowed"
    )
    parser.add_argument(
        "--strict", 
        action="store_true",
        help="Enforce SSH closed outside access windows (WARNING: disconnects existing sessions)"
    )
    
    args = parser.parse_args()
    
    return Config(
        port=args.port,
        baud=args.baud,
        open_seconds=args.open_seconds,
        streak_threshold=args.streak,
        cooldown_seconds=args.cooldown,
        strict_mode=args.strict
    )


def check_prerequisites() -> bool:
    """
    Verify that all prerequisites are met.
    
    Returns:
        True if all checks pass
    """
    # Must run as root for systemctl and runuser
    if os.geteuid() != 0:
        print("Error: This script must run as root.")
        print("Usage: sudo python3 sshguard.py --open-seconds 15 --strict")
        return False
    
    # Check for systemctl
    if not is_tool_available("systemctl"):
        print("Error: systemctl not found. This script requires systemd.")
        return False
    
    return True


def run_detection_loop(ser: serial.Serial, config: Config) -> None:
    """
    Main detection loop - monitors for authorized faces and controls SSH access.
    
    Args:
        ser: Open serial connection to HuskyLens
        config: Application configuration
    """
    consecutive_detections = 0
    next_trigger_allowed_time = 0.0

    logger.info("Detection loop started. Waiting for authorized face...")

    # Track last logged state to avoid spamming logs
    last_detection_event = None
    
    while True:
        # Request face detection from HuskyLens
        detected_ids = request_detected_faces(ser)
        authorized_ids = get_authorized_face_ids(detected_ids)
        unauthorized_ids = sorted(face_id for face_id in detected_ids if face_id == 0)
        is_authorized = len(authorized_ids) > 0

        # Update detection streak
        if is_authorized:
            consecutive_detections += 1
        else:
            consecutive_detections = 0

        # Determine current detection event type
        if is_authorized:
            current_event = EVENT_AUTHORIZED
        elif len(unauthorized_ids) > 0:
            current_event = EVENT_UNAUTHORIZED
        else:
            current_event = EVENT_NO_FACE
        
        # Log face detection events (log on state change or periodically for authorized)
        if current_event != last_detection_event:
            if current_event == EVENT_AUTHORIZED:
                names = [get_user_name(fid) for fid in authorized_ids]
                logger.info(f"[{EVENT_AUTHORIZED}] Face detected: {', '.join(names)} (IDs: {authorized_ids})")
            elif current_event == EVENT_UNAUTHORIZED:
                logger.info(f"[{EVENT_UNAUTHORIZED}] Unknown face detected (not learned)")
            # Don't log NO_FACE to avoid spam
            
            last_detection_event = current_event

        # Debug output
        logger.debug(
            f"Detected: {sorted(detected_ids)} | "
            f"Authorized: {authorized_ids} | "
            f"Streak: {consecutive_detections}/{config.streak_threshold}"
        )

        # Check if we should trigger SSH access
        current_time = time.time()
        streak_met = consecutive_detections >= config.streak_threshold
        cooldown_passed = current_time >= next_trigger_allowed_time

        if streak_met and cooldown_passed:
            # Trigger SSH access window
            primary_face_id = authorized_ids[0]
            user_name = get_user_name(primary_face_id)

            open_message = (
                f"{user_name} detected – "
                f"Opening SSH (port 22) for {config.open_seconds} seconds..."
            )
            logger.info(f"TRIGGER: {open_message}")

            # Show notification and open SSH (only allow the detected user)
            broadcast_zenity_notification(APP_NAME, open_message, config.open_seconds)
            new_logins = open_ssh_access_window(
                config.open_seconds, 
                config.strict_mode,
                allowed_username=user_name
            )
            
            # Report login summary
            if new_logins:
                login_ips = [login["remote_ip"] for login in new_logins]
                logger.info(f"SSH window summary: {len(new_logins)} new connection(s) from {login_ips}")
                close_message = f"SSH window closed. {len(new_logins)} login(s) recorded."
            else:
                logger.info("SSH window summary: No new connections")
                close_message = "SSH window closed. No logins recorded."
            
            broadcast_zenity_notification(
                APP_NAME, 
                close_message, 
                timeout_seconds=5
            )

            # Set cooldown and reset streak
            next_trigger_allowed_time = time.time() + config.cooldown_seconds
            consecutive_detections = 0
            
            logger.info(f"Cooldown: {config.cooldown_seconds}s until next trigger allowed")

        # Small delay before next detection cycle
        time.sleep(0.15)


def main() -> int:
    """
    Application entry point.
    
    Returns:
        Exit code (0 for success, non-zero for errors)
    """
    # Parse configuration
    config = parse_arguments()

    # Check prerequisites
    if not check_prerequisites():
        return 1

    # Log configuration
    logger.info(f"Starting {APP_NAME}")
    logger.info(
        f"Config: port={config.port}, baud={config.baud}, "
        f"open_seconds={config.open_seconds}, streak={config.streak_threshold}, "
        f"cooldown={config.cooldown_seconds}, strict={config.strict_mode}"
    )

    # Show startup notification
    broadcast_zenity_notification(
        APP_NAME, 
        "sshGuard started. Waiting for authorized face…", 
        timeout_seconds=3
    )

    # In strict mode, ensure SSH is stopped on startup
    # This prevents SSH from remaining open if it was running before sshguard started
    if config.strict_mode:
        logger.info("Strict mode: ensuring SSH is stopped on startup")
        stop_ssh_service()

    # Connect to HuskyLens and run detection loop
    try:
        with serial.Serial(
            config.port, 
            config.baud, 
            timeout=0.15, 
            write_timeout=1.0
        ) as ser:
            # Allow connection to stabilize
            time.sleep(0.5)
            ser.reset_input_buffer()

            # Run main detection loop (runs forever until interrupted)
            run_detection_loop(ser, config)

    except serial.SerialException as e:
        logger.error(f"Serial connection failed: {e}")
        logger.error(f"Check that HuskyLens is connected to {config.port}")
        return 1
    except KeyboardInterrupt:
        logger.info("Interrupted by user. Shutting down...")
        return 0
    except Exception as e:
        logger.error(f"Unexpected error: {e}")
        return 1

    return 0


if __name__ == "__main__":
    sys.exit(main())
