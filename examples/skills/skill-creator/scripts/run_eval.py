"""Evaluate how reliably a skill *description* triggers across a set of labelled queries.

For each query we ask the model (via the `claude -p` CLI) whether it would consult a skill that has
the given name + description, repeated a few times to get a stable trigger rate, then score that
against the query's `should_trigger` label.

Usage:
    python -m scripts.run_eval --eval-set queries.json --name <skill> \
        --description "..." --model <model-id> [--repeats 3] [--verbose]
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
from pathlib import Path

TRIGGER_PROMPT = """You are deciding whether to consult a specialized Skill for a user request.

Available skill:
- name: {name}
- description: {description}

Consult a skill when the request clearly falls in its domain AND benefits from it (multi-step or
specialized work) — not for trivial one-step tasks you can already handle directly.

User request:
{query}

Answer with exactly one word: YES if you would consult this skill, or NO if you would not."""


def _ask(prompt: str, model: str, timeout: int = 120) -> str:
    """Run `claude -p <prompt>` and return stdout (or '__ERROR__ ...' if the CLI is unavailable)."""
    cmd = ["claude", "-p", prompt]
    if model:
        cmd += ["--model", model]
    try:
        r = subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)
        return (r.stdout or "").strip()
    except (subprocess.TimeoutExpired, FileNotFoundError, OSError) as e:
        return f"__ERROR__ {e}"


def query_trigger_rate(name: str, description: str, query: str, model: str, repeats: int = 3) -> float:
    yes = n = 0
    for _ in range(repeats):
        out = _ask(TRIGGER_PROMPT.format(name=name, description=description, query=query), model)
        if out.startswith("__ERROR__"):
            continue
        n += 1
        tokens = re.findall(r"[A-Za-z]+", out.upper())
        if "YES" in tokens[:3]:  # look at the leading word(s) of the answer
            yes += 1
    return (yes / n) if n else 0.0


def evaluate(eval_set, name, description, model, repeats=3, threshold=0.5, verbose=False) -> dict:
    results = []
    for item in eval_set:
        rate = query_trigger_rate(name, description, item["query"], model, repeats)
        triggered = rate >= threshold
        correct = triggered == bool(item["should_trigger"])
        results.append({
            "query": item["query"],
            "should_trigger": bool(item["should_trigger"]),
            "trigger_rate": round(rate, 3),
            "triggered": triggered,
            "correct": correct,
        })
        if verbose:
            print(f"  [{'ok  ' if correct else 'MISS'}] rate={rate:.2f} "
                  f"want={str(item['should_trigger']):5} :: {item['query'][:70]}")
    score = (sum(1 for r in results if r["correct"]) / len(results)) if results else 0.0
    return {"score": round(score, 4), "results": results}


def main():
    ap = argparse.ArgumentParser(description="Evaluate a skill description's triggering accuracy.")
    ap.add_argument("--eval-set", type=Path, required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--description", required=True)
    ap.add_argument("--model", default="")
    ap.add_argument("--repeats", type=int, default=3)
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    eval_set = json.loads(args.eval_set.read_text(encoding="utf-8"))
    out = evaluate(eval_set, args.name, args.description, args.model, args.repeats, verbose=args.verbose)
    print(json.dumps({"score": out["score"]}, indent=2))


if __name__ == "__main__":
    main()
