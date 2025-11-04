namespace TallyService.Models;

public sealed class VoteEvent
{
    // GUID-based user id produced by WebApp; preserved here for schema alignment / auditing.
    public Guid UserId { get; set; }

    public string Option { get; set; } = default!;

    public DateTimeOffset Timestamp { get; set; }
}
