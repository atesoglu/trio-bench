import http from 'k6/http';
import { check, sleep } from 'k6';

const TARGET_URL = __ENV.TARGET_URL || 'http://api-dotnet:8080';
const VUS = parseInt(__ENV.VUS || '10');
const DURATION = __ENV.DURATION || '30s';

export const options = {
  stages: [
    { duration: '10s', target: VUS },
    { duration: '20s', target: VUS },
    { duration: '10s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<500'],
  },
};

function waitForApi() {
  let ready = false;
  let attempts = 0;
  const maxAttempts = 30;
  while (!ready && attempts < maxAttempts) {
    attempts++;
    const res = http.get(`${TARGET_URL}/.ops/health/ready`);
    if (res.status === 200) ready = true;
    else sleep(1);
  }
  return ready;
}

export default async function () {
  // Every VU waits for the API on its first iteration
  if (__ITER === 0) {
    if (!waitForApi()) {
      console.log(`VU ${__VU} failed to connect to API. Skipping.`);
      return;
    }
  }

  const authPayload = JSON.stringify({ clientId: 'admin', clientSecret: 'password' });
  const authRes = http.post(`${TARGET_URL}/v1/auth/token`, authPayload, {
    headers: { 'Content-Type': 'application/json' },
  });

  if (!check(authRes, { 'auth success': (r) => r.status === 200 })) return;

  const token = JSON.parse(authRes.body).accessToken;
  const params = { headers: { 'Content-Type': 'application/json', 'Authorization': `Bearer ${token}` } };

  const deviceId = 'a73b8d7a-0f6a-4f47-b1d8-8f7b2b8d3210'; 
  const generateEvent = () => ({
    eventId: __VU + '_' + __ITER + '_' + Math.floor(Math.random() * 1000000),
    occurredAt: new Date().toISOString(),
    metricId: 'temp_01',
    value: Math.random() * 100,
    status: 'OK',
  });

  const telemetryPayload = JSON.stringify({ events: Array.from({ length: 10 }, generateEvent) });
  const ingestRes = http.post(`${TARGET_URL}/v1/devices/${deviceId}/events`, telemetryPayload, params);
  check(ingestRes, { 'ingest success': (r) => r.status === 201 });

  const readRes = http.get(`${TARGET_URL}/v1/devices/${deviceId}/latest`, params);
  check(readRes, { 'read success': (r) => r.status === 200 });

  sleep(1);
}
