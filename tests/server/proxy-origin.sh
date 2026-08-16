#!/usr/bin/env bash
# Exercises the audio proxy's same-origin check against a REAL running server.
#
# The Playwright suite mocks /api/proxy/drive/*, so it never touches this code —
# which is exactly how a 403-on-every-playback shipped to production. The
# failure only reproduces when the client says "https://host" while the server
# sees scheme=http, as it does behind Cloud Run's TLS termination.
#
# Allowed requests are expected to return 401: the proxy accepts them and
# forwards a dummy token to Google, which rejects it. 401 therefore means
# "the origin check let it through", which is what we are testing.
#
# Exit 0 = all checks passed, 1 = a check failed.
set -uo pipefail

PORT="${PORT:-5299}"
BASE="http://localhost:$PORT"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LOG=/tmp/proxy-origin-server.log
FAILED=0

check() {
  local desc="$1" expected="$2" actual="$3"
  if [[ "$actual" == "$expected" ]]; then
    printf '  ✓ %-56s %s\n' "$desc" "$actual"
  else
    printf '  ✗ %-56s got %s, want %s\n' "$desc" "$actual" "$expected"
    FAILED=1
  fi
}

status() { curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$@"; }

echo "Starting server on :$PORT ..."
# net9.0 target, but only the .NET 10 runtime is installed locally; the
# container image has 9.0 so this is a local-dev concern only.
(cd "$ROOT" && ASPNETCORE_URLS="$BASE" DOTNET_ROLL_FORWARD=Major \
  ASPNETCORE_ENVIRONMENT=Development \
  dotnet run --project TraktorGoogleDrive.Server/TraktorGoogleDrive.Server.csproj \
  >"$LOG" 2>&1) &
SERVER_PID=$!
trap 'kill $SERVER_PID 2>/dev/null; wait $SERVER_PID 2>/dev/null' EXIT

# Probe the API, not "/": readiness must not depend on static assets being
# wired up, or a static-file regression looks like "server never started".
UP=0
for _ in $(seq 1 90); do
  code=$(status "$BASE/api/proxy/drive/x")
  if [[ "$code" == "400" ]]; then UP=1; break; fi
  sleep 1
done

if [[ $UP -ne 1 ]]; then
  echo "  ✗ server never became ready — last 15 lines of $LOG:"
  tail -15 "$LOG" | sed 's/^/      /'
  exit 1
fi

URL="$BASE/api/proxy/drive/some-file-id?token=dummy"

echo "Same-origin check:"

# The regression: browser sends https, server sees http. Must be allowed.
check "https Referer, http server (the prod case)" "401" \
  "$(status -H "Referer: https://localhost:$PORT/playlist/abc" "$URL")"

check "https Origin, http server" "401" \
  "$(status -H "Origin: https://localhost:$PORT" "$URL")"

check "same-origin http Referer" "401" \
  "$(status -H "Referer: $BASE/playlist/abc" "$URL")"

check "no Origin and no Referer (curl) is allowed" "401" \
  "$(status "$URL")"

check "cross-origin Origin is rejected" "403" \
  "$(status -H 'Origin: https://evil.example' "$URL")"

check "cross-origin Referer is rejected" "403" \
  "$(status -H 'Referer: https://evil.example/page' "$URL")"

check "missing token is a 400" "400" \
  "$(status "$BASE/api/proxy/drive/some-file-id")"

echo "Static assets (the ProjectReference must actually serve the client):"
check "/ serves the app shell" "200" "$(status "$BASE/")"
check "auth.js is served" "200" "$(status "$BASE/auth.js")"
check "index.html is not cached without revalidating" "no-cache" \
  "$(curl -s -o /dev/null -D - --max-time 20 "$BASE/" | grep -i '^cache-control' | tr -d '\r' | awk '{print $2}')"

echo
if [[ $FAILED -eq 0 ]]; then echo "All proxy-origin checks passed."; else echo "FAILURES — see above."; fi
exit $FAILED
