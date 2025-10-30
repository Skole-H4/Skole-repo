namespace TallyService.Models;

using System;
using System.Text;

public sealed class CityTopic
{
    public CityTopic(string realCityName, string asciiCityName, int zipCode)
    {
        if (string.IsNullOrWhiteSpace(realCityName))
        {
            throw new ArgumentException("City name is required", nameof(realCityName));
        }

        if (string.IsNullOrWhiteSpace(asciiCityName))
        {
            throw new ArgumentException("ASCII city name is required", nameof(asciiCityName));
        }

        RealCityName = realCityName;
        AsciiCityName = asciiCityName;
        ZipCode = zipCode;
        DisplayName = $"{zipCode} - {realCityName}";
        TopicName = BuildTopicName(zipCode, asciiCityName);
    }

    public string RealCityName { get; }
    public string AsciiCityName { get; }
    public int ZipCode { get; }
    public string DisplayName { get; }
    public string TopicName { get; }

    public string City => RealCityName;

    private static string BuildTopicName(int zipCode, string asciiCityName)
    {
        var safeName = asciiCityName ?? string.Empty;
        var builder = new StringBuilder(safeName.Length);

        foreach (var ch in safeName)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
            else if (ch is '-' or '_' or ' ')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (string.IsNullOrEmpty(slug))
        {
            slug = "city";
        }

        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return $"{zipCode}-{slug}";
    }
}
