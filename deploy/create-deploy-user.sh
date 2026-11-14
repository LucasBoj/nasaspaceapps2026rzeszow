#!/usr/bin/env bash
# SELENE OPS — one-time server setup of a dedicated user for CI deploys (GitHub Actions → rsync),
# so the CI key never has root. Run ON the server as root, AFTER server-setup.sh.
# The public key (mikrus_deploy.pub) comes from a file, from the argument itself, or from stdin (-):
#
#   bash /root/create-deploy-user.sh /root/mikrus_deploy.pub
#   ssh -p 10177 root@kamil177.mikrus.xyz 'bash /root/create-deploy-user.sh -' < ~/.ssh/mikrus_deploy.pub
#
# What it does (safe to run again):
#   - creates the user "deploy" (override with DEPLOY_USER=…) with NO password: key login only,
#   - makes it the owner of /var/www/selene/stable and /var/www/selene/preview, its only write access
#     outside its home; nginx keeps read access (directories 755, files 644),
#   - adds the key to ~deploy/.ssh/authorized_keys with "restrict" (no tty, no port/agent/X11 forwarding;
#     rsync over ssh still works),
#   - warns if the same key is also authorized for root (then CI would still have root access).
# Afterwards set the GitHub secret MIKRUS_USER=deploy (deploy/README.md → Automatyczne wdrażanie).
set -euo pipefail

DEPLOY_USER="${DEPLOY_USER:-deploy}"
WEB_ROOT=/var/www/selene
SITES=(stable preview)

die()  { echo "ERROR: $*" >&2; exit 1; }
warn() { echo "WARNING: $*" >&2; }
ok()   { echo "  ok  $*"; }

[[ "$(id -u)" -eq 0 ]] || die "Run as root."
command -v useradd >/dev/null && command -v ssh-keygen >/dev/null || die "needs useradd and ssh-keygen (Debian/Ubuntu: passwd, openssh-client)"

# ---------------------------------------------------------------- public key
src="${1:-}"
case "$src" in
  "")                 die "usage: $0 <public key file | 'ssh-ed25519 AAAA… comment' | - (stdin)>" ;;
  -)                  key="$(cat)" ;;
  ssh-*|ecdsa-*|sk-*) key="$src" ;;
  *)                  [[ -f "$src" ]] || die "no such file: $src"; key="$(cat "$src")" ;;
esac
# Drop CR (a key copied from Windows) and blank lines; exactly one key line must remain.
key="$(printf '%s\n' "$key" | tr -d '\r' | grep -v '^[[:space:]]*$' || true)"
[[ "$key" != *"PRIVATE KEY"* ]] || die "this is a PRIVATE key. Use the .pub file (the private key goes only into the GitHub secret)."
[[ "$(printf '%s\n' "$key" | wc -l)" -eq 1 ]] || die "expected exactly one public key line"
[[ "$key" =~ ^(ssh-|ecdsa-|sk-) ]] || die "the key line must start with its type (ssh-ed25519 …), without authorized_keys options"
tmp="$(mktemp)"; trap 'rm -f "$tmp"' EXIT
printf '%s\n' "$key" > "$tmp"
fingerprint="$(ssh-keygen -l -f "$tmp" 2>/dev/null)" || die "not a valid SSH public key"
key_body="$(awk '{ print $2 }' "$tmp")"   # the base64 part identifies the key
ok "key: $fingerprint"

# ---------------------------------------------------------------- user
if id "$DEPLOY_USER" >/dev/null 2>&1; then
  ok "user $DEPLOY_USER already exists"
else
  useradd --create-home --shell /bin/bash --comment "SELENE OPS CI deploy" "$DEPLOY_USER"
  ok "user $DEPLOY_USER created"
fi
# '*' = no password can ever match, but the account is not "locked": with a '!' (useradd's default)
# sshd refuses even key logins when UsePAM is off. A shell is needed: rsync runs through it.
usermod --password '*' --shell /bin/bash "$DEPLOY_USER"
group="$(id -gn "$DEPLOY_USER")"
home="$(getent passwd "$DEPLOY_USER" | cut -d: -f6)"
[[ -n "$home" && -d "$home" ]] || die "home directory of $DEPLOY_USER not found"

install -d -m 700 -o "$DEPLOY_USER" -g "$group" "$home/.ssh"
ak="$home/.ssh/authorized_keys"
touch "$ak"
chown "$DEPLOY_USER:$group" "$ak"
chmod 600 "$ak"
if grep -qF "$key_body" "$ak"; then
  ok "key already in $ak"
else
  printf 'restrict %s\n' "$key" >> "$ak"
  ok "key added to $ak (restrict)"
fi

# ---------------------------------------------------------------- web directories
install -d -m 755 -o root -g root "$WEB_ROOT"
for s in "${SITES[@]}"; do
  install -d "$WEB_ROOT/$s"
  chown -R "$DEPLOY_USER:$group" "$WEB_ROOT/$s"
  chmod -R u=rwX,go=rX "$WEB_ROOT/$s"
  ok "$WEB_ROOT/$s → owner $DEPLOY_USER, readable by nginx"
done

# ---------------------------------------------------------------- checks
if [[ -f /root/.ssh/authorized_keys ]] && grep -qF "$key_body" /root/.ssh/authorized_keys; then
  warn "this key is ALSO in /root/.ssh/authorized_keys, so CI can still log in as root. Remove it with:"
  echo "    sed -i '\\#$key_body#d' /root/.ssh/authorized_keys" >&2
fi
if command -v sshd >/dev/null 2>&1; then
  allow="$(sshd -T 2>/dev/null | awk 'tolower($1) == "allowusers" { $1 = ""; print }')"
  if [[ -n "$allow" && " $allow " != *" $DEPLOY_USER "* ]]; then
    warn "sshd only allows: $allow — add $DEPLOY_USER to AllowUsers in /etc/ssh/sshd_config and reload sshd"
  fi
fi

cat <<EOF

Done. Test from your machine (should list both directories, owned by $DEPLOY_USER):
  ssh -i ~/.ssh/mikrus_deploy -p <ssh port> $DEPLOY_USER@<host> 'ls -ld $WEB_ROOT/*'
Then set the GitHub secret MIKRUS_USER=$DEPLOY_USER.
If you ever deploy locally as root again, run this script once more afterwards: it gives the
files back to $DEPLOY_USER (new directories created by root would block the next CI deploy).
EOF
