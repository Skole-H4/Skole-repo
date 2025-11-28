namespace WebApp.Models;

public sealed class VoteEnvelope
{
    private VoteEvent _event = default!;

    public VoteEvent Event
    {
        get => _event;
        set => _event = value ?? throw new ArgumentNullException(nameof(value));
    }

    public string? CityTopic { get; set; }

    public string? City { get; set; }

    public int? ZipCode { get; set; }

    public Guid UserId => _event?.UserId ?? Guid.Empty;

    public string Option => _event?.Option ?? string.Empty;

    public DateTime TimestampUtc => _event?.Timestamp.UtcDateTime ?? DateTime.MinValue;
}
