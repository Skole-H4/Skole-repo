namespace WebApp.Models;

public sealed class VoteTotal
{
    public string Option { get; set; } = default!; // key mirrors this
    public int Count { get; set; }
    public string? City { get; set; }
    public int? ZipCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
