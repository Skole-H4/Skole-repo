namespace WebApp.Models;

public sealed class VoteEvent
{
    public string UserId { get; set; } = default!;
    public string Option { get; set; } = default!; // "A" | "B" | "C"
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
}
