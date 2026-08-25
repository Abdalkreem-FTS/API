#!/usr/bin/env python3
"""Turns the JSON a suite run leaves behind into one markdown report.

    python3 scripts/build-report.py LoadTestReports/standard-20260812-120000

Reads every <name>.json the load tests wrote, plus BenchmarkDotNet's own markdown, and
writes the comparison to stdout.
"""

import json
import statistics
import sys
from pathlib import Path

STRATEGIES = ("denylist", "allowlist")


def load(directory: Path) -> dict[str, dict]:
    results = {}

    for path in sorted(directory.glob("*.json")):
        try:
            results[path.stem] = json.loads(path.read_text())
        except json.JSONDecodeError:
            print(f"<!-- could not parse {path.name} -->")

    return results


def scenarios(report: dict, name: str) -> list[dict]:
    """Every instance of one scenario across a report's runs."""
    return [
        scenario
        for run in report.get("runs", [])
        for scenario in run.get("scenarios", [])
        if scenario["name"] == name
    ]


def spread(values: list[float]) -> tuple[float, float, float, float]:
    """Median, min, max, and (max-min)/median as a fraction."""
    if not values:
        return (0.0, 0.0, 0.0, 0.0)

    median = statistics.median(values)

    return (median, min(values), max(values), (max(values) - min(values)) / median if median else 0.0)


def megabytes(value: int) -> str:
    return f"{value / (1024 * 1024):.2f} MB"


def table(rows: list[list[str]], header: list[str]) -> None:
    print("| " + " | ".join(header) + " |")
    print("| " + " | ".join("---" for _ in header) + " |")

    for row in rows:
        print("| " + " | ".join(row) + " |")

    print()


def section_latency(results: dict, mode: str, scenario: str, title: str) -> None:
    present = [s for s in STRATEGIES if f"{s}-{mode}" in results]

    if not present:
        return

    print(f"### {title}")
    print()

    rows = []
    noise = {}

    for strategy in present:
        report = results[f"{strategy}-{mode}"]
        found = scenarios(report, scenario)

        if not found:
            continue

        runs = len(found)
        requested = found[0]["requestedRps"]
        achieved = statistics.median([s["achievedRps"] for s in found])
        failed = sum(s["failed"] for s in found)

        cells = [strategy, str(runs), f"{requested:,}", f"{achieved:,.0f}"]

        for metric in ("p50Ms", "p95Ms", "p99Ms"):
            median, low, high, pct = spread([s[metric] for s in found])
            cells.append(f"{median:.2f} [{low:.2f}–{high:.2f}]")
            noise.setdefault(metric, []).append(pct)

        cells.append(f"{failed:,}")
        rows.append(cells)

    table(rows, ["strategy", "runs", "req/s", "got", "p50 ms [min–max]",
                 "p95 ms [min–max]", "p99 ms [min–max]", "failed"])

    if noise:
        floor = ", ".join(
            f"{metric[:3]} {max(values):.1%}" for metric, values in noise.items()
        )
        print(f"Run-to-run spread (the noise floor a real difference has to beat): {floor}.")
        print()

    # Whether the gap between the strategies clears that floor.
    if len(rows) == 2:
        verdict = []

        for index, metric in ((4, "p50"), (5, "p95"), (6, "p99")):
            a, b = (float(rows[i][index].split(" ")[0]) for i in (0, 1))
            gap = abs(a - b) / min(a, b) if min(a, b) else 0
            worst = max(noise[f"{metric}Ms"]) if f"{metric}Ms" in noise else 0
            verdict.append(f"{metric}: gap {gap:.1%} vs noise {worst:.1%} "
                           f"({'real' if gap > worst else 'indistinguishable'})")

        print("- " + "\n- ".join(verdict))
        print()


def section_stages(results: dict, mode: str, title: str) -> None:
    present = [s for s in STRATEGIES if f"{s}-{mode}" in results]

    if not present:
        return

    print(f"### {title}")
    print()

    rows = []

    for strategy in present:
        runs = results[f"{strategy}-{mode}"].get("runs", [])

        if not runs:
            continue

        # Stage shares are only comparable when every stage saw the same requests, which
        # holds for a single-operation run.
        stages = {t["stage"]: t for t in runs[0].get("timings", [])}
        total = stages.get("request.total", {}).get("meanMs", 0)

        for name, stage in stages.items():
            share = (
                f"{stage['meanMs'] / total:.1%}"
                if total and name != "request.total" and stage["count"] == stages["request.total"]["count"]
                else "-"
            )
            rows.append([
                strategy, name, f"{stage['count']:,}",
                f"{stage['meanMs']:.3f}", f"{stage['p50Ms']:.3f}",
                f"{stage['p95Ms']:.3f}", f"{stage['p99Ms']:.3f}", share,
            ])

    table(rows, ["strategy", "stage", "count", "mean ms", "p50", "p95", "p99", "share of total"])


def section_ramp(results: dict) -> None:
    present = [s for s in STRATEGIES if f"{s}-ramp" in results]

    if not present:
        return

    print("## Where the bottleneck is")
    print()

    for strategy in present:
        runs = results[f"{strategy}-ramp"].get("runs", [])

        if not runs:
            continue

        print(f"### {strategy}")
        print()

        rows = []
        saturated = None

        for run in runs:
            scenario = (run.get("scenarios") or [{}])[0]
            stages = {t["stage"]: t for t in run.get("timings", [])}

            requested = scenario.get("requestedRps", 0)
            achieved = scenario.get("achievedRps", 0)
            total = stages.get("request.total", {}).get("meanMs", 0)
            check = stages.get("store.check", {}).get("meanMs", 0)

            held = achieved >= requested * 0.95 and scenario.get("failed", 0) == 0

            if not held and saturated is None:
                saturated = requested

            rows.append([
                f"{requested:,}", f"{achieved:,.0f}",
                f"{scenario.get('p50Ms', 0):.2f}", f"{scenario.get('p95Ms', 0):.2f}",
                f"{scenario.get('p99Ms', 0):.2f}",
                f"{total:.3f}", f"{check:.3f}",
                f"{check / total:.1%}" if total else "-",
                f"{scenario.get('failed', 0):,}",
                "yes" if held else "**no**",
            ])

        table(rows, ["req/s", "got", "p50 ms", "p95 ms", "p99 ms",
                     "total ms", "store.check ms", "check %", "failed", "held"])

        if saturated:
            print(f"First rate that did not hold: **{saturated:,} req/s**.")
        else:
            print("Never saturated - raise `--ramp-to`.")

        print()
        print("A falling `check %` as the rate climbs means the bottleneck moved off Redis "
              "and onto the API or the load generator.")
        print()


def section_memory(results: dict, mode: str) -> None:
    present = [s for s in STRATEGIES if f"{s}-{mode}" in results]

    if len(present) < 1:
        return

    print("## Redis footprint")
    print()

    rows = []
    keyspace = {}

    for strategy in present:
        report = results[f"{strategy}-{mode}"]
        runs = report.get("runs", [])

        if not runs:
            continue

        space = runs[0]["keyspace"]
        keyspace[strategy] = space

        rows.append([
            strategy,
            f"{report['sessions']:,}",
            f"{space['keysAfter']:,}",
            megabytes(space["usedMemoryAfter"]),
            f"{space['averageKeyBytes']:.0f} B",
            f"{space['fragmentation']:.2f}",
        ])

    table(rows, ["strategy", "sessions", "keys", "used_memory",
                 "per key (MEMORY USAGE)", "fragmentation"])

    if len(keyspace) == 2:
        deny, allow = keyspace["denylist"], keyspace["allowlist"]
        extra_keys = allow["keysAfter"] - deny["keysAfter"]
        extra_bytes = allow["usedMemoryAfter"] - deny["usedMemoryAfter"]

        if extra_keys > 0:
            per = extra_bytes / extra_keys
            print(f"The allowlist holds **{extra_keys:,} more keys** for "
                  f"**{megabytes(extra_bytes)} more memory**: {per:.0f} B per session all-in, "
                  f"including dict and expiry overhead.")
            print()
            for scale in (1_000_000, 10_000_000):
                print(f"- at {scale:,} active sessions: about "
                      f"{megabytes(int(per * scale))} of Redis just to track them")
            print()
            print("Note the denylist's fragmentation ratio: a keyspace this small sits inside "
                  "allocator arenas sized for much more, which is why its `used_memory` is not a "
                  "clean baseline to subtract.")
            print()


def section_soak(results: dict) -> None:
    present = [s for s in STRATEGIES if f"{s}-soak" in results]

    if not present:
        return

    print("## Steady state")
    print()
    print("Token lifetime is shortened so the run outlasts it and TTL expiry becomes visible. "
          "A keyspace that only ever sits where the preloader put it was never observed reaching "
          "steady state.")
    print()

    rows = []

    for strategy in present:
        report = results[f"{strategy}-soak"]

        for run in report.get("runs", []):
            space = run["keyspace"]
            series = run.get("series", [])
            failed = sum(s["failed"] for s in run.get("scenarios", []))

            rows.append([
                strategy,
                f"{report['durationSeconds']:.0f}s",
                f"{report['tokenLifetimeMinutes']} min",
                f"{space['keysBefore']:,}",
                f"{space['keysMin']:,}",
                f"{space['keysMax']:,}",
                f"{space['keysAfter']:,}",
                f"{len(series)}",
                f"{failed:,}",
            ])

    table(rows, ["strategy", "duration", "token life", "keys at start",
                 "min", "max", "keys at end", "samples", "failed"])


def section_failure(results: dict) -> None:
    present = [s for s in STRATEGIES if f"{s}-failure" in results]

    if not present:
        return

    print("## Failure modes")
    print()

    cases = ["failure-baseline", "failure-unreachable", "failure-stalled", "failure-oom"]
    labels = {
        "failure-baseline": "Redis healthy",
        "failure-unreachable": "Redis unreachable",
        "failure-stalled": "Redis stalled",
        "failure-oom": "Redis at maxmemory",
    }

    rows = []

    for strategy in present:
        runs = {run["label"]: run for run in results[f"{strategy}-failure"].get("runs", [])}

        for case in cases:
            run = runs.get(case)

            if not run:
                continue

            for scenario in run.get("scenarios", []):
                codes = scenario.get("statusCodes") or {}
                rows.append([
                    strategy, labels[case], scenario["name"],
                    f"{scenario['ok']:,}", f"{scenario['failed']:,}",
                    ", ".join(f"{k}={v:,}" for k, v in sorted(
                        codes.items(), key=lambda kv: -kv[1])) or "none",
                ])

    table(rows, ["strategy", "case", "operation", "ok", "failed", "status codes"])

    print("A `401` would mean the strategy refused the request (fail closed) and a `200` that it "
          "let it through (fail open). A `500` means neither was decided: the Redis exception "
          "reached the top of the pipeline. Under either strategy, Redis being reachable is "
          "currently a hard requirement for serving an authenticated request.")
    print()


def section_benchmarks(directory: Path) -> None:
    reports = sorted((directory / "benchmarkdotnet").glob("*-report-github.md"))

    if not reports:
        return

    print("## Per-operation cost (BenchmarkDotNet)")
    print()
    print("No HTTP, no pipeline: one store call at a time against Redis on loopback. This is the "
          "floor each operation costs, not what a request costs.")
    print()

    for report in reports:
        name = report.stem.replace("-report-github", "").split(".")[-1]

        print(f"### {name}")
        print()
        print("\n".join(
            line for line in report.read_text().splitlines()
            if line.startswith("|")
        ))
        print()


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 1

    directory = Path(sys.argv[1])

    if not directory.is_dir():
        print(f"No such directory: {directory}", file=sys.stderr)
        return 1

    results = load(directory)

    print(f"# Token revocation: denylist vs allowlist")
    print()
    print(f"Suite: `{directory.name}`")
    print()

    if not results:
        print("No load-test JSON found. Check the logs under `logs/`.")
        return 0

    any_report = next(iter(results.values()))

    print(f"Injected Redis latency: {any_report.get('redisLatencyMs', 0)} ms "
          f"± {any_report.get('redisJitterMs', 0)} ms jitter. "
          f"Load generator, API and Redis all run on this machine, so any rate high enough to "
          f"contend for CPU is measuring the harness as much as the API.")
    print()

    print("## Latency")
    print()
    section_latency(results, "mix", "authenticated_request",
                    "Authenticated requests, under the production mix")
    section_latency(results, "request", "authenticated_request",
                    "Authenticated requests only (enough samples for the tail)")
    section_latency(results, "login", "login", "Logins only")
    section_latency(results, "logout", "logout", "Logouts only")

    print("## Where a request's time goes")
    print()
    section_stages(results, "request", "On the authenticated path")
    section_stages(results, "login", "On login")
    section_stages(results, "logout", "On logout")

    section_ramp(results)
    section_memory(results, "request")
    section_soak(results)
    section_failure(results)
    section_benchmarks(directory)

    return 0


if __name__ == "__main__":
    sys.exit(main())
