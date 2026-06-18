"""Aggregate an iteration's per-run grading + timing into benchmark.json / benchmark.md.

Usage:
    python -m scripts.aggregate_benchmark <iteration-dir> --skill-name <name>

Each <iteration-dir>/<eval-name>/ holds an eval_metadata.json plus one or more run directories
(with_skill / without_skill / old_skill), each containing grading.json and timing.json. We compute
pass_rate, time, and tokens per configuration (mean ± population stddev across evals) and the delta
versus the baseline. with_skill is always listed before its baseline.
"""
from __future__ import annotations

import argparse
import json
import re
import statistics
from pathlib import Path

RUN_DIRS = ("with_skill", "without_skill", "old_skill")
BASELINES = ("without_skill", "old_skill")


def _mean_std(values: list) -> dict:
    xs = [v for v in values if v is not None]
    if not xs:
        return {"mean": None, "stddev": None}
    return {
        "mean": round(statistics.fmean(xs), 4),
        "stddev": round(statistics.pstdev(xs), 4) if len(xs) > 1 else 0.0,
    }


def _run_metrics(run_dir: Path):
    """(pass_rate, time_seconds, total_tokens) for one run dir; each is None when unavailable."""
    pass_rate = time_s = tokens = None

    grading = run_dir / "grading.json"
    if grading.exists():
        try:
            exps = json.loads(grading.read_text(encoding="utf-8")).get("expectations", [])
            if exps:
                pass_rate = sum(1 for e in exps if e.get("passed")) / len(exps)
        except (json.JSONDecodeError, OSError):
            pass

    timing = run_dir / "timing.json"
    if timing.exists():
        try:
            t = json.loads(timing.read_text(encoding="utf-8"))
            time_s = t.get("total_duration_seconds")
            if time_s is None and t.get("duration_ms") is not None:
                time_s = t["duration_ms"] / 1000.0
            tokens = t.get("total_tokens")
        except (json.JSONDecodeError, OSError):
            pass

    return pass_rate, time_s, tokens


def aggregate(iteration_dir: Path, skill_name: str) -> dict:
    eval_dirs = sorted(
        d for d in iteration_dir.iterdir()
        if d.is_dir() and (d / "eval_metadata.json").exists()
    )

    configs: dict[str, dict[str, list]] = {}
    per_eval = []
    for ed in eval_dirs:
        try:
            name = json.loads((ed / "eval_metadata.json").read_text(encoding="utf-8")).get("eval_name", ed.name)
        except (json.JSONDecodeError, OSError):
            name = ed.name

        row = {"eval_name": name, "configurations": {}}
        for cfg in RUN_DIRS:
            run_dir = ed / cfg
            if not run_dir.is_dir():
                continue
            pr, ts, tok = _run_metrics(run_dir)
            row["configurations"][cfg] = {"pass_rate": pr, "time_seconds": ts, "total_tokens": tok}
            bucket = configs.setdefault(cfg, {"pass_rate": [], "time_seconds": [], "total_tokens": []})
            bucket["pass_rate"].append(pr)
            bucket["time_seconds"].append(ts)
            bucket["total_tokens"].append(tok)
        per_eval.append(row)

    ordered = [c for c in ("with_skill", *BASELINES) if c in configs]
    configurations = [
        {
            "name": cfg,
            "pass_rate": _mean_std(configs[cfg]["pass_rate"]),
            "time_seconds": _mean_std(configs[cfg]["time_seconds"]),
            "total_tokens": _mean_std(configs[cfg]["total_tokens"]),
            "n_evals": len(configs[cfg]["pass_rate"]),
        }
        for cfg in ordered
    ]

    delta = {}
    if "with_skill" in configs:
        base = next((b for b in BASELINES if b in configs), None)
        if base:
            def d(metric: str):
                a = _mean_std(configs["with_skill"][metric])["mean"]
                b = _mean_std(configs[base][metric])["mean"]
                return round(a - b, 4) if a is not None and b is not None else None

            delta = {
                "baseline": base,
                "pass_rate": d("pass_rate"),
                "time_seconds": d("time_seconds"),
                "total_tokens": d("total_tokens"),
            }

    return {
        "skill_name": skill_name,
        "iteration": _iteration_number(iteration_dir),
        "configurations": configurations,
        "delta": delta,
        "per_eval": per_eval,
        "observations": [],
    }


def _iteration_number(p: Path):
    m = re.search(r"iteration-(\d+)", p.name)
    return int(m.group(1)) if m else None


def _fmt(v) -> str:
    if isinstance(v, dict):
        return "—" if v.get("mean") is None else f"{v['mean']} ± {v.get('stddev')}"
    return "—" if v is None else str(v)


def to_markdown(b: dict) -> str:
    out = [f"# Benchmark — {b['skill_name']} (iteration {b.get('iteration')})", ""]
    out += ["| Configuration | Pass rate | Time (s) | Tokens | Evals |", "|---|---|---|---|---|"]
    for c in b["configurations"]:
        out.append(f"| {c['name']} | {_fmt(c['pass_rate'])} | {_fmt(c['time_seconds'])} | {_fmt(c['total_tokens'])} | {c['n_evals']} |")
    if b.get("delta"):
        d = b["delta"]
        out += ["", f"**Delta** (with_skill − {d.get('baseline')}): pass_rate {d.get('pass_rate')}, "
                    f"time {d.get('time_seconds')} s, tokens {d.get('total_tokens')}"]
    if b.get("per_eval"):
        out += ["", "## Per eval", "", "| Eval | Config | Pass | Time (s) | Tokens |", "|---|---|---|---|---|"]
        for r in b["per_eval"]:
            for cfg, m in r["configurations"].items():
                out.append(f"| {r['eval_name']} | {cfg} | {_fmt(m['pass_rate'])} | {_fmt(m['time_seconds'])} | {_fmt(m['total_tokens'])} |")
    if b.get("observations"):
        out += ["", "## Observations", ""] + [f"- {o}" for o in b["observations"]]
    return "\n".join(out) + "\n"


def main():
    ap = argparse.ArgumentParser(description="Aggregate an iteration's runs into a benchmark.")
    ap.add_argument("iteration_dir", type=Path)
    ap.add_argument("--skill-name", required=True)
    args = ap.parse_args()

    if not args.iteration_dir.is_dir():
        raise SystemExit(f"Not a directory: {args.iteration_dir}")

    b = aggregate(args.iteration_dir, args.skill_name)
    (args.iteration_dir / "benchmark.json").write_text(json.dumps(b, indent=2), encoding="utf-8")
    (args.iteration_dir / "benchmark.md").write_text(to_markdown(b), encoding="utf-8")
    print(f"Wrote {args.iteration_dir / 'benchmark.json'} and benchmark.md")
    print(json.dumps(b["configurations"], indent=2))


if __name__ == "__main__":
    main()
