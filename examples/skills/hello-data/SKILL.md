---
name: hello-data
description: Summarize a list of numbers — count, mean, and standard deviation. Use when the user gives you numbers and wants quick descriptive statistics.
license: MIT
compatibility: Requires python3
metadata:
  author: ada-example
  version: "1.0"
---

# Hello, data

A tiny example skill showing how Ada runs a skill's bundled Python script inside the AIO sandbox.

## When to use

The user gives you a set of numbers and wants a quick summary — count, mean, and standard deviation.

## How to run it

Call the `summarize` script, passing each number as a separate string argument. For the numbers
4, 8, 15, 16, 23, 42 call it with `["4", "8", "15", "16", "23", "42"]`. The script prints a JSON
object with `count`, `mean`, and `stdev`; read that back to the user in one short sentence.

## Notes

- The script uses only the Python standard library (`statistics`), so it needs no extra packages.
- It runs inside Ada's sandbox — nothing touches the host machine.
