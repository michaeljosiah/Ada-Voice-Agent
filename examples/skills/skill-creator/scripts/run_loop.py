"""Optimize a skill's `description` for triggering accuracy.

Splits the eval set 60/40 into train / held-out test, evaluates the current description, asks the
model to propose an improvement based on the TRAIN failures, and iterates — selecting the best
description by TEST score (so we don't overfit the train queries). Writes an HTML report and prints
JSON containing `best_description`.

Usage:
    python -m scripts.run_loop --eval-set queries.json --skill-path <skill> \
        --model <model-id> --max-iterations 5 --verbose
"""
from __future__ import annotations

import argparse
import json
import random
import re
import webbrowser
from pathlib import Path

from .run_eval import _ask, evaluate

PROPOSE_PROMPT = """You are improving the `description` of an Agent Skill so it triggers on the right
requests and avoids the wrong ones. The description is the only thing the agent sees when deciding
whether to use the skill.

Skill name: {name}
Current description:
{description}

It was tested on user requests. These are the ones it got WRONG (should_trigger is the correct label,
got_rate is how often it actually triggered):
{failures}

Rewrite the description so it triggers correctly on these without breaking the ones it already gets
right. Keep it accurate to what the skill does, include concrete trigger contexts, and lean slightly
"pushy" about when to use it. Respond with ONLY the new description text — one paragraph, no quotes,
no preamble."""


def _read_frontmatter(skill_md: Path):
    text = skill_md.read_text(encoding="utf-8")
    m = re.search(r"^---\s*\n(.*?)\n---\s*\n?(.*)$", text, re.DOTALL)
    if not m:
        raise SystemExit("SKILL.md has no YAML frontmatter.")
    fm = m.group(1)
    nm = re.search(r"^name:\s*(.+)$", fm, re.MULTILINE)
    dm = re.search(r"^description:\s*(.+)$", fm, re.MULTILINE)
    name = nm.group(1).strip().strip("\"'") if nm else skill_md.parent.name
    desc = dm.group(1).strip().strip("\"'") if dm else ""
    return name, desc


def _propose(name, description, failures, model):
    rendered = "\n".join(
        f"- want={str(f['should_trigger']):5} got_rate={f['trigger_rate']} :: {f['query']}"
        for f in failures
    ) or "(none)"
    out = _ask(PROPOSE_PROMPT.format(name=name, description=description, failures=rendered), model, timeout=180).strip()
    return out if out and not out.startswith("__ERROR__") else None


def run(eval_set_path: Path, skill_path: Path, model: str, max_iter: int, verbose: bool) -> dict:
    name, desc = _read_frontmatter(skill_path / "SKILL.md")

    items = json.loads(eval_set_path.read_text(encoding="utf-8"))
    random.Random(1234).shuffle(items)  # deterministic split
    split = max(1, int(len(items) * 0.6))
    train, test = items[:split], items[split:]

    history = []
    best = {"description": desc, "test_score": -1.0, "train_score": 0.0}
    current = desc
    for it in range(max_iter + 1):
        if verbose:
            print(f"\n=== iteration {it} ===")
        tr = evaluate(train, name, current, model, verbose=verbose)
        te = evaluate(test, name, current, model)
        if verbose:
            print(f"  train={tr['score']:.2f}  test={te['score']:.2f}")
        history.append({"iteration": it, "description": current,
                        "train_score": tr["score"], "test_score": te["score"]})
        if te["score"] > best["test_score"]:
            best = {"description": current, "test_score": te["score"], "train_score": tr["score"]}
        if tr["score"] >= 1.0 or it == max_iter:
            break
        proposed = _propose(name, current, [r for r in tr["results"] if not r["correct"]], model)
        if not proposed:
            break
        current = proposed

    return {"skill_name": name, "best_description": best["description"],
            "best_test_score": best["test_score"], "history": history,
            "train_size": len(train), "test_size": len(test)}


def _esc(s: str) -> str:
    return (s or "").replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")


def _report_html(result: dict) -> str:
    rows = "".join(
        f"<tr><td>{h['iteration']}</td><td>{h['train_score']:.2f}</td><td>{h['test_score']:.2f}</td>"
        f"<td style='text-align:left'>{_esc(h['description'])}</td></tr>"
        for h in result["history"]
    )
    return f"""<!doctype html><meta charset="utf-8">
<title>Description optimization — {_esc(result['skill_name'])}</title>
<style>body{{font:14px system-ui,sans-serif;margin:2rem;max-width:62rem}}
table{{border-collapse:collapse;width:100%;margin-top:1rem}}
td,th{{border:1px solid #ccc;padding:.4rem .6rem;text-align:center;vertical-align:top}}
.best{{background:#eaffea;padding:1rem;border-radius:8px;margin:1rem 0}}</style>
<h1>Description optimization — {_esc(result['skill_name'])}</h1>
<div class="best"><b>Best description</b> — test score {result['best_test_score']:.2f}, chosen on the
held-out test split:<p>{_esc(result['best_description'])}</p></div>
<p>Train / test split: {result['train_size']} / {result['test_size']} queries.</p>
<table><tr><th>Iter</th><th>Train</th><th>Test</th><th>Description</th></tr>{rows}</table>"""


def main():
    ap = argparse.ArgumentParser(description="Optimize a skill description for triggering accuracy.")
    ap.add_argument("--eval-set", type=Path, required=True)
    ap.add_argument("--skill-path", type=Path, required=True)
    ap.add_argument("--model", default="")
    ap.add_argument("--max-iterations", type=int, default=5)
    ap.add_argument("--verbose", action="store_true")
    ap.add_argument("--no-open", action="store_true", help="don't open the HTML report in a browser")
    args = ap.parse_args()

    result = run(args.eval_set, args.skill_path, args.model, args.max_iterations, args.verbose)
    report = args.skill_path / "description_optimization.html"
    report.write_text(_report_html(result), encoding="utf-8")
    if not args.no_open:
        try:
            webbrowser.open(report.as_uri())
        except Exception:
            pass
    print(json.dumps({"best_description": result["best_description"],
                      "best_test_score": result["best_test_score"],
                      "report": str(report)}, indent=2))


if __name__ == "__main__":
    main()
