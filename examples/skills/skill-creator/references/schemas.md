# Schemas

The JSON files the skill-creator workflow reads and writes. Field names matter — the
`eval-viewer/generate_review.py` and `scripts/aggregate_benchmark.py` tools depend on the exact
names below.

## Table of contents

- [evals/evals.json](#evalsevalsjson) — the test set
- [eval_metadata.json](#eval_metadatajson) — per-run metadata (in each eval dir)
- [timing.json](#timingjson) — per-run tokens + duration
- [grading.json](#gradingjson) — per-run assertion results
- [benchmark.json](#benchmarkjson) — aggregated stats for an iteration
- [feedback.json](#feedbackjson) — the human review output
- [trigger eval set](#trigger-eval-set) — for description optimization

---

## evals/evals.json

The canonical test set for a skill. `assertions` starts empty and is filled in once the runs are in
progress. `files` lists input files (paths relative to `evals/`) the prompt needs, or `[]`.

```json
{
  "skill_name": "example-skill",
  "evals": [
    {
      "id": 1,
      "prompt": "User's task prompt",
      "expected_output": "Plain-English description of the expected result",
      "files": [],
      "assertions": [
        "The output file is valid CSV",
        "A 'profit_margin' column is present"
      ]
    }
  ]
}
```

## eval_metadata.json

Written into each `iteration-<N>/<eval-name>/` directory. `eval_name` is a short descriptive slug
(e.g. `csv-profit-margin`), not just `eval-0`. Assertions may be empty when first created.

```json
{
  "eval_id": 0,
  "eval_name": "csv-profit-margin",
  "prompt": "The user's task prompt",
  "assertions": [
    "The output file is valid CSV",
    "A 'profit_margin' column is present and equals (revenue-cost)/revenue * 100"
  ]
}
```

## timing.json

Captured from each subagent task's completion notification (`total_tokens`, `duration_ms`). Written
into the run directory (`with_skill/`, `without_skill/`, or `old_skill/`).

```json
{ "total_tokens": 84852, "duration_ms": 23332, "total_duration_seconds": 23.3 }
```

## grading.json

The result of evaluating each assertion against a run's outputs. Written into the run directory.
The `expectations` array **must** use the fields `text`, `passed`, and `evidence` — the viewer and
the aggregator read those exact names.

```json
{
  "eval_id": 0,
  "eval_name": "csv-profit-margin",
  "configuration": "with_skill",
  "expectations": [
    { "text": "The output file is valid CSV", "passed": true,  "evidence": "csv.reader parsed 42 rows, 5 columns" },
    { "text": "A 'profit_margin' column is present", "passed": false, "evidence": "header row had no 'profit_margin' field" }
  ]
}
```

## benchmark.json

Produced by `scripts/aggregate_benchmark.py` for one iteration. Each configuration carries a
`pass_rate`, `time`, and `tokens` block with `mean` and `stddev`, plus a `delta` versus the baseline.
List each `with_skill` configuration before its baseline counterpart.

```json
{
  "skill_name": "example-skill",
  "iteration": 1,
  "configurations": [
    {
      "name": "with_skill",
      "pass_rate": { "mean": 0.92, "stddev": 0.08 },
      "time_seconds": { "mean": 23.3, "stddev": 4.1 },
      "total_tokens": { "mean": 84852, "stddev": 12010 },
      "n_evals": 3
    },
    {
      "name": "without_skill",
      "pass_rate": { "mean": 0.55, "stddev": 0.16 },
      "time_seconds": { "mean": 19.8, "stddev": 3.2 },
      "total_tokens": { "mean": 61200, "stddev": 8400 },
      "n_evals": 3
    }
  ],
  "delta": { "pass_rate": 0.37, "time_seconds": 3.5, "total_tokens": 23652 },
  "per_eval": [
    {
      "eval_name": "csv-profit-margin",
      "configurations": {
        "with_skill":    { "pass_rate": 1.0, "time_seconds": 22.1, "total_tokens": 80100 },
        "without_skill": { "pass_rate": 0.5, "time_seconds": 18.0, "total_tokens": 60050 }
      }
    }
  ],
  "observations": []
}
```

## feedback.json

Written by the eval viewer when the user clicks "Submit All Reviews". `run_id` is
`<eval-name>-<configuration>` (e.g. `csv-profit-margin-with_skill`). Empty feedback means the user
was satisfied with that run.

```json
{
  "reviews": [
    { "run_id": "csv-profit-margin-with_skill", "feedback": "chart is missing axis labels", "timestamp": "2026-01-01T12:00:00Z" },
    { "run_id": "pdf-extract-with_skill", "feedback": "", "timestamp": "2026-01-01T12:00:05Z" }
  ],
  "status": "complete"
}
```

## Trigger eval set

For description optimization (`scripts/run_loop.py`). A flat array of queries, each labelled with
whether the skill *should* trigger on it.

```json
[
  { "query": "ok my boss sent me a Q4 sales xlsx and wants a profit margin column added", "should_trigger": true },
  { "query": "help me write a cover letter for a barista job", "should_trigger": false }
]
```
