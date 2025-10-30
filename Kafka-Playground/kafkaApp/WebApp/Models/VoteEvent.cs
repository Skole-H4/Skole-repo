namespace WebApp.Models;

public sealed class VoteEvent
{
    public string UserId { get; set; } = default!;
    public string Option { get; set; } = default!; // Party letter from appData/parties.json
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
