#!/usr/bin/env bash
# Live smoke: a few tiny real calls per keyed provider under hard spend caps, or the same run against the local mock server.
#
#   tools/live-smoke.sh --env-file <file> --out <dir>            real calls; keys are read from the file inside this script
#   tools/live-smoke.sh --mock --out <dir>                       rehearsal against the local mock server, no key, no real call
#
# Options: --openai-cap USD (default 0.90)  --gemini-cap USD (default 0.45)  --anthropic-cap USD (default 0.45)
#          --providers openai,gemini,anthropic
#
# The env file is only read for OPENAI_API_KEY, GEMINI_API_KEY and ANTHROPIC_API_KEY; the values go to the console's environment and
# nowhere else (never to an argument, a log or a trace). Writes <dir>/live-smoke.txt (scrubbed) and <dir>/spend.json (dollars per
# provider), which tools/examples-e2e.sh reads to find what is left. Needs the .NET 10 SDK on PATH.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE=""
OUT=""
MOCK=0
PORT="${MOCK_PORT:-5313}"
EXTRA=()
while [ $# -gt 0 ]; do
  case "$1" in
    --env-file) ENV_FILE="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --mock) MOCK=1; shift ;;
    --openai-cap|--gemini-cap|--anthropic-cap|--providers) EXTRA+=("$1" "$2"); shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -n "$OUT" ] || { echo "--out is required" >&2; exit 2; }
if [ "$MOCK" = 0 ] && [ -z "$ENV_FILE" ]; then echo "give --env-file, or --mock" >&2; exit 2; fi
mkdir -p "$OUT"

if [ "$MOCK" = 0 ]; then
  for v in OPENAI_API_KEY GEMINI_API_KEY ANTHROPIC_API_KEY; do
    # `|| true`: a variable missing from the file is not an error; that provider is skipped.
    value="$(grep "^$v=" "$ENV_FILE" | tail -n1 | cut -d= -f2- | tr -d "\"'" || true)"
    if [ -n "$value" ]; then export "$v=$value"; fi
  done
  unset value
fi

(cd "$ROOT" && dotnet build -c Release -v quiet -clp:NoSummary tools/OneLlm.LiveSmoke >/dev/null)
MOCK_PID=""
stop() { if [ -n "$MOCK_PID" ]; then kill "$MOCK_PID" 2>/dev/null || true; fi; }
trap stop EXIT
ARGS=(--out "$OUT/live-smoke.txt" --spend-out "$OUT/spend.json")
if [ "$MOCK" = 1 ]; then
  (cd "$ROOT" && dotnet build -c Release -v quiet -clp:NoSummary tools/OneLlm.MockServer >/dev/null)
  ART="${ONELLM_ARTIFACTS:-$ROOT/artifacts}"
  dotnet "$ART/bin/OneLlm.MockServer/release/onellm-mockserver.dll" --synthetic --port "$PORT" > "$OUT/mock.log" 2>&1 &
  MOCK_PID=$!
  for _ in $(seq 1 60); do (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && break; sleep 0.5; done
  # Short fixed answer, so the checks that look for words in the answer can pass.
  curl -s -X POST "http://127.0.0.1:$PORT/_mock/config?bytes=4&flushEvery=1&text=pong%20391" >/dev/null
  ARGS+=(--mock-url "http://127.0.0.1:$PORT" --providers openai,gemini)
fi

set +e
(cd "$ROOT" && dotnet run -c Release --no-build --project tools/OneLlm.LiveSmoke -- "${ARGS[@]}" ${EXTRA[@]+"${EXTRA[@]}"})
status=$?
set -e
echo "live smoke exit code: $status (report: $OUT/live-smoke.txt, spend: $OUT/spend.json)"
exit "$status"
