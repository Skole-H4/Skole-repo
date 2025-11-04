#!/bin/bash
set -euo pipefail

PASSWORD='ASdgfbbnh5t4!DSDsa_..S_'
SERVER='mssql'
MAX_WAIT=120

echo "[mssql-init] Waiting for SQL Server to accept connections..."
for i in $(seq 1 $MAX_WAIT); do
  if /opt/mssql-tools18/bin/sqlcmd -C -S $SERVER -U sa -P "$PASSWORD" -Q "SELECT 1" &>/dev/null; then
    break
  fi
  if [ "$i" -eq "$MAX_WAIT" ]; then
    echo "[mssql-init] SQL Server not reachable after ${MAX_WAIT}s" >&2
    exit 1
  fi
  sleep 1
done

echo "[mssql-init] Applying schema script 01-init.sql..."
/opt/mssql-tools18/bin/sqlcmd -C -S $SERVER -U sa -P "$PASSWORD" -d master -i /sql-init/01-init.sql

echo "[mssql-init] Verifying VotesRaw table exists..."
TABLE_ID=$(/opt/mssql-tools18/bin/sqlcmd -C -S $SERVER -U sa -P "$PASSWORD" -d KafkaVotes -h -1 -Q "SET NOCOUNT ON; SELECT OBJECT_ID('dbo.VotesRaw')")
if [ -z "$TABLE_ID" ] || [ "$TABLE_ID" = "NULL" ]; then
  echo "[mssql-init] Table verification failed." >&2
  exit 1
fi

echo "[mssql-init] Initialization complete."
