using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using WebApp.Models;

namespace WebApp.Services;

// -------------------------------------------------------------------------------------------------
// PartyCatalog
//
// Responsibilities:
// 1. Load and validate the party metadata (full name, ASCII name, letter code) from a JSON file.
// 2. Provide quick lookup by party letter for validation in the vote submission endpoint.
// 3. Expose an ordered list of party letters used by the auto vote simulation.
//
// Notes:
// - Catalog is immutable after construction for thread safety.
// - All party letters are normalized as stored; lookups are case-insensitive.
// -------------------------------------------------------------------------------------------------
public sealed class PartyCatalog
{
    private static readonly string[] PartiesPathSegments = ["..", "..", "appData", "parties.json"];

    private readonly IReadOnlyList<PartyInfo> _allParties;
    private readonly IReadOnlyDictionary<string, PartyInfo> _partiesByLetter;
    private readonly string[] _allPartyLetters;

    /// <summary>
    /// Loads party definitions and builds lookup indexes.
    /// </summary>
    public PartyCatalog(IHostEnvironment environment)
    {
        var catalogFilePath = ResolvePath(environment);
        if (!File.Exists(catalogFilePath))
        {
            throw new FileNotFoundException($"Expected party catalog at '{catalogFilePath}'");
        }

        using var fileStream = File.OpenRead(catalogFilePath);
        var rawPartyRecords = JsonSerializer.Deserialize<List<PartyRecord>>(fileStream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Party catalog is empty");

        var orderedPartyInfos = rawPartyRecords
            .Where(record => !string.IsNullOrWhiteSpace(record.PartyLetter))
            .OrderBy(record => record.PartyLetter, StringComparer.OrdinalIgnoreCase)
            .Select(record => new PartyInfo(
                record.RealPartyName,
                record.ASCIIPartyName,
                record.PartyLetter,
                record.ASCIIFriendlyPartyLetter))
            .ToArray();

        if (orderedPartyInfos.Length == 0)
        {
            throw new InvalidOperationException("Party catalog must contain at least one party");
        }

        _allParties = orderedPartyInfos;
        _partiesByLetter = orderedPartyInfos.ToDictionary(party => party.PartyLetter, StringComparer.OrdinalIgnoreCase);
        _allPartyLetters = orderedPartyInfos.Select(party => party.PartyLetter).ToArray();
    }

    /// <summary>
    /// Ordered party metadata.
    /// </summary>
    public IReadOnlyList<PartyInfo> Parties => _allParties;

    /// <summary>
    /// Ordered party letter codes used by auto vote simulation.
    /// </summary>
    public IReadOnlyList<string> PartyLetters => _allPartyLetters;

    /// <summary>
    /// Attempts to find party metadata by its letter code.
    /// </summary>
    public bool TryGetByLetter(string? letter, out PartyInfo? party)
    {
        if (string.IsNullOrWhiteSpace(letter))
        {
            party = null;
            return false;
        }

        return _partiesByLetter.TryGetValue(letter, out party);
    }

    /// <summary>
    /// Resolves the absolute file path of the party catalog.
    /// </summary>
    private static string ResolvePath(IHostEnvironment environment)
    {
        var rootDirectory = environment.ContentRootPath;
        var candidatePath = Path.Combine(new[] { rootDirectory }.Concat(PartiesPathSegments).ToArray());
        return Path.GetFullPath(candidatePath);
    }

    private sealed record PartyRecord(
        string RealPartyName,
        string ASCIIPartyName,
        string PartyLetter,
        string ASCIIFriendlyPartyLetter);
}
