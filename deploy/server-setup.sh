#!/usr/bin/env bash
# SELENE OPS — one-time server setup. Run ON the server as root:
#   bash /root/server-setup.sh
# Expects /root/nginx-selene.conf (copied with scp). Safe to run again: it only (re)installs
# packages, (re)creates directories and replaces the site config.
set -euo pipefail

[[ "$(id -u)" -eq 0 ]] || { echo "Run as root." >&2; exit 1; }
[[ -f /root/nginx-selene.conf ]] || { echo "Missing /root/nginx-selene.conf — copy it with scp first." >&2; exit 1; }

export DEBIAN_FRONTEND=noninteractive
apt update
apt install -y nginx rsync

mkdir -p /var/www/selene/stable /var/www/selene/preview
for d in stable preview; do
  # Placeholder until the first deploy (never overwrites a deployed site).
  if [[ ! -e "/var/www/selene/$d/index.html" ]]; then
    printf '<!doctype html><meta charset="utf-8"><title>SELENE OPS</title><p>SELENE OPS (%s) — not deployed yet.</p>\n' "$d" > "/var/www/selene/$d/index.html"
  fi
done

install -m 644 /root/nginx-selene.conf /etc/nginx/sites-available/selene
ln -sfn /etc/nginx/sites-available/selene /etc/nginx/sites-enabled/selene
rm -f /etc/nginx/sites-enabled/default

nginx -t
systemctl enable --now nginx
systemctl reload nginx

echo "nginx is serving:"
echo "  stable  → /var/www/selene/stable  (port 80)"
echo "  preview → /var/www/selene/preview (port 8080)"
