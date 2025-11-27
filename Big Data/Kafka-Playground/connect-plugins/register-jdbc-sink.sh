#!/bin/sh
set -eu
CONNECT_URL=${CONNECT_URL:-http://connect:8083}
CONFIG=/register/jdbc-sink-votes.json
MAX_WAIT=120

echo "[connect-init] Waiting for Kafka Connect REST API..."
for i in $(seq 1 $MAX_WAIT); do
  if curl -fsS "$CONNECT_URL/connectors" >/dev/null 2>&1; then
    break
  fi
  if [ "$i" -eq "$MAX_WAIT" ]; then
    echo "[connect-init] Connect API not reachable after ${MAX_WAIT}s" >&2
    exit 1
  fi
  sleep 2
done

echo "[connect-init] Registering (or updating) JDBC sink connector..."
# Try update first (PUT); if it fails (404), fallback to POST create.
if ! curl -fsS -X PUT -H 'Content-Type: application/json' --data @"${CONFIG}" "$CONNECT_URL/connectors/jdbc-sink-votes/config" >/dev/null 2>&1; then
  curl -fsS -X POST -H 'Content-Type: application/json' --data @"${CONFIG}" "$CONNECT_URL/connectors" >/dev/null 2>&1 || {
    echo "[connect-init] Failed to create connector." >&2
    exit 1
  }
fi

echo "[connect-init] Checking connector status..."
STATUS_JSON=$(curl -fsS "$CONNECT_URL/connectors/jdbc-sink-votes/status" || true)
if echo "$STATUS_JSON" | grep -qi 'FAILED'; then
  echo "[connect-init] Connector reported FAILED state." >&2
  echo "$STATUS_JSON"
  exit 1
fi

echo "[connect-init] Connector registration complete."
