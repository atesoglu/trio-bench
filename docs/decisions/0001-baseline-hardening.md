# Decision Records — Baseline Hardening (Stages 1–2)

Status: **accepted**, applies to all three implementations.

## Context

The original baseline produced green-looking benchmark numbers while measuring a broken system: the load generator sent non-UUID `eventId`s that the only working endpoint chain rejected at different layers (mock token not matching the API's own bearer validation, missing schema, missing EF mapping to the shared snake_case tables, device auto-registration races). Stage 1 made the pipeline truthful (run gate on checks, pinned k6, real signed tokens, schema auto-apply, EF mapping fixes). Stage 2 raised the baseline to the behavior the project charter requires. Decisions 1–4 concern Stage 1 plumbing; the rest govern all stacks.

## Decisions

1. **Schema application is automatic.** The Compose Postgres mounts `database/migrations/` into `/docker-entrypoint-initdb.d`; `001` (core tables) and `002` (clients, assignments, refresh lineage + seeded benchmark clients) run alphabetically on the first boot of an empty volume. Reason: the shared schema is the contract; manual application steps caused an entire invisible failure mode. Consequence: schema changes require `down -v` before the next run.

2. **EF Core maps to the real table/column names explicitly** (`devices`, `telemetry_events`, `clients`, ... snake case). Reason: EF's default quoted PascalCase names silently created a second, invisible schema. Mapping is declared in one place (`AppDbContext`).

3. **Readiness checks the schema, not just connectivity.** `/.ops/health/ready` executes a real query against `devices` in addition to `CanConnect` + Redis ping. Reason: a database without tables passed the old probe; a broken run must show up immediately.

4. **Load generator integrity.** k6 is pinned (2.3.0), eventIds are real RFC 4122 UUIDs, and `checks: rate>0.99` gates every run. Reason: the benchmark's first job is to not lie.

5. **Real authentication with seeded benchmark tenants.** Clients `admin` (wildcard) and `restricted` (no devices) live in the shared schema with SHA-256-hex secrets. Access tokens are HS256 JWTs carrying `sub`, `usage`, `jti`, and one `did` claim per authorized device (`*` for wildcard). `usage` claim validation ensures refresh tokens cannot act as access tokens. Reason: the charter requires device-scoped authorization (403) and it must be identical across implementations. Consequence: the k6 flow (admin, fixed device) works unchanged.

6. **Refresh token rotation is stateful, one-time-use, and strict.** Tokens become opaque 32-byte values hashed in `refresh_tokens` with a family chain; rotation atomically claims the token (`UPDATE ... WHERE used_at IS NULL AND revoked_at IS NULL`), issues the successor in the same family with a new 24 h window, and treats any replay of a used/revoked token as theft by revoking the entire family. There is no retry grace window on purpose (RFC 6819 semantics; documented). Reason: "detect reuse of an old refresh token" is a charter requirement and needs a canonical reference behavior that Go and Rust can mirror exactly.

7. **Canonical validation in one parser, not a validator framework.** All request parsing lives in `RequestReader`; FluentValidation is removed. Field validation order is fixed (`eventId → occurredAt → metricId → value → status`), `occurredAt` must be RFC 3339 with an explicit offset and is normalized to UTC, `eventId` must be canonical-hyphen UUID, `value` must be finite. Reason: the three implementations must emit byte-identical error semantics; a single explicit parser is the portable definition of correctness and removes deps from the hot path.

8. **Operationally-shaped error responses.** Every 400/403 and every auth 401/body returns `ErrorResponse{code, message}` from an exact catalog (`docs/api-contract.md` §3). Framework-shaped 401 challenges on protected routes keep empty bodies (per OpenAPI). Malformed `{id}` paths answer `400 INVALID_DEVICE_ID` instead of framework-default binding failures. Reason: identical external outputs are measurable only if error bodies are specified.

9. **Read answering 404 for unauthorized devices.** A client without assignment to a device gets `404` on `GET /latest` (and `403` on ingest). Reason: never disclose other clients' device existence; both statuses are documented contract behavior rather than implementation accidents.

10. **Telemetry hot path is one idempotent statement.** Device upsert + event insert run in one transaction; events insert via `INSERT ... SELECT unnest(...) ... ON CONFLICT (event_id) DO NOTHING RETURNING`. This removes the per-event existence-check N+1, resolves duplicate and race semantics in the database, and makes in-batch duplicates a plain `201`. Reason: idempotency is a correctness requirement under concurrency, and fewer round trips is also what the benchmark is measuring.

11. **Snapshot cache is strictly-after-commit and CAS-protected.** The cache update selects the batch's max-`occurredAt` accepted event and applies a Lua compare-and-set that skips ties and older values, so concurrent batches can never regress the snapshot. Cache failures degrade gracefully (read path repopulates from PostgreSQL). Reason: the cache is an accelerator, not truth; a failed save must not leave a phantom and a must-not-regress invariant is cheap to enforce.

12. **Real metrics endpoint in the baseline.** `/.ops/metrics` serves Prometheus exposition (per-endpoint request duration histograms) via prometheus-net; OTel → OTLP stays unchanged. Reason: readiness and metrics are part of "production-shaped," and Go/Rust will implement the same semantics (property names may be idiomatic per stack).

## Consequences

- The C#/.NET baseline is now the reference implementation for contract behavior, not merely a mock: parity work for Go/Rust can start from `docs/api-contract.md` alone.
- Error code/message catalog and the cache/idempotency semantics in `docs/api-contract.md` are binding; changing them requires updating that document in the same commit.
- Volume reset (`down -v`) is expected behavior after schema changes, not a workaround.
