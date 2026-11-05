#!/usr/bin/env bash
# Runs the speed rows against the local mock server and writes a results table.
#
#   bench/run.sh [--group request|stream|agent|count|own|all] [--mode gate|indicative] [--out DIR] [--report-only]
#
# Groups are sized to finish within about 20 minutes each on a quiet machine. Each group starts the mock server, records the machine's
# load before, during and after, runs its benchmarks with BenchmarkDotNet, and stops the server. Results of every group accumulate in
# --out; the table (results.md) is rebuilt from everything there after each group. `--mode indicative` uses BenchmarkDotNet's short job
# and is for checking that rows run, not for publishing numbers. Timing runs belong on an otherwise idle machine.
#
# Gate run, one job per group:
#   for g in request stream agent count own; do
#     wq run --class bench --max 20m --label "onellm bench $g" -- bench/run.sh --group $g --out results/gate
#   done
# Needs the .NET 10 SDK on PATH. Nothing here needs a key or the network: every client talks to the mock server on loopback.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GROUP=all
MODE=gate
OUT="$ROOT/bench-results"
REPORT_ONLY=0
PORT="${MOCK_PORT:-5311}"
while [ $# -gt 0 ]; do
  case "$1" in
    --group) GROUP="$2"; shift 2 ;;
    --mode) MODE="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --report-only) REPORT_ONLY=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"

ART="${ONELLM_ARTIFACTS:-$ROOT/artifacts}"
BENCH_DIR="$ROOT/bench/OneLlm.Bench"
MOCK_DLL="$ART/bin/OneLlm.MockServer/release/onellm-mockserver.dll"
JOB_ARGS=()
if [ "$MODE" = indicative ]; then JOB_ARGS=(--job short); fi

declare -A FILTERS=(
  [request]='*RequestBench*'
  [stream]='*StreamBench* *FirstEventBench*'
  [agent]='*AgentBench*'
  [count]='*CountBench* *CatalogBench*'
  [own]='*PersistenceBench* *TemplatingBench*'
)

report() { (cd "$BENCH_DIR" && dotnet run -c Release --no-build --project . -- report "$OUT" --label "$MODE") > "$OUT/report.log" 2>&1 || cat "$OUT/report.log"; }

if [ "$REPORT_ONLY" = 1 ]; then report; cat "$OUT/results.md"; exit 0; fi

sample() { printf '%s\t%s\t%s\t%s\n' "$1" "$2" "$(date -u +%H:%M:%S)" "$(cut -d' ' -f1-3 /proc/loadavg)" >> "$OUT/load.tsv"; }

if [ ! -f "$OUT/machine.txt" ]; then
  {
    echo "mode: $MODE"
    echo "cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2- | sed 's/^ //')"
    echo "logical cores: $(nproc)"
    echo "isa flags: $(grep -m1 '^flags' /proc/cpuinfo | tr ' ' '\n' | grep -E '^(sse4_2|avx|avx2|avx512[a-z_0-9]*|bmi1|bmi2|fma|popcnt|aes|pclmulqdq|sha_ni)$' | sort -u | tr '\n' ' ')"
    echo "kernel: $(uname -sr)"
    echo "sdk: $(dotnet --version)"
    echo "memory: $(awk '/MemTotal/ {printf "%.0f GB", $2/1048576}' /proc/meminfo)"
  } > "$OUT/machine.txt"
  grep -E 'PackageVersion Include="(OpenAI|Anthropic|Google.GenAI|Microsoft.Extensions.AI|Microsoft.Extensions.AI.OpenAI|Microsoft.Agents.AI|Microsoft.ML.Tokenizers|Microsoft.ML.Tokenizers.Data.O200kBase|BenchmarkDotNet)"' "$ROOT/Directory.Packages.props" \
    | sed -E 's/.*Include="([^"]+)" Version="([^"]+)".*/\1 \2/' > "$OUT/versions.txt"
fi

(cd "$ROOT" && dotnet build -c Release -v quiet -clp:NoSummary tools/OneLlm.MockServer && dotnet build -c Release -v quiet -clp:NoSummary bench/OneLlm.Bench)

MOCK_PID=""
SAMPLER_PID=""
stop() {
  if [ -n "$SAMPLER_PID" ]; then kill "$SAMPLER_PID" 2>/dev/null || true; fi
  if [ -n "$MOCK_PID" ]; then kill "$MOCK_PID" 2>/dev/null || true; fi
}
trap stop EXIT

run_group() {
  local g="$1"
  dotnet "$MOCK_DLL" --synthetic --port "$PORT" > "$OUT/mock-$g.log" 2>&1 &
  MOCK_PID=$!
  for _ in $(seq 1 50); do (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null && break; sleep 0.2; done
  sample "$g" before
  ( while true; do sleep 30; sample "$g" during; done ) &
  SAMPLER_PID=$!
  local filter
  for filter in ${FILTERS[$g]}; do
    (cd "$BENCH_DIR" && MOCK_URL="http://127.0.0.1:$PORT" BENCH_PEAK_FILE="$OUT/peak.tsv" \
      timeout 1200 dotnet run -c Release --no-build --project . -- --filter "$filter" "${JOB_ARGS[@]}" --exporters json --artifacts "$OUT/bdn-$g") \
      2>&1 | tee -a "$OUT/bdn-$g.log" | grep -E '^(// \*\*\*\*\* |\| |Run time|Global total)' || true
  done
  kill "$SAMPLER_PID" 2>/dev/null || true; SAMPLER_PID=""
  sample "$g" after
  kill "$MOCK_PID" 2>/dev/null || true; MOCK_PID=""
}

if [ "$GROUP" = all ]; then GROUPS_TO_RUN=(request stream agent count own); else GROUPS_TO_RUN=("$GROUP"); fi
for g in "${GROUPS_TO_RUN[@]}"; do
  [ -n "${FILTERS[$g]:-}" ] || { echo "unknown group: $g" >&2; exit 2; }
  run_group "$g"
done
report
cat "$OUT/results.md"
