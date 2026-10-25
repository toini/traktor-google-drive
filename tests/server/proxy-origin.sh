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
# The Cast ticket checks work the same way: a ticket the server accepts reaches
# Google (401 for the dummy token), one it refuses is a 403 from us, and a valid
# ticket with no token behind it is a 401 marked "X-Cast-Ticket: no-token".
#
# Exit 0 = all checks passed, 1 = a check failed.
set -uo pipefail

PORT="${PORT:-5299}"
BASE="http://localhost:$PORT"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LOG=/tmp/proxy-origin-server.log
FAILED=0
# Fixed, so tickets survive the restart check and an expired one can be forged.
SECRET="proxy-origin-test-secret-0123456789abcdef"

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

SERVER_PID=""
start_server() {
  echo "Starting server on :$PORT ..."
  # net9.0 target, but only the .NET 10 runtime is installed locally; the
  # container image has 9.0 so this is a local-dev concern only.
  (cd "$ROOT" && ASPNETCORE_URLS="$BASE" DOTNET_ROLL_FORWARD=Major \
    ASPNETCORE_ENVIRONMENT=Development RemoteAuth__TicketSecret="$SECRET" \
    dotnet run --project TraktorGoogleDrive.Server/TraktorGoogleDrive.Server.csproj \
    >"$LOG" 2>&1) &
  SERVER_PID=$!

  # Probe the API, not "/": readiness must not depend on static assets being
  # wired up, or a static-file regression looks like "server never started".
  for _ in $(seq 1 90); do
    if [[ "$(status "$BASE/api/proxy/drive/x")" == "400" ]]; then return 0; fi
    sleep 1
  done
  echo "  ✗ server never became ready — last 15 lines of $LOG:"
  tail -15 "$LOG" | sed 's/^/      /'
  exit 1
}

stop_server() {
  [[ -n "$SERVER_PID" ]] || return 0
  # dotnet run starts the app as a child; take the whole group down.
  pkill -P "$SERVER_PID" 2>/dev/null
  kill "$SERVER_PID" 2>/dev/null
  wait "$SERVER_PID" 2>/dev/null
  # The port is free once nothing answers on it.
  for _ in $(seq 1 30); do
    [[ "$(status "$BASE/api/proxy/drive/x")" == "000" ]] && break
    sleep 1
  done
  SERVER_PID=""
}
trap stop_server EXIT

start_server

URL="$BASE/api/proxy/drive/some-file-id"
COOKIE=(--cookie "drive_token=dummy")

echo "Same-origin check:"

# The regression: browser sends https, server sees http. Must be allowed.
check "https Referer, http server (the prod case)" "401" \
  "$(status "${COOKIE[@]}" -H "Referer: https://localhost:$PORT/playlist/abc" "$URL")"

check "https Origin, http server" "401" \
  "$(status "${COOKIE[@]}" -H "Origin: https://localhost:$PORT" "$URL")"

check "same-origin http Referer" "401" \
  "$(status "${COOKIE[@]}" -H "Referer: $BASE/playlist/abc" "$URL")"

check "no Origin and no Referer (curl) is allowed" "401" \
  "$(status "${COOKIE[@]}" "$URL")"

check "cross-origin Origin is rejected" "403" \
  "$(status "${COOKIE[@]}" -H 'Origin: https://evil.example' "$URL")"

check "cross-origin Referer is rejected" "403" \
  "$(status "${COOKIE[@]}" -H 'Referer: https://evil.example/page' "$URL")"

check "missing token is a 400" "400" \
  "$(status "$BASE/api/proxy/drive/some-file-id")"

# The browser's player sends the token as a cookie, so its URLs carry none.
# 401 (not 400) means the proxy found the cookie and forwarded it to Google.
check "token from the drive_token cookie is accepted" "401" \
  "$(status -H "Referer: $BASE/playlist/abc" --cookie "drive_token=dummy" "$BASE/api/proxy/drive/some-file-id")"

# Cast has its own route now; nothing may put the token in a URL any more.
check "?token= is no longer accepted (no cookie: 400)" "400" \
  "$(status "$URL?token=dummy")"

echo "Cast tickets:"

# POST to the ticket endpoints as the page does: same origin, token in a header.
api() {
  local path="$1" body="$2"; shift 2
  curl -s --max-time 20 -X POST -H "Origin: $BASE" -H 'Content-Type: application/json' \
    "$@" -d "$body" "$BASE$path"
}
api_status() {
  local path="$1" body="$2"; shift 2
  curl -s -o /dev/null -w '%{http_code}' --max-time 20 -X POST -H "Origin: $BASE" \
    -H 'Content-Type: application/json' "$@" -d "$body" "$BASE$path"
}
# printf, not a "{…,…}" literal: bash brace-expands an unquoted-looking comma list.
renew_body() { printf '{"ticket":"%s","renewKey":"%s"}' "$1" "$2"; }
json() { python3 -c "import json,sys; print(json.load(sys.stdin)['$1'])"; }

# Status of a Cast fetch, plus " no-token" when we (not Google) said 401.
cast_get() {
  local headers code
  headers=$(curl -s -o /dev/null -D - --max-time 20 -H 'Range: bytes=0-' "$@")
  code=$(printf '%s' "$headers" | awk 'NR==1 {print $2}')
  if printf '%s' "$headers" | grep -qi '^x-cast-ticket: no-token'; then code="$code no-token"; fi
  printf '%s' "$code"
}

MINT=$(api /api/cast/tickets '{"fileId":"file-a"}' -H 'Authorization: Bearer dummy')
TICKET=$(printf '%s' "$MINT" | json ticket)
RENEW=$(printf '%s' "$MINT" | json renewKey)
CAST="$BASE/api/proxy/cast"

check "a minted ticket is not the token" "yes" \
  "$([[ -n "$TICKET" && "$TICKET" != *dummy* ]] && echo yes || echo no)"
check "minting cross-origin is refused" "403" \
  "$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Origin: https://evil.example' \
     -H 'Authorization: Bearer dummy' -H 'Content-Type: application/json' -d '{"fileId":"file-a"}' \
     "$BASE/api/cast/tickets")"
check "minting without a token is refused" "401" \
  "$(api_status /api/cast/tickets '{"fileId":"file-a"}')"
check "minting for a malformed file id is refused" "400" \
  "$(api_status /api/cast/tickets '{"fileId":"../etc"}' -H 'Authorization: Bearer dummy')"

check "the ticket works for its own file (as a TV: no headers)" "401" \
  "$(cast_get "$CAST/file-a?ticket=$TICKET")"
check "the same ticket is refused for another file" "403" \
  "$(cast_get "$CAST/file-b?ticket=$TICKET")"
check "a forged signature is refused" "403" \
  "$(cast_get "$CAST/file-a?ticket=${TICKET%.*}.AAAA${TICKET##*.}")"
check "garbage is refused" "403" "$(cast_get "$CAST/file-a?ticket=not-a-ticket")"
check "no ticket is refused, even with the drive_token cookie" "403" \
  "$(cast_get --cookie "drive_token=dummy" "$CAST/file-a")"
check "a cross-origin page is refused, ticket or not" "403" \
  "$(cast_get -H 'Origin: https://evil.example' "$CAST/file-a?ticket=$TICKET")"

# Signed with the real key, for the right file, but past its exp. Built the way
# CastTickets builds one: HKDF-SHA256 over the secret, then HMAC-SHA256.
EXPIRED=$(SECRET="$SECRET" python3 - <<'PY'
import base64, hashlib, hmac, os, time
b64 = lambda b: base64.urlsafe_b64encode(b).rstrip(b"=").decode()
prk = hmac.new(b"\0" * 32, os.environ["SECRET"].encode(), hashlib.sha256).digest()
key = hmac.new(prk, b"traktor-google-drive/cast-ticket/v1\x01", hashlib.sha256).digest()
payload = f"c1|expiredgrant|file-a|{int(time.time()) - 60}".encode()
print(b64(payload) + "." + b64(hmac.new(key, payload, hashlib.sha256).digest()))
PY
)
check "an expired (correctly signed) ticket is refused" "403" \
  "$(cast_get "$CAST/file-a?ticket=$EXPIRED")"
check "renewing an expired ticket says Gone" "410" \
  "$(api_status /api/cast/tickets/renew "$(renew_body "$EXPIRED" "x")" -H 'Authorization: Bearer dummy')"

check "renewing with the wrong renew key is refused" "403" \
  "$(api_status /api/cast/tickets/renew "$(renew_body "$TICKET" "wrong")" -H 'Authorization: Bearer dummy2')"
check "renewing with the right renew key works" "204" \
  "$(api_status /api/cast/tickets/renew "$(renew_body "$TICKET" "$RENEW")" -H 'Authorization: Bearer dummy2')"
check "after revoking, the ticket has no token behind it" "204 / 401 no-token" \
  "$(api_status /api/cast/tickets/revoke "$(renew_body "$TICKET" "$RENEW")") / $(cast_get "$CAST/file-a?ticket=$TICKET")"

# The cost of keeping tokens in memory: a restart drops them. The ticket stays
# valid (stable secret), and the sender's next renewal puts the token back.
MINT=$(api /api/cast/tickets '{"fileId":"file-r"}' -H 'Authorization: Bearer dummy')
TICKET=$(printf '%s' "$MINT" | json ticket)
RENEW=$(printf '%s' "$MINT" | json renewKey)
echo "  (restarting the server)"
stop_server
start_server
check "after a restart the ticket is valid but holds no token" "401 no-token" \
  "$(cast_get "$CAST/file-r?ticket=$TICKET")"
check "the sender's renewal restores it" "204" \
  "$(api_status /api/cast/tickets/renew "$(renew_body "$TICKET" "$RENEW")" -H 'Authorization: Bearer dummy')"
check "and the TV's URL works again" "401" "$(cast_get "$CAST/file-r?ticket=$TICKET")"

echo "Static assets (the ProjectReference must actually serve the client):"
check "/ serves the app shell" "200" "$(status "$BASE/")"
check "auth.js is served" "200" "$(status "$BASE/auth.js")"
check "index.html is not cached without revalidating" "no-cache" \
  "$(curl -s -o /dev/null -D - --max-time 20 "$BASE/" | grep -i '^cache-control' | tr -d '\r' | awk '{print $2}')"

echo
if [[ $FAILED -eq 0 ]]; then echo "All proxy-origin checks passed."; else echo "FAILURES — see above."; fi
exit $FAILED
