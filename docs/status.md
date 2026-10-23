# Project Status & Handoff — trio-bench

> Purpose: this file is the **session handoff**. Any assistant-contributor session reading it
> plus the two cited specs can resume work without prior conversation context.

## Current state (as of commit)

- **C#/.NET baseline: complete, hardened, and verified** — commit `fe06e43` on `main`
  (see `git log` for the Stage-1 fix commits preceding it).
- Verified green: k6 run **100% checks** (auth/ingest/read), p(95) ≈ 7.5 ms;
  a 17-case behavioral probe suite (rotation reuse → family revocation, 403 device
  authz, error-shape catalog, duplicate handling, out-of-order, `/metrics`).
- Infra: `deploy/compose.yaml` — Postgres 16 / Redis 7 / OTel collector / api-dotnet
  (`5000→8080`) / k6 loadgen (pinned `grafana/k6:2.3.0`). Shared schema is **auto-applied**
  by Postgres initdb on first boot of an empty volume. After schema changes always
  `podman compose -p trio-bench down -v` before rerunning.

## Binding documents

1. **`docs/api-contract.md`** — the parity spec: exact status codes, response shapes, auth
   semantics, the complete error `code`/`message` catalog, idempotency/out-of-order/CAS
   cache rules, tie-breaks. Every implementation must reproduce it; behavior changes must
   update it in the same commit.
2. **`docs/decisions/0001-baseline-hardening.md`** — 12 accepted decisions with rationale.
3. `docs/architecture.md`, `docs/benchmark-methodology.md` — shapes/runbook.
4. `benchmarks/reports/raw-results/latest_run.log` — latest archived k6 summary.

## Next mission: the Go implementation (src/go)

Workflow (unchanged): **user hand-writes Go → assistant reviews (correctness/parity, idiom,
concurrency, tests) → lock → commit.** Move feature-by-feature; the baseline is the judge.

Planned feature units, each ending in a lock + commit:

| # | Unit | Acceptance sketch |
| :--- | :--- | :--- |
| G1 | Skeleton: `go.mod`, stdlib `net/http` router (no router dep), config from env, graceful shutdown, `/.ops/health/live` + `ready` (schema check!), Dockerfile, `api-go` service in compose (port `5001→8080`) | live/readiness green, compose boots, contract-tested endpoints respond with the shapes of §3–4 of the parity spec |
| G2 | Storage: `pgx/v5` pool + `redis/go-redis/v9` client; parity of init behavior with the C# EF mapping (same tables) | ready endpoint with real checks; manual SQL round-trips |
| G3 | Ingest: same validation rules/order (see RequestReader semantics in the C# tree), single tx device upsert + `INSERT … unnest(…) ON CONFLICT (event_id) DO NOTHING RETURNING`, Lua-CAS cache strictly newer | 17-probe equivalent passes (exact error catalog incl. messages) |
| G4 | Read: cache → DB fallback (`ORDER BY occurred_at DESC, event id DESC`) → repopulate; identical snapshot JSON shape; 404 rules (incl. unassigned devices) | probe suite |
| G5 | Auth: HS256 JWT (shared key constant), `sub`/`usage`/`did` claims, opaque refresh tokens hashed (SHA-256 hex), family rotation + reuse revocation, `usage` mismatch → 401 | probe suite incl. cross-use and race semantics of the rotation `UPDATE` |
| G6 | Observability: OTel → OTLP collector, structured logging, real Prometheus `/.ops/metrics` | parity with baseline ops endpoints |
| G7 | Contract test suite vs. C# baseline + head-to-head k6 runs; record results | benchmark-methodology.md gates |

Suggested module layout: `src/go/cmd/api/main.go` + `src/go/internal/{api,auth,telemetry,domain,storage/postgres,storage/redis,config,obs}`. Prefer stdlib; only pull: `jackc/pgx/v5`, `redis/go-redis/v9`, `golang-jwt/jwt/v5`, `google/uuid`, `prometheus/client_golang`, OTel SDK. Validation is hand-rolled (mirror the C# `RequestReader` — do not use a validator framework, to keep the error catalog exact).

Non-goals / known minor debt (park): pin the OTel collector image (`latest`), bench.sh's
monitoring loop promising teardown it doesn't perform, exhaustive in-batch duplicate
tie-contention tests.

## Session logistics

- Start a fresh assistant session in the repo root; first prompt: *"Read `docs/status.md`
  and `docs/api-contract.md`, then start Go G1 with your planned structure."*
- Commits: conventional style (`feat:`, `fix:`, `docs:`), and push after each lock.
