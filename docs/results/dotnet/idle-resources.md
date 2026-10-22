# .NET Baseline: Idle Resource Analysis

**Date:** 2026-10-23
**Environment:** Fedora 44 / Podman
**Runtime:** .NET 10.0 (Alpine)

## 1. Image Metrics
- **Image Name:** `localhost/trio-bench_api-dotnet:latest`
- **Image Size:** 134 MB

## 2. Runtime Metrics (Idle)
Captured via `podman stats` while the service was running with no active load.

| Metric | Value |
| :--- | :--- |
| **CPU Usage** | 0.40% |
| **Memory Usage** | 32.57 MB |
| **Memory Limit** | 62.95 GB |
| **Memory %** | 0.05% |
| **PIDS (Threads)** | 35 |
| **Net I/O** | 6.844kB / 5.423kB |

## 3. Notes
- The memory footprint is remarkably lean for a .NET application, likely due to the use of Minimal APIs and the Alpine base image.
- This serves as the baseline for comparison against Go and Rust implementations.
