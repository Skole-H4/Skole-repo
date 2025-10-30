namespace TallyService.Models;

public sealed class VoteEvent
{
    public string UserId { get; set; } = default!;

    public string Option { get; set; } = default!;

    public DateTimeOffset Timestamp { get; set; }
}
