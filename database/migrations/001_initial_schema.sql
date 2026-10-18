-- Enable UUID extension
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- Device table to track registered IoT devices
CREATE TABLE IF NOT EXISTS devices (
    device_id UUID PRIMARY KEY,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    last_seen_at TIMESTAMP WITH TIME ZONE
);

-- Telemetry events table
-- We use a composite unique constraint on event_id for idempotency
CREATE TABLE IF NOT EXISTS telemetry_events (
    id BIGSERIAL PRIMARY KEY,
    event_id UUID NOT NULL,
    device_id UUID NOT NULL REFERENCES devices(device_id),
    occurred_at TIMESTAMP WITH TIME ZONE NOT NULL,
    metric_id TEXT NOT NULL,
    value DOUBLE PRECISION NOT NULL,
    status TEXT NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP,
    
    CONSTRAINT uq_event_id UNIQUE (event_id)
);

-- Index for fast retrieval of latest events per device
CREATE INDEX idx_telemetry_device_time ON telemetry_events (device_id, occurred_at DESC);
