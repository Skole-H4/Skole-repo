namespace TallyService.Configuration;

public sealed class KafkaOptions
{
    public string BootstrapServers { get; set; } = "localhost:9092";
    public string SchemaRegistryUrl { get; set; } = "http://localhost:8081";
    public string VotesTopic { get; set; } = "votes";
    public string TotalsTopic { get; set; } = "vote-totals";
    public string VotesByCityTopic { get; set; } = "votes-by-city";
    public string TallyGroupId { get; set; } = "tally-service";
    public string TallyTransactionalId { get; set; } = "tally-service-tx-1";
    public int DefaultPartitions { get; set; } = 1;
    public short DefaultReplicationFactor { get; set; } = 1;

    // Startup behavior tuning for Streamiz hosted service:
    // Seconds to wait before evaluating whether the stream task completed (gives cleanup time).
    public int StartupGuardDelaySeconds { get; set; } = 5;

    // Maximum retries when internal topic partition mismatches are detected.
    public int MaxStreamStartupRetries { get; set; } = 2;

    // Enable deletion of stale internal Streamiz topics on startup.
    public bool EnableInternalTopicCleanup { get; set; } = true;

    // Enable resizing expected business topics to DefaultPartitions when they exist with fewer partitions.
    public bool EnableTopicResize { get; set; } = true;

    // Maximum seconds to wait for Kafka to actually drop internal topics after issuing delete.
    public int InternalTopicDeletionTimeoutSeconds { get; set; } = 120;
}
