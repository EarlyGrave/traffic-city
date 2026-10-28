# Бриф облаку: 5.2 масштаб s100k ≤ 15 мс (2026-10-28)

Скопировать целиком в новую облачную сессию (репозиторий EarlyGrave/traffic-city).

---

You are the cloud orchestrator session for traffic-city, stage 5 task 5.2: s100k (full city, 4 threads) tick p95 ≤ 15 ms. Reply to the owner in Russian; briefs to your subagents and their reports in English; commits/docs in Russian (CLAUDE.md rules).

Start checklist (CLAUDE.md "Две среды"): `git fetch origin && git checkout dev && git pull origin dev`. The repo default branch is `main`; work from `dev`. Read `docs/plan/claims.md` — the 5.2 claim (cloud; folders `src/Sim.Core/Routing/**`, `Lod/**`, `Metrics/**`, `src/Sim.Headless/Bench/**`, `scenarios/s100k*`, `s1m*`) is already written by the local orchestrator; put your actual branch name into that line and push. Local is working on 5.1 (s10k-res congestion diagnosis) — do not touch City/Demand/Parking/Rules without claiming first.

Read: `docs/plan/stage5-plan.md` §5.2, `docs/REPORT-stage4.md` (Техдолг 2), `docs/design/scale.md` (§7 reserve: parallel phase-3 queries), notes for agents 159/165 in `docs/plan/stage4-plan.md` and `docs/plan/agents.md`, `docs/plan/lessons.md`.

Current state: s100k p95 26.94 ms (agents 159/165); phase 3 (routing) ≈ 4 ms mean, ~240k relaxations/tick; stuck s100k = micro queues at signals and yielding lefts (not deadlock); s1m never reaches full load in the container (ramp limited by phase 3).

Plan (Pareto, owner-approved): 1) parallel phase-3 route queries via `Util/WorkerPool` (ADR-0017: hash independent of thread count, test at 1/2/3 threads, 0 B/tick); 2) timed-reroute thresholds if still needed (behavior change → separate ADR, before/after waits); 3) stuck s100k; 4) s1m: reach full load, record the number. ADR for each significant decision. Real runs only (cloud is the perf reference); `update-baseline` only deliberately, separate commit. ≤ 2 parallel agents/heavy runs. Finish: merge into `dev` per the finish checklist, remove the claim line, agents.md rows (cloud numbering 169+), short report section in `docs/plan/stage5-plan.md`. No PR to main.
