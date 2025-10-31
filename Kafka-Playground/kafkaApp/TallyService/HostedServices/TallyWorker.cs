namespace TallyService.HostedServices;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
    private long _processedEvents;

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

    private async Task RunTallyLoopAsync(CancellationToken stoppingToken)
    {
        using var consumer = _clientFactory.CreateVoteConsumer();
        using var producer = _clientFactory.CreateTallyProducer();

        producer.InitTransactions(TransactionTimeout);

        await RestoreStateAsync(stoppingToken).ConfigureAwait(false);

        var topics = BuildSubscriptions();
        consumer.Subscribe(topics);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Tally worker subscribed to topics: {Topics}", string.Join(", ", topics));
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            ConsumeResult<string, VoteEnvelope>? record;

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
    }

    private Task RestoreStateAsync(CancellationToken stoppingToken)
    {
        _globalCounts.Clear();
        _cityCounts.Clear();
        Interlocked.Exchange(ref _processedEvents, 0);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("Restoring tally state from compacted output topics...");
        }

        using var consumer = _clientFactory.CreateTotalsConsumer($"{_options.TallyGroupId}-rehydrate-{Guid.NewGuid():N}");
        consumer.Subscribe(new[] { _options.TotalsTopic, _options.VotesByCityTopic });

        var remaining = new HashSet<TopicPartition>();
        var stopwatch = Stopwatch.StartNew();

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var partition in consumer.Assignment)
            {
                remaining.Add(partition);
            }

            var result = consumer.Consume(TimeSpan.FromMilliseconds(200));
            if (result is null)
            {
                if (remaining.Count == 0)
                {
                    if (stopwatch.Elapsed > TimeSpan.FromSeconds(5))
                    {
                        break;
                    }

                    break;
                }

                continue;
            }

            if (result.IsPartitionEOF)
            {
                remaining.Remove(result.TopicPartition);
                if (remaining.Count == 0)
                {
                    break;
                }

                continue;
            }

            ApplyRestoredRecord(result);
        }

        consumer.Close();
        stopwatch.Stop();

        if (_logger.IsEnabled(LogLevel.Information))
        {
            var cityTotals = _cityCounts.Sum(bucket => bucket.Value.Count);
            _logger.LogInformation(
                "State restored: {GlobalOptions} global options and {CityOptions} city-option pairs in {Elapsed}.",
                _globalCounts.Count,
                cityTotals,
                stopwatch.Elapsed);
        }

        return Task.CompletedTask;
    }

    private void ProcessRecord(
        ConsumeResult<string, VoteEnvelope> record,
        IProducer<string, VoteTotal> producer,
        IConsumer<string, VoteEnvelope> consumer)
    {
        var offsets = new[]
        {
            new TopicPartitionOffset(record.TopicPartition, record.Offset + 1)
        };

        var envelope = record.Message?.Value;
        if (!TryNormalizeOption(envelope, out var option))
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

        if (envelope is not null && TryResolveCity(record, envelope, out var city) && city is not null)
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

        var processed = Interlocked.Increment(ref _processedEvents);
        if (_logger.IsEnabled(LogLevel.Debug) && processed % 500 == 0)
        {
            _logger.LogDebug(
                "Processed {Processed} votes; last option={Option}, partition={TopicPartitionOffset}, global count={GlobalCount}",
                processed,
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
        IConsumer<string, VoteEnvelope> consumer,
        TopicPartitionOffset[] offsets)
    {
        producer.SendOffsetsToTransaction(offsets, consumer.ConsumerGroupMetadata, TransactionTimeout);
    }

    private bool TryNormalizeOption(VoteEnvelope? envelope, out string option)
    {
        var vote = envelope?.Event;
        if (vote is null || string.IsNullOrWhiteSpace(vote.Option))
        {
            option = string.Empty;
            return false;
        }

        option = NormalizeOption(vote.Option);
        return option.Length > 0;
    }

    private bool TryResolveCity(
        ConsumeResult<string, VoteEnvelope> record,
        VoteEnvelope envelope,
        out CityTopic? city)
    {
        if (!string.IsNullOrWhiteSpace(envelope.CityTopic) &&
            _cityCatalog.TryGetByTopic(envelope.CityTopic, out city) &&
            city is not null)
        {
            return true;
        }

        if (envelope.ZipCode.HasValue)
        {
            var zipValue = envelope.ZipCode.Value.ToString(CultureInfo.InvariantCulture);
            if (_cityCatalog.TryResolve(zipValue, out city) && city is not null)
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(envelope.City) &&
            _cityCatalog.TryResolve(envelope.City, out city) &&
            city is not null)
        {
            return true;
        }

        var key = record.Message?.Key;
        if (!string.IsNullOrWhiteSpace(key))
        {
            var separator = key.IndexOf('|');
            if (separator > 0)
            {
                var candidate = key[..separator];
                if (!string.Equals(candidate, "GLOBAL", StringComparison.OrdinalIgnoreCase) &&
                    _cityCatalog.TryResolve(candidate, out city) &&
                    city is not null)
                {
                    return true;
                }
            }
        }

        city = null;
        return false;
    }

    private void ApplyRestoredRecord(ConsumeResult<string, VoteTotal> record)
    {
        var message = record.Message;
        if (message?.Value is not { } total || string.IsNullOrWhiteSpace(total.Option))
        {
            return;
        }

        var option = NormalizeOption(total.Option);
        if (option.Length == 0)
        {
            return;
        }

        if (string.Equals(record.Topic, _options.TotalsTopic, StringComparison.OrdinalIgnoreCase))
        {
            _globalCounts[option] = total.Count;
            return;
        }

        if (!string.Equals(record.Topic, _options.VotesByCityTopic, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var cityKey = ExtractCityBucketKey(record, total);
        if (string.IsNullOrEmpty(cityKey))
        {
            return;
        }

        var bucket = GetCityBucket(cityKey);
        bucket[option] = total.Count;
    }

    private string? ExtractCityBucketKey(ConsumeResult<string, VoteTotal> record, VoteTotal total)
    {
        var messageKey = record.Message?.Key;
        if (!string.IsNullOrWhiteSpace(messageKey))
        {
            var separator = messageKey.IndexOf(':');
            return separator > 0 ? messageKey[..separator] : messageKey;
        }

        if (!string.IsNullOrWhiteSpace(total.City) && _cityCatalog.TryResolve(total.City, out var resolvedCity) && resolvedCity is not null)
        {
            return resolvedCity.TopicName;
        }

        if (_cityCatalog.TryGetByTopic(record.Topic, out var topicCity) && topicCity is not null)
        {
            return topicCity.TopicName;
        }

        return null;
    }

    private static string NormalizeOption(string option) => option.Trim().ToUpperInvariant();

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
        return new[] { _options.VotesTopic };
    }
}
