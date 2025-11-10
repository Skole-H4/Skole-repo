namespace TallyService.HostedServices;

// -------------------------------------------------------------------------------------------------
// StreamTallyHostedService
//
// This hosted service builds and runs a single Streamiz Kafka topology which consumes raw vote
// envelopes and produces two compacted aggregation topics:
//   1. TotalsTopic        (global vote counts per option)
//   2. VotesByCityTopic   (vote counts per option scoped to a city topic)
//
// High-level processing flow:
//   - Source vote envelope stream(s) are normalized (option casing and empty filtering).
//   - Each normalized vote is expanded (FlatMap) into one or two aggregation records:
//       * Global record:  G|<OPTION>
//       * City record:    C|<CITY_TOPIC>|<OPTION> (only if CityTopic present in envelope)
//   - A single grouped-count aggregation tallies all records (Count). This produces a unified
//     KTable keyed by aggregation markers.
//   - Aggregation keys are parsed back into strongly typed AggregationResult objects.
//   - Results are branched into global and city outputs and mapped into VoteTotal objects.
//   - VoteTotal records are written to their respective compacted topics.
//
// Operational safeguards:
//   - EXACTLY_ONCE processing guarantee minimizes duplicate output during failures.
//   - Internal Streamiz topics are cleaned up on startup if stale (e.g., after abrupt shutdown).
//   - If Kafka reports pre-existing internal topics causing startup failure, a targeted cleanup
//     retry is executed (limited attempts) before surfacing the error.
//   - ApplicationId is derived from the configured tally consumer group for consistent naming.
//
// Key design choices:
//   - Single aggregation path (merged streams) reduces complexity vs. parallel per-city counting.
//   - Compact output topics hold only the latest count per key; upstream consumers can treat them
//     as materialized views.
//   - NormalizedVote and AggregationResult are lightweight internal record types to keep the flow
//     explicit and self-documenting.
// -------------------------------------------------------------------------------------------------

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

    /// <summary>
    /// Builds the stream topology and starts the Kafka stream. Performs an initial cleanup of
    /// stale internal topics, then attempts startup with limited retry logic if conflicting
    /// internal topics are detected (e.g., leftover from previous run with different configuration).
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return;
        }

        if (_options.EnableInternalTopicCleanup)
        {
            await CleanupStaleInternalTopicsAsync(cancellationToken).ConfigureAwait(false);
        }

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
        var maxAttempts = Math.Max(1, _options.MaxStreamStartupRetries);
        var startupGuardDelay = TimeSpan.FromSeconds(Math.Max(0, _options.StartupGuardDelaySeconds));

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

                    if (_options.EnableInternalTopicCleanup)
                    {
                        await CleanupInternalTopicsAsync(duplicateTopics, cancellationToken).ConfigureAwait(false);
                    }

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
            break;
        }
    }

    /// <summary>
    /// Stops the running Kafka stream (if active) disposing resources and awaiting any completion
    /// tasks. Errors during disposal are logged but suppressed to allow graceful shutdown.
    /// </summary>
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
        }
    }

    /// <summary>
    /// Constructs a source stream for vote envelopes and maps values into <see cref="NormalizedVote"/>.
    /// </summary>
    private IKStream<string, NormalizedVote> BuildStream(
        StreamBuilder builder,
        string topic,
        ISerDes<VoteEnvelope> valueSerDes)
    {
        return builder
            .Stream<string, VoteEnvelope>(topic, new StringSerDes(), valueSerDes)
            .MapValues<NormalizedVote>((value, _) => NormalizeVote(value));
    }

    /// <summary>
    /// Merges multiple vote streams into a single stream. Requires at least one source stream.
    /// </summary>
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

    /// <summary>
    /// Creates the Streamiz configuration with exactly-once semantics and a deterministic state directory.
    /// </summary>
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

    /// <summary>
    /// Removes any internal Streamiz topics from previous runs whose names match the current application id.
    /// This helps avoid topology startup conflicts after abrupt shutdowns or configuration changes.
    /// </summary>
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

    /// <summary>
    /// Deletes a specific set of internal Streamiz topics (typically reported as duplicates during startup)
    /// and waits for deletion to propagate before retrying stream initialization.
    /// </summary>
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

    /// <summary>
    /// Attaches a continuation to the stream task to log failures and surface specific internal topic
    /// duplication scenarios distinctly from general unexpected errors.
    /// </summary>
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

    /// <summary>
    /// Parses a stream startup exception chain to extract internal topic names when Kafka signals they already exist.
    /// </summary>
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

    /// <summary>
    /// Polls Kafka for topic deletion completion for a set of pending internal topics until removal or timeout.
    /// </summary>
    private async Task WaitForTopicDeletionAsync(IAdminClient admin, HashSet<string> pending, CancellationToken cancellationToken)
    {
        if (pending.Count == 0)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
    var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.InternalTopicDeletionTimeoutSeconds));

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

    /// <summary>
    /// Creates a global (non-city scoped) VoteTotal record with timestamp and safe count conversion.
    /// </summary>
    private VoteTotal CreateGlobalTotal(string option, long count)
    {
        return new VoteTotal
        {
            Option = option,
            Count = ToCount(option, count),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Creates a city-scoped VoteTotal based on composite key "<CITY_TOPIC>:<OPTION>". Falls back gracefully
    /// if the city topic cannot be resolved in the catalog.
    /// </summary>
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

    /// <summary>
    /// Converts a long count to int, logging and capping when the value exceeds Int32.MaxValue.
    /// </summary>
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

    /// <summary>
    /// Extracts and normalizes a vote envelope into a <see cref="NormalizedVote"/> (upper-case option, trimmed).
    /// Returns an empty marker if the envelope lacks a valid event.
    /// </summary>
    private static NormalizedVote NormalizeVote(VoteEnvelope? envelope)
    {
        if (envelope is null || envelope.Event is null)
        {
            return NormalizedVote.Empty;
        }

        var option = NormalizeOption(envelope.Option);
        return new NormalizedVote(
            option,
            envelope.CityTopic,
            envelope.City,
            envelope.ZipCode);
    }

    /// <summary>
    /// Normalizes an option string by trimming and upper-casing, returning empty string for null/whitespace.
    /// </summary>
    private static string NormalizeOption(string? option)
    {
        return string.IsNullOrWhiteSpace(option)
            ? string.Empty
            : option.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Expands a normalized vote into global and optional city aggregation records keyed with prefix markers.
    /// </summary>
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

    /// <summary>
    /// Translates a counted aggregation key back into a typed AggregationResult mapping output key to VoteTotal.
    /// </summary>
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

    /// <summary>
    /// Builds an aggregation key using markers: G|OPTION for global and C|CITY_TOPIC|OPTION for city scoped.
    /// </summary>
    private static string BuildAggregationKey(AggregationOutputType type, string? cityTopic, string option)
    {
        return type switch
        {
            AggregationOutputType.Global => string.Concat("G|", option),
            AggregationOutputType.City when !string.IsNullOrWhiteSpace(cityTopic) => string.Concat("C|", cityTopic, '|', option),
            _ => throw new InvalidOperationException("Invalid aggregation key parameters.")
        };
    }

    /// <summary>
    /// Parses an aggregation key into its constituent parts (type, city topic, option). Returns false if malformed.
    /// </summary>
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

    /// <summary>
    /// Application id used by Streamiz to name internal topics (derived from tally consumer group id).
    /// </summary>
    private string ApplicationId => string.Concat(_options.TallyGroupId, "-stream");

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
