# Architecture

## 1. System overview

```
                    ┌─────────────────────────┐
                    │        k6 loadgen        │  benchmarks/loadgen/benchmark.js
                    │  (pinned grafana/k6:x)   │
                    └────────────┬────────────┘
                                 │ HTTP
                                 ▼
┌──────────────────────────────────────────────────────────────┐
│                        API service                           │
│  ┌────────────┐  ┌──────────────────┐  ┌───────────────────┐ │
│  │ HTTP layer  │→ │ Application layer │→ │ Persistence layer │ │
│  │ handlers,   │  │ TelemetryService, │  │ EF Core (queries, │ │
│  │ authn/authz,│  │ AuthService       │  │ auth) / raw Npgsql│ │
│  │ RequestReader│ │      (use cases) │  │ (hot-path, txns)  │ │
│  └────────────┘  └──────────────────┘  └─────────┬─────────┘ │
│  ┌────────────────────────────────┐              │           │
│  │ Observability (OTel → OTLP,    │              │           │
│  │ Prometheus /metrics, health)   │              │           │
│  └────────────────────────────────┘              │           │
└───────────────────────────────────────────────┬──┼───────────┘
                                                │  │
                     ┌──────────────────────────┘  │
                     ▼                             ▼
          ┌──────────────────┐            ┌──────────────────┐
          │    PostgreSQL     │            │       Redis      │
          │  durable events,  │            │ latest snapshot  │
          │  devices, auth    │            │ cache (best-effort)│
          └──────────────────┘            └──────────────────┘
```

## 2. Layers (C#/.NET baseline; other stacks follow the same shape idiomatically)

- **HTTP layer** (`Program.cs`, `Services/RequestReader.cs`): routing, middleware (Prometheus collection, authn, authz), canonical request parsing. All validation rules live in `RequestReader` so error codes/messages are deterministic (see `docs/api-contract.md`).
- **Application layer** (`Services/TelemetryService.cs`, `Services/AuthService.cs`): the two use cases — ingest (+snapshot cache) and token issuance/rotation — plus the device-authorization policy check.
- **Domain layer** (`Domain/`, `Models/`): entities and DTOs; no business logic hidden in properties.
- **Persistence layer** (`Data/AppDbContext.cs`): EF Core with explicit snake_case mapping to the shared schema. The telemetry hot path uses raw Npgsql inside an explicit transaction (single batched `INSERT ... ON CONFLICT ... RETURNING`); auth uses plain EF.

## 3. Key flows

### Ingest (`POST /v1/devices/{id}/events`)
1. Parse + validate the whole batch (`RequestReader.ReadBatchAsync`) — a single invalid event rejects everything atomically with `400 VALIDATION_FAILED`.
2. In one transaction: upsert the device (`INSERT ... ON CONFLICT (device_id) DO NOTHING`) and batch-insert events (`INSERT ... SELECT unnest(...) ... ON CONFLICT (event_id) DO NOTHING RETURNING ...`).
3. `RETURNING` identifies the freshly inserted rows; duplicates inside the batch, duplicates from retries, and concurrent double-submissions are all resolved by the database, guaranteeing exactly-once rows.
4. After commit: the accepted event with the greatest `occurredAt` updates the Redis snapshot key through a Lua compare-and-set script that only overwrites when the new `occurredAt` is **strictly** newer (ties never regress the cache). Cache errors are tolerated (the read path repopulates from PostgreSQL).

### Read (`GET /v1/devices/{id}/latest`)
1. Redis `GET device:latest:{id}` → hit: respond immediately.
2. Miss: `ORDER BY occurred_at DESC, id DESC LIMIT 1` from PostgreSQL (deterministic tie-break), repopulate the cache via the same CAS script, respond.

### Token issuance and rotation
- **Issue**: verify the client credential against the stored SHA-256 digest, mint an access JWT with the client's device claims (`did`), and insert a fresh refresh-token family (opaque token stored hashed).
- **Rotate**: atomically claim the presented token (one-time-use), detect reuse (consumed/revoked presentation → revoke the whole family), then issue the successor pair with a fresh window.
- **Validate** (on every protected route): signature, expiry, and the `usage == access` claim — refresh tokens are rejected as bearers.

## 4. Cross-cutting concerns

- **Observability**: OpenTelemetry traces/metrics exported OTLP → collector; Prometheus scrape at `/.ops/metrics` (prometheus-net + ASP.NET metric middleware); readiness that verifies the actual schema; structured logs at `warn`/`error` for the paths that matter (cache failures, reuse detection).
- **Health**: `live` = process up; `ready` = schema present + DB connect + Redis ping.
- **Deployment**: everything (Postgres, Redis, collector, API, loadgen) is managed by `deploy/compose.yaml` under the `trio-bench` project name; migrations are applied automatically by the Postgres image on first boot of an empty volume. Known-good rebuilds start from `down -v` (destroys volumes).
