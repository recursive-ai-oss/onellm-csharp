#!/usr/bin/env bash
# Fills the speed rows and the agent-loop sentence of docs/results.md from a bench run's results.md (written by bench/run.sh).
#
#   tools/results-doc.sh <bench results.md> [docs/results.md]
#
# Only OneLLM's own numbers are copied: the reference columns of the bench table are dropped. The agent-loop sentence takes the per-turn
# time (the run is eleven model turns) and the ratio to the fastest managed reference from the agent-loop row; the floor is left as TBD
# for the person running the release check to fill in from the recorded first measurement.
set -euo pipefail
BENCH="${1:?bench results.md}"
DOC="${2:-$(dirname "${BASH_SOURCE[0]}")/../docs/results.md}"

ROWS="$(awk -F'|' '
  function trim(s) { gsub(/^ +| +$/, "", s); return s }
  /^## Compared rows/ { mode = "cmp"; next }
  /^## Rows without a comparison/ { mode = "own"; next }
  mode == "cmp" && NF > 10 && trim($2) != "Row" && trim($2) !~ /^-+$/ {
    key = trim($2) "|" trim($3)
    if (!(key in seen)) { seen[key] = 1; split(trim($10), peak, " / "); print "| " trim($2) " | " trim($3) " | " trim($4) " | " trim($8) " | " peak[1] " |" }
  }
  mode == "own" && NF > 5 && trim($2) != "Benchmark" && trim($2) !~ /^-+$/ && trim($2) !~ /\.(Ref_|Info_)/ {
    print "| " trim($2) " | " trim($3) " | " trim($4) " | " trim($5) " | " trim($6) " |"
  }' "$BENCH")"

# The compared table has the columns: Row | Parameters | OneLLM mean | Reference | Reference mean | Ratio | Alloc ours | Alloc reference | Peak | Status.
LOOP_LINE="$(grep -E '^\| agent-loop \|' "$BENCH" | head -n1 || true)"
TURN="TBD"; RATIO="TBD"
if [ -n "$LOOP_LINE" ]; then
  MEAN="$(printf '%s' "$LOOP_LINE" | awk -F'|' '{ gsub(/^ +| +$/, "", $4); print $4 }')"
  RATIO="$(printf '%s' "$LOOP_LINE" | awk -F'|' '{ gsub(/^ +| +$/, "", $7); print $7 }')"
  TURN="$(printf '%s' "$MEAN" | awk '{ v = $1 / 11; printf "%.3g %s", v, $2 }')"
fi

TMP="$(mktemp)"
awk -v rows="| Row | Parameters | Mean | Allocated | Peak working set (MB) |\n|---|---|---:|---:|---:|\n$ROWS" -v turn="$TURN" -v ratio="$RATIO" '
  /<!-- speed-rows:begin -->/ { print; gsub(/\\n/, "\n", rows); print rows; skip = 1; next }
  /<!-- speed-rows:end -->/ { skip = 0 }
  /<!-- agent-loop:begin -->/ { print; print "OneLLM'"'"'s agent loop costs **" turn "** per turn (floor: **TBD**). That is above the fastest managed reference by a factor of **" ratio "**, because every OneLLM run is a persisted instance with a snapshot, priced line items and events for each turn."; skip = 1; next }
  /<!-- agent-loop:end -->/ { skip = 0 }
  !skip { print }' "$DOC" > "$TMP"
mv "$TMP" "$DOC"
echo "updated $DOC"
