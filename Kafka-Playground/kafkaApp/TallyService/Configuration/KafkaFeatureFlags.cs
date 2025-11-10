namespace TallyService.Configuration;

public sealed class KafkaFeatureFlags
{
    public bool StreamTopologyEnabled { get; set; } = true;
    public bool ForceStateResetOnStart { get; set; } = false;
    public bool AutoStallRecoveryEnabled { get; set; } = true;
    public bool RawVoteFallbackEnabled { get; set; } = false;
    public bool TestVoteGeneratorEnabled { get; set; } = false;
    public bool EphemeralStreamId { get; set; } = false;
    public int DeserWarnLimit { get; set; } = 10;
    public int DeserSummaryInterval { get; set; } = 100;
    public int DeserPreviewLimit { get; set; } = 120;
}