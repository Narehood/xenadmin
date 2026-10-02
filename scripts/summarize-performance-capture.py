#!/usr/bin/env python3
"""Summarize the shell's opt-in JSONL performance capture; no third-party modules."""
import argparse
import json
import math
from collections import Counter, defaultdict
from pathlib import Path
from statistics import median


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * fraction) - 1)]


def number(value, minimum=0):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < minimum:
        raise ValueError("Invalid numeric performance sample")
    return value


def summarize(path, evidence_kind="unclassified"):
    operations = defaultdict(list)
    allocations = defaultdict(list)
    startup = Counter()
    notifications = batches = connections = frames = samples = 0
    maximum_width = maximum_height = 0
    metadata = footer = None
    with Path(path).open(encoding="utf-8") as capture:
        for line_number, line in enumerate(capture, 1):
            row = json.loads(line)
            kind = row.get("kind")
            if line_number == 1:
                if kind != "capture" or row.get("schema") != 1 or row.get("provider") != "XcpNgCenter-Shell-Performance":
                    raise ValueError("Unsupported performance capture header")
                metadata = row
                continue
            if footer is not None:
                raise ValueError("Records after capture summary")
            if kind == "summary":
                footer = row
                continue
            if kind != "event":
                raise ValueError("Unexpected performance record")
            number(row["elapsedMilliseconds"])
            event = row["eventId"]
            values = row["payload"]
            if event == 1 and len(values) == 3:
                operation, elapsed, allocated = values
                if not isinstance(operation, str) or len(operation) > 100:
                    raise ValueError("Invalid operation name")
                operations[operation].append(number(elapsed))
                allocated = number(allocated, -1)
                if allocated != -1:
                    allocations[operation].append(allocated)
            elif event == 2 and len(values) == 2:
                notifications += number(values[0])
                connections += number(values[1])
                batches += 1
            elif event == 3 and len(values) == 2:
                maximum_width = max(maximum_width, number(values[0]))
                maximum_height = max(maximum_height, number(values[1]))
                frames += 1
            elif event == 4 and len(values) == 2 and all(isinstance(v, str) and len(v) <= 100 for v in values):
                startup[tuple(values)] += 1
            else:
                raise ValueError("Unsupported performance event")
            samples += 1
    if metadata is None:
        raise ValueError("Empty performance capture")
    complete = footer is not None
    dropped = number(footer["eventsDropped"]) if complete else None
    duration = number(footer["durationMilliseconds"]) / 1000 if complete else None
    if complete and number(footer["eventsWritten"]) != samples:
        raise ValueError("Capture sample count does not match summary")
    timings = {}
    for operation, values in sorted(operations.items()):
        timings[operation] = {
            "count": len(values), "medianMs": median(values), "p95Ms": percentile(values, .95),
            "p99Ms": percentile(values, .99), "maximumMs": max(values),
            "knownAllocationSamples": len(allocations[operation]),
            "medianAllocatedBytes": median(allocations[operation]) if allocations[operation] else None,
        }
    coverage = {
        "inventory": batches > 0 and "inventory.refresh" in timings,
        "details": "details.refresh" in timings,
        "graphs": "graphs.rrd-fetch" in timings,
        "console": frames > 0 and bool(startup),
    }
    return {
        "schema": 1, "evidenceKind": evidence_kind, "applicationVersion": metadata.get("applicationVersion"),
        "complete": complete, "events": samples, "eventsDropped": dropped, "durationSeconds": duration,
        "usableForComparison": complete and dropped == 0 and samples > 0,
        "coverage": coverage,
        "pendingPhases": [phase for phase, covered in coverage.items() if not covered],
        "inventory": {"batches": batches, "notifications": notifications, "connectionRebuilds": connections,
                      "notificationsPerBatch": notifications / batches if batches else None},
        "console": {"frames": frames, "framesPerSecond": frames / duration if duration else None,
                    "maximumWidth": maximum_width, "maximumHeight": maximum_height,
                    "startup": [{"stage": stage, "outcome": outcome, "count": count}
                                for (stage, outcome), count in sorted(startup.items())]},
        "operations": timings,
        "limitations": ["Evidence kind is supplied by the operator; this reader cannot verify a live pool.",
                        "Timings exclude GPU presentation and full network/decoder costs.",
                        "Allocations cover only the measuring thread; -1 means unknown."],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path)
    parser.add_argument("--evidence-kind", choices=("unclassified", "synthetic", "live"), default="unclassified")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    try:
        report = json.dumps(summarize(args.capture, args.evidence_kind), indent=2, allow_nan=False)
        if args.output:
            with args.output.open("x", encoding="utf-8", newline="\n") as output:
                output.write(report + "\n")
        else:
            print(report)
    except (ValueError, KeyError, TypeError, OSError) as error:
        parser.exit(1, f"Performance capture rejected: {error}\n")


if __name__ == "__main__":
    main()
