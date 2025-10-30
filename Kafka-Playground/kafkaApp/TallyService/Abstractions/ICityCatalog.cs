namespace TallyService.Abstractions;

using System.Collections.Generic;
using TallyService.Models;

public interface ICityCatalog
{
    IReadOnlyList<CityTopic> Cities { get; }

    bool TryGetByTopic(string topicName, out CityTopic? city);

    bool TryResolve(string value, out CityTopic? city);
}
