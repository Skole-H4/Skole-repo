namespace WebApp.Models;

public sealed class VoteEvent
{
    // Unique identifier for the user casting the vote. Now a Guid to guarantee uniqueness.
    public Guid UserId { get; set; }

    // Party letter from appData/parties.json (upper-case normalized before sending).
    public string Option { get; set; } = default!;

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
