# Dockerfile for testing sshGuard .deb package
# Uses Debian (same base as Raspberry Pi OS)

FROM debian:bookworm-slim

# Install dependencies that would be on a real Pi
RUN apt-get update && apt-get install -y \
    python3 \
    python3-serial \
    systemd \
    systemd-sysv \
    openssh-server \
    sudo \
    procps \
    && rm -rf /var/lib/apt/lists/*

# Create SSH directories
RUN mkdir -p /run/sshd /etc/ssh/sshd_config.d

# Enable systemd (needed for service testing)
ENV container=docker
STOPSIGNAL SIGRTMIN+3

# Copy the built .deb package
COPY sshguard-face_1.0.0_all.deb /tmp/

# Test installation
RUN dpkg -i /tmp/sshguard-face_1.0.0_all.deb || true
RUN apt-get install -f -y

# Verify installation
RUN echo "=== Checking installation ===" && \
    ls -la /opt/sshGuard/ && \
    ls -la /etc/systemd/system/sshguard.service && \
    cat /etc/ssh/sshd_config.d/sshguard.conf && \
    grep sshguard /etc/pam.d/sshd && \
    getent group sshGuard && \
    echo "=== Installation verified ==="

# Default command shows status
CMD ["bash", "-c", "echo 'sshGuard package installed successfully!' && echo '' && echo 'Files:' && ls -la /opt/sshGuard/ && echo '' && echo 'SSH Config:' && cat /etc/ssh/sshd_config.d/sshguard.conf && echo '' && echo 'PAM Rule:' && grep -A1 sshguard /etc/pam.d/sshd"]
