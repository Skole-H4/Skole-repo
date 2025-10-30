namespace TallyService.Models;

public sealed class VoteTotal
{
    public string Option { get; set; } = default!;

    public int Count { get; set; }

    public string? City { get; set; }

    public int? ZipCode { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
