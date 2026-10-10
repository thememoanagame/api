#!/usr/bin/env bash
set -euo pipefail

IMAGE_REPOSITORY="ghcr.io/thememoanagame/memoana-api"
DEFAULT_TAG="v0.0.1-rc1"
TAG="${1:-$DEFAULT_TAG}"
DATA_DIR="/data/memoana-api"
SERVICE_NAME="memoana-api.service"
CLOUDFLARED_HOME="${HOME}/.cloudflared"
CLOUDFLARED_ETC="/etc/cloudflared"
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${1:-}" == "-r" || "${1:-}" == "--rollback" ]]; then
    exec bash "${SCRIPT_DIR}/fix.sh"
fi
if [[ "${EUID}" -ne 0 ]]; then
    echo "Run as root (for example: sudo -H ./deploy/deploy.sh)." >&2
    exit 1
fi
if [[ "${1:-}" == -* ]]; then
    echo "Usage: $0 [image-tag | -r | --rollback]" >&2
    exit 2
fi

TAG="${1:-$DEFAULT_TAG}"
cd "$HOME"
command -v docker >/dev/null 2>&1 || { echo "Docker is required." >&2; exit 1; }
mkdir -p "$DATA_DIR" "$CLOUDFLARED_HOME" "$CLOUDFLARED_ETC"

if [[ -n "${GHCR_TOKEN:-}" ]]; then
    : "${GHCR_USERNAME:?GHCR_USERNAME must be set when GHCR_TOKEN is used.}"
    printf '%s' "$GHCR_TOKEN" | docker login ghcr.io --username "$GHCR_USERNAME" --password-stdin
fi

IMAGE="${IMAGE_REPOSITORY}:${TAG}"
echo "Pulling ${IMAGE}..."
docker pull "$IMAGE"

if ! command -v cloudflared >/dev/null 2>&1; then
    echo "Installing cloudflared from the official release package..."
    command -v curl >/dev/null 2>&1 || { apt-get update && apt-get install -y curl; }
    case "$(dpkg --print-architecture)" in
        amd64) package_arch="amd64" ;;
        arm64) package_arch="arm64" ;;
        armhf) package_arch="arm" ;;
        *) echo "Unsupported architecture: $(dpkg --print-architecture)" >&2; exit 1 ;;
    esac
    package_file="$(mktemp /tmp/cloudflared.XXXXXX.deb)"
    curl -fsSL "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-${package_arch}.deb" -o "$package_file"
    apt-get install -y "$package_file"
    rm -f "$package_file"
fi

CONFIG_SOURCE=""
[[ -f "${CLOUDFLARED_ETC}/config.yml" ]] && CONFIG_SOURCE="${CLOUDFLARED_ETC}/config.yml"
if [[ -z "$CONFIG_SOURCE" && -f "${CLOUDFLARED_HOME}/config.yml" ]]; then
    CONFIG_SOURCE="${CLOUDFLARED_HOME}/config.yml"
fi

TUNNEL_ID=""
if [[ -n "$CONFIG_SOURCE" ]]; then
    TUNNEL_ID="$(awk '$1 == "tunnel:" {gsub(/["'"'"']/,"",$2); print $2; exit}' "$CONFIG_SOURCE")"
fi

if [[ -z "$TUNNEL_ID" ]]; then
    mapfile -t credential_files < <(find "$CLOUDFLARED_HOME" "$CLOUDFLARED_ETC" -maxdepth 1 -type f -name '*.json' -printf '%f\n' 2>/dev/null | sort -u)
    if (( ${#credential_files[@]} == 1 )); then
        TUNNEL_ID="${credential_files[0]%.json}"
        echo "Using existing tunnel credentials: ${TUNNEL_ID}"
    elif (( ${#credential_files[@]} > 1 )); then
        echo "Select an existing local tunnel:"
        for i in "${!credential_files[@]}"; do printf '  %d) %s\n' "$((i + 1))" "${credential_files[$i]}"; done
        read -r -p "Tunnel [1-${#credential_files[@]}]: " selection
        [[ "$selection" =~ ^[0-9]+$ ]] && (( selection >= 1 && selection <= ${#credential_files[@]} )) || { echo "Invalid selection." >&2; exit 1; }
        TUNNEL_ID="${credential_files[$((selection - 1))]%.json}"
    fi
fi

if [[ -z "$TUNNEL_ID" ]]; then
    read -r -p "No local tunnel found. Create a new Cloudflare Tunnel? [y/N] " answer
    [[ "$answer" =~ ^[Yy]$ ]] || { echo "Deployment cancelled."; exit 1; }
    if [[ ! -f "${CLOUDFLARED_HOME}/cert.pem" ]]; then
        echo "Complete the Cloudflare browser authorization when prompted."
        cloudflared tunnel login
    fi
    read -r -p "New tunnel name [memoana-api]: " tunnel_name
    tunnel_name="${tunnel_name:-memoana-api}"
    cloudflared tunnel create "$tunnel_name"
    TUNNEL_ID="$(find "$CLOUDFLARED_HOME" -maxdepth 1 -type f -name '*.json' -printf '%T@ %f\n' | sort -nr | head -n 1 | cut -d' ' -f2- | sed 's/\.json$//')"
    CONFIG_SOURCE=""
fi

[[ "$TUNNEL_ID" =~ ^[0-9a-fA-F-]{36}$ ]] || { echo "Invalid tunnel UUID: ${TUNNEL_ID}" >&2; exit 1; }
CREDENTIAL_SOURCE=""
for candidate in "${CLOUDFLARED_HOME}/${TUNNEL_ID}.json" "${CLOUDFLARED_ETC}/${TUNNEL_ID}.json"; do
    if [[ -f "$candidate" ]]; then CREDENTIAL_SOURCE="$candidate"; break; fi
done
[[ -n "$CREDENTIAL_SOURCE" ]] || { echo "Missing credentials for tunnel ${TUNNEL_ID}; refusing to overwrite tunnel settings." >&2; exit 1; }
ETC_CREDENTIAL="${CLOUDFLARED_ETC}/${TUNNEL_ID}.json"
HOME_CREDENTIAL="${CLOUDFLARED_HOME}/${TUNNEL_ID}.json"

# Never copy/install a file onto itself. Keep /etc as the canonical runtime copy,
# then mirror it to the home directory so both configs and credentials stay aligned.
if [[ "$CREDENTIAL_SOURCE" != "$ETC_CREDENTIAL" ]]; then
    install -m 0600 "$CREDENTIAL_SOURCE" "$ETC_CREDENTIAL"
fi
if [[ "$ETC_CREDENTIAL" != "$HOME_CREDENTIAL" ]]; then
    install -m 0600 "$ETC_CREDENTIAL" "$HOME_CREDENTIAL"
fi

read -r -p "Public hostname for MemoAna (for example, api.example.com): " HOSTNAME
HOSTNAME="${HOSTNAME,,}"
[[ "$HOSTNAME" =~ ^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$ ]] || { echo "Invalid hostname: ${HOSTNAME}" >&2; exit 1; }

echo "Ensuring DNS route ${HOSTNAME} on existing tunnel ${TUNNEL_ID}..."
dns_output="$(mktemp)"
if cloudflared tunnel route dns "$TUNNEL_ID" "$HOSTNAME" >"$dns_output" 2>&1; then
    cat "$dns_output"
else
    cat "$dns_output" >&2
    if grep -Eqi 'already exists|record already exists|already configured|duplicate' "$dns_output"; then
        echo "DNS route already exists; reusing it."
    else
        echo "DNS route creation failed (for example, due to an existing record or missing DNS permissions)."
        read -r -p "Continue with this hostname anyway? [y/N] " answer
        [[ "$answer" =~ ^[Yy]$ ]] || { rm -f "$dns_output"; exit 1; }
    fi
fi
rm -f "$dns_output"

if [[ -n "$CONFIG_SOURCE" ]]; then
    if [[ "$CONFIG_SOURCE" != "${CLOUDFLARED_ETC}/config.yml" ]]; then
        cp -p "$CONFIG_SOURCE" "${CLOUDFLARED_ETC}/config.yml"
    fi
else
    printf 'tunnel: %s\ncredentials-file: /etc/cloudflared/%s.json\n' "$TUNNEL_ID" "$TUNNEL_ID" > "${CLOUDFLARED_ETC}/config.yml"
fi

python3 - "${CLOUDFLARED_ETC}/config.yml" "$TUNNEL_ID" "$HOSTNAME" <<'PY'
from pathlib import Path
import re, sys

path = Path(sys.argv[1])
tunnel_id, hostname = sys.argv[2:]
text = path.read_text(encoding="utf-8")
newline = "\r\n" if "\r\n" in text else "\n"
lines = text.splitlines()

def set_key(key, value):
    pattern = re.compile(r"^([ \t]*)" + re.escape(key) + r":[ \t]*.*$")
    for i, line in enumerate(lines):
        match = pattern.match(line)
        if match:
            lines[i] = match.group(1) + key + ": " + value
            return
    lines.insert(0, key + ": " + value)

set_key("tunnel", tunnel_id)
set_key("credentials-file", f"/etc/cloudflared/{tunnel_id}.json")

ingress = next((i for i, line in enumerate(lines) if re.match(r"^ingress:[ \t]*$", line)), None)
route = None
if ingress is None:
    if lines and lines[-1].strip(): lines.append("")
    lines += ["ingress:", "  - hostname: " + hostname,
              "    service: http://127.0.0.1:7081", "", "  - service: http_status:404"]
else:
    end = len(lines)
    for i in range(ingress + 1, len(lines)):
        if lines[i] and not lines[i][0].isspace() and not lines[i].lstrip().startswith("#"):
            end = i
            break
    block = lines[ingress + 1:end]
    starts = [i for i, line in enumerate(block) if re.match(r"^[ \t]*-[ \t]+", line)]
    route_indent = re.match(r"^([ \t]*)-", block[starts[0]]).group(1) if starts else "  "
    child_indent = route_indent + ("\t" if "\t" in route_indent else "  ")
    route = [f"{route_indent}- hostname: {hostname}",
             f"{child_indent}service: http://127.0.0.1:7081"]
    match_start = next((j for j, line in enumerate(block)
                        if re.match(r"^[ \t]*-[ \t]+hostname:[ \t]*['\"]?" + re.escape(hostname) + r"['\"]?[ \t]*$", line)), None)
    if match_start is not None:
        match_end = next((k for k in range(match_start + 1, len(block))
                          if re.match(r"^[ \t]*-[ \t]+", block[k])), len(block))
        old = block[match_start:match_end]
        service_index = next((k for k, line in enumerate(old) if re.match(r"^[ \t]*service:[ \t]*", line)), None)
        if service_index is None: old.append(f"{child_indent}service: http://127.0.0.1:7081")
        else: old[service_index] = f"{child_indent}service: http://127.0.0.1:7081"
        block[match_start:match_end] = old
    else:
        catchall = next((j for j, line in enumerate(block)
                         if re.match(r"^[ \t]*-[ \t]+service:[ \t]*http_status:", line)), None)
        if catchall is None:
            if block and block[-1].strip(): block.append("")
            block.extend(route)
        else:
            before, after = block[:catchall], block[catchall:]
            if before and before[-1].strip(): before.append("")
            block = before + route + [""] + after
    if not any(re.match(r"^[ \\t]*-[ \\t]+service:[ \\t]*http_status:", line) for line in block):
        if block and block[-1].strip(): block.append("")
        block.append(f"{route_indent}- service: http_status:404")
    lines[ingress + 1:end] = block

path.write_text(newline.join(lines).rstrip() + newline, encoding="utf-8")
PY

install -m 0600 "${CLOUDFLARED_ETC}/config.yml" "${CLOUDFLARED_HOME}/config.yml"

if ! systemctl cat cloudflared.service >/dev/null 2>&1; then
    cat > /etc/systemd/system/cloudflared.service <<'EOF'
[Unit]
Description=Cloudflare Tunnel
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
Restart=on-failure
RestartSec=5s
ExecStart=/usr/bin/cloudflared --no-autoupdate --config /etc/cloudflared/config.yml tunnel run

[Install]
WantedBy=multi-user.target
EOF
    systemctl daemon-reload
    systemctl enable cloudflared.service
fi

install -m 0644 "$SCRIPT_DIR/memoana-api.service" "/etc/systemd/system/$SERVICE_NAME"
sed -i "s#${IMAGE_REPOSITORY}:v0.0.1-rc1#${IMAGE}#g" "/etc/systemd/system/$SERVICE_NAME"
sed "s/__HOSTNAME__/${HOSTNAME}/g" "$SCRIPT_DIR/memoana.conf" > /etc/nginx/sites-available/memoana.conf
ln -sfn /etc/nginx/sites-available/memoana.conf /etc/nginx/sites-enabled/memoana.conf

nginx -t
systemctl reload nginx
systemctl daemon-reload
systemctl enable --now "$SERVICE_NAME"
systemctl restart "$SERVICE_NAME"
systemctl enable --now cloudflared.service
systemctl restart cloudflared.service

echo
echo "Deployment complete: https://${HOSTNAME}"
echo "Nginx origin: http://127.0.0.1:7081"
echo "Docker API: 127.0.0.1:7080 -> container:7080; Nginx: 127.0.0.1:7081"
systemctl --no-pager --full status "$SERVICE_NAME"
systemctl --no-pager --full status cloudflared.service
