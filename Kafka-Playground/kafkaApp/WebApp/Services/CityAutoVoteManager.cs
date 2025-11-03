using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Confluent.Kafka;
using WebApp.Configuration;
using WebApp.Models;

namespace WebApp.Services;

// -------------------------------------------------------------------------------------------------
// CityAutoVoteManager
//
// Responsibilities:
// 1. Construct and manage per-city auto vote controllers for simulation/testing.
// 2. Provide resolution helpers from mixed identifiers to controller instances.
// 3. Coordinate bulk start/stop and rate adjustments across controllers.
// 4. Ensure clean disposal (stopping workers and disposing controllers) on application shutdown.
//
// Notes:
// - Controllers are created once at startup; party options are captured to avoid repeated catalog lookups.
// - All collections are maintained in memory; operations are cheap and primarily dictionary lookups.
// - No abbreviations in field names for clarity.
// -------------------------------------------------------------------------------------------------
public sealed class CityAutoVoteManager : IAsyncDisposable
{
    private readonly CityCatalog _cityCatalog;
    private readonly string _votesTopicName;
    private readonly List<CityAutoVoteController> _allControllers;
    private readonly Dictionary<string, CityAutoVoteController> _controllersByTopic;

    /// <summary>
    /// Constructs auto vote controllers for every city using provided producer and party options.
    /// </summary>
    public CityAutoVoteManager(IProducer<string, VoteEnvelope> producer, CityCatalog cityCatalog, PartyCatalog partyCatalog, KafkaOptions kafkaOptions)
    {
        _cityCatalog = cityCatalog;
        var partyLetters = partyCatalog.PartyLetters;
        if (partyLetters.Count == 0)
        {
            throw new InvalidOperationException("No party options available for auto vote manager");
        }
        if (string.IsNullOrWhiteSpace(kafkaOptions?.VotesTopic))
        {
            throw new InvalidOperationException("Votes topic configuration is required for auto vote manager");
        }
        _votesTopicName = kafkaOptions.VotesTopic;
        _allControllers = cityCatalog.Cities
            .OrderBy(city => city.ZipCode)
            .Select(city => new CityAutoVoteController(city, producer, partyLetters, _votesTopicName))
            .ToList();
        _controllersByTopic = _allControllers.ToDictionary(controller => controller.Topic.TopicName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// All instantiated city auto vote controllers.
    /// </summary>
    public IReadOnlyList<CityAutoVoteController> Controllers => _allControllers;

    /// <summary>
    /// Finds a controller by its city topic name (case-insensitive) or returns null if not found.
    /// </summary>
    public CityAutoVoteController? Find(string topicName) => _controllersByTopic.TryGetValue(topicName, out var controller) ? controller : null;

    /// <summary>
    /// Resolves controllers from a sequence of identifiers (city name, topic, display name or zip code).
    /// Returns all controllers if identifiers is null.
    /// </summary>
    public IReadOnlyList<CityAutoVoteController> ResolveControllers(IEnumerable<string>? identifiers)
    {
        if (identifiers is null)
        {
            return _allControllers;
        }

        var resolvedTopicNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var identifier in identifiers)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            if (_cityCatalog.TryResolve(identifier, out var matchedCity) && matchedCity is not null)
            {
                resolvedTopicNames.Add(matchedCity.TopicName);
            }
            else if (_controllersByTopic.ContainsKey(identifier))
            {
                resolvedTopicNames.Add(identifier);
            }
        }

        if (resolvedTopicNames.Count == 0)
        {
            return Array.Empty<CityAutoVoteController>();
        }

        return _allControllers.Where(controller => resolvedTopicNames.Contains(controller.Topic.TopicName)).ToArray();
    }

    /// <summary>
    /// Starts the specified controllers optionally setting a target rate first.
    /// </summary>
    public void Start(IEnumerable<CityAutoVoteController> controllers, int? targetPerMinute = null)
    {
        foreach (var controller in controllers)
        {
            if (targetPerMinute is int targetRate)
            {
                controller.SetTargetRate(targetRate);
            }

            controller.Start();
        }
    }

    /// <summary>
    /// Starts all controllers.
    /// </summary>
    public void StartAll(int? targetPerMinute = null) => Start(_allControllers, targetPerMinute);

    /// <summary>
    /// Stops the specified controllers asynchronously.
    /// </summary>
    public async Task StopAsync(IEnumerable<CityAutoVoteController> controllers)
    {
        foreach (var controller in controllers)
        {
            await controller.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops all controllers.
    /// </summary>
    public Task StopAllAsync() => StopAsync(_allControllers);

    /// <summary>
    /// Sets the target rate for the specified controllers.
    /// </summary>
    public void SetRate(IEnumerable<CityAutoVoteController> controllers, int perMinute)
    {
        foreach (var controller in controllers)
        {
            controller.SetTargetRate(perMinute);
        }
    }

    /// <summary>
    /// Sets the target rate for all controllers.
    /// </summary>
    public void SetRateAll(int perMinute) => SetRate(_allControllers, perMinute);

    /// <summary>
    /// Gracefully stops and disposes all controllers.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        foreach (var controller in _allControllers)
        {
            await controller.DisposeAsync().ConfigureAwait(false);
        }
    }
}
