-- Stage 2: benchmark tenants, device assignments and refresh token lineage.
-- Applied automatically by the Postgres container on first boot with an empty volume
-- (see deploy/compose.yaml); scripts in /docker-entrypoint-initdb.d run alphabetically.

CREATE TABLE IF NOT EXISTS clients (
    client_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name TEXT NOT NULL UNIQUE,
    secret_hash TEXT NOT NULL,
    all_devices BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Explicit device assignments for clients without wildcard access.
CREATE TABLE IF NOT EXISTS client_devices (
    client_id UUID NOT NULL REFERENCES clients (client_id) ON DELETE CASCADE,
    device_id UUID NOT NULL REFERENCES devices (device_id) ON DELETE CASCADE,
    PRIMARY KEY (client_id, device_id)
);

-- Refresh token lineage: rows form a chain per credential grant (family). A successful
-- rotation marks the presented row used and inserts its successor; presenting a used or
-- revoked row again is treated as theft and revokes the whole family.
CREATE TABLE IF NOT EXISTS refresh_tokens (
    token_hash TEXT PRIMARY KEY,
    family_id UUID NOT NULL,
    client_id UUID NOT NULL REFERENCES clients (client_id) ON DELETE CASCADE,
    created_at TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    expires_at TIMESTAMP WITH TIME ZONE NOT NULL,
    used_at TIMESTAMP WITH TIME ZONE,
    revoked_at TIMESTAMP WITH TIME ZONE
);

CREATE INDEX IF NOT EXISTS idx_refresh_tokens_family ON refresh_tokens (family_id);

-- Seeded benchmark clients. Secrets are stored as SHA-256 hex digests (a deliberate,
-- deterministic choice for the benchmark — see docs/api-contract.md):
--   sha256("password") = 5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8
-- 'admin' issues wildcard tokens, 'restricted' has no devices (exercises HTTP 403).
INSERT INTO clients (name, secret_hash, all_devices)
VALUES ('admin', '5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8', TRUE),
       ('restricted', '5e884898da28047151d0e56f8dc6292773603d0d6aabbdd62a11ef721d1542d8', FALSE)
ON CONFLICT (name) DO NOTHING;
