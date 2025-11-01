# TallyService Critical Paths & Operational Guide

## Overview
TallyService exposes two aggregation mechanisms for vote tallies:
1. Stream Processing (StreamTallyHostedService + Streamiz topology)
2. Manual Transactional Worker (TallyWorker using Confluent.Kafka directly)
3. Diagnostic Minimal Topology (MiniStreamHostedService)

Only one should produce authoritative tallies in production to avoid duplication.

## Critical Paths

### 1. Ingestion Path
- Source Topics: Votes topic (`KafkaOptions.VotesTopic`).
- Consumers:
  - Streamiz: `builder.Stream<string, VoteEnvelope>(...)` or minimal strings in mini service.
  - Worker: `IKafkaClientFactory.CreateVoteConsumer()` (VoteEnvelope via JSON Schema Registry serializer).

### 2. Deserialization & Normalization
- Tolerant SerDes (Streamiz path): Handles nested `{"Event":{...}}` and flattened JSON forms, classifies malformed payloads, throttles logging.
- Worker path: `JsonDeserializer<VoteEnvelope>` with Schema Registry.
- Normalization: `Option` uppercased and trimmed; city resolved via topic, zip code, city name, or key prefix.

### 3. Aggregation Logic
- Streamiz: KTable counts and per-city aggregations (currently filtered until schema alignment ensures valid `VoteEnvelope`).
- Worker: In-memory dictionaries `_globalCounts` and `_cityCounts`, updated inside Kafka transactions to ensure exactly-once semantics for output topics.

### 4. Output Emission
- Topics: `TotalsTopic` (global option counts) and `VotesByCityTopic` (city-option composite key `cityTopic:OPTION`).
- Worker produces within transactions: `BeginTransaction -> Produce -> SendOffsetsToTransaction -> CommitTransaction` to tie consumed offsets and produced messages atomically.
- Mini topology emits `CountRecord` JSON (diagnostic only) to totals topic.

### 5. State Restoration (Worker Only)
- On startup: `RestoreStateAsync()` consumes compacted output topics with a temporary group id to rebuild dictionaries before new votes processed.

### 6. Internal Topic Lifecycle
- Streamiz uses internal changelog/repartition topics named with the `ApplicationId` prefix.
- Lenient topic manager swallows benign `TopicAlreadyExists` exceptions to allow restarts without destructive resets.
- Mini topology enumerates existing internal topics for visibility.

### 7. Configuration & Operational Toggles
- Environment flags (planned):
  - `ENABLE_STREAM_TOPOLOGY=true|false`
  - `ENABLE_TALLY_WORKER=true|false`
  - `ENABLE_MINI_STREAM=true|false`
  - `KAFKA_EPHEMERAL_ID=true|false` controls appId uniqueness for Streamiz.
  - Deserialization tuning: `KAFKA_DESER_WARN_LIMIT`, `KAFKA_DESER_SUMMARY_INTERVAL`, `KAFKA_DESER_PREVIEW_LIMIT`.

### 8. Error Handling & Resilience
- Streamiz: Custom inner exception handler continues on benign internal topic conflicts.
- Worker: Retries loop on transient consume or Kafka exceptions with backoff.
- Transactions aborted safely on failures to avoid partial commits.

## Choosing Aggregator Strategy
| Strategy | Pros | Cons | Use Case |
|----------|------|------|---------|
| Worker (Transactional) | Deterministic exactly-once output; simple dictionaries | Manual logic; less declarative | Production when schema stable but DSL overhead not needed |
| Streamiz Topology | Declarative; scalable; windowing & joins available | More internal topics; requires robust SerDes | Future advanced analytics or multi-join/count features |
| Mini Diagnostic | Minimal code; rapid verification | Not feature-complete; AT_LEAST_ONCE | Troubleshooting ingestion & counting viability |

## Avoiding Duplicate Tallies
Ensure only one of the three hosted services producing to `TotalsTopic` / `VotesByCityTopic` is active. Mixing them will cause inconsistent counts.

## Fast Checklist (Prod Deploy)
- [ ] Confirm single active tally producer (worker or stream topology).
- [ ] Validate replication factor against cluster broker count.
- [ ] Ensure Schema Registry reachable if using JSON serdes.
- [ ] Observe startup logs for internal topic reuse warnings (expected, benign).
- [ ] Verify first vote increments expected option count.
- [ ] Confirm transactions committing (no repeated abort warnings).

## Future Enhancements (Next Steps)
- Add dead-letter topic for malformed vote payloads.
- Expand SerDes alias mapping (choice / selection / candidate). 
- Introduce metrics exporting (Prometheus counters for processed votes, error categories).
- Implement health endpoint broadcasting current tally snapshot.
- Add integration tests using a test container Kafka cluster.

## Glossary
- VoteEnvelope: Incoming event wrapper including city hints (topic, zip, city name).
- VoteTotal: Output record representing cumulative count for an option (global or city-scoped).
- Exactly-Once Semantics: Guarantee consumed offsets and produced outputs atomically committed.

## Ownership
Primary code owners: Stream topology (`StreamTallyHostedService`), transactional worker (`TallyWorker`), diagnostics (`MiniStreamHostedService`).

---
This document should be updated when topology structure or toggles change.
