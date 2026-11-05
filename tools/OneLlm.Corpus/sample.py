#!/usr/bin/env python3
"""Builds the list file for onellm-corpus.

usage: sample.py <definitions-root> <instances-root> <out-list> [--instances N] [--seed S]

Definitions: every agent.yaml, *.flow, decider.yaml and flow.yaml under <definitions-root>.
Instances: a stratified sample of <instances-root>/*.yaml, by age (modification-time buckets) and by feature
(tool calls, reasoning, assets, message sources, caller links, each state value, legacy inline media, very large files).
The script reads file contents only to look for markers; it prints counts and writes paths, never content.
"""
import os
import random
import re
import sys

PRUNE = {"node_modules", ".git", "www"}
MAX_BYTES = 20 * 1024 * 1024
MARKERS = {
    "feature-toolCalls": re.compile(rb"^\s+toolCalls:", re.M),
    "feature-reasoning": re.compile(rb"^\s+reasoning:", re.M),
    "feature-asset": re.compile(rb"^\s+- asset:|^\s+asset:", re.M),
    "feature-source": re.compile(rb"^\s+source:", re.M),
    "feature-callerLink": re.compile(rb"^(callerInstanceId|spawnerInstanceId|callerToolCallId|delegationPrompt):", re.M),
    "feature-legacyMedia": re.compile(rb"^\s+- (image|audio|video|pdf):", re.M),
    "feature-injected": re.compile(rb"^\s+injected: true", re.M),
    "feature-subscriptionFields": re.compile(rb"^(subId|subscriptionWait|subscriptions):", re.M),
}
STATE = re.compile(rb'^state:\s*"?([A-Za-z_]+)"?\s*$', re.M)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    opts = dict(a[2:].split("=", 1) for a in sys.argv[1:] if a.startswith("--") and "=" in a)
    defs_root, inst_root, out = args[:3]
    want = int(opts.get("instances", 500))
    rng = random.Random(int(opts.get("seed", 1)))
    lines = []

    counts = {}
    for dirpath, dirnames, filenames in os.walk(defs_root):
        dirnames[:] = [d for d in dirnames if d not in PRUNE]
        for f in filenames:
            kind = None
            if f == "agent.yaml":
                kind = "agent"
            elif f.endswith(".flow"):
                kind = "flow"
            elif f == "decider.yaml":
                kind = "decider"
            elif f == "flow.yaml":
                kind = "flowmeta"
            if kind:
                lines.append(f"def-{kind}\t{kind}\t{os.path.join(dirpath, f)}")
                counts[kind] = counts.get(kind, 0) + 1
    print("definitions:", counts)

    files = []
    with os.scandir(inst_root) as it:
        for e in it:
            if e.name.endswith(".yaml") and e.is_file():
                st = e.stat()
                if 0 < st.st_size <= MAX_BYTES:
                    files.append((st.st_mtime, st.st_size, e.path))
    files.sort()
    print("instance files:", len(files))

    picked = set()

    def take(stratum, pool, n):
        pool = [p for p in pool if p not in picked]
        rng.shuffle(pool)
        for p in pool[:n]:
            picked.add(p)
            lines.append(f"{stratum}\tinstance\t{p}")
        return min(n, len(pool))

    # Age: ten buckets of equal file count.
    per_age = max(1, want // 25)
    for b in range(10):
        lo, hi = b * len(files) // 10, (b + 1) * len(files) // 10
        take(f"age-{b}", [p for _, _, p in files[lo:hi]], per_age)

    # Features: scan a candidate pool spread over the whole age range.
    pool = rng.sample(files, min(len(files), 9000))
    by_feature = {k: [] for k in MARKERS}
    by_state = {}
    for _, _, p in pool:
        with open(p, "rb") as fh:
            data = fh.read()
        for k, rx in MARKERS.items():
            if rx.search(data):
                by_feature[k].append(p)
        m = STATE.search(data)
        by_state.setdefault(m.group(1).decode() if m else "none", []).append(p)
    per_feature = max(1, want // 16)
    for k, ps in by_feature.items():
        take(k, ps, per_feature)
    for s, ps in by_state.items():
        take(f"state-{s}", ps, max(2, per_feature // 2))
    big = [p for _, size, p in sorted(files, key=lambda t: -t[1])[:40]]
    take("feature-large", big, max(3, per_feature // 3))

    with open(out, "w") as fh:
        fh.write("\n".join(lines) + "\n")
    print("list entries:", len(lines), "instances picked:", len(picked))


if __name__ == "__main__":
    main()
