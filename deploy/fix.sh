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
EXISTING_PATH=""
if [[ -f /etc/cloudflared/config.yml ]]; then
    EXISTING_PATH="$(python3 - /etc/cloudflared/config.yml "$HOSTNAME" <<'PYPATH'
from pathlib import Path
import re, sys
lines=Path(sys.argv[1]).read_text(encoding="utf-8").splitlines()
host=sys.argv[2].lower()
for i,line in enumerate(lines):
    m=re.match(r"^\s*-\s*hostname:\s*['\"]?([^'\"]+)['\"]?\s*$",line)
    if m and m.group(1).lower()==host:
        for item in lines[i+1:]:
            if re.match(r"^\s*-\s+",item): break
            p=re.match(r"^\s*path:\s*(.*?)\s*$",item)
            if p:
                value = p.group(1).strip()
                print(value.strip("\'").strip('"'))
                raise SystemExit
        break
PYPATH
)"
fi
read -r -p "Optional root redirect path (existing: ${EXISTING_PATH:-none}; empty disables): " ROUTE_PATH
ROUTE_PATH="${ROUTE_PATH%/}"
ROUTE_PATH="${ROUTE_PATH:-$EXISTING_PATH}"
[[ -z "$ROUTE_PATH" || "$ROUTE_PATH" == /* ]] || { echo "Path must start with /." >&2; exit 1; }
[[ "$ROUTE_PATH" != *" "* && "$ROUTE_PATH" != *"?"* && "$ROUTE_PATH" != *"#"* ]] || { echo "Enter a path only, without spaces, query strings, or fragments." >&2; exit 1; }

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
if [[ -n "$ROUTE_PATH" ]]; then
python3 - "$ROUTE_PATH" "$AVAILABLE" <<'PYNGINX'
from pathlib import Path
import sys
route_path, filename = sys.argv[1:]
p=Path(filename); text=p.read_text(encoding="utf-8")
needle="    location / {"
redirect=f"    location = / {{\n        return 302 {route_path};\n    }}\n\n"
if needle not in text: raise SystemExit("Could not add root redirect to Nginx config.")
p.write_text(text.replace(needle,redirect+needle,1),encoding="utf-8")
PYNGINX
fi

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
    python3 - "$CONFIG" "$HOSTNAME" "$ROUTE_PATH" <<'PY'
from pathlib import Path
import re, sys

path = Path(sys.argv[1])
hostname = sys.argv[2].lower()
route_path = sys.argv[3]
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
        lines.insert(i + 1, "  service: http://127.0.0.1:7080")
    if route_path:
        route_end = next((j for j in range(i + 1, len(lines)) if re.match(r"^\s*-\s+", lines[j])), len(lines))
        existing_path = next((j for j in range(i + 1, route_end) if re.match(r"^\s*path\s*:", lines[j])), None)
        path_line = match.group(1) + "  path: ^" + re.escape(route_path) + "$"
        if existing_path is None:
            lines.insert(i + 1, path_line)
        else:
            lines[existing_path] = path_line
    break

path.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")
PY
done

if [[ -n "$ROUTE_PATH" && -f /etc/cloudflared/config.yml ]]; then
python3 - /etc/cloudflared/config.yml "$HOSTNAME" <<'PYROOT'
from pathlib import Path
import re, sys
p=Path(sys.argv[1]); host=sys.argv[2]
lines=p.read_text(encoding="utf-8").splitlines()
ingress=next((i for i,line in enumerate(lines) if re.match(r"^ingress:\s*$",line)),None)
has_root_route=False
if ingress is not None:
    for i,line in enumerate(lines[ingress+1:], ingress+1):
        host_match=re.match(r"^\s*-\s*hostname:\s*['\"]?([^'\"]+)['\"]?\s*$",line)
        if not host_match or host_match.group(1).lower()!=host.lower(): continue
        end=next((j for j in range(i+1,len(lines)) if re.match(r"^\s*-\s+",lines[j])),len(lines))
        has_root_route=any(item.strip()=="path: ^/$" for item in lines[i+1:end])
        break
if ingress is not None and not has_root_route:
    catchall=next((i for i in range(ingress+1,len(lines)) if re.match(r"^\s*-\s+service:\s*http_status:",lines[i])),len(lines))
    lines[catchall:catchall]=[f"  - hostname: {host}", "    path: ^/$", "    service: http://127.0.0.1:7080", ""]
    p.write_text("\n".join(lines).rstrip()+"\n",encoding="utf-8")
PYROOT
fi

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
[[ -n "$ROUTE_PATH" ]] && echo "Root redirect: / -> $ROUTE_PATH"
echo "Backups were saved with suffix .bak.${STAMP}."
