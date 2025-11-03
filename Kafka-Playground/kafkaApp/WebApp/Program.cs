// -------------------------------------------------------------------------------------------------
// WebApp entrypoint and service wiring
//
// Responsibilities:
// 1. Configure Kafka producer and schema registry client.
// 2. Register in-memory stores for vote totals and city-level breakdowns.
// 3. Expose minimal API endpoints for interacting with the vote stream.
// 4. Host background consumers that keep UI state updated in real time.
// 5. Provide utilities for resolving target city topics and constructing message keys.
//
// Notes:
// - All Kafka interaction uses strongly typed JSON (Schema Registry) for envelope and tally records.
// - City auto vote simulation is managed through CityAutoVoteManager and controllers per city.
// - Minimal APIs return JSON payloads; error handling favors concise validation messages.
// - Background consumers run on a Task to allow cooperative cancellation with ASP.NET host shutdown.
// -------------------------------------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using Confluent.Kafka;
using Confluent.Kafka.SyncOverAsync;
using Confluent.SchemaRegistry;
using Confluent.SchemaRegistry.Serdes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WebApp.Components;
using WebApp.Configuration;
using WebApp.Models;
using WebApp.Services;

// Create the web application builder (configuration + DI root)
var builder = WebApplication.CreateBuilder(args);

// Bind Kafka options section and expose the concrete instance for easy injection.
builder.Services.Configure<KafkaOptions>(builder.Configuration.GetSection("Kafka"));
builder.Services.AddSingleton(serviceProvider => serviceProvider.GetRequiredService<IOptions<KafkaOptions>>().Value);

// Shared HttpClient factory for internal API calls (used by UI components to call endpoints).
builder.Services.AddHttpClient();

// Shared metadata and live in-memory state keyed by vote option with change notifications
builder.Services.AddSingleton<PartyCatalog>();
builder.Services.AddSingleton<CityCatalog>();
builder.Services.AddSingleton<VoteTotalsStore>();
builder.Services.AddSingleton<CityVoteStore>();

// Schema Registry client for JSON serialization of typed messages.
builder.Services.AddSingleton<ISchemaRegistryClient>(serviceProvider =>
{
    var kafkaOptions = serviceProvider.GetRequiredService<KafkaOptions>();
    return new CachedSchemaRegistryClient(new SchemaRegistryConfig
    {
        Url = kafkaOptions.SchemaRegistryUrl
    });
});

// High reliability producer for vote envelopes (idempotent + fully acknowledged).
builder.Services.AddSingleton<IProducer<string, VoteEnvelope>>(serviceProvider =>
{
    var kafkaOptions = serviceProvider.GetRequiredService<KafkaOptions>();
    var producerConfig = new ProducerConfig
    {
        BootstrapServers = kafkaOptions.BootstrapServers,
        Acks = Acks.All, // ensure replication before acknowledging
        EnableIdempotence = true, // guarantees ordering and deduplication on retries
        LingerMs = 5, // small batching window to reduce network overhead
        BatchSize = 64_000 // moderate batch size tuned for envelope payload structure
    };

    var schemaRegistryClient = serviceProvider.GetRequiredService<ISchemaRegistryClient>();

    return new ProducerBuilder<string, VoteEnvelope>(producerConfig)
        .SetValueSerializer(new JsonSerializer<VoteEnvelope>(schemaRegistryClient, new JsonSerializerConfig
        {
            AutoRegisterSchemas = true // simplify development; production could use pre-registration
        }))
        .Build();
});

builder.Services.AddSingleton<CityAutoVoteManager>();
builder.Services.AddHostedService<KafkaTopicSeeder>();
builder.Services.AddHostedService<TotalsConsumer>();
builder.Services.AddHostedService<CityVotesConsumer>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = true;
    });
// Build the application (finalize DI service provider)
var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/api/totals", (VoteTotalsStore store) => Results.Json(store.GetSnapshot()));
app.MapGet("/api/cities", (CityVoteStore store) => Results.Json(store.GetSnapshot()));

// Enqueue a vote into Kafka, optionally targeting one or more city topics.
app.MapPost("/api/vote", async (VoteRequest request,
                                 CityCatalog cityCatalog,
                                 PartyCatalog partyCatalog,
                                 IProducer<string, VoteEnvelope> voteProducer,
                                 KafkaOptions kafkaOptions) =>
{
    // Basic validation for required fields.
    if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.Option))
    {
        return Results.BadRequest("UserId and Option are required.");
    }

    var normalizedOption = request.Option.Trim().ToUpperInvariant();
    if (!partyCatalog.TryGetByLetter(normalizedOption, out _))
    {
        return Results.BadRequest($"Unknown option '{normalizedOption}'.");
    }

    var voteEvent = new VoteEvent
    {
        UserId = request.UserId,
        Option = normalizedOption,
        Timestamp = DateTimeOffset.UtcNow
    };

    var targetCities = ResolveTargets(request.TargetTopics, cityCatalog);
    var deliveryResults = new List<object>(targetCities.Count == 0 ? 1 : targetCities.Count);

    // If no city targets specified, produce a single global vote.
    if (targetCities.Count == 0)
    {
        var globalEnvelope = CreateEnvelope(voteEvent, null);
        var globalDelivery = await voteProducer.ProduceAsync(kafkaOptions.VotesTopic, new Message<string, VoteEnvelope>
        {
            Key = BuildMessageKey(null, normalizedOption),
            Value = globalEnvelope
        });

        deliveryResults.Add(new
        {
            topic = kafkaOptions.VotesTopic,
            partition = globalDelivery.Partition.Value,
            offset = globalDelivery.Offset.Value,
            city = globalEnvelope.City,
            zip = globalEnvelope.ZipCode
        });
    }
    else
    {
        foreach (var cityTopic in targetCities)
        {
            var targetedEnvelope = CreateEnvelope(voteEvent, cityTopic);
            var targetedDelivery = await voteProducer.ProduceAsync(kafkaOptions.VotesTopic, new Message<string, VoteEnvelope>
            {
                Key = BuildMessageKey(cityTopic.ZipCode, normalizedOption),
                Value = targetedEnvelope
            });

            deliveryResults.Add(new
            {
                topic = kafkaOptions.VotesTopic,
                partition = targetedDelivery.Partition.Value,
                offset = targetedDelivery.Offset.Value,
                city = targetedEnvelope.City,
                zip = targetedEnvelope.ZipCode
            });
        }
    }

    return Results.Ok(new
    {
        status = "queued",
        deliveries = deliveryResults
    });
});

app.MapPost("/api/cities/start", (CityControlRequest request, CityAutoVoteManager manager) =>
{
    var controllers = manager.ResolveControllers(request.Targets);
    if (controllers.Count == 0)
    {
        return Results.NotFound("No matching cities found.");
    }

    manager.Start(controllers, request.Rate);
    return Results.Ok(new
    {
        started = controllers.Count,
        rate = request.Rate
    });
});

app.MapPost("/api/cities/stop", async (CityControlRequest request, CityAutoVoteManager manager) =>
{
    var controllers = manager.ResolveControllers(request.Targets);
    if (controllers.Count == 0)
    {
        return Results.NotFound("No matching cities found.");
    }

    await manager.StopAsync(controllers);
    return Results.Ok(new
    {
        stopped = controllers.Count
    });
});

app.MapPost("/api/cities/rate", (CityControlRequest request, CityAutoVoteManager manager) =>
{
    if (request.Rate is null)
    {
        return Results.BadRequest("Rate is required.");
    }

    var controllers = manager.ResolveControllers(request.Targets);
    if (controllers.Count == 0)
    {
        return Results.NotFound("No matching cities found.");
    }

    manager.SetRate(controllers, request.Rate.Value);
    return Results.Ok(new
    {
        updated = controllers.Count,
        rate = request.Rate.Value
    });
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>
/// Resolve a collection of raw identifiers (city name, ASCII variant, topic name or zip code)
/// into distinct <see cref="CityTopic"/> instances using catalog lookups.
/// </summary>
/// <param name="requested">Raw identifiers supplied by caller (may be null or contain blanks).</param>
/// <param name="catalog">The city catalog used for resolution.</param>
/// <returns>Distinct resolved city topics, or empty list if none match.</returns>
static List<CityTopic> ResolveTargets(IEnumerable<string>? requested, CityCatalog catalog)
{
    var uniqueResults = new HashSet<CityTopic>(CityTopicComparer.Instance);

    if (requested is not null)
    {
        foreach (var rawEntry in requested)
        {
            var trimmedValue = rawEntry?.Trim();
            if (string.IsNullOrEmpty(trimmedValue))
            {
                continue;
            }

            if (catalog.TryResolve(trimmedValue, out var resolvedCity) && resolvedCity is not null)
            {
                uniqueResults.Add(resolvedCity);
            }
        }
    }

    return uniqueResults.ToList();
}

/// <summary>
/// Constructs a Kafka message key combining either a global scope or a zip code with the vote option.
/// Keys are used to aid partitioning and potential compaction by option within a locality.
/// </summary>
/// <param name="zipCode">Optional zip code; if null a global prefix is used.</param>
/// <param name="option">Normalized vote option (upper case).</param>
/// <returns>Canonical key format "GLOBAL|OPTION" or "ZIP|OPTION".</returns>
static string BuildMessageKey(int? zipCode, string option)
{
    return zipCode.HasValue
        ? string.Concat(zipCode.Value, '|', option)
        : string.Concat("GLOBAL|", option);
}

/// <summary>
/// Creates a strongly typed vote envelope optionally bound to a city context.
/// </summary>
/// <param name="vote">The underlying vote event.</param>
/// <param name="city">Optional city metadata.</param>
/// <returns>Populated envelope ready for Kafka serialization.</returns>
static VoteEnvelope CreateEnvelope(VoteEvent vote, CityTopic? city)
{
    return new VoteEnvelope
    {
        Event = vote,
        CityTopic = city?.TopicName,
        City = city?.City,
        ZipCode = city?.ZipCode
    };
}

/// <summary>
/// Equality comparer for <see cref="CityTopic"/> based on topic name (case-insensitive).
/// Used to enforce uniqueness when merging resolved city identifiers.
/// </summary>
sealed class CityTopicComparer : IEqualityComparer<CityTopic>
{
    public static CityTopicComparer Instance { get; } = new();

    public bool Equals(CityTopic? x, CityTopic? y)
    {
        if (ReferenceEquals(x, y))
        {
            return true;
        }

        if (x is null || y is null)
        {
            return false;
        }

        return string.Equals(x.TopicName, y.TopicName, StringComparison.OrdinalIgnoreCase);
    }

    public int GetHashCode(CityTopic obj)
        => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.TopicName);
}

/// <summary>
/// Background consumer that maintains in-memory global vote totals by consuming the compacted totals topic.
/// </summary>
sealed class TotalsConsumer : BackgroundService
{
    private readonly VoteTotalsStore _voteTotalsStore;
    private readonly ISchemaRegistryClient _schemaRegistryClient;
    private readonly KafkaOptions _kafkaOptions;

    public TotalsConsumer(VoteTotalsStore voteTotalsStore, ISchemaRegistryClient schemaRegistryClient, KafkaOptions kafkaOptions)
    {
        _voteTotalsStore = voteTotalsStore;
        _schemaRegistryClient = schemaRegistryClient;
        _kafkaOptions = kafkaOptions;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Offload loop to long-running task so BackgroundService can manage cancellation cleanly.
        return Task.Run(() =>
        {
            var consumerConfig = new ConsumerConfig
            {
                BootstrapServers = _kafkaOptions.BootstrapServers,
                GroupId = _kafkaOptions.UiTotalsGroupId ?? "webapp-totals-ui",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = true,
                IsolationLevel = IsolationLevel.ReadCommitted
            };

            consumerConfig.Set("socket.keepalive.enable", "true");
            consumerConfig.Set("debug", "broker,protocol,security");
            consumerConfig.Set("broker.address.family", "v4");

            using var consumer = new ConsumerBuilder<string, VoteTotal>(consumerConfig)
                .SetValueDeserializer(new JsonDeserializer<VoteTotal>(_schemaRegistryClient).AsSyncOverAsync())
                .Build();

            consumer.Subscribe(_kafkaOptions.TotalsTopic);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult?.Message?.Value is { } totalRecord && !string.IsNullOrWhiteSpace(totalRecord.Option))
                    {
                        _voteTotalsStore.SetTotal(totalRecord.Option, totalRecord.Count);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            finally
            {
                try
                {
                    consumer.Close();
                }
                catch
                {
                    // swallow shutdown errors
                }
            }
        }, stoppingToken);
    }
}

/// <summary>
/// Background consumer that maintains in-memory per-city vote distributions by consuming the city totals topic.
/// </summary>
sealed class CityVotesConsumer : BackgroundService
{
    private readonly CityVoteStore _cityVoteStore;
    private readonly ISchemaRegistryClient _schemaRegistryClient;
    private readonly KafkaOptions _kafkaOptions;

    public CityVotesConsumer(CityVoteStore cityVoteStore, ISchemaRegistryClient schemaRegistryClient, KafkaOptions kafkaOptions)
    {
        _cityVoteStore = cityVoteStore;
        _schemaRegistryClient = schemaRegistryClient;
        _kafkaOptions = kafkaOptions;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        return Task.Run(() =>
        {
            var consumerConfig = new ConsumerConfig
            {
                BootstrapServers = _kafkaOptions.BootstrapServers,
                GroupId = _kafkaOptions.UiCityGroupId ?? "webapp-city-metrics",
                AutoOffsetReset = AutoOffsetReset.Earliest,
                EnableAutoCommit = true,
                IsolationLevel = IsolationLevel.ReadCommitted
            };

            consumerConfig.Set("socket.keepalive.enable", "true");
            consumerConfig.Set("debug", "broker,protocol,security");
            consumerConfig.Set("broker.address.family", "v4");

            using var consumer = new ConsumerBuilder<string, VoteTotal>(consumerConfig)
                .SetValueDeserializer(new JsonDeserializer<VoteTotal>(_schemaRegistryClient).AsSyncOverAsync())
                .Build();

            consumer.Subscribe(_kafkaOptions.VotesByCityTopic);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var consumeResult = consumer.Consume(stoppingToken);
                    if (consumeResult?.Message?.Value is { } totalRecord && !string.IsNullOrWhiteSpace(totalRecord.Option))
                    {
                        _cityVoteStore.SetCityVote(totalRecord);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
            finally
            {
                try
                {
                    consumer.Close();
                }
                catch
                {
                    // swallow shutdown errors
                }
            }
        }, stoppingToken);
    }
}
