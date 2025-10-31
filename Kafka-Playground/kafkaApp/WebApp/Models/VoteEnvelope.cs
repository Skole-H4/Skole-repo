namespace WebApp.Models;

public sealed class VoteEnvelope
{
    public VoteEvent Event { get; set; } = default!;

    public string? CityTopic { get; set; }

    public string? City { get; set; }

    public int? ZipCode { get; set; }
}
