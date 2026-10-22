#!/bin/bash

# --- Configuration ---
PROJECT_NAME="trio-bench"
COMPOSE_FILE="deploy/compose.yaml"
TARGET_CONTAINER="trio-bench-dotnet"

echo "🚀 Starting Benchmark Suite..."

# 1. Start the infra and loadgen
podman compose -p $PROJECT_NAME -f $COMPOSE_FILE --profile benchmark up -d --build

echo "⏳ Waiting for services to stabilize..."
sleep 5

# 2. Run a loop to show stats and logs simultaneously
echo "--------------------------------------------------------------------------------"
echo " MONITORING MODE: Press Ctrl+C to stop monitoring and tear down"
echo "--------------------------------------------------------------------------------"

# We use a loop to print a snapshot of stats every 2 seconds
while true; do
    clear
    echo "=== TRIO-BENCH REAL-TIME METRICS ==="
    echo "Timestamp: $(date)"
    echo "-----------------------------------"
    podman stats $TARGET_CONTAINER --no-stream
    echo "-----------------------------------"
    echo "Last 10 lines of LoadGen logs:"
    podman logs --tail 10 trio-bench-loadgen
    echo "-----------------------------------"
    echo "Press Ctrl+C to stop and clean up."
    sleep 2
done
