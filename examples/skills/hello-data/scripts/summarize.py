#!/usr/bin/env python3
"""Summarize numbers passed as CLI arguments: count, mean, and population stdev (as JSON)."""
import json
import statistics
import sys


def main() -> None:
    values = []
    for arg in sys.argv[1:]:
        try:
            values.append(float(arg))
        except ValueError:
            pass  # ignore non-numeric arguments

    if not values:
        print(json.dumps({"error": "no numeric arguments provided"}))
        return

    print(json.dumps({
        "count": len(values),
        "mean": round(statistics.fmean(values), 4),
        "stdev": round(statistics.pstdev(values), 4),
    }))


if __name__ == "__main__":
    main()
