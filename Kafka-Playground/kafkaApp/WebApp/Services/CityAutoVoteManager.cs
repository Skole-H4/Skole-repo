using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Confluent.Kafka;
using WebApp.Configuration;
using WebApp.Models;

namespace WebApp.Services;

public sealed class CityAutoVoteManager : IAsyncDisposable
{
    private readonly CityCatalog _catalog;
    private readonly string _votesTopic;
    private readonly List<CityAutoVoteController> _controllers;
    private readonly Dictionary<string, CityAutoVoteController> _byTopic;

    public CityAutoVoteManager(IProducer<string, VoteEnvelope> producer, CityCatalog catalog, PartyCatalog partyCatalog, KafkaOptions kafkaOptions)
    {
        _catalog = catalog;
        var partyOptions = partyCatalog.PartyLetters;
        if (partyOptions.Count == 0)
        {
            throw new InvalidOperationException("No party options available for auto vote manager");
        }
        if (string.IsNullOrWhiteSpace(kafkaOptions?.VotesTopic))
        {
            throw new InvalidOperationException("Votes topic configuration is required for auto vote manager");
        }
        _votesTopic = kafkaOptions.VotesTopic;
        _controllers = catalog.Cities
            .OrderBy(city => city.ZipCode)
            .Select(city => new CityAutoVoteController(city, producer, partyOptions, _votesTopic))
            .ToList();
        _byTopic = _controllers.ToDictionary(c => c.Topic.TopicName, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CityAutoVoteController> Controllers => _controllers;

    public CityAutoVoteController? Find(string topicName) => _byTopic.TryGetValue(topicName, out var controller) ? controller : null;

    public IReadOnlyList<CityAutoVoteController> ResolveControllers(IEnumerable<string>? identifiers)
    {
        if (identifiers is null)
        {
            return _controllers;
        }

        var resolvedTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in identifiers)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (_catalog.TryResolve(token, out var city) && city is not null)
            {
                resolvedTopics.Add(city.TopicName);
            }
            else if (_byTopic.ContainsKey(token))
            {
                resolvedTopics.Add(token);
            }
        }

        if (resolvedTopics.Count == 0)
        {
            return Array.Empty<CityAutoVoteController>();
        }

        return _controllers.Where(c => resolvedTopics.Contains(c.Topic.TopicName)).ToArray();
    }

    public void Start(IEnumerable<CityAutoVoteController> controllers, int? targetPerMinute = null)
    {
        foreach (var controller in controllers)
        {
            if (targetPerMinute is int target)
            {
                controller.SetTargetRate(target);
            }

            controller.Start();
        }
    }

    public void StartAll(int? targetPerMinute = null)
    {
        Start(_controllers, targetPerMinute);
    }

    public async Task StopAsync(IEnumerable<CityAutoVoteController> controllers)
    {
        foreach (var controller in controllers)
        {
            await controller.StopAsync().ConfigureAwait(false);
        }
    }

    public Task StopAllAsync() => StopAsync(_controllers);

    public void SetRate(IEnumerable<CityAutoVoteController> controllers, int perMinute)
    {
        foreach (var controller in controllers)
        {
            controller.SetTargetRate(perMinute);
        }
    }

    public void SetRateAll(int perMinute) => SetRate(_controllers, perMinute);

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        foreach (var controller in _controllers)
        {
            await controller.DisposeAsync().ConfigureAwait(false);
        }
    }
}
