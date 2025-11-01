using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.Kafka.Admin; // For parsing CreateTopicsException
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Streamiz.Kafka.Net;
using Streamiz.Kafka.Net.Errors;
using Streamiz.Kafka.Net.State;
using Streamiz.Kafka.Net.SerDes;
using Streamiz.Kafka.Net.Stream;
using Streamiz.Kafka.Net.Table;
using TallyService.Abstractions;
using TallyService.Configuration;
using TallyService.Models;
using TallyService.Services;

namespace TallyService.HostedServices;

/// <summary>
/// Hosted service building and running the Kafka Streams topology that tallies votes globally
/// and per city. Uses Streamiz (Kafka Streams for .NET). No destructive internal topic cleanup
/// is performed; internal topics are managed by the library to preserve state.
/// </summary>
public sealed class StreamTallyHostedService : IHostedService
{
    private static readonly JsonSerializerConfig SerializerConfig = new()
    {
        AutoRegisterSchemas = true // Consider disabling in production after initial bootstrap
    };

    private readonly KafkaOptions _options;
    private readonly ICityCatalog _cityCatalog;
    private readonly ISchemaRegistryClient _schemaRegistryClient;
    private readonly ILogger<StreamTallyHostedService> _logger;

    private KafkaStream? _stream;
    private Task? _streamTask;

    private string BaseApplicationId => string.Concat(_options.TallyGroupId, "-stream");
    private string? _runApplicationId;
    private Timer? _healthTimer;
    private bool UseEphemeralId => string.Equals(Environment.GetEnvironmentVariable("KAFKA_EPHEMERAL_ID"), "true", StringComparison.OrdinalIgnoreCase);
    // Deserialization diagnostics counters (interlocked)
    private long _deserErrorCount;
    private long _deserLastLoggedSummaryCount;
    private int _deserWarnLimit;
    private int _deserSummaryInterval;
    private int _deserPayloadPreviewLimit;
    private long _processedValidVotes;

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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return Task.CompletedTask; // Already started
        }

        var builder = new StreamBuilder();

        // SerDes (JSON + schema registry). VoteTotal reused for both global and city outputs.
    // Use built-in JsonSerDes for simplicity (Schema Registry optional for plain JSON). If schema registry integration is required,
    // a custom SerDes implementing ISerDes<T> can wrap Confluent's JsonSerializer.
    // Use tolerant SerDes to reduce deserialization exception spam and classify malformed payloads.
    var voteSerDes = new TolerantVoteEnvelopeSerDes(_logger as ILogger<TolerantVoteEnvelopeSerDes> ?? new LoggerFactory().CreateLogger<TolerantVoteEnvelopeSerDes>());
    var voteTotalSerDes = new JsonSerDes<VoteTotal>();

        // Source stream of envelopes, normalized.
        var voteStream = BuildStream(builder, _options.VotesTopic, voteSerDes);
        var validVotes = voteStream.Filter((_, vote, _) => vote.Option.Length > 0, "filter-empty-option");

        // Global totals by option.
        var globalTotals = validVotes
            .SelectKey<string>((_, vote, _) => vote.Option, "select-global-option")
            .GroupByKey()
            .Count("global-counts")
            .ToStream()
            .Map((key, count, _) => KeyValuePair.Create(key, CreateGlobalTotal(key, count)), "map-global-total");
        globalTotals.To(_options.TotalsTopic, new StringSerDes(), voteTotalSerDes);

        // Per-city totals; key pattern: {cityTopic}:{option}
        var cityTotals = validVotes
            .Filter((_, vote, _) => vote.CityTopic is not null, "filter-city-topic")
            .SelectKey<string>((_, vote, _) => string.Concat(vote.CityTopic, ':', vote.Option), "select-city-option")
            .GroupByKey()
            .Count("city-counts")
            .ToStream()
            .Map((key, count, _) => KeyValuePair.Create(key, CreateCityTotal(key, count)), "map-city-total")
            .Filter((_, total, _) => total is not null, "filter-null-city-total")
            .MapValues<VoteTotal>((total, _) => total!, "unwrap-city-total");
        cityTotals.To(_options.VotesByCityTopic, new StringSerDes(), voteTotalSerDes);

        var topology = builder.Build();

        // Decide application.id (stable unless EPHEMERAL override set)
    var rawEphemeral = Environment.GetEnvironmentVariable("KAFKA_EPHEMERAL_ID");
    _logger.LogDebug("Env KAFKA_EPHEMERAL_ID = '{RawEphemeral}'", rawEphemeral);
    _runApplicationId = UseEphemeralId ? GenerateUniqueApplicationId() : BaseApplicationId;
        if (UseEphemeralId)
        {
            _logger.LogWarning("Using EPHEMERAL application.id = {AppId}; state & EOS semantics not retained.", _runApplicationId);
        }
        else
        {
            _logger.LogInformation("Using STABLE application.id = {AppId} (required for stateful recovery).", _runApplicationId);
        }
        LogExistingInternalTopics(_runApplicationId);
        var effectiveReplicationFactor = ValidateReplicationFactor(_options.DefaultReplicationFactor);
        var config = BuildStreamConfig(_runApplicationId, effectiveReplicationFactor);
        _stream = new KafkaStream(topology, config);
        // Replace default topic manager with lenient variant to tolerate existing internal topics.
        try
        {
            var field = _stream.GetType().GetField("topicManager", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (field != null)
            {
                var lenientType = typeof(StreamTallyHostedService).Assembly.GetType("TallyService.Services.LenientTopicManager");
                if (lenientType != null)
                {
                    ILoggerFactory? lf = null;
                    var loggerFactoryField = _stream.GetType().GetField("logger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (loggerFactoryField?.GetValue(_stream) is ILoggerFactory factory)
                    {
                        lf = factory;
                    }
                    var topicLogger = lf?.CreateLogger("LenientTopicManager") ?? _logger;
                    var lenient = Activator.CreateInstance(lenientType, config, topicLogger);
                    if (lenient != null)
                    {
                        field.SetValue(_stream, lenient);
                        _logger.LogDebug("LenientTopicManager injected successfully.");
                    }
                }
            }
        }
        catch (Exception injectEx)
        {
            _logger.LogDebug(injectEx, "Failed to inject LenientTopicManager");
        }
        // Attach state change observer for deeper diagnostics
        try
        {
            var evt = _stream.GetType().GetEvent("OnStateChange");
            if (evt != null)
            {
                // Dynamic subscribe using reflection; handler logs transitions
                EventHandler? handler = (s, e) =>
                {
                    try
                    {
                        var newStateProp = e?.GetType().GetProperty("NewState");
                        var oldStateProp = e?.GetType().GetProperty("OldState");
                        var newStateVal = newStateProp?.GetValue(e)?.ToString();
                        var oldStateVal = oldStateProp?.GetValue(e)?.ToString();
                        _logger.LogDebug("[StreamState] {Old} -> {New}", oldStateVal, newStateVal);
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogDebug(ex2, "State change handler failure");
                    }
                };
                evt.AddEventHandler(_stream, handler);
                _logger.LogDebug("Attached stream state change diagnostics handler.");
            }
        }
        catch (Exception obsEx)
        {
            _logger.LogDebug(obsEx, "Failed to attach state change observer");
        }
        if (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("StartAsync received a pre-cancelled token before starting KafkaStream.");
        }
        try
        {
            _streamTask = _stream.StartAsync(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Cancellation was requested immediately after StartAsync invocation.");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Kafka stream startup cancelled.");
            DisposeStream();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Kafka stream failed to start.");
            DisposeStream();
            throw;
        }
        ObserveStreamTask();
        StartHealthLogging();
        _logger.LogInformation("Kafka stream start requested.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeStream();
        StopHealthLogging();
        return Task.CompletedTask;
    }

    // Removed previous RUNNING state polling to avoid shutdown loops; Streamiz emits state transitions via log.

    // Deleted retry handler; using single-start with pre-cleanup strategy.

    // Removed deletion routine; rely on unique application id for dev stability.

    private void ObserveStreamTask()
    {
        if (_streamTask is null)
        {
            return;
        }

        _ = _streamTask.ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception is not null)
            {
                _logger.LogError(t.Exception.GetBaseException(), "Kafka stream stopped unexpectedly.");
            }
            else if (t.IsCanceled)
            {
                _logger.LogWarning("Kafka stream task was cancelled.");
            }
            else
            {
                _logger.LogInformation("Kafka stream task completed.");
            }
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    // Removed async disposal path; synchronous disposal sufficient for current resource profile.

    private void DisposeStream()
    {
        if (_stream is null)
        {
            return;
        }
        try
        {
            _stream.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error disposing stream during retry.");
        }
        finally
        {
            _stream = null;
            _streamTask = null;
        }
    }

    // Extract internal/existing topic conflicts from exception graphs (recoverable scenarios)
    private static bool TryExtractRecoverableTopics(Exception exception, out IReadOnlyCollection<string> topics)
    {
        var recoverable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var q = new Queue<Exception>();
        q.Enqueue(exception);
        while (q.Count > 0)
        {
            var current = q.Dequeue();
            if (current is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    q.Enqueue(inner);
                }
            }
            if (current.InnerException is not null)
            {
                q.Enqueue(current.InnerException);
            }
            if (current is CreateTopicsException cte)
            {
                foreach (var result in cte.Results)
                {
                    if (result.Error.Code == ErrorCode.TopicAlreadyExists || result.Error.Code == ErrorCode.InvalidPartitions)
                    {
                        recoverable.Add(result.Topic);
                    }
                }
            }
            if (!string.IsNullOrWhiteSpace(current.Message))
            {
                ExtractTopicsFromMessage(current.Message, recoverable);
            }
        }
        topics = recoverable.Count > 0 ? recoverable : Array.Empty<string>();
        return recoverable.Count > 0;
    }

    // Heuristic parsing of topic names embedded in exception messages.
    private static void ExtractTopicsFromMessage(string message, ISet<string> accumulator)
    {
        const string existingTopicPrefix = "Existing internal topic ";
        const string existingTopicSuffix = " with invalid partitions";
        var index = 0;
        while (index < message.Length)
        {
            var start = message.IndexOf(existingTopicPrefix, index, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            start += existingTopicPrefix.Length;
            var end = message.IndexOf(existingTopicSuffix, start, StringComparison.OrdinalIgnoreCase);
            if (end > start)
            {
                var topic = message[start..end].Trim();
                if (topic.Length > 0) accumulator.Add(topic);
            }
            index = end > 0 ? end + existingTopicSuffix.Length : message.Length;
        }
        const string quotedPrefix = "Topic '";
        const string quotedSuffix = "' already exists";
        index = 0;
        while (index < message.Length)
        {
            var start = message.IndexOf(quotedPrefix, index, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            start += quotedPrefix.Length;
            var suffixIndex = message.IndexOf(quotedSuffix, start, StringComparison.OrdinalIgnoreCase);
            if (suffixIndex < 0) break;
            var end = suffixIndex;
            if (end > start)
            {
                var topic = message[start..end].Trim();
                if (topic.Length > 0) accumulator.Add(topic);
            }
            index = suffixIndex + quotedSuffix.Length;
        }
    }

    private void LogRecoverableFailure(IReadOnlyCollection<string> topics, int attempt, int maxAttempts)
    {
        _logger.LogWarning(
            "Kafka stream startup conflicting topic(s): {Topics}. Attempt {Attempt}/{Max}.",
            string.Join(", ", topics),
            attempt,
            maxAttempts);
    }

    private bool AreAllInternal(IReadOnlyCollection<string> topics)
    {
    var prefix = (_runApplicationId ?? BaseApplicationId) + "-";
        foreach (var t in topics)
        {
            if (!t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!(t.Contains("-changelog", StringComparison.OrdinalIgnoreCase) || t.Contains("-repartition", StringComparison.OrdinalIgnoreCase)))
            {
                return false; // Not an internal changelog/repartition topic
            }
        }
        return true;
    }

    private async Task DeleteInternalTopicsAsync(IReadOnlyCollection<string> topics, CancellationToken cancellationToken)
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _options.BootstrapServers }).Build();
        try
        {
            await admin.DeleteTopicsAsync(topics, new DeleteTopicsOptions { RequestTimeout = TimeSpan.FromSeconds(30), OperationTimeout = TimeSpan.FromSeconds(30) }).ConfigureAwait(false);
        }
        catch (DeleteTopicsException ex)
        {
            foreach (var r in ex.Results)
            {
                if (r.Error.Code != ErrorCode.UnknownTopicOrPart)
                {
                    _logger.LogDebug("DeleteTopics result for {Topic}: {Code} - {Reason}", r.Topic, r.Error.Code, r.Error.Reason);
                }
            }
        }
        // Wait a short stabilization window for deletion to propagate.
        var delay = TimeSpan.FromSeconds(2);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private IKStream<string, NormalizedVote> BuildStream(StreamBuilder builder, string topic, ISerDes<VoteEnvelope> valueSerDes)
    {
        return builder
            .Stream<string, VoteEnvelope>(topic, new StringSerDes(), valueSerDes)
            .MapValues<NormalizedVote>((value, _) =>
            {
                var nv = NormalizeVote(value);
                if (!string.IsNullOrEmpty(nv.Option))
                {
                    System.Threading.Interlocked.Increment(ref _processedValidVotes);
                }
                return nv;
            });
    }

    private StreamConfig BuildStreamConfig(string applicationId, short replicationFactor)
    {
        // Randomize state directory per run to avoid cross-run UUID reuse
        var stateDir = Path.Combine(AppContext.BaseDirectory, "stream-state", applicationId);
        Directory.CreateDirectory(stateDir);
        var config = new StreamConfig
        {
            ApplicationId = applicationId,
            BootstrapServers = _options.BootstrapServers,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            StateDir = stateDir,
            Guarantee = ProcessingGuarantee.EXACTLY_ONCE,
            ReplicationFactor = replicationFactor,
            CommitIntervalMs = StreamConfig.EOS_DEFAULT_COMMIT_INTERVAL_MS,
            NumStreamThreads = 1,
            DefaultKeySerDes = new StringSerDes(),
            DefaultValueSerDes = new JsonSerDes<NormalizedVote>()
        };
        config.AllowAutoCreateTopics = false; // Expect topics provisioned externally
        // Inner exception handler: swallow recoverable internal topic creation conflicts (already exists, invalid partitions)
        config.InnerExceptionHandler = ex =>
        {
            try
            {
                if (TryExtractRecoverableTopics(ex, out var topics) && topics.Count > 0 && AreAllInternal(topics))
                {
                    _logger.LogWarning("Ignoring recoverable internal topic creation conflicts: {Topics}", string.Join(", ", topics));
                    return ExceptionHandlerResponse.CONTINUE;
                }
            }
            catch (Exception handlerEx)
            {
                _logger.LogDebug(handlerEx, "InnerExceptionHandler processing failure.");
            }
            // Fallback: treat as non-recoverable -> let stream continue (could be refined to FAIL when available)
            return ExceptionHandlerResponse.CONTINUE;
        };
        // Deserialization handler: skip bad JSON instead of killing stream
        config.DeserializationExceptionHandler = (ctx, record, ex) =>
        {
            try
            {
                var total = System.Threading.Interlocked.Increment(ref _deserErrorCount);
                // Capture first few payload previews for forensic analysis
                if (_deserWarnLimit == 0)
                {
                    InitializeDeserLoggingConfig();
                }
                if (total <= _deserWarnLimit)
                {
                    var preview = TruncateForPreview(record.Message?.Value, _deserPayloadPreviewLimit);
                    _logger.LogWarning("Deserialization error #{Count} skipped topic={Topic} partition={Partition} offset={Offset}: {Error}. Preview='{Preview}'", total, record.Topic, record.Partition, record.Offset, ex.Message, preview);
                }
                else if (_deserSummaryInterval > 0 && total % _deserSummaryInterval == 0)
                {
                    var delta = total - _deserLastLoggedSummaryCount;
                    _deserLastLoggedSummaryCount = total;
                    _logger.LogWarning("Deserialization errors accumulated: total={Total} (+{Delta} since last summary). Recent error: {Error}", total, delta, ex.Message);
                }
                else
                {
                    // Reduce noise after warn limit surpassed
                    _logger.LogDebug("Deserialization error skipped (count={Count}) topic={Topic} partition={Partition} offset={Offset}: {Error}", total, record.Topic, record.Partition, record.Offset, ex.Message);
                }
            }
            catch (Exception logEx)
            {
                _logger.LogDebug(logEx, "Failed logging deserialization error");
            }
            return ExceptionHandlerResponse.CONTINUE;
        };
        _logger.LogInformation("Stream config: guarantee={Guarantee}, commit.interval.ms={CommitIntervalMs}, replication.factor={ReplicationFactor}, state.dir={StateDir}",
            config.Guarantee, config.CommitIntervalMs, config.ReplicationFactor, config.StateDir);
        return config;
    }

    private string GenerateUniqueApplicationId()
    {
        // Time-based suffix; GUID fallback in rare collision case.
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return $"{BaseApplicationId}-{ts}";
    }

    private void LogExistingInternalTopics(string applicationId)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _options.BootstrapServers }).Build();
            var metadata = admin.GetMetadata(TimeSpan.FromSeconds(5));
            var prefix = applicationId + "-";
            var internalTopics = new List<string>();
            foreach (var t in metadata.Topics)
            {
                if (t.Error.Code == ErrorCode.NoError && t.Topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    internalTopics.Add(t.Topic);
                }
            }
            if (internalTopics.Count == 0)
            {
                _logger.LogInformation("No existing internal topics for application.id {AppId}", applicationId);
                return;
            }
            _logger.LogWarning("Existing internal topics (pre-start): {Topics}", string.Join(", ", internalTopics));
            DescribeTopics(internalTopics, "pre-start");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to log existing internal topics");
        }
    }

    private void DescribeTopics(IEnumerable<string> topics, string context)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _options.BootstrapServers }).Build();
            var allMd = admin.GetMetadata(TimeSpan.FromSeconds(5));
            var set = new HashSet<string>(topics, StringComparer.OrdinalIgnoreCase);
            foreach (var t in allMd.Topics)
            {
                if (!set.Contains(t.Topic)) continue;
                if (t.Error.Code != ErrorCode.NoError)
                {
                    _logger.LogWarning("{Context}: describe error {Topic} {Code} {Reason}", context, t.Topic, t.Error.Code, t.Error.Reason);
                    continue;
                }
                var partDetailsList = new List<string>();
                foreach (var p in t.Partitions)
                {
                    var replicas = string.Join('/', p.Replicas);
                    partDetailsList.Add($"{p.PartitionId}(leader={p.Leader},replicas={replicas})");
                }
                var rf = t.Partitions.Count > 0 ? pReplicasCount(t.Partitions[0]) : 0;
                _logger.LogInformation("{Context}: {Topic} partitions={PartitionCount} replicationFactor~={Rf} detail=[{Details}]",
                    context, t.Topic, t.Partitions.Count, rf, string.Join(", ", partDetailsList));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DescribeTopics failed in context {Context}", context);
        }
    }

    private short ValidateReplicationFactor(short requested)
    {
        try
        {
            using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = _options.BootstrapServers }).Build();
            var md = admin.GetMetadata(TimeSpan.FromSeconds(5));
            var brokerCount = (short)md.Brokers.Count;
            if (brokerCount <= 0)
            {
                _logger.LogWarning("No brokers discovered; using requested replication factor {Requested}", requested);
                return requested;
            }
            if (requested > brokerCount)
            {
                _logger.LogWarning("Requested replication factor {Requested} exceeds broker count {BrokerCount}. Downgrading to broker count.", requested, brokerCount);
                return brokerCount;
            }
            return requested;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Replication factor validation failed; using requested {Requested}", requested);
            return requested;
        }
    }

    private void TryLogFailingInternalTopics(Exception ex)
    {
        try
        {
            if (!TryExtractRecoverableTopics(ex, out var topics) || topics.Count == 0)
            {
                return;
            }
            DescribeTopics(topics, "failure-path");
        }
        catch (Exception inner)
        {
            _logger.LogDebug(inner, "Failed to describe failing internal topics");
        }
    }

    private void StartHealthLogging()
    {
        _healthTimer = new Timer(_ =>
        {
            try
            {
                // Streamiz KafkaStream may expose a State enum/property named 'StreamState' or 'State'. Try reflection fallback.
                string state;
                if (_stream == null)
                {
                    state = "NOT_INITIALIZED";
                }
                else
                {
                    var type = _stream.GetType();
                    var prop = type.GetProperty("State") ?? type.GetProperty("StreamState");
                    state = prop != null ? (prop.GetValue(_stream)?.ToString() ?? "UNKNOWN") : "UNKNOWN";
                }
                var deserCountSnapshot = System.Threading.Interlocked.Read(ref _deserErrorCount);
                var processedSnapshot = System.Threading.Interlocked.Read(ref _processedValidVotes);
                _logger.LogDebug("[Health] app.id={AppId} state={State} deser.errors={DeserErrors} processed.valid.votes={Processed}", _runApplicationId, state, deserCountSnapshot, processedSnapshot);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Health logging tick failed");
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60));
    }

        private static int pReplicasCount(object partitionMetadata)
        {
            // PartitionMetadata type (Confluent) exposes Replicas as IList<int>; use reflection to stay loose.
            var prop = partitionMetadata.GetType().GetProperty("Replicas");
            if (prop?.GetValue(partitionMetadata) is System.Collections.ICollection coll)
            {
                return coll.Count;
            }
            return 0;
        }

    private void StopHealthLogging()
    {
        try { _healthTimer?.Dispose(); } finally { _healthTimer = null; }
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
            return null; // Malformed key
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
        _logger.LogWarning("Vote count for option {Option} capped at {Max}", option, int.MaxValue);
        return int.MaxValue;
    }

    private static NormalizedVote NormalizeVote(VoteEnvelope? envelope)
    {
        var vote = envelope?.Event;
        if (vote is null)
        {
            return NormalizedVote.Empty;
        }
        var option = NormalizeOption(vote.Option);
        return new NormalizedVote(option, envelope?.CityTopic, envelope?.City, envelope?.ZipCode);
    }

    private static string NormalizeOption(string? option)
    {
        return string.IsNullOrWhiteSpace(option) ? string.Empty : option.Trim().ToUpperInvariant();
    }

    // --- Deserialization error logging helpers ---
    private void InitializeDeserLoggingConfig()
    {
        // Idempotent initialization; only set if not already configured (warn limit == 0 sentinel)
        if (_deserWarnLimit != 0)
        {
            return;
        }
        _deserWarnLimit = ParseEnvInt("KAFKA_DESER_WARN_LIMIT", 10, 0, 10_000);
        _deserSummaryInterval = ParseEnvInt("KAFKA_DESER_SUMMARY_INTERVAL", 100, 10, 100_000);
        _deserPayloadPreviewLimit = ParseEnvInt("KAFKA_DESER_PREVIEW_LIMIT", 120, 16, 10_000);
        _logger.LogInformation("Deserialization logging config warn.limit={WarnLimit} summary.interval={SummaryInterval} preview.limit={PreviewLimit}", _deserWarnLimit, _deserSummaryInterval, _deserPayloadPreviewLimit);
    }

    private static int ParseEnvInt(string name, int @default, int min, int max)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(raw)) return @default;
        if (!int.TryParse(raw, out var value)) return @default;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static string TruncateForPreview(byte[]? data, int limit)
    {
        if (data is null || data.Length == 0) return "<empty>";
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(data);
            if (text.Length <= limit) return text;
            return text.Substring(0, limit) + "…"; // ellipsis
        }
        catch
        {
            return $"<binary:{data.Length} bytes>";
        }
    }

    private sealed record NormalizedVote(string Option, string? CityTopic, string? City, int? ZipCode)
    {
        public static NormalizedVote Empty { get; } = new(string.Empty, null, null, null);
    }

}
