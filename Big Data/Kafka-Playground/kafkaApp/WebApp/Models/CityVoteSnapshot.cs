using System;
using System.Collections.Generic;
using System.Linq;

namespace WebApp.Models;

public sealed class CityVoteSnapshot
{
    public CityVoteSnapshot(string city, int zipCode, IDictionary<string, int> partyTotals, DateTimeOffset updatedAt)
    {
        City = city;
        ZipCode = zipCode;
        PartyTotals = new Dictionary<string, int>(partyTotals, StringComparer.OrdinalIgnoreCase);
        TotalVotes = PartyTotals.Values.Sum();
        UpdatedAt = updatedAt;
    }

    public string City { get; }
    public int ZipCode { get; }
    public IReadOnlyDictionary<string, int> PartyTotals { get; }
    public int TotalVotes { get; }
    public DateTimeOffset UpdatedAt { get; }

    public static CityVoteSnapshot Create(string city, int zipCode, IDictionary<string, int> partyTotals, DateTimeOffset updatedAt)
        => new(city, zipCode, partyTotals, updatedAt);
}
