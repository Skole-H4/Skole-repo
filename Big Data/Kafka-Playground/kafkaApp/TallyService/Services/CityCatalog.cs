namespace TallyService.Services;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using TallyService.Abstractions;
using TallyService.Models;

public sealed class CityCatalog : ICityCatalog
{
    private static readonly string[] ZipcodesPathSegments = ["..", "..", "appData", "zipcodes.json"];

    private readonly IReadOnlyList<CityTopic> _cities;
    private readonly IReadOnlyDictionary<string, CityTopic> _topicLookup;
    private readonly Dictionary<string, CityTopic> _lookup;

    public CityCatalog(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        var path = ResolvePath(environment);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Expected zip code catalog at '{path}'");
        }

        using var stream = File.OpenRead(path);
        var payload = JsonSerializer.Deserialize<List<CityRecord>>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Zip code catalog is empty");

        var ordered = payload
            .OrderBy(entry => entry.Zipcode)
            .ThenBy(entry => entry.RealCityName, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new CityTopic(entry.RealCityName, entry.ASCIICityName, entry.Zipcode))
            .ToArray();

        _cities = ordered;
        _topicLookup = ordered.ToDictionary(c => c.TopicName, StringComparer.OrdinalIgnoreCase);
        _lookup = new Dictionary<string, CityTopic>(StringComparer.OrdinalIgnoreCase);

        foreach (var city in ordered)
        {
            _lookup[city.TopicName] = city;
            _lookup[city.DisplayName] = city;
            _lookup[city.City] = city;
            _lookup[city.AsciiCityName] = city;
            _lookup[city.ZipCode.ToString(CultureInfo.InvariantCulture)] = city;
        }
    }

    public IReadOnlyList<CityTopic> Cities => _cities;

    public bool TryGetByTopic(string topicName, out CityTopic? city)
    {
        if (string.IsNullOrWhiteSpace(topicName))
        {
            city = null;
            return false;
        }

        if (_topicLookup.TryGetValue(topicName, out var value))
        {
            city = value;
            return true;
        }

        city = null;
        return false;
    }

    public bool TryResolve(string value, out CityTopic? city)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            city = null;
            return false;
        }

        if (_lookup.TryGetValue(value, out var match))
        {
            city = match;
            return true;
        }

        city = null;
        return false;
    }

    private static string ResolvePath(IHostEnvironment environment)
    {
        var root = environment.ContentRootPath;
        var candidate = Path.Combine(new[] { root }.Concat(ZipcodesPathSegments).ToArray());
        return Path.GetFullPath(candidate);
    }

    private sealed record CityRecord(string RealCityName, string ASCIICityName, int Zipcode);
}
