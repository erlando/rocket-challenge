#!/usr/bin/env bash
# Runs the rockets test program against the capture server and prints how it behaved.
# Used in Phase 0 to observe the test program; see docs/devdiary.md entry 15.
#
# usage: scripts/probe.sh <name> <timeout-seconds> [rockets launch args...] -- [capture server args...]
# example: scripts/probe.sh probe-503 120 --max-messages=50 --message-delay=50ms -- --Probe:FirstAttemptStatus=503
#
# Note: rockets opens a new connection per message. On Windows, back-to-back large runs can use up
# the client ports (TIME_WAIT); wait about a minute between runs of 100k messages.
set -u
name=$1; limit=$2; shift 2
rockets_args=(); while [ $# -gt 0 ] && [ "$1" != "--" ]; do rockets_args+=("$1"); shift; done; [ $# -gt 0 ] && shift

root=$(cd "$(dirname "$0")/.." && pwd)
capture_tool="$root/tools/Rockets.Capture/bin/Debug/net10.0/Rockets.Capture.dll"
case "$(uname -s)-$(uname -m)" in
  MINGW*|MSYS*|CYGWIN*) rockets="$root/vendor/rockets/windows_amd64/rockets.exe" ;;
  Darwin-arm64)         rockets="$root/vendor/rockets/darwin_arm64/rockets" ;;
  Darwin-*)             rockets="$root/vendor/rockets/darwin_amd64/rockets" ;;
  Linux-aarch64)        rockets="$root/vendor/rockets/linux_arm64/rockets" ;;
  *)                    rockets="$root/vendor/rockets/linux_amd64/rockets" ;;
esac

if curl -s -o /dev/null http://localhost:8088/; then echo "Something is already listening on port 8088. Stop it first." >&2; exit 1; fi

out="$root/artifacts/capture"; mkdir -p "$out"
capture="$out/$name.ndjson"; rm -f "$capture"

dotnet "$capture_tool" serve --Capture:Path="$capture" "$@" > "$out/$name.server.log" 2>&1 &
server=$!
for _ in $(seq 1 20); do curl -s -o /dev/null http://localhost:8088/ && break; sleep 0.5; done

start=$(date +%s)
timeout "$limit" "$rockets" launch "http://localhost:8088/messages" "${rockets_args[@]}" > "$out/$name.rockets.log" 2>&1
echo "[$name] rockets exit=$? after $(( $(date +%s) - start ))s"
# A forced kill: from Git Bash, a plain kill can leave the native dotnet process running and holding the port.
kill -9 "$server" 2>/dev/null; wait "$server" 2>/dev/null

echo "rockets log, excluding successful sends:"
grep -v "Message sent" "$out/$name.rockets.log" | sed -E 's/^[0-9\/: ]+//' | cut -c1-110 | sort | uniq -c | sort -rn | head -6
dotnet "$capture_tool" analyze "$capture"
