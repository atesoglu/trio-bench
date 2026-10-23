# Benchmark Methodology

## 1. Goal

Measure implementation effort, code size, throughput, latency, and resource efficiency for **identical workloads** against three implementations of the same service contract (`docs/api-contract.md`) and the same shared schema (`database/migrations/*.sql`).

## 2. Environment

| Component | Pinned to / measured on | Notes |
| :--- | :--- | :--- |
| Host | Benchmark operator's machine; record hardware in this file per run | Fedora Linux, Podman rootless |
| Container engine | `podman 5.x` + `podman-compose 1.6` | project name `trio-bench` |
| PostgreSQL | `postgres:16-alpine` | ports `5432`, healthcheck gated |
| Redis | `redis:7-alpine` | ports `6379` |
| OTel collector | `otel/opentelemetry-collector-contrib` | `latest` tag — pin pending, does not gate results |
| C# API | `aspnet:10.0-alpine` image, ports `5000`→`8080` | the baseline |
| Load generator | `grafana/k6:2.3.0` (**pinned**) | `benchmarks/loadgen/Dockerfile` |

## 3. Workload (benchmarks/loadgen/benchmark.js)

Each VU iteration:
1. `POST /v1/auth/token` (credentials `admin`/`password`),
2. `POST /v1/devices/{id}/events` with **10 events** (RFC 3339 `occurredAt` = now, UUID eventIds, random values),
3. `GET /v1/devices/{id}/latest`,
4. `sleep 1s`.

Stages per run: ramp to `VUS` over 10 s, hold 20 s, ramp down 10 s. `TARGET_URL`, `VUS`, `DURATION` are environment-driven so the same script runs against any stack.

**Result gates** (a run that fails a gate is a broken run, not a slow one):
- `checks rate > 0.99`
- `http_req_duration p(95) < 500 ms`

The k6 process must exit 0; any `checks` threshold violation fails the `podman compose` run.

## 4. Runbook

```bash
cd deploy
# clean, reproducible start (migrations auto-apply on the fresh volume)
podman compose -p trio-bench --profile benchmark down -v
podman compose -p trio-bench --profile benchmark up -d --build
# k6 writes its summary to container logs; archive it after the run:
podman logs trio-bench-loadgen > ../benchmarks/reports/raw-results/dotnet-$(date +%F-%H%M).log
```

For long-running monitoring use `./bench.sh` at the repository root (stats + loadgen log snapshot every 2 s).

**Data hygiene**: reset with `down -v` between runs unless absolute-volume measurements are intended. The loadgen re-authenticates per iteration, so each iteration's token metrics reflect the true auth cost of the stack under test.

## 5. Metrics captured per run

- k6: checks rate, per-scenario request counts, `p50/p90/p95` latency, HTTP failures, throughput.
- Resource efficiency: `podman stats --no-stream` snapshots for the API container (CPU %, memory) during the steady phase; archive alongside results.

## 6. Fairness rules

1. One loadgen script for all stacks; only `TARGET_URL` changes.
2. Identical schema and migrations; identical seeded clients.
3. Identical container resource access; no per-stack tuning inside the benchmark.
4. Correctness is a precondition: a stack with `checks < 0.99` is **not reportable** — fix the implementation first.
5. Report versions of every pinned image together with results.

## 7. Known measurement effects (documented, not hidden)

- The `checks` gate includes authentication: auth cost is part of the mixed workload by design.
- k6 UUIDs make eventId collisions statistically negligible; `uq_event_id` would reject true duplicates anyway (recorded as skip, not error).
- Timestamp standardization (RFC 3339 UTC) means cross-stack JSON bodies may differ in suffix rendering (`+00:00` vs `Z`); parity is semantic, not byte-level, and `lastUpdated` is inherently per-read.
