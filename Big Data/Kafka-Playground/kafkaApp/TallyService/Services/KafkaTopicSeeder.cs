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
using TallyService.Configuration;

public sealed class KafkaTopicSeeder : IHostedService
{
    private readonly KafkaOptions _options;
    private readonly ILogger<KafkaTopicSeeder> _logger;

    public KafkaTopicSeeder(KafkaOptions options, ILogger<KafkaTopicSeeder> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
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

        // Detect topics that exist with fewer partitions than expected and prepare resize specification.
        var resizeSpecs = _options.EnableTopicResize
            ? metadata.Topics
                .Where(t => expectedTopics.Contains(t.Topic) && t.Partitions.Count < _options.DefaultPartitions)
                .Select(t => new PartitionsSpecification
                {
                    Topic = t.Topic,
                    IncreaseTo = _options.DefaultPartitions
                })
                .ToList()
            : new List<PartitionsSpecification>();

        if (missingSpecs.Count == 0 && resizeSpecs.Count == 0)
        {
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation("All expected Kafka topics exist with required partition counts.");
            }
            return;
        }

        if (missingSpecs.Count > 0)
        {
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
                }
                else
                {
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
        }

        if (resizeSpecs.Count > 0)
        {
            try
            {
                await admin.CreatePartitionsAsync(resizeSpecs, new CreatePartitionsOptions
                {
                    OperationTimeout = TimeSpan.FromSeconds(30)
                }).ConfigureAwait(false);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Increased partitions for {Count} topic(s): {Topics} -> {TargetPartitions}",
                        resizeSpecs.Count,
                        string.Join(", ", resizeSpecs.Select(s => s.Topic)),
                        _options.DefaultPartitions);
                }
            }
            catch (CreatePartitionsException ex)
            {
                foreach (var result in ex.Results)
                {
                    if (_logger.IsEnabled(LogLevel.Error))
                    {
                        _logger.LogError("Failed to resize topic {Topic}: {Error}", result.Topic, result.Error.Reason);
                    }
                }
                // Non-fatal: surface but don't throw to allow startup to proceed (stream may still start if partition mismatch irrelevant).
            }
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
