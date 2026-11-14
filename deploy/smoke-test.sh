#!/usr/bin/env bash
# SELENE OPS — smoke test of a deployed site. CI runs it after every upload; works locally too.
#
#   ./deploy/smoke-test.sh <site-url> [local index.html]
#   ./deploy/smoke-test.sh http://kamil177.mikrus.xyz:8080/ out/publish/wwwroot/index.html
#
# Checks HTTP 200 for:
#   /                                    (and that it contains the importmap, see CLAUDE.md → Gotchas)
#   /_framework/blazor.webassembly*.js   the fingerprinted name is read from the served index.html
#   /lib/cesium-*/Cesium.js, widgets.css self-hosted CesiumJS, path read from the served index.html
#   /data/illumination/index.json        and that it is JSON with a non-empty "sites" list
#   /appsettings.json                    and whether it carries a Cesium token (the value is never printed)
# With a local index.html it also checks that the server serves THIS build (same Blazor fingerprint),
# which catches a wrong port/root (stable vs preview) or a stale cache.
#
# Exit codes: 0 all checks passed · 1 a check failed · 2 wrong usage · 3 missing tool
set -euo pipefail

URL="$(printf '%s' "${1:-}" | tr -d '[:space:]')"
LOCAL_INDEX="${2:-}"
if [[ -z "$URL" ]]; then
  echo "Usage: $0 <http(s)://site-url/> [local index.html]" >&2; exit 2
fi
# Same rule as deploy.sh: no scheme → https://
[[ "$URL" == *://* ]] || URL="https://$URL"
[[ "$URL" =~ ^https?:// ]] || { echo "Only http:// and https:// URLs are supported, got: $URL" >&2; exit 2; }
[[ -z "$LOCAL_INDEX" || -f "$LOCAL_INDEX" ]] || { echo "No such file: $LOCAL_INDEX" >&2; exit 2; }
command -v curl >/dev/null 2>&1 || { echo "missing tool: curl" >&2; exit 3; }

BASE="${URL%/}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
FAILED=0
LAST_TYPE=""

IN_GHA=false; [[ "${GITHUB_ACTIONS:-}" == "true" ]] && IN_GHA=true
ok()  { echo "  ok    $*"; }
bad() {
  FAILED=1
  if [[ "$IN_GHA" == true ]]; then echo "::error title=Smoke test::$*"; else echo "  FAIL  $*" >&2; fi
}

export PYTHONIOENCODING=utf-8
PY=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 && "$candidate" -c 'import sys' >/dev/null 2>&1; then PY="$candidate"; break; fi
done

# fetch <path> <outfile> → "<http code> <content type>"; never fails (code 000 = no answer).
fetch() {
  curl -sS -o "$2" -w '%{http_code} %{content_type}' \
       --max-time 30 --retry 3 --retry-delay 3 --retry-connrefused \
       -H 'Cache-Control: no-cache' "$BASE$1" || true
}

# expect_200 <path> <outfile> → 0 if HTTP 200, else reports and returns 1.
expect_200() {
  local res code type
  res="$(fetch "$1" "$2")"
  code="${res%% *}"; type="${res#* }"
  if [[ "$code" == 200 ]]; then
    ok "200  $1  ($(wc -c < "$2" | tr -d ' ') B, ${type:-no content type})"
    LAST_TYPE="$type"
    return 0
  fi
  bad "HTTP ${code:-000} for $BASE$1 (expected 200)"
  return 1
}

# The <script src> (fingerprinted, e.g. _framework/blazor.webassembly.w3qd1tpl0e.js). Not the
# importmap, which also lists the plain name as an alias for module imports.
blazor_name() {
  grep -oE 'src="[^"]*_framework/blazor\.webassembly(\.[A-Za-z0-9]+)?\.js"' "$1" \
    | awk 'NR == 1 { sub(/^src="(\.?\/)?/, ""); sub(/"$/, ""); print }'
}

echo "Smoke test: $BASE/"

# 1. App shell
if expect_200 "/" "$TMP/index.html"; then
  grep -q '<script type="importmap">' "$TMP/index.html" || bad "served index.html has no importmap (the app will not start)"

  # 2. Blazor boot script, name taken from the served page
  served="$(blazor_name "$TMP/index.html")"
  if [[ -z "$served" ]]; then
    bad "served index.html does not reference _framework/blazor.webassembly*.js"
  else
    if [[ -n "$LOCAL_INDEX" ]]; then
      expected="$(blazor_name "$LOCAL_INDEX")"
      if [[ "$served" == "$expected" ]]; then
        ok "served index.html is this build ($served)"
      else
        bad "server serves another build: $served, this build has ${expected:-nothing} (wrong port/root, or the upload did not land)"
      fi
    fi
    if expect_200 "/$served" "$TMP/blazor.js"; then
      [[ "$LAST_TYPE" == *javascript* ]] || bad "/$served is served as '${LAST_TYPE}', expected a JavaScript MIME type"
    fi
  fi

  # 2b. CesiumJS: self-hosted (lib/cesium-<version>/Cesium.js). An absolute URL means it comes from a CDN.
  cesium="$(grep -oE 'src="[^"]*Cesium\.js"' "$TMP/index.html" | awk 'NR == 1 { sub(/^src="(\.?\/)?/, ""); sub(/"$/, ""); print }')"
  if [[ -z "$cesium" ]]; then
    bad "served index.html does not load Cesium.js"
  elif [[ "$cesium" == *://* ]]; then
    ok "Cesium.js comes from a CDN ($cesium), not checked"
  else
    expect_200 "/$cesium" "$TMP/cesium.js" || true
    expect_200 "/${cesium%Cesium.js}Widgets/widgets.css" "$TMP/widgets.css" || true
  fi
fi

# 3. Precomputed data
if expect_200 "/data/illumination/index.json" "$TMP/data-index.json"; then
  if [[ -n "$PY" ]]; then
    if sites="$("$PY" -c 'import json,sys; print(len(json.load(open(sys.argv[1], encoding="utf-8"))["sites"]))' "$TMP/data-index.json" 2>/dev/null)" && [[ "$sites" -gt 0 ]]; then
      ok "data/illumination/index.json: $sites sites"
    else
      bad "data/illumination/index.json is not JSON with a non-empty \"sites\" list"
    fi
  elif ! grep -q '"sites"' "$TMP/data-index.json"; then
    bad "data/illumination/index.json has no \"sites\""
  fi
fi

# 4. Runtime config (fetched by the app at boot). Only reports whether a token is present.
if expect_200 "/appsettings.json" "$TMP/appsettings.json" && [[ -n "$PY" ]]; then
  mode="$("$PY" -c 'import json,sys; print("3D (Cesium token present)" if json.load(open(sys.argv[1], encoding="utf-8")).get("CesiumIonToken") else "2D mosaic (no Cesium token)")' "$TMP/appsettings.json" 2>/dev/null || echo "unreadable")"
  ok "appsettings.json → $mode"
fi

if [[ "$FAILED" -ne 0 ]]; then
  echo "Smoke test FAILED: $BASE/" >&2
  exit 1
fi
echo "Smoke test passed: $BASE/"
