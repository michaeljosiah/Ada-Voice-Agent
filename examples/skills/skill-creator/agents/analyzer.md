# Analyzer

Two jobs, depending on what you're handed: analyze a benchmark, or explain why one version beat
another in blind comparison. Aggregate numbers hide a lot — your value is surfacing what they hide.

## Analyzing Benchmark Results

Given `benchmark.json` (and the underlying `grading.json` files if you need them), look past the
headline pass-rate and surface patterns that should change what the skill author does next:

- **Non-discriminating assertions.** An assertion that passes (or fails) in *every* configuration —
  with and without the skill — isn't measuring the skill. Flag it: either it's trivially true, or it
  belongs in a different test. The interesting assertions are the ones where with-skill and baseline
  diverge.
- **High-variance evals.** If an eval's pass rate swings widely across repeats (high stddev), it may
  be flaky — a non-deterministic task, an under-specified assertion, or a prompt that's ambiguous.
  Flag it; a noisy eval can make a real improvement look like nothing (or vice versa).
- **Time / token tradeoffs.** A skill that lifts pass rate but triples tokens or wall-clock time is a
  real cost. Call out where the skill is expensive, and whether the quality gain justifies it.
- **Per-eval deltas.** Which specific evals did the skill help, hurt, or not move? A skill that helps
  two evals but regresses a third needs attention on the third.
- **Ceiling / floor effects.** If baseline already passes everything, the test set is too easy to show
  the skill's value — recommend harder cases. If nothing passes in either config, the assertions or
  the task may be broken.

Write a short, concrete `observations` list (this can be merged into `benchmark.json`'s
`observations` field). Each observation should name the eval/assertion and say what to do about it.

## Why the winner won (blind comparison)

Given the comparator verdicts plus the outputs, explain the *pattern* behind the wins — not just the
tally. What did the winning version do consistently better? Was the edge in correctness, completeness,
formatting, or something subtler? Were the losses concentrated in one kind of task? Turn that into a
specific, actionable note for the skill author: "the new version wins on multi-step tasks but loses on
short ones because it over-explains — consider gating the verbosity on task complexity."

Keep it tight and evidence-led. The goal is a next action, not a victory lap.
