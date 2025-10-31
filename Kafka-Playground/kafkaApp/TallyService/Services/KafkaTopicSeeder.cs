namespace TallyService.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TallyService.Abstractions;
using TallyService.Configuration;

public sealed class KafkaTopicSeeder : IHostedService
{
    private readonly KafkaOptions _options;
    private readonly ICityCatalog _cityCatalog;
    private readonly ILogger<KafkaTopicSeeder> _logger;

    public KafkaTopicSeeder(KafkaOptions options, ICityCatalog cityCatalog, ILogger<KafkaTopicSeeder> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cityCatalog = cityCatalog ?? throw new ArgumentNullException(nameof(cityCatalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureTopicsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // graceful cancellation
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "Failed to seed Kafka topics for tally service");
            }
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureTopicsAsync(CancellationToken cancellationToken)
    {
        var expectedTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _options.VotesTopic,
            _options.TotalsTopic,
            _options.VotesByCityTopic
        };

        foreach (var city in _cityCatalog.Cities)
        {
            expectedTopics.Add(city.TopicName);
        }

        if (expectedTopics.Count == 0)
        {
            return;
        }

        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = _options.BootstrapServers
        }).Build();

        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));
        var existingTopics = new HashSet<string>(metadata.Topics.Select(t => t.Topic), StringComparer.OrdinalIgnoreCase);

        var missingSpecs = expectedTopics
            .Where(topic => !existingTopics.Contains(topic))
            .Select(topic => new TopicSpecification
            {
                Name = topic,
                NumPartitions = _options.DefaultPartitions,
                ReplicationFactor = _options.DefaultReplicationFactor,
                Configs = BuildTopicConfigs(topic)
            })
            .ToList();

        if (missingSpecs.Count == 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("All expected Kafka topics exist.");
            }
            return;
        }

        try
        {
            await admin.CreateTopicsAsync(missingSpecs, new CreateTopicsOptions
            {
                OperationTimeout = TimeSpan.FromSeconds(10)
            }).ConfigureAwait(false);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Created {Count} Kafka topic(s): {Topics}",
                    missingSpecs.Count,
                    string.Join(", ", missingSpecs.Select(s => s.Name)));
            }
        }
        catch (CreateTopicsException ex)
        {
            var unexpectedErrors = ex.Results
                .Where(result => result.Error.Code != ErrorCode.TopicAlreadyExists)
                .ToList();

            if (unexpectedErrors.Count == 0)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation("Kafka topics already existed when tally seeding ran.");
                }
                return;
            }

            foreach (var error in unexpectedErrors)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError("Failed to create topic {Topic}: {Error}", error.Topic, error.Error.Reason);
                }
            }

            throw;
        }
    }

    private Dictionary<string, string>? BuildTopicConfigs(string topic)
    {
        if (string.Equals(topic, _options.TotalsTopic, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(topic, _options.VotesByCityTopic, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string>
            {
                ["cleanup.policy"] = "compact",
                ["segment.ms"] = "600000",
                ["min.cleanable.dirty.ratio"] = "0.01"
            };
        }

        return null;
    }
}
