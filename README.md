# trio-bench

trio-bench is a production-shaped engineering experiment designed to compare three different backend implementations of the same IoT telemetry ingestion system. The project evaluates the trade-offs between **C#/.NET**, **Go**, and **Rust** across implementation effort, code size, throughput, latency, and resource efficiency.

## 1. The Scenario
A fleet of generic IoT devices sends telemetry events to a central service. The service authenticates clients, validates incoming events, stores them for durability, maintains a current snapshot for each device, and exposes health and operational metrics.

### Core Question
What are the differences in code size, implementation effort, throughput, latency, resource usage, and operational complexity when the same backend is built in these three languages?

## 2. Functional Requirements

### Telemetry Ingestion
- Devices send batches of telemetry events.
- Each event must be validated and authenticated.
- **Idempotency:** A unique event identifier prevents duplicate insertions.
- **Persistence:** Accepted events are stored in PostgreSQL.
- **State Management:** The service maintains the latest accepted snapshot for each device, using Redis as a cache to accelerate reads.

### Authentication & Authorization
- Clients obtain a JWT access token and a refresh token.
- Only authorized clients can submit events for their assigned devices.
- **Refresh Token Rotation:** Tokens are rotated upon use; reuse of an old refresh token is detected and handled.

### API Contract
All three implementations must adhere to the following externally observable behavior:

| Endpoint | Purpose |
| :--- | :--- |
| `POST /api/v1/auth/token` | Exchange client credentials for access and refresh tokens |
| `POST /api/v1/auth/refresh` | Rotate the refresh token and issue a new token pair |
| `POST /api/v1/devices/{id}/events` | Submit a batch of telemetry events |
| `GET /api/v1/devices/{id}/latest` | Retrieve the latest device snapshot |
| `GET /health/live` | Check whether the process is alive |
| `GET /health/ready` | Check whether required dependencies are ready |
| `GET /metrics` | Expose Prometheus-compatible metrics |

### Standard Telemetry Event Schema
```json
{
  "events": [
    {
      "eventId": "uuid",
      "occurredAt": "ISO8601 Timestamp",
      "metricId": "string",
      "value": "double",
      "status": "string"
    }
  ]
}
```

## 3. Behavioral Rules & Engineering Constraints
To ensure a fair benchmark, all implementations must handle the following scenarios identically:

### Event Processing
- **Out-of-Order Events:** The "latest snapshot" in Redis is determined by the `occurredAt` timestamp, not the arrival time. An event with an older timestamp than the current snapshot is persisted in PostgreSQL but **does not** update the Redis cache.
- **Batch Atomicity:** Event batches are processed **atomically**. If any single event in the batch fails validation or authorization, the entire request is rejected (HTTP 400/403), and no events from that batch are persisted.
- **Idempotency:** If an `eventId` already exists in PostgreSQL, the event is ignored (skipped), but the request is still considered successful (HTTP 201/202) to ensure retry safety.

### Experiment Baseline
- **The C# implementation serves as the functional baseline.** Its behavior defines "correctness." Go and Rust implementations must produce identical external outputs (API responses and Database state) given the same inputs.

## 4. Technical Architecture
The boundary remains consistent, but the internal implementation is idiomatic to each language.

- **HTTP API:** Routing, Auth, Validation.
- **Application Layer:** Use cases (Ingest, Rotate, Retrieve).
- **Domain Layer:** Business rules and state transition.
- **Persistence:** PostgreSQL (Durable storage).
- **Caching:** Redis (Latest-state cache).
- **Cross-cutting:** OpenTelemetry, Structured Logging, Prometheus Metrics.

### Implementation Styles
- **C#**: ASP.NET Core, EF Core, FluentValidation.
- **Go**: `net/http` or lightweight router, explicit dependencies.
- **Rust**: Axum, Tokio, SQLx.

## 5. Infrastructure & Setup
The project uses a `compose.yaml` file located in the `deploy/` directory.

### Running with Podman (Fedora/Linux)
To group all containers under the `trio-bench` project name:
```bash
cd deploy
podman compose -p trio-bench up -d
```
To tear down the project (keeping data):
```bash
podman compose -p trio-bench down
```
To completely wipe the project (including volumes):
```bash
podman compose -p trio-bench down -v
```

### Running with Docker (Windows/macOS/Linux)
To group all containers under the `trio-bench` project name:
```bash
cd deploy
docker compose -p trio-bench up -d
```
To tear down the project (keeping data):
```bash
docker compose -p trio-bench down
```
To completely wipe the project (including volumes):
```bash
docker compose -p trio-bench down -v
```

## 6. Implementation Plan (Milestones)

1. **Foundation**: API contracts, SQL schema, Docker Compose (Postgres/Redis), Health endpoints.
2. **Functional Parity**: Ingestion, snapshots, Auth/Refresh rotation, idempotency, and caching.
3. **Production Readiness**: OpenTelemetry, structured logs, integration tests, graceful shutdown.
4. **Performance**: Load generator (k6), latency/throughput benchmarking, resource profiling.
5. **Analysis & Publishing**: LOC comparison, implementation time logs, and result charts.

## 7. Benchmark Design
Benchmarks will be run on identical hardware with pinned versions:
- **Ingestion Throughput**: Batches of 10 events; measure events/sec and latency.
- **Read Performance**: Latest state retrieval with warm cache (p50, p95, p99).
- **Mixed Workload**: Combined ingestion, reads, and auth requests.
- **Resource Efficiency**: CPU/Memory usage per unit of throughput.

## 8. Repository Structure
```
trio-bench/
├── README.md
├── docs/
│   ├── architecture.md
│   ├── api-contract.md
│   ├── decisions/
│   ├── benchmark-methodology.md
│   └── results/
├── contracts/
│   ├── openapi.yaml
│   └── schemas/
├── database/
│   └── migrations/
├── src/
│   ├── dotnet/
│   ├── go/
│   └── rust/
├── tests/
│   ├── contract/
│   └── integration/
├── benchmarks/
│   ├── scenarios/
│   ├── scripts/
│   └── reports/
├── deploy/
│   └── compose.yaml
└── .github/
    └── workflows/
```
