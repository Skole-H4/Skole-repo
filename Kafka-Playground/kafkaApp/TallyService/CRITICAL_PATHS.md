# TallyService Critical Paths & Operational Guide

## Overview
TallyService aggregates vote totals exclusively through the Stream Processing path
(`StreamTallyHostedService` + Streamiz topology). The legacy transactional worker and
mini diagnostic services have been removed so there is a single source of truth for
produced tallies.

## Critical Paths

### 1. Ingestion Path
- Source Topic: Votes topic (`KafkaOptions.VotesTopic`).
- Consumer: Streamiz `builder.Stream<string, VoteEnvelope>(...)` using the Confluent JSON serdes wrapper.

### 2. Deserialization & Normalization
- Confluent JSON SerDes with schema registry support ensures incoming envelopes are materialized consistently.
- Normalization: `Option` uppercased and trimmed; city resolved via topic, zip code, city name, or key prefix.

### 3. Aggregation Logic
- Streamiz: KTable counts and per-city aggregations (currently filtered until schema alignment ensures valid `VoteEnvelope`).

### 4. Output Emission
- Topics: `TotalsTopic` (global option counts) and `VotesByCityTopic` (city-option composite key `cityTopic:OPTION`).
- Streamiz topology writes compacted KTables to both topics.

### 5. State Restoration
- Streamiz KTable changelog topics rebuild state on startup automatically.

### 6. Internal Topic Lifecycle
- Streamiz uses internal changelog/repartition topics named with the `ApplicationId` prefix.
- The topology relies on Kafka's default topic manager; ensure topics exist or allow auto-creation per environment policy.

### 7. Configuration & Operational Toggles
- Environment flags (planned):
  - `ENABLE_STREAM_TOPOLOGY=true|false`
  - `KAFKA_EPHEMERAL_ID=true|false` controls appId uniqueness for Streamiz.
  - Deserialization tuning: `KAFKA_DESER_WARN_LIMIT`, `KAFKA_DESER_SUMMARY_INTERVAL`, `KAFKA_DESER_PREVIEW_LIMIT`.

### 8. Error Handling & Resilience
- Streamiz custom inner exception handler continues on benign internal topic conflicts and logs failures for manual remediation.

## Avoiding Duplicate Tallies
Only the Streamiz hosted service emits tallies, simplifying operational guardrails. Ensure no additional ad hoc producers publish to the totals topics.

## Fast Checklist (Prod Deploy)
- [ ] Validate replication factor against cluster broker count.
- [ ] Ensure Schema Registry reachable for Confluent JSON serdes.
- [ ] Observe startup logs for internal topic reuse warnings (expected, benign).
- [ ] Verify first vote increments expected option count.

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
Primary code owners: Stream topology (`StreamTallyHostedService`).

---
This document should be updated when topology structure or toggles change.
