#!/usr/bin/env bash
# SELENE OPS — build the static site and upload it to the Mikrus VPS.
# Works locally (Git Bash, Linux, macOS) and in CI (.github/workflows/deploy.yml): no prompts in CI,
# every path is relative to the repo root, and the exit code says what went wrong.
#
#   ./deploy/deploy.sh preview                build + upload to /var/www/selene/preview  (test first)
#   ./deploy/deploy.sh stable                 build + upload to /var/www/selene/stable   (jury link)
#   ./deploy/deploy.sh preview --build-only   build + checks only, nothing is uploaded
#   ./deploy/deploy.sh preview --upload-only  upload the existing out/publish (CI runs --build-only first)
#
# Optional environment variables:
#   CESIUM_ION_TOKEN         injected into the PUBLISHED appsettings.json only (never into the repo, never printed).
#                            Use a token restricted to the site URLs (Cesium ion → Access Tokens → Allowed URLs).
#   STABLE_URL / PREVIEW_URL public addresses (defaults below): og:image and the check after the upload.
#   MIKRUS_HOST / MIKRUS_PORT / MIKRUS_USER   SSH target (defaults below). After create-deploy-user.sh: MIKRUS_USER=deploy.
#
# Exit codes: 0 ok · 1 build, check or upload failed · 2 wrong usage · 3 missing tool
set -euo pipefail

# ---------------------------------------------------------------- server
MIKRUS_HOST="${MIKRUS_HOST:-kamil177.mikrus.xyz}"
MIKRUS_PORT="${MIKRUS_PORT:-${MIKRUS_SSH_PORT:-10177}}"
MIKRUS_USER="${MIKRUS_USER:-root}"

# Public addresses of the two sites. Change them to the subdomain/port from the Mikrus panel
# if port 80/8080 on this host is not reachable from the internet. Empty counts as unset (CI passes "").
STABLE_URL="${STABLE_URL:-http://kamil177.mikrus.xyz/}"
PREVIEW_URL="${PREVIEW_URL:-http://kamil177.mikrus.xyz:8080/}"

# ---------------------------------------------------------------- output helpers
IN_GHA=false; [[ "${GITHUB_ACTIONS:-}" == "true" ]] && IN_GHA=true
IN_CI=false;  [[ -n "${CI:-}" || "$IN_GHA" == true ]] && IN_CI=true
if [[ -z "${NO_COLOR:-}" && ( -t 1 || "$IN_GHA" == true ) ]]; then
  C_SAY=$'\033[1;36m'; C_WARN=$'\033[1;33m'; C_ERR=$'\033[1;31m'; C_OFF=$'\033[0m'
else
  C_SAY=""; C_WARN=""; C_ERR=""; C_OFF=""
fi
say()  { printf '\n%s==> %s%s\n' "$C_SAY" "$*" "$C_OFF"; }
# In GitHub Actions warnings and errors also become annotations on the run page.
warn() { if [[ "$IN_GHA" == true ]]; then echo "::warning title=deploy.sh::$*"; else printf '%sWARNING: %s%s\n' "$C_WARN" "$*" "$C_OFF" >&2; fi; }
err()  { if [[ "$IN_GHA" == true ]]; then echo "::error title=deploy.sh::$*"; else printf '%sERROR: %s%s\n' "$C_ERR" "$*" "$C_OFF" >&2; fi; }
fail() { err "$*"; exit 1; }

usage() {
  sed -n '2,/^set -euo/{/^set -euo/d;s/^# \{0,1\}//;p}' "${BASH_SOURCE[0]}"
}
usage_error() { err "$*"; echo "Usage: $0 stable|preview [--build-only|--upload-only]   (--help for details)" >&2; exit 2; }

need() {
  local t
  for t in "$@"; do
    command -v "$t" >/dev/null 2>&1 || { err "missing tool: $t"; exit 3; }
  done
}

# ---------------------------------------------------------------- arguments
TARGET=""
MODE=full
for arg in "$@"; do
  case "$arg" in
    stable|preview)
      [[ -z "$TARGET" ]] || usage_error "target given twice"
      TARGET="$arg" ;;
    --build-only|--upload-only)
      [[ "$MODE" == full ]] || usage_error "use only one of --build-only / --upload-only"
      MODE="${arg#--}"; MODE="${MODE%-only}" ;;
    -h|--help) usage; exit 0 ;;
    *) usage_error "unknown argument: $arg" ;;
  esac
done
case "$TARGET" in
  stable)  REMOTE_DIR=/var/www/selene/stable;  SITE_URL="$STABLE_URL" ;;
  preview) REMOTE_DIR=/var/www/selene/preview; SITE_URL="$PREVIEW_URL" ;;
  *) usage_error "missing target (stable or preview)" ;;
esac
[[ "$MIKRUS_PORT" =~ ^[0-9]+$ ]] || usage_error "MIKRUS_PORT must be a number, got '$MIKRUS_PORT'"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
OUT=out/publish
WWW="$OUT/wwwroot"
BUILD_INFO="$OUT/selene-build.txt"   # outside wwwroot: never uploaded

# A working Python 3 (on Windows `python3` is often only the Microsoft Store stub). UTF-8 output:
# Windows Python would otherwise print with the console code page and crash on "→" when piped.
export PYTHONIOENCODING=utf-8
PY=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import sys; sys.exit(sys.version_info < (3, 8))' >/dev/null 2>&1; then
    PY="$candidate"; break
  fi
done

# Re-create the compressed variants after editing a published file (nginx gzip_static would
# otherwise keep serving the stale .gz). Brotli is regenerated if the CLI exists, else removed.
recompress() {
  local f="$1"
  gzip -9 -k -f "$f"
  if command -v brotli >/dev/null 2>&1; then brotli -q 11 -f -o "$f.br" "$f"; else rm -f "$f.br"; fi
}

# Exact byte count of the published files matching the given find(1) tests.
bytes_of() { find "$WWW" -type f "$@" -exec cat {} + | wc -c | tr -d ' '; }
human() { awk -v b="$1" 'BEGIN { if (b >= 1048576) printf "%.1f MB", b/1048576; else printf "%.0f kB", b/1024 }'; }

# ---------------------------------------------------------------- build
build() {
  need dotnet gzip find awk sort
  [[ -n "$PY" ]] || { err "missing tool: python3 (3.8+)"; exit 3; }

  say "dotnet publish (Release) → $OUT"
  rm -rf "$OUT"
  dotnet publish SeleneFrontend -c Release -o "$OUT" --nologo

  say "Checking the build"
  local p
  for p in index.html _framework data/illumination/index.json appsettings.json; do
    [[ -e "$WWW/$p" ]] || fail "missing $WWW/$p"
    echo "  ok  $p"
  done
  if [[ -e "$WWW/appsettings.Development.json" ]]; then
    fail "appsettings.Development.json (local Cesium token) must never be published"
  fi
  grep -q '<script type="importmap">' "$WWW/index.html" || fail "index.html has no importmap (see CLAUDE.md → Gotchas)"
  local blazor
  blazor=$(grep -oE 'src="[^"]*_framework/blazor\.webassembly(\.[A-Za-z0-9]+)?\.js"' "$WWW/index.html" | awk 'NR == 1 { sub(/^src="(\.?\/)?/, ""); sub(/"$/, ""); print }')
  [[ -n "$blazor" ]] || fail "index.html has no <script src=\"_framework/blazor.webassembly*.js\"> (blazor.web.js gives a black screen, see CLAUDE.md)"
  [[ -f "$WWW/$blazor" ]] || fail "index.html loads $blazor, but the file is not in the build"
  echo "  ok  $blazor"

  # Optional Cesium ion token, injected into the published copy only. Python reads it from the
  # environment (not argv, so it never shows up in a process list), and it is never echoed.
  local token_state=empty
  if [[ -n "${CESIUM_ION_TOKEN:-}" ]]; then
    [[ "$IN_GHA" == true ]] && echo "::add-mask::$CESIUM_ION_TOKEN"
    CESIUM_ION_TOKEN="$CESIUM_ION_TOKEN" "$PY" - "$WWW/appsettings.json" <<'PY'
import json, os, sys
path = sys.argv[1]
with open(path, encoding="utf-8") as f:
    cfg = json.load(f)
cfg["CesiumIonToken"] = os.environ["CESIUM_ION_TOKEN"]
with open(path, "w", encoding="utf-8") as f:
    json.dump(cfg, f, indent=2)
    f.write("\n")
PY
    recompress "$WWW/appsettings.json"
    echo "  ok  CesiumIonToken injected from \$CESIUM_ION_TOKEN (published copy only)"
  fi
  if [[ "$("$PY" -c 'import json,sys; print("yes" if json.load(open(sys.argv[1], encoding="utf-8")).get("CesiumIonToken") else "no")' "$WWW/appsettings.json")" == yes ]]; then
    token_state=injected
  else
    warn "CesiumIonToken in appsettings.json is empty → the site runs in 2D MOSAIC MODE (no 3D terrain). For 3D set CESIUM_ION_TOKEN (a token restricted to $SITE_URL)."
  fi

  # Absolute og:image so link previews (Messenger, Slack, Discord…) can fetch it.
  "$PY" - "$WWW/index.html" "${SITE_URL%/}/og.png" <<'PY'
import sys
path, url = sys.argv[1], sys.argv[2]
with open(path, encoding="utf-8") as f:
    html = f.read()
new = html.replace('content="og.png"', f'content="{url}"')
if new != html:
    with open(path, "w", encoding="utf-8", newline="") as f:
        f.write(new)
    print(f"  ok  og:image → {url}")
PY
  recompress "$WWW/index.html"

  # 3D models are not compressed by dotnet publish; pre-compress them for gzip_static.
  local glb
  while IFS= read -r -d '' glb; do
    gzip -9 -k -f "$glb"
    echo "  ok  pre-compressed ${glb#"$WWW/"} ($(human "$(wc -c < "$glb")") → $(human "$(wc -c < "$glb.gz")"))"
  done < <(find "$WWW" -name '*.glb' -print0)

  say "Size"
  local total raw files
  total=$(bytes_of)
  raw=$(bytes_of ! -name '*.gz' ! -name '*.br')
  files=$(find "$WWW" -type f | wc -l | tr -d ' ')
  echo "  $(human "$total") in $files files ($(human "$raw") without the .gz/.br variants)"
  echo "Largest files:"
  # awk reads the whole list (no early-closing `head`, so no SIGPIPE under pipefail).
  find "$WWW" -type f -exec du -k {} + | sort -rn | awk -v p="$WWW/" 'NR <= 10 { f = $2; if (index(f, p) == 1) f = substr(f, length(p) + 1); printf "  %8.1f MB  %s\n", $1/1024, f }'

  # Which target this build was made for (og:image differs): checked by --upload-only.
  {
    echo "target=$TARGET"
    echo "site_url=$SITE_URL"
    echo "commit=$(git rev-parse --short HEAD 2>/dev/null || echo unknown)"
  } > "$BUILD_INFO"

  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    {
      echo "publish_dir=$WWW"
      echo "size_total=$(human "$total")"
      echo "size_raw=$(human "$raw")"
      echo "files=$files"
      echo "token=$token_state"
    } >> "$GITHUB_OUTPUT"
  fi
}

# ---------------------------------------------------------------- upload
upload() {
  need rsync ssh
  [[ -f "$WWW/index.html" && -f "$BUILD_INFO" ]] || fail "nothing to upload in $WWW — build first (--build-only)"
  local built_for
  built_for=$(sed -n 's/^target=//p' "$BUILD_INFO")
  [[ "$built_for" == "$TARGET" ]] || fail "$WWW was built for '$built_for', not '$TARGET' (og:image differs) — rebuild with: $0 $TARGET --build-only"

  # CI must never wait for a prompt: key auth only, known_hosts must already contain the server.
  local ssh_cmd="ssh -p $MIKRUS_PORT"
  if [[ "$IN_CI" == true ]]; then
    ssh_cmd+=" -o BatchMode=yes -o StrictHostKeyChecking=yes -o ConnectTimeout=20"
  fi

  say "rsync → $MIKRUS_USER@$MIKRUS_HOST:$REMOTE_DIR/"
  # -rlt + --chmod: same permissions whatever the source OS (nginx needs dirs 755 / files 644), and
  # no owner/group, so it works as root and as the unprivileged deploy user (create-deploy-user.sh).
  # --delay-updates + --delete-after: new files go live together at the end, old ones vanish after,
  # so visitors never get a new index.html that points at files that are not there yet.
  rsync -rltpz --omit-dir-times --chmod=D755,F644 --delete-after --delay-updates \
        --stats --human-readable -e "$ssh_cmd" \
        "$WWW/" "$MIKRUS_USER@$MIKRUS_HOST:$REMOTE_DIR/"
}

check_site() {
  say "Checking $SITE_URL"
  if bash "$REPO_ROOT/deploy/smoke-test.sh" "$SITE_URL" "$WWW/index.html"; then :; else
    warn "smoke test of $SITE_URL failed — check the port / panel subdomain (deploy/README.md, step 3)"
  fi
}

case "$MODE" in
  full)   build; upload; check_site ;;
  build)  build; say "Build only — nothing uploaded. Output: $WWW"; exit 0 ;;
  upload) upload ;;
esac

say "Done: $TARGET → $SITE_URL"
