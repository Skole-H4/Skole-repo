namespace TallyService.HostedServices;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Streamiz.Kafka.Net;
using Streamiz.Kafka.Net.SerDes;
using Streamiz.Kafka.Net.Stream;
using Streamiz.Kafka.Net.Table;
using Streamiz.Kafka.Net.Errors;
using TallyService.Abstractions;
using TallyService.Configuration;
using TallyService.Models;
using TallyService.Streaming;

public sealed class StreamTallyHostedService : IHostedService
{
    private static readonly JsonSerializerConfig SerializerConfig = new()
    {
        AutoRegisterSchemas = true
    };

    private readonly KafkaOptions _options;
    private readonly ICityCatalog _cityCatalog;
    private readonly ISchemaRegistryClient _schemaRegistryClient;
    private readonly ILogger<StreamTallyHostedService> _logger;

    private KafkaStream? _stream;
    private Task? _streamTask;
    private Timer? _stallTimer;
    private bool _performedAutoRecovery;
    private long _processedValidVotes;
    private DateTime _startUtc;
    private readonly TimeSpan _stallCheckInterval = TimeSpan.FromSeconds(45);
    private readonly TimeSpan _initialGracePeriod = TimeSpan.FromSeconds(30);

    public StreamTallyHostedService(
        KafkaOptions options,
        ICityCatalog cityCatalog,
        ISchemaRegistryClient schemaRegistryClient,
        ILogger<StreamTallyHostedService> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _cityCatalog = cityCatalog ?? throw new ArgumentNullException(nameof(cityCatalog));
        _schemaRegistryClient = schemaRegistryClient ?? throw new ArgumentNullException(nameof(schemaRegistryClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return;
        }

        _startUtc = DateTime.UtcNow;

        // Optional local state reset (development recovery) controlled by feature flag
        TryForceLocalStateReset();

        await CleanupStaleInternalTopicsAsync(cancellationToken).ConfigureAwait(false);

        var builder = new StreamBuilder();

        var voteSerDes = new ConfluentJsonSerDes<VoteEnvelope>(_schemaRegistryClient, SerializerConfig);
        var voteTotalSerDes = new ConfluentJsonSerDes<VoteTotal>(_schemaRegistryClient, SerializerConfig);

        var streams = new List<IKStream<string, NormalizedVote>>
        {
            BuildStream(builder, _options.VotesTopic, voteSerDes)
        };

        var merged = MergeStreams(streams);
        var validVotes = merged.Filter((_, vote, _) => vote.Option.Length > 0, "filter-empty-option");

        var aggregationStream = validVotes
            .FlatMap((_, vote, _) => BuildAggregationRecords(vote), "flatmap-aggregation-records");

        var combinedCounts = aggregationStream
            .GroupByKey()
            .Count("combined-tally-counts");

        var aggregationResults = combinedCounts
            .ToStream()
            .Map((key, count, _) => KeyValuePair.Create(key, CreateAggregationResult(key, count)), "map-aggregation-result");

        var branches = aggregationResults.Branch(
            (_, result, _) => result.Type == AggregationOutputType.Global,
            (_, result, _) => result.Type == AggregationOutputType.City);

        branches[0]
            .Filter((_, result, _) => result.Total is not null, "filter-null-global-total")
            .Map((_, result, _) => KeyValuePair.Create(result.OutputKey, result.Total!), "map-global-output")
            .To(_options.TotalsTopic, new StringSerDes(), voteTotalSerDes);

        branches[1]
            .Filter((_, result, _) => result.Total is not null, "filter-null-city-total")
            .Map((_, result, _) => KeyValuePair.Create(result.OutputKey, result.Total!), "map-city-output")
            .To(_options.VotesByCityTopic, new StringSerDes(), voteTotalSerDes);

        var config = BuildStreamConfig();
        var topology = builder.Build();

        var attempt = 0;
        const int maxAttempts = 2;
    var startupGuardDelay = TimeSpan.FromSeconds(5);

        while (true)
        {
            attempt++;

            _stream = new KafkaStream(topology, config);
            _streamTask = _stream.StartAsync(cancellationToken);

            var startupGuard = Task.Delay(startupGuardDelay);
            var completed = await Task.WhenAny(_streamTask, startupGuard).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (completed == _streamTask)
            {
                try
                {
                    await _streamTask.ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (TryExtractExistingInternalTopics(ex, out var duplicateTopics) && attempt < maxAttempts)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                    {
                        _logger.LogWarning(
                            "Kafka reported existing Streamiz internal topic(s): {Topics}. Retrying startup after cleanup.",
                            string.Join(", ", duplicateTopics));
                    }

                    await CleanupInternalTopicsAsync(duplicateTopics, cancellationToken).ConfigureAwait(false);

                    _stream.Dispose();
                    _stream = null;
                    _streamTask = null;
                    continue;
                }
                catch
                {
                    throw;
                }
            }

            ObserveStreamTask();
            StartStallDetection();
            break;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stream is null)
        {
            return;
        }

        try
        {
            _stream.Dispose();
            if (_streamTask is not null)
            {
                await _streamTask.ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(ex, "Error while stopping tally stream");
            }
        }
        finally
        {
            _stream = null;
            _streamTask = null;
            StopStallDetection();
        }
    }

    private IKStream<string, NormalizedVote> BuildStream(
        StreamBuilder builder,
        string topic,
        ISerDes<VoteEnvelope> valueSerDes)
    {
        return builder
            .Stream<string, VoteEnvelope>(topic, new StringSerDes(), valueSerDes)
            .MapValues<NormalizedVote>((value, _) => NormalizeVote(value));
    }

    private static IKStream<string, NormalizedVote> MergeStreams(IReadOnlyList<IKStream<string, NormalizedVote>> streams)
    {
        if (streams.Count == 0)
        {
            throw new InvalidOperationException("At least one vote stream is required.");
        }

        var merged = streams[0];
        for (var i = 1; i < streams.Count; i++)
        {
            merged = merged.Merge(streams[i]);
        }

        return merged;
    }

    private StreamConfig BuildStreamConfig()
    {
        var stateDir = Path.Combine(AppContext.BaseDirectory, "stream-state");
        Directory.CreateDirectory(stateDir);

        var config = new StreamConfig
        {
            ApplicationId = ApplicationId,
            BootstrapServers = _options.BootstrapServers,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            StateDir = stateDir,
            Guarantee = ProcessingGuarantee.EXACTLY_ONCE,
            ReplicationFactor = _options.DefaultReplicationFactor,
            CommitIntervalMs = StreamConfig.EOS_DEFAULT_COMMIT_INTERVAL_MS,
            NumStreamThreads = 1,
            DefaultKeySerDes = new StringSerDes(),
            DefaultValueSerDes = new JsonSerDes<NormalizedVote>()
        };

        config.AllowAutoCreateTopics = false;

        return config;
    }

    private async Task CleanupStaleInternalTopicsAsync(CancellationToken cancellationToken)
    {
        var prefix = string.Concat(ApplicationId, '-');

        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = _options.BootstrapServers
        }).Build();

        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(10));
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Streamiz cleanup scanning {TopicCount} topic(s) for prefix {Prefix}",
                metadata.Topics.Count,
                prefix);
        }
        var candidates = metadata.Topics
            .Select(t => t.Topic)
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (candidates.Count == 0)
        {
            return;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Deleting {Count} stale Streamiz internal topic(s) before startup: {Topics}",
                candidates.Count,
                string.Join(", ", candidates));
        }

        var pending = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);

        try
        {
            var options = new DeleteTopicsOptions
            {
                OperationTimeout = TimeSpan.FromSeconds(60),
                RequestTimeout = TimeSpan.FromSeconds(60)
            };

            await admin.DeleteTopicsAsync(pending, options).ConfigureAwait(false);
        }
        catch (DeleteTopicsException ex)
        {
            foreach (var result in ex.Results)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "DeleteTopics result for {Topic}: {Error} ({Reason})",
                        result.Topic,
                        result.Error.Code,
                        result.Error.Reason);
                }

                if (result.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                    pending.Remove(result.Topic);
                }
            }
        }

        await WaitForTopicDeletionAsync(admin, pending, cancellationToken).ConfigureAwait(false);
    }

    private async Task CleanupInternalTopicsAsync(IEnumerable<string> topics, CancellationToken cancellationToken)
    {
        var targets = topics
            .Where(topic => topic.StartsWith(string.Concat(ApplicationId, '-'), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            return;
        }

        using var admin = new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = _options.BootstrapServers
        }).Build();

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Cleaning up {Count} conflicting Streamiz internal topic(s): {Topics}",
                targets.Count,
                string.Join(", ", targets));
        }

        var pending = new HashSet<string>(targets, StringComparer.OrdinalIgnoreCase);

        try
        {
            var options = new DeleteTopicsOptions
            {
                OperationTimeout = TimeSpan.FromSeconds(60),
                RequestTimeout = TimeSpan.FromSeconds(60)
            };

            await admin.DeleteTopicsAsync(pending, options).ConfigureAwait(false);
        }
        catch (DeleteTopicsException ex)
        {
            foreach (var result in ex.Results)
            {
                if (_logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "DeleteTopics result for {Topic}: {Error} ({Reason})",
                        result.Topic,
                        result.Error.Code,
                        result.Error.Reason);
                }

                if (result.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                    pending.Remove(result.Topic);
                }
            }
        }

        await WaitForTopicDeletionAsync(admin, pending, cancellationToken).ConfigureAwait(false);
    }

    private void ObserveStreamTask()
    {
        if (_streamTask is null)
        {
            return;
        }

        _ = _streamTask.ContinueWith(
            t =>
            {
                if (!t.IsFaulted || t.Exception is null)
                {
                    return;
                }

                var exception = t.Exception.GetBaseException();

                if (TryExtractExistingInternalTopics(exception, out var topics) && _logger.IsEnabled(LogLevel.Warning))
                {
                    _logger.LogWarning(
                        "Kafka stream stopped because internal topic(s) already existed: {Topics}",
                        string.Join(", ", topics));
                    return;
                }

                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(exception, "Kafka stream stopped unexpectedly");
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool TryExtractExistingInternalTopics(Exception exception, out IReadOnlyCollection<string> topics)
    {
        if (exception is StreamsException { InnerException: CreateTopicsException createException })
        {
            var duplicates = createException.Results
                .Where(r => r.Error.Code == ErrorCode.TopicAlreadyExists)
                .Select(r => r.Topic)
                .ToList();

            if (duplicates.Count > 0 && duplicates.Count == createException.Results.Count)
            {
                topics = duplicates;
                return true;
            }
        }

        topics = Array.Empty<string>();
        return false;
    }

    private async Task WaitForTopicDeletionAsync(IAdminClient admin, HashSet<string> pending, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMinutes(2);

        while (!cancellationToken.IsCancellationRequested && watch.Elapsed < timeout && pending.Count > 0)
        {
            foreach (var topic in pending.ToArray())
            {
                try
                {
                    var topicMetadata = admin.GetMetadata(topic, TimeSpan.FromSeconds(5)).Topics.FirstOrDefault();
                    if (topicMetadata is null || topicMetadata.Error.Code == ErrorCode.UnknownTopicOrPart)
                    {
                        pending.Remove(topic);
                        continue;
                    }

                    var partitionErrors = topicMetadata.Partitions?
                        .Select(p => p.Error.Code)
                        .ToList() ?? new List<ErrorCode>();

                    if (partitionErrors.Count > 0 && partitionErrors.All(code => code == ErrorCode.UnknownTopicOrPart))
                    {
                        pending.Remove(topic);
                    }
                }
                catch (KafkaException ex) when (ex.Error.Code == ErrorCode.UnknownTopicOrPart)
                {
                    pending.Remove(topic);
                }
            }

            if (pending.Count == 0)
            {
                break;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "Streamiz cleanup waiting for {Remaining} internal topic(s) to delete: {Topics}",
                    pending.Count,
                    string.Join(", ", pending));
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        if (pending.Count > 0 && _logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Streamiz cleanup timed out after {Elapsed} while waiting for Kafka to drop internal topics: {Topics}",
                watch.Elapsed,
                string.Join(", ", pending));
        }
    }

    private VoteTotal CreateGlobalTotal(string option, long count)
    {
        return new VoteTotal
        {
            Option = option,
            Count = ToCount(option, count),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private VoteTotal? CreateCityTotal(string key, long count)
    {
        var separator = key.IndexOf(':');
        if (separator <= 0 || separator >= key.Length - 1)
        {
            return null;
        }

        var cityTopic = key[..separator];
        var option = key[(separator + 1)..];

        _cityCatalog.TryGetByTopic(cityTopic, out var city);

        return new VoteTotal
        {
            Option = option,
            Count = ToCount(option, count),
            City = city?.City ?? cityTopic,
            ZipCode = city?.ZipCode,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private int ToCount(string option, long count)
    {
        if (count <= int.MaxValue)
        {
            return (int)count;
        }

        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(
                "Vote count for option {Option} capped at {Max}",
                option,
                int.MaxValue);
        }

        return int.MaxValue;
    }

    private NormalizedVote NormalizeVote(VoteEnvelope? envelope)
    {
        var vote = envelope?.Event;
        if (vote is null)
        {
            return NormalizedVote.Empty;
        }

        var option = NormalizeOption(vote.Option);
        if (option.Length > 0)
        {
            System.Threading.Interlocked.Increment(ref _processedValidVotes);
        }
        return new NormalizedVote(
            option,
            envelope?.CityTopic,
            envelope?.City,
            envelope?.ZipCode);
    }

    private static string NormalizeOption(string? option)
    {
        return string.IsNullOrWhiteSpace(option)
            ? string.Empty
            : option.Trim().ToUpperInvariant();
    }

    private IEnumerable<KeyValuePair<string, NormalizedVote>> BuildAggregationRecords(NormalizedVote vote)
    {
        var results = new List<KeyValuePair<string, NormalizedVote>>(2)
        {
            KeyValuePair.Create(BuildAggregationKey(AggregationOutputType.Global, null, vote.Option), vote)
        };

        if (!string.IsNullOrWhiteSpace(vote.CityTopic))
        {
            results.Add(KeyValuePair.Create(BuildAggregationKey(AggregationOutputType.City, vote.CityTopic, vote.Option), vote));
        }

        return results;
    }

    private AggregationResult CreateAggregationResult(string aggregationKey, long count)
    {
        if (!TryParseAggregationKey(aggregationKey, out var type, out var cityTopic, out var option))
        {
            return AggregationResult.Empty;
        }

        if (type == AggregationOutputType.Global)
        {
            var total = CreateGlobalTotal(option, count);
            return new AggregationResult(type, option, total);
        }

        if (string.IsNullOrWhiteSpace(cityTopic))
        {
            return AggregationResult.Empty;
        }

        var cityKey = string.Concat(cityTopic, ':', option);
        var cityTotal = CreateCityTotal(cityKey, count);

        return cityTotal is null
            ? AggregationResult.Empty
            : new AggregationResult(type, cityKey, cityTotal);
    }

    private static string BuildAggregationKey(AggregationOutputType type, string? cityTopic, string option)
    {
        return type switch
        {
            AggregationOutputType.Global => string.Concat("G|", option),
            AggregationOutputType.City when !string.IsNullOrWhiteSpace(cityTopic) => string.Concat("C|", cityTopic, '|', option),
            _ => throw new InvalidOperationException("Invalid aggregation key parameters.")
        };
    }

    private static bool TryParseAggregationKey(string aggregationKey, out AggregationOutputType type, out string? cityTopic, out string option)
    {
        type = default;
        cityTopic = null;
        option = string.Empty;

        if (string.IsNullOrWhiteSpace(aggregationKey) || aggregationKey.Length < 3 || aggregationKey[1] != '|')
        {
            return false;
        }

        var marker = aggregationKey[0];

        if (marker == 'G')
        {
            type = AggregationOutputType.Global;
            option = aggregationKey[2..];
            return option.Length > 0;
        }

        if (marker == 'C')
        {
            var secondSeparator = aggregationKey.IndexOf('|', 2);
            if (secondSeparator < 0 || secondSeparator >= aggregationKey.Length - 1)
            {
                return false;
            }

            type = AggregationOutputType.City;
            cityTopic = aggregationKey[2..secondSeparator];
            option = aggregationKey[(secondSeparator + 1)..];

            return !string.IsNullOrWhiteSpace(cityTopic) && option.Length > 0;
        }

        return false;
    }

    private string ApplicationId => string.Concat(_options.TallyGroupId, "-stream");

    private void StartStallDetection()
    {
        try
        {
            _stallTimer = new Timer(_ =>
            {
                try
                {
                    var elapsed = DateTime.UtcNow - _startUtc;
                    var processed = System.Threading.Interlocked.Read(ref _processedValidVotes);
                    if (elapsed < _initialGracePeriod)
                    {
                        return; // still within grace window
                    }
                    if (processed > 0)
                    {
                        return; // activity observed
                    }
                    if (_options != null && !_performedAutoRecovery)
                    {
                        // If flagged, perform one-time auto recovery (local state reset + restart)
                        // We rely on feature flags via Program controlling ForceStateResetOnStart & AutoStallRecoveryEnabled; simplified reflection check.
                        // Without direct access to flags instance here (not injected previously), we do conservative recovery using environment fallbacks.
                        var autoRecover = string.Equals(Environment.GetEnvironmentVariable("KAFKA_AUTO_STALL_RECOVERY"), "true", StringComparison.OrdinalIgnoreCase);
                        if (!autoRecover)
                        {
                            _logger.LogWarning("[StallDetection] No votes processed after {Elapsed}. Enable AutoStallRecovery to reset local state.", elapsed);
                            return;
                        }
                        _performedAutoRecovery = true;
                        _logger.LogWarning("[StallDetection] Performing ONE-TIME auto recovery after {Elapsed} with 0 processed votes.", elapsed);
                        try
                        {
                            DisposeCurrentStream();
                            ForceLocalStateReset();
                            RestartStream();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Auto stall recovery failed");
                        }
                    }
                }
                catch (Exception ex2)
                {
                    _logger.LogDebug(ex2, "Stall detection tick failed");
                }
            }, null, _stallCheckInterval, _stallCheckInterval);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to start stall detection timer");
        }
    }

    private void StopStallDetection()
    {
        try { _stallTimer?.Dispose(); } finally { _stallTimer = null; }
    }

    private void DisposeCurrentStream()
    {
        try
        {
            _stream?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DisposeCurrentStream failure");
        }
        finally
        {
            _stream = null;
            _streamTask = null;
        }
    }

    private void RestartStream()
    {
        try
        {
            _startUtc = DateTime.UtcNow;
            _processedValidVotes = 0;
            // Minimal rebuild: builder + config + start
            var builder = new StreamBuilder();
            var voteSerDes = new ConfluentJsonSerDes<VoteEnvelope>(_schemaRegistryClient, SerializerConfig);
            var voteTotalSerDes = new ConfluentJsonSerDes<VoteTotal>(_schemaRegistryClient, SerializerConfig);
            var merged = BuildStream(builder, _options.VotesTopic, voteSerDes);
            var valid = merged.Filter((_, v, _) => v.Option.Length > 0, "filter-empty-option-r");
            var agg = valid.FlatMap((_, v, _) => BuildAggregationRecords(v), "flatmap-aggregation-records-r");
            var counts = agg.GroupByKey().Count("combined-tally-counts-r");
            var results = counts.ToStream().Map((key, count, _) => KeyValuePair.Create(key, CreateAggregationResult(key, count)), "map-aggregation-result-r");
            var branches = results.Branch((_, r, _) => r.Type == AggregationOutputType.Global, (_, r, _) => r.Type == AggregationOutputType.City);
            branches[0]
                .Filter((_, r, _) => r.Total is not null, "filter-null-global-total-r")
                .Map((_, r, _) => KeyValuePair.Create(r.OutputKey, r.Total!), "map-global-output-r")
                .To(_options.TotalsTopic, new StringSerDes(), voteTotalSerDes);
            branches[1]
                .Filter((_, r, _) => r.Total is not null, "filter-null-city-total-r")
                .Map((_, r, _) => KeyValuePair.Create(r.OutputKey, r.Total!), "map-city-output-r")
                .To(_options.VotesByCityTopic, new StringSerDes(), voteTotalSerDes);
            var config = BuildStreamConfig();
            _stream = new KafkaStream(builder.Build(), config);
            _streamTask = _stream.StartAsync(CancellationToken.None);
            ObserveStreamTask();
            _logger.LogWarning("Stall recovery restart issued.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart stream during stall recovery");
        }
    }

    private void TryForceLocalStateReset()
    {
        var force = string.Equals(Environment.GetEnvironmentVariable("KAFKA_FORCE_STATE_RESET"), "true", StringComparison.OrdinalIgnoreCase);
        if (!force)
        {
            return;
        }
        ForceLocalStateReset();
    }

    private void ForceLocalStateReset()
    {
        try
        {
            var stateDir = Path.Combine(AppContext.BaseDirectory, "stream-state");
            if (Directory.Exists(stateDir))
            {
                Directory.Delete(stateDir, true);
                _logger.LogWarning("Local stream state directory deleted for forced reset: {Dir}", stateDir);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete local state directory during forced reset");
        }
    }

    private sealed record NormalizedVote(string Option, string? CityTopic, string? City, int? ZipCode)
    {
        public static NormalizedVote Empty { get; } = new(string.Empty, null, null, null);
    }

    private sealed record AggregationResult(AggregationOutputType Type, string OutputKey, VoteTotal? Total)
    {
        public static AggregationResult Empty { get; } = new(AggregationOutputType.Global, string.Empty, null);
    }

    private enum AggregationOutputType
    {
        Global,
        City
    }
}
