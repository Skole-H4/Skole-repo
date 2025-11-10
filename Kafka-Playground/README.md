# Kafka Playground (Official Apache image, KRaft)

This replaces Bitnami with the **official `apache/kafka`** image and works out-of-the-box on Windows/macOS/Linux.

Includes:
- Kafka 4.0.1 in **KRaft** (no ZooKeeper), single-node
- Kafka UI (http://localhost:8080)
- Schema Registry (http://localhost:8081)
- Kafka Connect (http://localhost:8083) with the Confluent JDBC sink + Microsoft SQL Server driver baked in
- One-shot topic bootstrap (`demo`, `deadletter`)

## Quick start

docker compose up -d
```bash
unzip kafka-playground-apache.zip && cd kafka-playground-apache
docker compose build connect          # ensures JDBC sink plugin + MSSQL driver present
docker compose up -d
```

**Connect:**
- From your host: `localhost:9092`
- From other containers: `kafka:29092`

**Notes**
- This compose omits the `version:` key to silence the Compose warning.
- If your client runs on another machine, edit `.env` and set `DOCKER_HOST_NAME` to your host’s IP/DNS, then `docker compose up -d` again.
- The `CLUSTER_ID` in `.env` is used only on first run to format storage and is persisted in `data/kafka/meta.properties`.

## Quick test

```bash
# Producer (type lines, then Ctrl+C)
docker compose exec kafka kafka-console-producer.sh --bootstrap-server kafka:29092 --topic demo

# Consumer (reads what you produced)
docker compose exec kafka kafka-console-consumer.sh --bootstrap-server kafka:29092 --topic demo --from-beginning
```

## Troubleshooting

- If you previously tried the Bitnami image, remove it and start clean:
  ```bash
  docker compose down -v
  docker image rm bitnami/kafka || true
  docker compose up -d
  ```
- If you change `DOCKER_HOST_NAME`, update clients to match `PLAINTEXT_HOST://<host>:9092`.
