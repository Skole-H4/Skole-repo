using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using WebApp.Models;

namespace WebApp.Services;

// -------------------------------------------------------------------------------------------------
// CityCatalog
//
// Responsibilities:
// 1. Load, validate and materialize city metadata (zip code, display name, topic name) from a JSON file.
// 2. Provide fast resolution of multiple identifier forms (topic name, display name, real name, ASCII name, zip code).
// 3. Offer lookup helpers used by vote endpoints and auto vote controllers.
//
// Notes:
// - The JSON is loaded once on startup and stored in read-only collections for thread safety.
// - Multiple identifier keys are pre-indexed into a dictionary to avoid repeated linear searches.
// - No abbreviations are used for clarity; internal fields are intentionally descriptive.
// -------------------------------------------------------------------------------------------------
public sealed class CityCatalog
{
    private static readonly string[] ZipcodesPathSegments = ["..", "..", "appData", "zipcodes.json"];

    private readonly IReadOnlyList<CityTopic> _allCities;
    private readonly IReadOnlyDictionary<string, CityTopic> _citiesByTopic;
    private readonly Dictionary<string, CityTopic> _lookupByIdentifier;

    /// <summary>
    /// Loads the city catalog from disk and builds lookup indexes.
    /// </summary>
    /// <param name="environment">Host environment used to resolve content root path.</param>
    /// <exception cref="FileNotFoundException">Thrown when the expected JSON catalog file is missing.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the catalog payload is empty.</exception>
    public CityCatalog(IHostEnvironment environment)
    {
        var catalogFilePath = ResolvePath(environment);
        if (!File.Exists(catalogFilePath))
        {
            throw new FileNotFoundException($"Expected zip code catalog at '{catalogFilePath}'");
        }

        using var fileStream = File.OpenRead(catalogFilePath);
        var rawRecords = JsonSerializer.Deserialize<List<CityRecord>>(fileStream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Zip code catalog is empty");

        var orderedCities = rawRecords
            .OrderBy(record => record.Zipcode)
            .ThenBy(record => record.RealCityName, StringComparer.OrdinalIgnoreCase)
            .Select(record => new CityTopic(record.RealCityName, record.ASCIICityName, record.Zipcode))
            .ToArray();

        _allCities = orderedCities;
        _citiesByTopic = orderedCities.ToDictionary(city => city.TopicName, StringComparer.OrdinalIgnoreCase);
        _lookupByIdentifier = new Dictionary<string, CityTopic>(StringComparer.OrdinalIgnoreCase);

        // Pre-index multiple identifiers for each city.
        foreach (var city in orderedCities)
        {
            _lookupByIdentifier[city.TopicName] = city;
            _lookupByIdentifier[city.DisplayName] = city;
            _lookupByIdentifier[city.City] = city;
            _lookupByIdentifier[city.AsciiCityName] = city;
            _lookupByIdentifier[city.ZipCode.ToString(CultureInfo.InvariantCulture)] = city;
        }
    }

    /// <summary>
    /// All city topic metadata ordered by zip code then name.
    /// </summary>
    public IReadOnlyList<CityTopic> Cities => _allCities;

    /// <summary>
    /// Attempts to resolve a city by its topic name.
    /// </summary>
    /// <param name="topicName">The Kafka topic name segment.</param>
    /// <param name="city">Resolved city or null.</param>
    /// <returns>True if found.</returns>
    public bool TryGetByTopic(string topicName, out CityTopic? city)
    {
        if (_citiesByTopic.TryGetValue(topicName, out var matchedCity))
        {
            city = matchedCity;
            return true;
        }

        city = null;
        return false;
    }

    /// <summary>
    /// Attempts to resolve a city using any supported identifier (topic, display name, real name, ASCII name, zip code).
    /// </summary>
    /// <param name="identifier">Raw identifier text.</param>
    /// <param name="city">Resolved city or null.</param>
    /// <returns>True if found.</returns>
    public bool TryResolve(string identifier, out CityTopic? city)
    {
        if (_lookupByIdentifier.TryGetValue(identifier, out var matchedCity))
        {
            city = matchedCity;
            return true;
        }

        city = null;
        return false;
    }

    /// <summary>
    /// Builds the absolute path to the zip codes JSON file.
    /// </summary>
    private static string ResolvePath(IHostEnvironment environment)
    {
        var rootDirectory = environment.ContentRootPath;
        var candidatePath = Path.Combine(new[] { rootDirectory }.Concat(ZipcodesPathSegments).ToArray());
        return Path.GetFullPath(candidatePath);
    }

    private sealed record CityRecord(string RealCityName, string ASCIICityName, int Zipcode);
}
