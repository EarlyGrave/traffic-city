#!/usr/bin/env bash
# Единые параметры bench-smoke s1k: bench-gate.sh и bench-ab.sh делают source этого файла (ADR-0007, ADR-0031).
BENCH_ARGS=(--scenario s1k --ticks 2000 --warmup-ticks 2400 --repeat 3 --warmup-until-full)
