#!/usr/bin/env bash
set -euo pipefail
if [[ "${EUID}" -ne 0 ]]; then echo "Run as root." >&2; exit 1; fi
echo "This temporary rollback removes the MemoAna container, Docker network, systemd unit,"
echo "Nginx proxy configuration, and the initial SQLite database."
echo "Cloudflare Tunnel configuration and DNS routes are preserved."
read -r -p "Continue? [y/N] " answer
[[ "$answer" =~ ^[Yy]$ ]] || { echo "Cancelled."; exit 0; }

systemctl stop memoana-api.service 2>/dev/null || true
docker stop memoana-api 2>/dev/null || true
docker rm memoana-api 2>/dev/null || true
docker network rm memoana-network 2>/dev/null || true
systemctl disable memoana-api.service 2>/dev/null || true
rm -f /etc/systemd/system/memoana-api.service
systemctl daemon-reload

rm -f /etc/nginx/sites-enabled/memoana.conf
rm -f /etc/nginx/sites-available/memoana.conf
if command -v nginx >/dev/null 2>&1 && nginx -t; then systemctl reload nginx 2>/dev/null || true; fi

rm -f /data/memoana-api/memoana.db
rmdir /data/memoana-api 2>/dev/null || true
echo "Rollback complete. Cloudflare Tunnel and DNS configuration were left untouched."
