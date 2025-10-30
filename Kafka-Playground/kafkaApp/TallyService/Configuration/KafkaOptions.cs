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
    public int DefaultPartitions { get; set; } = 3;
    public short DefaultReplicationFactor { get; set; } = 1;
}
