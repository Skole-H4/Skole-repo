using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebApp.Configuration;

namespace WebApp.Services;

public sealed class KafkaTopicSeeder : IHostedService
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaTopicSeeder> _logger;

    public KafkaTopicSeeder(KafkaOptions options, ILogger<KafkaTopicSeeder> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureTopicsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to seed Kafka topics");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureTopicsAsync(CancellationToken cancellationToken)
    {
        // Only ensure the core topics still in active use. Per-city raw topics were deprecated.
        var expectedTopics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _options.VotesTopic,
            _options.TotalsTopic,
            _options.VotesByCityTopic
        };

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
                ReplicationFactor = _options.DefaultReplicationFactor
            })
            .ToList();

        if (missingSpecs.Count == 0)
        {
            _logger.LogInformation("All expected Kafka topics are present.");
            return;
        }

        try
        {
            await admin.CreateTopicsAsync(missingSpecs, new CreateTopicsOptions
            {
                OperationTimeout = TimeSpan.FromSeconds(10)
            }).ConfigureAwait(false);

            _logger.LogInformation("Created {TopicCount} Kafka topic(s): {Topics}", missingSpecs.Count, string.Join(", ", missingSpecs.Select(s => s.Name)));
        }
        catch (CreateTopicsException ex)
        {
            var unexpectedErrors = ex.Results
                .Where(result => result.Error.Code != ErrorCode.TopicAlreadyExists)
                .ToList();

            if (unexpectedErrors.Count == 0)
            {
                _logger.LogInformation("Kafka topics already existed when seeding ran.");
                return;
            }

            foreach (var error in unexpectedErrors)
            {
                _logger.LogError("Failed to create topic {Topic}: {Error}", error.Topic, error.Error.Reason);
            }

            throw;
        }
    }
}
