#!/usr/bin/env bash
set -euo pipefail

IMAGE_REPOSITORY="ghcr.io/thememoanagame/memoana-api"
DEFAULT_TAG="v0.0.1-rc1"
TAG="${1:-$DEFAULT_TAG}"
CONTAINER_NAME="memoana-api"
DATA_DIR="/data/memoana-api"
SERVICE_NAME="memoana-api.service"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${EUID}" -ne 0 ]]; then
    echo "Run this script as root."
    exit 1
fi

if ! command -v docker >/dev/null 2>&1; then
    echo "Docker is required."
    exit 1
fi

mkdir -p "$DATA_DIR"

if [[ -n "${GHCR_TOKEN:-}" ]]; then
    : "${GHCR_USERNAME:?GHCR_USERNAME must be set when GHCR_TOKEN is used.}"
    printf '%s' "$GHCR_TOKEN" | docker login ghcr.io --username "$GHCR_USERNAME" --password-stdin
fi

IMAGE="${IMAGE_REPOSITORY}:${TAG}"

echo "Pulling ${IMAGE}..."
docker pull "$IMAGE"

install -m 0644 "$SCRIPT_DIR/memoana-api.service" "/etc/systemd/system/$SERVICE_NAME"
sed -i "s#${IMAGE_REPOSITORY}:v0.0.1-rc1#${IMAGE}#g" "/etc/systemd/system/$SERVICE_NAME"

if command -v nginx >/dev/null 2>&1 && [[ -f "$SCRIPT_DIR/memoana.conf" ]]; then
    install -m 0644 "$SCRIPT_DIR/memoana.conf" /etc/nginx/sites-available/memoana.conf
    ln -sfn /etc/nginx/sites-available/memoana.conf /etc/nginx/sites-enabled/memoana.conf
    nginx -t
    systemctl reload nginx
fi

systemctl daemon-reload
systemctl enable --now "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"

systemctl --no-pager --full status "$SERVICE_NAME"
