#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
    echo "Run as root (for example: sudo -H bash ./deploy/fix.sh)." >&2
    exit 1
fi

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
AVAILABLE="/etc/nginx/sites-available/memoana.conf"
ENABLED="/etc/nginx/sites-enabled/memoana.conf"
SERVICE_FILE="/etc/systemd/system/memoana-api.service"
STAMP="$(date +%Y%m%d%H%M%S)"

[[ -f "$AVAILABLE" ]] || { echo "Missing $AVAILABLE" >&2; exit 1; }
[[ -f "$SCRIPT_DIR/memoana-api.service" ]] || { echo "Missing $SCRIPT_DIR/memoana-api.service" >&2; exit 1; }
HOSTNAME="$(awk '$1 == "server_name" {gsub(/;/, "", $2); print $2; exit}' "$AVAILABLE")"
[[ -n "$HOSTNAME" && "$HOSTNAME" != "__HOSTNAME__" ]] || {
    echo "Could not determine server_name from $AVAILABLE." >&2
    exit 1
}

cp -a "$AVAILABLE" "${AVAILABLE}.bak.${STAMP}"
cp -a "$SERVICE_FILE" "${SERVICE_FILE}.bak.${STAMP}" 2>/dev/null || true

# Publish the container's HTTP port 7080 on host port 7081.
# Nginx listens on port 7080 and proxies to the Docker-published API port 7081.
cat > "$AVAILABLE" <<EOF
server {
    listen 7080;
    listen [::]:7080;

    server_name ${HOSTNAME};

    location / {
        proxy_pass http://127.0.0.1:7081;
        proxy_http_version 1.1;

        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$http_x_forwarded_proto;
        proxy_set_header X-Forwarded-Port \$http_x_forwarded_port;

        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
EOF

install -m 0644 "$SCRIPT_DIR/memoana-api.service" "$SERVICE_FILE"
sed -i 's#--publish 127.0.0.1:7080:7080#--publish 127.0.0.1:7081:7080#g' "$SERVICE_FILE"

ln -sfn "$AVAILABLE" "$ENABLED"
if ! nginx -t; then
    echo "Nginx validation failed; restoring previous Nginx config." >&2
    cp -a "${AVAILABLE}.bak.${STAMP}" "$AVAILABLE"
    nginx -t || true
    exit 1
fi

systemctl reload nginx
systemctl daemon-reload
systemctl enable memoana-api.service
systemctl restart memoana-api.service

# Keep the existing tunnel and direct its hostname ingress to Nginx.
for CONFIG in /etc/cloudflared/config.yml "${HOME}/.cloudflared/config.yml"; do
    [[ -f "$CONFIG" ]] || continue
    cp -a "$CONFIG" "${CONFIG}.bak.${STAMP}"
    python3 - "$CONFIG" "$HOSTNAME" <<'PY'
from pathlib import Path
import re, sys

path = Path(sys.argv[1])
hostname = sys.argv[2].lower()
lines = path.read_text(encoding="utf-8").splitlines()

for i, line in enumerate(lines):
    match = re.match(r"^(\s*)-\s*hostname:\s*['\"]?([^'\"]+)['\"]?\s*$", line)
    if not match or match.group(2).lower() != hostname:
        continue

    for j in range(i + 1, len(lines)):
        if re.match(r"^\s*-\s+", lines[j]):
            break
        if re.match(r"^\s*service\s*:", lines[j]):
            indent = re.match(r"^(\s*)", lines[j]).group(1)
            lines[j] = indent + "service: http://127.0.0.1:7080"
            break
    else:
        lines.insert(i + 1, "  service: http://127.0.0.1:80")
    break

path.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")
PY
done

# /etc is canonical; mirror its resulting config into root's cloudflared directory.
if [[ -f /etc/cloudflared/config.yml ]]; then
    mkdir -p "${HOME}/.cloudflared"
    install -m 0600 /etc/cloudflared/config.yml "${HOME}/.cloudflared/config.yml"
fi

if systemctl cat cloudflared.service >/dev/null 2>&1; then
    systemctl restart cloudflared.service
fi

echo "Fix complete."
echo "Docker HTTP: 127.0.0.1:7081 -> container:7080"
echo "Nginx HTTP origin for Cloudflare Tunnel: 127.0.0.1:7080"
echo "Backups were saved with suffix .bak.${STAMP}."
