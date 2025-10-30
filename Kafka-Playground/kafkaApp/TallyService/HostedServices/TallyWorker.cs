namespace TallyService.HostedServices;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TallyService.Abstractions;
using TallyService.Configuration;
using TallyService.Messaging;
using TallyService.Models;

public sealed class TallyWorker : BackgroundService
{
    private static readonly TimeSpan TransactionTimeout = TimeSpan.FromSeconds(10);

    private readonly KafkaOptions _options;
    private readonly ICityCatalog _cityCatalog;
    private readonly IKafkaClientFactory _clientFactory;
    private readonly ILogger<TallyWorker> _logger;
    private readonly Dictionary<string, int> _globalCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, int>> _cityCounts = new(StringComparer.OrdinalIgnoreCase);

    public TallyWorker(
        KafkaOptions options,
        ICityCatalog cityCatalog,
        IKafkaClientFactory clientFactory,
        ILogger<TallyWorker> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cityCatalog = cityCatalog ?? throw new ArgumentNullException(nameof(cityCatalog));
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTallyLoopAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ConsumeException ex)
            {
                _logger.LogError(ex, "Kafka consume failure in tally worker");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
            catch (KafkaException ex)
            {
                _logger.LogError(ex, "Kafka processing failure in tally worker");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error in tally worker");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private Task RunTallyLoopAsync(CancellationToken stoppingToken)
    {
        using var consumer = _clientFactory.CreateVoteConsumer();
        using var producer = _clientFactory.CreateTallyProducer();

        producer.InitTransactions(TransactionTimeout);

        var topics = BuildSubscriptions();
        consumer.Subscribe(topics);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Tally worker subscribed to topics: {Topics}", string.Join(", ", topics));
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, VoteEvent>? record;

            try
            {
                record = consumer.Consume(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (record is null)
            {
                continue;
            }

            try
            {
                ProcessRecord(record, producer, consumer);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (KafkaException)
            {
                AbortTransactionQuietly(producer);
                throw;
            }
            catch (Exception ex)
            {
                AbortTransactionQuietly(producer);

                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(ex, "Failed to process vote record at {TopicPartitionOffset}", record.TopicPartitionOffset);
                }

                throw;
            }
        }

        try
        {
            consumer.Close();
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Error closing tally consumer");
            }
        }

        return Task.CompletedTask;
    }

    private void ProcessRecord(
        ConsumeResult<string, VoteEvent> record,
        IProducer<string, VoteTotal> producer,
        IConsumer<string, VoteEvent> consumer)
    {
        var offsets = new[]
        {
            new TopicPartitionOffset(record.TopicPartition, record.Offset + 1)
        };

        var vote = record.Message?.Value;
        if (!TryNormalizeOption(vote, out var option))
        {
            BeginTransaction(producer);
            SendOffsets(producer, consumer, offsets);
            CommitTransaction(producer);
            return;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var globalTotal = CreateGlobalTotal(option, timestamp);

        VoteTotal? cityTotal = null;
        string? cityKey = null;

        if (_cityCatalog.TryGetByTopic(record.Topic, out var city) && city is not null)
        {
            cityTotal = CreateCityTotal(city, option, timestamp);
            cityKey = string.Concat(city.TopicName, ':', option);
        }

        BeginTransaction(producer);
        producer.Produce(_options.TotalsTopic, new Message<string, VoteTotal>
        {
            Key = option,
            Value = globalTotal
        });

        if (cityTotal is not null && cityKey is not null)
        {
            producer.Produce(_options.VotesByCityTopic, new Message<string, VoteTotal>
            {
                Key = cityKey,
                Value = cityTotal
            });
        }

        SendOffsets(producer, consumer, offsets);
        CommitTransaction(producer);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Processed vote for {Option} at {TopicPartitionOffset}; global count={GlobalCount}",
                option,
                record.TopicPartitionOffset,
                globalTotal.Count);
        }
    }

    private static void BeginTransaction(IProducer<string, VoteTotal> producer)
    {
        producer.BeginTransaction();
    }

    private static void CommitTransaction(IProducer<string, VoteTotal> producer)
    {
        producer.CommitTransaction();
    }

    private void AbortTransactionQuietly(IProducer<string, VoteTotal> producer)
    {
        try
        {
            producer.AbortTransaction();
        }
        catch (KafkaException ex)
        {
            if (_logger.IsEnabled(LogLevel.Warning))
            {
                _logger.LogWarning(ex, "Failed to abort Kafka transaction cleanly");
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(ex, "Unexpected exception aborting Kafka transaction");
            }
        }
    }

    private void SendOffsets(
        IProducer<string, VoteTotal> producer,
        IConsumer<string, VoteEvent> consumer,
        TopicPartitionOffset[] offsets)
    {
        producer.SendOffsetsToTransaction(offsets, consumer.ConsumerGroupMetadata, TransactionTimeout);
    }

    private bool TryNormalizeOption(VoteEvent? vote, out string option)
    {
        if (vote is null || string.IsNullOrWhiteSpace(vote.Option))
        {
            option = string.Empty;
            return false;
        }

        option = vote.Option.Trim().ToUpperInvariant();
        return option.Length > 0;
    }

    private VoteTotal CreateGlobalTotal(string option, DateTimeOffset timestamp)
    {
        var count = IncrementCount(_globalCounts, option);
        return new VoteTotal
        {
            Option = option,
            Count = count,
            UpdatedAt = timestamp
        };
    }

    private VoteTotal CreateCityTotal(CityTopic city, string option, DateTimeOffset timestamp)
    {
        var bucket = GetCityBucket(city.TopicName);
        var count = IncrementCount(bucket, option);

        return new VoteTotal
        {
            Option = option,
            Count = count,
            UpdatedAt = timestamp,
            City = city.City,
            ZipCode = city.ZipCode
        };
    }

    private Dictionary<string, int> GetCityBucket(string cityKey)
    {
        if (!_cityCounts.TryGetValue(cityKey, out var bucket))
        {
            bucket = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _cityCounts[cityKey] = bucket;
        }

        return bucket;
    }

    private static int IncrementCount(IDictionary<string, int> store, string option)
    {
        if (store.TryGetValue(option, out var current))
        {
            current++;
        }
        else
        {
            current = 1;
        }

        store[option] = current;
        return current;
    }

    private string[] BuildSubscriptions()
    {
        var topics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            _options.VotesTopic
        };

        foreach (var city in _cityCatalog.Cities)
        {
            topics.Add(city.TopicName);
        }

        return topics.ToArray();
    }
}
