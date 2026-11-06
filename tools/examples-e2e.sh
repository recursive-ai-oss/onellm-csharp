#!/usr/bin/env bash
# Runs each example agent through the `onellm` tool on one model with the representative task from agents/README.md, in a fresh working
# directory each, and prints a table: outcome, turns, cost, wall time.
#
#   tools/examples-e2e.sh --env-file <file> --model <model> --work <dir> (--budget USD | --spend-file F --provider P --cap USD)
#   tools/examples-e2e.sh --mock --work <dir>                      rehearsal against the local mock server (no key, no real call)
#
# Options: --per-run-cap USD (default 0.15; also the most one run may start with)  --timeout SECONDS (default 600)
#          --only name,name   run just these
#
# The spend cap is shared with the live smoke: --budget is what is left, or --spend-file points at the smoke's spend.json and the script
# subtracts its provider's spend from --cap. A run starts only if its --max-cost fits what is left; the rest are reported as skipped.
# Keys are read from the env file inside this script, only the three provider variables, and go to the tool's environment only.
# Every line printed or saved is scrubbed of those values and of key-shaped strings. Needs the .NET 10 SDK on PATH.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE=""; MODEL=""; WORK=""; BUDGET=""; SPEND_FILE=""; PROVIDER=""; CAP=""; MOCK=0
PER_RUN="0.15"; TIMEOUT=600; ONLY=""; PORT="${MOCK_PORT:-5314}"
while [ $# -gt 0 ]; do
  case "$1" in
    --env-file) ENV_FILE="$2"; shift 2 ;;
    --model) MODEL="$2"; shift 2 ;;
    --work) WORK="$2"; shift 2 ;;
    --budget) BUDGET="$2"; shift 2 ;;
    --spend-file) SPEND_FILE="$2"; shift 2 ;;
    --provider) PROVIDER="$2"; shift 2 ;;
    --cap) CAP="$2"; shift 2 ;;
    --per-run-cap) PER_RUN="$2"; shift 2 ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    --only) ONLY="$2"; shift 2 ;;
    --mock) MOCK=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
[ -n "$WORK" ] || { echo "--work is required" >&2; exit 2; }
mkdir -p "$WORK"; WORK="$(cd "$WORK" && pwd)"

ART="${ONELLM_ARTIFACTS:-$ROOT/artifacts}"
CATALOG_FILE=""
MOCK_PID=""
stop() { if [ -n "$MOCK_PID" ]; then kill "$MOCK_PID" 2>/dev/null || true; fi; }
trap stop EXIT

if [ "$MOCK" = 1 ]; then
  MODEL="mockserver:mock-model"
  BUDGET="${BUDGET:-1.00}"
  (cd "$ROOT" && dotnet build -c Release -v quiet -clp:NoSummary tools/OneLlm.MockServer >/dev/null) || exit 1
  dotnet "$ART/bin/OneLlm.MockServer/release/onellm-mockserver.dll" --synthetic --port "$PORT" > "$WORK/mock.log" 2>&1 &
  MOCK_PID=$!
  for _ in $(seq 1 60); do (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && break; sleep 0.5; done
  # Every model turn is a call to task_complete, so each agent ends at once; the rehearsal checks the plumbing, not the agents' work.
  curl -s -X POST "http://127.0.0.1:$PORT/_mock/config?tool=task_complete" >/dev/null
  CATALOG_FILE="$WORK/mock-catalog.yaml"
  cat > "$CATALOG_FILE" <<YAML
name: openai-compatible
presets:
  mockserver:
    displayName: Mock server
    baseUrl: http://127.0.0.1:$PORT/v1
    protocol: chat-completions
    modelPatterns: ["*"]
    capabilities: { streaming: true, tools: true }
    properties: { local: true }
    models:
      - name: mock-model
        contextWindow: 100000
        maxOutputTokens: 4000
        supportsTools: true
        pricing: { asOf: "2026-11-01", source: "https://example.test/prices", inputPerMillion: 1.00, outputPerMillion: 2.00 }
YAML
else
  [ -n "$ENV_FILE" ] && [ -n "$MODEL" ] || { echo "give --env-file and --model, or --mock" >&2; exit 2; }
  for v in OPENAI_API_KEY GEMINI_API_KEY ANTHROPIC_API_KEY; do
    value="$(grep "^$v=" "$ENV_FILE" | tail -n1 | cut -d= -f2- | tr -d "\"'" || true)"
    if [ -n "$value" ]; then export "$v=$value"; fi
  done
  unset value
fi

if [ -z "$BUDGET" ] && [ -n "$SPEND_FILE" ] && [ -n "$PROVIDER" ] && [ -n "$CAP" ]; then
  spent="$(sed -n "s/.*\"$PROVIDER\":\([0-9.]*\).*/\1/p" "$SPEND_FILE")"
  BUDGET="$(awk -v c="$CAP" -v s="${spent:-0}" 'BEGIN { printf "%.6f", c - s }')"
fi
[ -n "$BUDGET" ] || { echo "give --budget, or --spend-file with --provider and --cap" >&2; exit 2; }

# Removes the exact key values (from the environment, via awk's ENVIRON so they never appear in an argument) and key-shaped strings.
scrub() {
  awk '
    BEGIN { n = split("OPENAI_API_KEY GEMINI_API_KEY ANTHROPIC_API_KEY", names, " ") }
    {
      line = $0
      for (i = 1; i <= n; i++) {
        k = ENVIRON[names[i]]
        if (length(k) >= 8) { while ((p = index(line, k)) > 0) line = substr(line, 1, p - 1) "[redacted]" substr(line, p + length(k)) }
      }
      gsub(/sk-[A-Za-z0-9_-]+[A-Za-z0-9]/, "[redacted]", line)
      gsub(/AIza[0-9A-Za-z_-]+/, "[redacted]", line)
      gsub(/[Bb]earer [A-Za-z0-9._~+\/-]+/, "Bearer [redacted]", line)
      gsub(/(req|org|proj|resp|msg|chatcmpl)[-_][A-Za-z0-9]{8,}/, "[id]", line)
      print line
    }'
}

CLIDLL="$ART/bin/OneLlm.Cli/release/OneLlm.Cli.dll"
(cd "$ROOT" && dotnet build -c Release -v quiet -clp:NoSummary src/OneLlm.Cli >/dev/null) || { echo "the onellm tool did not build" >&2; exit 1; }
[ -f "$CLIDLL" ] || { echo "expected the tool at $CLIDLL (set ONELLM_ARTIFACTS to where it was built)" >&2; exit 1; }

# name | grant flags | task
AGENTS=(
  'cs-tool-writer|--allow-shell=dotnet,git|In ./src/Tools, write a tool that converts a length between metres, feet and miles; add a test.'
  'js-tool-writer||In ./tools, write a JS tool that reshapes {firstName, lastName} records into {name} records.'
  'provider-writer|--allow-shell=dotnet,git --allow-net=docs.example.test|Add a catalog preset for the OpenAI-compatible server at https://api.example.test/v1; its docs are at https://docs.example.test.'
  'agent-writer||Write ./agents/changelog.yaml: an agent that reads the git log text in ./log.txt and writes CHANGELOG.md.'
  'script-writer||Write ./commit-message.flow: summarize a diff, then tighten it to a 72-character subject plus a body.'
  'decider-writer||Write ./deciders/triage: classify a support ticket by area, urgency, and whether the customer is angry.'
  'hook-writer|--allow-shell=dotnet,git|Write a BeforeToolCall hook that blocks write_file outside ./out, with tests.'
  'orchestrator|--allow-shell=dotnet,git|Write a tool, a flow and a decider for ticket triage under ./triage, each by its own agent.'
)

TABLE="$WORK/e2e-table.txt"
: > "$TABLE"
emit() { printf '%s\n' "$1" | scrub | tee -a "$TABLE"; }
emit "$(printf '%-16s %-22s %5s %6s %10s %7s  %s' agent outcome exit turns cost secs note)"
remaining="$BUDGET"; total=0
for entry in "${AGENTS[@]}"; do
  IFS='|' read -r name flags task <<< "$entry"
  if [ -n "$ONLY" ] && [[ ",$ONLY," != *",$name,"* ]]; then continue; fi
  fits="$(awk -v r="$remaining" -v p="$PER_RUN" 'BEGIN { print (r >= p) ? 1 : 0 }')"
  if [ "$fits" != 1 ]; then
    emit "$(printf '%-16s %-22s %5s %6s %10s %7s  %s' "$name" 'skipped (budget)' - - - - "left \$$remaining, one run may start with \$$PER_RUN")"
    continue
  fi
  dir="$(mktemp -d "$WORK/$name.XXXXXX")"
  mkdir -p "$dir/.state"
  # decider-writer, agent-writer and hook-writer tasks name files that must exist; a log for agent-writer to read.
  [ "$name" = agent-writer ] && printf 'abc123 fix: handle empty input\ndef456 feat: add retry\n' > "$dir/log.txt"
  start=$(date +%s)
  # $flags is split on purpose: it holds the grant flags.
  # shellcheck disable=SC2086
  out="$(cd "$dir" && ONELLM_CATALOG="$CATALOG_FILE" timeout "$TIMEOUT" dotnet "$CLIDLL" run "examples/$name" --prompt "$task" --model "$MODEL" --max-cost "$PER_RUN" --root . --state "$dir/.state" --yes --quiet $flags 2>&1)"
  code=$?
  secs=$(( $(date +%s) - start ))
  summary="$(printf '%s\n' "$out" | grep -E 'instance .* · .* · [0-9]+ turns · cost' | tail -n1)"
  state="$(printf '%s' "$summary" | sed -E 's/.* · ([a-z_]+) · [0-9]+ turns.*/\1/')"
  turns="$(printf '%s' "$summary" | sed -E 's/.* · ([0-9]+) turns.*/\1/')"
  cost="$(printf '%s' "$summary" | sed -E 's/.*cost \$?([0-9.]+).*/\1/')"
  if [ -z "$summary" ]; then state="-"; turns="-"; cost="0"; fi
  case "$state" in
    stopped) outcome="task_complete" ;;
    waiting) outcome="answered, no task_complete" ;;
    -) outcome="no result (exit $code)" ;;
    *) outcome="$state" ;;
  esac
  note="$(printf '%s' "$out" | grep -vE 'instance .* · ' | tail -n1 | cut -c1-120)"
  remaining="$(awk -v r="$remaining" -v c="$cost" 'BEGIN { printf "%.6f", r - c }')"
  total="$(awk -v t="$total" -v c="$cost" 'BEGIN { printf "%.6f", t + c }')"
  emit "$(printf '%-16s %-22s %5s %6s %10s %7s  %s' "$name" "$outcome" "$code" "$turns" "\$$cost" "$secs" "$note")"
  printf '%s\n' "$out" | scrub > "$dir/run.log"
done
emit "total spent: \$$total; left of the budget: \$$remaining"
