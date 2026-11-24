#!/usr/bin/env bash
# First-time host prep for MeetJoe on Ubuntu 24.04 (run as ubuntu on the EC2).
set -euo pipefail

if [[ "$(id -u)" -eq 0 ]]; then
  echo "Run as ubuntu (not root). Script will sudo where needed." >&2
  exit 1
fi

# 2 GiB swap — t3.small is tight while building the .NET image.
if ! swapon --show | grep -q .; then
  sudo fallocate -l 2G /swapfile
  sudo chmod 600 /swapfile
  sudo mkswap /swapfile
  sudo swapon /swapfile
  echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab >/dev/null
fi

# Docker Engine + Compose plugin
if ! command -v docker >/dev/null 2>&1; then
  sudo apt-get update
  sudo apt-get install -y ca-certificates curl
  sudo install -m 0755 -d /etc/apt/keyrings
  sudo curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  sudo chmod a+r /etc/apt/keyrings/docker.asc
  echo \
    "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu \
    $(. /etc/os-release && echo "$VERSION_CODENAME") stable" \
    | sudo tee /etc/apt/sources.list.d/docker.list >/dev/null
  sudo apt-get update
  sudo apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
  sudo usermod -aG docker "$USER"
fi

echo "Host bootstrap done. Re-login (or newgrp docker) so docker works without sudo."
echo "Then place the repo under /opt/meetjoe and run render-config.sh + compose up."
