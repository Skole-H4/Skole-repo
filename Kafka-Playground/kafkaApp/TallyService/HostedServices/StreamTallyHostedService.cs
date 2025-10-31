namespace TallyService.HostedServices;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Streamiz.Kafka.Net;
using Streamiz.Kafka.Net.SerDes;
using Streamiz.Kafka.Net.Stream;
using Streamiz.Kafka.Net.Table;
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

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stream is not null)
        {
            return Task.CompletedTask;
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

        var globalTotals = validVotes
            .SelectKey<string>((_, vote, _) => vote.Option, "select-global-option")
            .GroupByKey()
            .Count("global-counts")
            .ToStream()
            .Map((key, count, _) => KeyValuePair.Create(key, CreateGlobalTotal(key, count)), "map-global-total");
    globalTotals.To(_options.TotalsTopic, new StringSerDes(), voteTotalSerDes);

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

        var config = BuildStreamConfig();
        var topology = builder.Build();

        _stream = new KafkaStream(topology, config);
        _streamTask = _stream.StartAsync(cancellationToken);

        if (_streamTask.IsCompleted)
        {
            return _streamTask;
        }

        return Task.CompletedTask;
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
            ApplicationId = string.Concat(_options.TallyGroupId, "-stream"),
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

    private static NormalizedVote NormalizeVote(VoteEnvelope? envelope)
    {
        var vote = envelope?.Event;
        if (vote is null)
        {
            return NormalizedVote.Empty;
        }

        var option = NormalizeOption(vote.Option);
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

    private sealed record NormalizedVote(string Option, string? CityTopic, string? City, int? ZipCode)
    {
        public static NormalizedVote Empty { get; } = new(string.Empty, null, null, null);
    }
}
