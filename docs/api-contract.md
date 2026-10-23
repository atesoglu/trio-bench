# API Contract & Parity Specification

This document is the **external behavior contract** for the trio-bench service. All three implementations (C#/.NET, Go, Rust) must reproduce this observable behavior given identical inputs. Where this document and the OpenAPI file (`contracts/openapi.yaml`) both speak, this document defines the *exact* wire behavior; the OpenAPI file declares the general shape.

Implementations must be containerized on the shared Compose environment and consume the shared schema (`database/migrations/*.sql`).

---

## 1. Conventions

- **Base path**: `/v1` for business endpoints; `/.ops/*` for operations.
- **JSON**: request/response bodies are UTF-8 `application/json` with camelCase property names.
- **Timestamps**: RFC 3339 `date-time`. Event timestamps with offsets are **normalized to UTC** at ingestion. UTC instants serialize with a `+00:00` offset on .NET; Go/Rust must serialize RFC 3339 UTC (`Z`). The `lastUpdated` of a snapshot is **server wall-clock time at the read**, not event time.
- **UUIDs**: hyphenated canonical form (`8-4-4-4-12`).
- **Delta rule**: every change of externally observable behavior requires an update to this document in the same commit.

## 2. Clients & Authentication

### 2.1 Seeded clients (migration `002_auth_and_tenancy.sql`)

| Client | Credential | Scope |
| :--- | :--- | :--- |
| `admin` | `password` | every device (wildcard) |
| `restricted` | `password` | no devices (for 403 demonstration) |

Client secrets are stored as **SHA-256 hex digests**. This is a deliberate, documented benchmark choice (deterministic, identical cost across languages); it is not production password hashing.

### 2.2 Token pair

`POST /v1/auth/token` with `{"clientId": "...", "clientSecret": "..."}` returns `200`:

```json
{
  "accessToken": "<JWT>",
  "refreshToken": "<opaque>",
  "expiresIn": 3600
}
```

- **Access token**: HS256-signed JWT, key `a_very_long_and_secure_secret_key_for_benchmarking_123`, TTL 60 min. Claims: `sub` (client name), `usage: access`, `jti`, `exp`, and one `did` claim per authorized device (`did: *` for wildcard clients).
- **Refresh token**: opaque random 32 bytes, base64url, TTL 24 h, stored as SHA-256 hex in `refresh_tokens`. Each credential grant starts a **new token family** (`family_id`).

`usage` is validated on protected endpoints: an access token is required as bearer; presenting a refresh token returns **401**.

### 2.3 Refresh rotation & reuse detection

`POST /v1/auth/refresh` with `{"refreshToken": "..."}`:

- Presenting an unused, unexpired token: the row is atomically claimed (`used_at = now`), a **successor** token pair is issued in the same family with a fresh 24 h window. `200` with a new pair.
- Presenting an already used/revoked token: **theft signal** → the entire family is revoked (all rows get `revoked_at`) and the response is `401`. There is **no retry grace window**.
- Unknown, expired, or revoked presentations: `401`.

Concurrency guarantee: exactly one concurrent caller can consume a given refresh token (atomic `UPDATE ... WHERE used_at IS NULL AND revoked_at IS NULL`). A legitimate retry that races itself may therefore kill its own family by design (RFC 6819 semantics); documentation-before-grace is intentional for the benchmark.

## 3. Error Catalog

All operational errors return an `ErrorResponse` object (`{"code": "...", "message": "..."}`) with these **exact** values:

| HTTP | `code` | `message` | When |
| :--- | :--- | :--- | :--- |
| 400 | `INVALID_JSON` | `request body is not valid JSON` | Malformed JSON body |
| 400 | `INVALID_REQUEST` | `clientId and clientSecret are required` | `/auth/token` fields absent/empty/non-string |
| 400 | `INVALID_REQUEST` | `refreshToken is required` | `/auth/refresh` field absent/empty/non-string |
| 400 | `INVALID_DEVICE_ID` | `device id is not a valid UUID` | `{id}` path segment not a canonical UUID |
| 400 | `VALIDATION_FAILED` | `events must be a non-empty array` | `events` missing, non-array, or empty |
| 400 | `VALIDATION_FAILED` | `events[N] must be a JSON object` | Batch element non-object |
| 400 | `VALIDATION_FAILED` | `events[N].eventId must be a valid UUID` | Not canonical UUID form |
| 400 | `VALIDATION_FAILED` | `events[N].occurredAt must be a valid RFC 3339 timestamp` | Missing offset, wrong format, unparseable |
| 400 | `VALIDATION_FAILED` | `events[N].metricId must not be empty` | Empty/whitespace/missing/non-string |
| 400 | `VALIDATION_FAILED` | `events[N].value must be a finite number` | Missing/non-number/Infinity/NaN |
| 400 | `VALIDATION_FAILED` | `events[N].status must not be empty` | Empty/whitespace/missing/non-string |
| 401 | `INVALID_CREDENTIALS` | `invalid client credentials` | Unknown client / wrong secret |
| 401 | `INVALID_REFRESH_TOKEN` | `invalid refresh token` | Unknown/expired/rotated/revoked refresh token (reuse included) |
| 403 | `DEVICE_NOT_AUTHORIZED` | `client is not authorized for this device` | Valid access token, device not assigned to that client (ingest only — see 4.5) |

**Batch atomicity**: validation is evaluated per event in schema order (`eventId → occurredAt → metricId → value → status`) and no state is written unless **every** event is valid (HTTP 400 precedes any persistence).

Unauthenticated/expired bearer tokens on protected endpoints return a framework-shaped `401` challenge with an empty body (OpenAPI declares 401 without content for these endpoints).

## 4. Endpoint Behaviors

### 4.1 `POST /v1/devices/{id}/events` (authorized)

Validation (section 3) → persistence → `201` with an **empty body**.

- **Device auto-registration**: unknown devices are inserted (`ON CONFLICT DO NOTHING`) in the same transaction as the events. Concurrent first-registrations must not produce errors.
- **Idempotency**: `event_id` is globally unique (`uq_event_id`). Duplicates — within the batch or against stored events — are **skipped**, never fatal, and the response remains `201`. Only freshly inserted rows are considered for the cache.
- **Single transaction**: device upsert + event insert commit together or not at all.
- **Snapshot cache**: strictly **after** commit, the accepted event with the greatest `occurredAt` (first in submission order breaks ties) updates the Redis key `device:latest:{deviceId}` **only if strictly newer** than the cached value (epoch-millisecond compare-and-set inside Redis; ties never overwrite). Cache failures are logged and tolerated — the read path self-heals from the database.

### 4.2 `GET /v1/devices/{id}/latest` (authorized)

- Newer-than-cached rule means the cache is always the current snapshot when present.
- Cache hit → `200` `DeviceSnapshot` (`{deviceId, latestEvent, lastUpdated}`).
- Cache miss → most recent event per the query `ORDER BY occurred_at DESC, id DESC` (deterministic tie-break: highest `id` = most recently stored). Then the cache is repopulated with the same strictly-newer rule, and `200` is returned.
- Device unknown **or** no events: `404` (empty body).
- Device not assigned to the caller: `404` as well (deliberate: never disclose the existence or content of another client's devices; ingest answers `403`, reads answer `404`).

### 4.3 `POST /v1/auth/token` / `POST /v1/auth/refresh`

Covered in section 2 and the error catalog.

### 4.4 Operations

| Endpoint | Success | Failure |
| :--- | :--- | :--- |
| `GET /.ops/health/live` | `200` (any body) | — |
| `GET /.ops/health/ready` | `200` when the **schema is present**, PostgreSQL connects, and Redis pings | `503` otherwise (a database without tables must fail readiness) |
| `GET /.ops/metrics` | `200` Prometheus text exposition (per-endpoint request duration histograms, process metrics) | — |

## 5. Data Model (shared)

Tables from `database/migrations/`:

- `devices(device_id PK, created_at, last_seen_at)` — `last_seen_at` is currently unused by the service.
- `telemetry_events(id BIGSERIAL PK, event_id UNIQUE, device_id FK, occurred_at, metric_id, value, status, created_at)` — `created_at` is DB default on insert.
- `clients(client_id PK, name UNIQUE, secret_hash, all_devices, created_at)`
- `client_devices(client_id, device_id)` — assignments for non-wildcard clients.
- `refresh_tokens(token_hash PK, family_id, client_id, created_at, expires_at, used_at, revoked_at)` — an index exists on `family_id`.

All implementations must read/write these tables identically. Persisted state after the same load must be identical: the same request set yields the same rows.
