using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using WebApp.Models;

namespace WebApp.Services;

public sealed class PartyCatalog
{
    private static readonly string[] PartiesPathSegments = ["..", "..", "appData", "parties.json"];

    private readonly IReadOnlyList<PartyInfo> _parties;
    private readonly IReadOnlyDictionary<string, PartyInfo> _byLetter;
    private readonly string[] _partyLetters;

    public PartyCatalog(IHostEnvironment environment)
    {
        var path = ResolvePath(environment);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Expected party catalog at '{path}'");
        }

        using var stream = File.OpenRead(path);
        var payload = JsonSerializer.Deserialize<List<PartyRecord>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Party catalog is empty");

        var ordered = payload
            .Where(entry => !string.IsNullOrWhiteSpace(entry.PartyLetter))
            .OrderBy(entry => entry.PartyLetter, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new PartyInfo(
                entry.RealPartyName,
                entry.ASCIIPartyName,
                entry.PartyLetter,
                entry.ASCIIFriendlyPartyLetter))
            .ToArray();

        if (ordered.Length == 0)
        {
            throw new InvalidOperationException("Party catalog must contain at least one party");
        }

        _parties = ordered;
        _byLetter = ordered.ToDictionary(p => p.PartyLetter, StringComparer.OrdinalIgnoreCase);
        _partyLetters = ordered.Select(p => p.PartyLetter).ToArray();
    }

    public IReadOnlyList<PartyInfo> Parties => _parties;

    public IReadOnlyList<string> PartyLetters => _partyLetters;

    public bool TryGetByLetter(string? letter, out PartyInfo? party)
    {
        if (string.IsNullOrWhiteSpace(letter))
        {
            party = null;
            return false;
        }

        return _byLetter.TryGetValue(letter, out party);
    }

    private static string ResolvePath(IHostEnvironment environment)
    {
        var root = environment.ContentRootPath;
        var candidate = Path.Combine(new[] { root }.Concat(PartiesPathSegments).ToArray());
        return Path.GetFullPath(candidate);
    }

    private sealed record PartyRecord(
        string RealPartyName,
        string ASCIIPartyName,
        string PartyLetter,
        string ASCIIFriendlyPartyLetter);
}
