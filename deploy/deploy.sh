#!/usr/bin/env bash
# SELENE OPS — build and upload the static site to the Mikrus VPS.
#
#   ./deploy/deploy.sh preview               build + upload to /var/www/selene/preview  (test first)
#   ./deploy/deploy.sh stable                build + upload to /var/www/selene/stable   (jury link)
#   ./deploy/deploy.sh preview --build-only  build + checks only, nothing is uploaded
#
# Optional environment variables:
#   CESIUM_ION_TOKEN=...   injected into the PUBLISHED appsettings.json only (never into the repo).
#                          Use a token restricted to the site URL (Cesium ion → Access Tokens → Allowed URLs).
#   STABLE_URL / PREVIEW_URL   public addresses (defaults below) — used for the final check and og:image.
set -euo pipefail

# ---------------------------------------------------------------- server
MIKRUS_HOST=kamil177.mikrus.xyz
MIKRUS_SSH_PORT=10177
MIKRUS_USER=root

# Public addresses of the two sites. Change them to the subdomain/port from the Mikrus panel
# if port 80/8080 on this host is not reachable from the internet.
STABLE_URL="${STABLE_URL:-http://kamil177.mikrus.xyz/}"
PREVIEW_URL="${PREVIEW_URL:-http://kamil177.mikrus.xyz:8080/}"

# ---------------------------------------------------------------- arguments
TARGET="${1:-}"
BUILD_ONLY="${2:-}"
case "$TARGET" in
  stable)  REMOTE_DIR=/var/www/selene/stable;  SITE_URL="$STABLE_URL" ;;
  preview) REMOTE_DIR=/var/www/selene/preview; SITE_URL="$PREVIEW_URL" ;;
  *) echo "Usage: $0 stable|preview [--build-only]" >&2; exit 2 ;;
esac
if [[ -n "$BUILD_ONLY" && "$BUILD_ONLY" != "--build-only" ]]; then
  echo "Unknown option: $BUILD_ONLY (expected --build-only)" >&2; exit 2
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
OUT=out/publish
WWW="$OUT/wwwroot"

say()  { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
warn() { printf '\033[1;33mWARNING: %s\033[0m\n' "$*" >&2; }
fail() { printf '\033[1;31mERROR: %s\033[0m\n' "$*" >&2; exit 1; }

# Re-create the compressed variants after editing a published file (nginx gzip_static would
# otherwise keep serving the stale .gz). Brotli is regenerated if the CLI exists, else removed.
recompress() {
  local f="$1"
  gzip -9 -k -f "$f"
  if command -v brotli >/dev/null 2>&1; then brotli -q 11 -f -o "$f.br" "$f"; else rm -f "$f.br"; fi
}

# ---------------------------------------------------------------- build
say "dotnet publish (Release) → $OUT"
rm -rf "$OUT"
dotnet publish SeleneFrontend -c Release -o "$OUT"

say "Checking the build"
for p in index.html _framework data/illumination/index.json appsettings.json; do
  [[ -e "$WWW/$p" ]] || fail "missing $WWW/$p"
  echo "  ok  $p"
done
if [[ -e "$WWW/appsettings.Development.json" ]]; then
  fail "appsettings.Development.json (local Cesium token) must never be published"
fi

# Optional Cesium ion token, injected into the published copy only.
if [[ -n "${CESIUM_ION_TOKEN:-}" ]]; then
  python3 - "$WWW/appsettings.json" "$CESIUM_ION_TOKEN" <<'PY'
import json, sys
path, token = sys.argv[1], sys.argv[2]
with open(path, encoding="utf-8") as f:
    cfg = json.load(f)
cfg["CesiumIonToken"] = token
with open(path, "w", encoding="utf-8") as f:
    json.dump(cfg, f, indent=2)
    f.write("\n")
PY
  recompress "$WWW/appsettings.json"
  echo "  ok  CesiumIonToken injected from \$CESIUM_ION_TOKEN (published copy only)"
fi
TOKEN_SET=$(python3 -c 'import json,sys; print("yes" if json.load(open(sys.argv[1])).get("CesiumIonToken") else "no")' "$WWW/appsettings.json")
if [[ "$TOKEN_SET" != "yes" ]]; then
  warn "CesiumIonToken in appsettings.json is empty → the site runs in 2D MOSAIC MODE (no 3D terrain)."
  warn "For 3D: CESIUM_ION_TOKEN=<token restricted to $SITE_URL> $0 $TARGET"
fi

# Absolute og:image so link previews (Messenger, Slack, Discord…) can fetch it.
python3 - "$WWW/index.html" "${SITE_URL%/}/og.png" <<'PY'
import sys
path, url = sys.argv[1], sys.argv[2]
html = open(path, encoding="utf-8").read()
new = html.replace('content="og.png"', f'content="{url}"')
if new != html:
    open(path, "w", encoding="utf-8").write(new)
    print(f"  ok  og:image → {url}")
PY
recompress "$WWW/index.html"

# 3D models are not compressed by dotnet publish; pre-compress them for gzip_static.
while IFS= read -r -d '' glb; do
  gzip -9 -k -f "$glb"
  echo "  ok  pre-compressed ${glb#"$WWW/"} ($(du -h "$glb" | cut -f1) → $(du -h "$glb.gz" | cut -f1))"
done < <(find "$WWW" -name '*.glb' -print0)

say "Size"
du -sh "$WWW"
echo "Largest files:"
find "$WWW" -type f -exec du -k {} + | sort -rn | head -10 | awk '{ printf "  %8.1f MB  %s\n", $1/1024, $2 }' | sed "s#$WWW/##"

if [[ "$BUILD_ONLY" == "--build-only" ]]; then
  say "Build only — nothing uploaded. Output: $WWW"
  exit 0
fi

# ---------------------------------------------------------------- upload
say "rsync → $MIKRUS_USER@$MIKRUS_HOST:$REMOTE_DIR/"
rsync -avz --delete -e "ssh -p $MIKRUS_SSH_PORT" "$WWW/" "$MIKRUS_USER@$MIKRUS_HOST:$REMOTE_DIR/"

say "Checking $SITE_URL"
if curl -sI --max-time 15 "$SITE_URL" | head -n 8; then :; else warn "no HTTP answer from $SITE_URL (check the port / panel subdomain)"; fi

say "Done: $TARGET → $SITE_URL"
